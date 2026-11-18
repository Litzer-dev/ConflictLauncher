using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ConflictLauncher.Services;

public sealed record GitHubReleaseAsset(string Name, string DownloadUrl, long Size);

public sealed record GitHubReleaseInfo(string TagName, IReadOnlyList<GitHubReleaseAsset> Assets);

public sealed class ModUpdaterService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/Litzer-dev/cgt-squadmod/releases/latest";
    public const string VersionFileName = "mod_version.txt";

    public static string? GetInstalledVersion(string gameDirectory)
    {
        var path = Path.Combine(gameDirectory, VersionFileName);
        if (!File.Exists(path))
            return null;

        var text = File.ReadAllText(path).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static void SaveInstalledVersion(string gameDirectory, string tagName)
    {
        Directory.CreateDirectory(gameDirectory);
        File.WriteAllText(Path.Combine(gameDirectory, VersionFileName), tagName.Trim());
    }

    public static bool IsNewerVersion(string? localTag, string remoteTag)
    {
        if (string.IsNullOrWhiteSpace(remoteTag))
            return false;

        if (string.IsNullOrWhiteSpace(localTag))
            return true;

        var cleanLocal = localTag.Trim().TrimStart('v', 'V');
        var cleanRemote = remoteTag.Trim().TrimStart('v', 'V');

        if (Version.TryParse(cleanLocal, out var vLocal) && Version.TryParse(cleanRemote, out var vRemote))
            return vRemote > vLocal;

        return !string.Equals(cleanLocal, cleanRemote, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<GitHubReleaseInfo?> CheckLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync(LatestReleaseUrl, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagProp))
                return null;

            var tagName = tagProp.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(tagName))
                return null;

            var assets = new List<GitHubReleaseAsset>();
            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in assetsProp.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = item.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    var size = item.TryGetProperty("size", out var s) ? s.GetInt64() : 0L;

                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                        assets.Add(new GitHubReleaseAsset(name, url, size));
                }
            }

            return new GitHubReleaseInfo(tagName, assets);
        }
        catch
        {
            return null;
        }
    }

    public async Task DownloadAndApplyUpdateAsync(
        GitHubReleaseInfo release,
        string gameDirectory,
        IProgress<string>? statusProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (release.Assets.Count == 0)
            throw new InvalidOperationException($"Release {release.TagName} has no downloadable assets.");

        var hadIni = File.Exists(Path.Combine(gameDirectory, "squadmod.ini"));
        var previousSteamMode = hadIni && GameInstallerService.GetSteamMode(gameDirectory);

        using var client = CreateClient();
        var zipAsset = release.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        if (zipAsset is not null)
        {
            statusProgress?.Report($"Downloading {zipAsset.Name} ({release.TagName})...");
            var tempZip = Path.Combine(Path.GetTempPath(), $"cgt_squadmod_{Guid.NewGuid():N}.zip");
            try
            {
                await DownloadAssetToFileAsync(client, zipAsset.DownloadUrl, tempZip, cancellationToken);
                statusProgress?.Report($"Extracting {zipAsset.Name}...");
                ExtractModZipToGameDir(tempZip, gameDirectory);
            }
            finally
            {
                if (File.Exists(tempZip))
                    File.Delete(tempZip);
            }
        }
        else
        {
            foreach (var asset in release.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                statusProgress?.Report($"Downloading {asset.Name}...");
                var targetName = asset.Name.Equals("README.txt", StringComparison.OrdinalIgnoreCase)
                    ? "SquadMod_README.txt"
                    : asset.Name;
                var targetPath = Path.Combine(gameDirectory, targetName);
                await DownloadAssetToFileAsync(client, asset.DownloadUrl, targetPath, cancellationToken);
            }
        }

        if (hadIni)
            GameInstallerService.SetSteamMode(gameDirectory, previousSteamMode);

        SaveInstalledVersion(gameDirectory, release.TagName);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ConflictLauncher/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static async Task DownloadAssetToFileAsync(
        HttpClient client,
        string url,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static void ExtractModZipToGameDir(string zipPath, string gameDirectory)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var fileEntries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();

        // Strip a common top-level wrapper folder (e.g. "release/" or "squadmod/") if all files share it
        string? commonPrefix = null;
        if (fileEntries.Count > 0)
        {
            var firstParts = fileEntries[0].FullName.Split('/');
            if (firstParts.Length > 1)
            {
                var candidate = firstParts[0] + "/";
                if (fileEntries.All(e => e.FullName.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)))
                    commonPrefix = candidate;
            }
        }

        foreach (var entry in fileEntries)
        {
            var relPath = commonPrefix is not null
                ? entry.FullName[commonPrefix.Length..]
                : entry.FullName;

            if (relPath.Equals("README.txt", StringComparison.OrdinalIgnoreCase))
                relPath = "SquadMod_README.txt";

            var destPath = Path.GetFullPath(Path.Combine(gameDirectory, relPath));
            if (!destPath.StartsWith(Path.GetFullPath(gameDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unsafe zip entry path: {entry.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }
}
