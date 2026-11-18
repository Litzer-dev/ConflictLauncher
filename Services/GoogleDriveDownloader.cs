using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Web;

namespace ConflictLauncher.Services;

public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes, double BytesPerSecond);

public sealed class GoogleDriveDownloader
{
    private const long FallbackTotalBytes = 1_725_793_128L;

    public async Task DownloadFileAsync(
        string fileId,
        string destinationZipPath,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = new CookieContainer()
        };

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromHours(2)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ConflictLauncher/1.0");

        var initialUrl = $"https://drive.google.com/uc?export=download&id={Uri.EscapeDataString(fileId)}";
        using var initialResponse = await client.GetAsync(
            initialUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        initialResponse.EnsureSuccessStatusCode();

        HttpResponseMessage downloadResponse = initialResponse;
        var contentType = initialResponse.Content.Headers.ContentType?.MediaType ?? string.Empty;

        if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            var html = await initialResponse.Content.ReadAsStringAsync(cancellationToken);
            var confirmUrl = ExtractConfirmUrl(html, fileId);
            initialResponse.Dispose();

            downloadResponse = await client.GetAsync(
                confirmUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            downloadResponse.EnsureSuccessStatusCode();

            var secondType = downloadResponse.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (secondType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                downloadResponse.Dispose();
                throw new InvalidOperationException(
                    "Google Drive returned an HTML page instead of the game archive. " +
                    "The file may have exceeded its download quota or is no longer shared.");
            }
        }

        using (downloadResponse)
        {
            var totalBytes = downloadResponse.Content.Headers.ContentLength ?? FallbackTotalBytes;
            var destDir = Path.GetDirectoryName(destinationZipPath)!;
            Directory.CreateDirectory(destDir);

            var partPath = destinationZipPath + ".part";
            if (File.Exists(partPath))
                File.Delete(partPath);

            await using var contentStream = await downloadResponse.Content.ReadAsStreamAsync(cancellationToken);
            await using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                var buffer = new byte[65536];
                long totalRead = 0;
                var startTime = DateTime.UtcNow;
                var lastReport = DateTime.MinValue;

                while (true)
                {
                    int read = await contentStream.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                        break;

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    totalRead += read;

                    var now = DateTime.UtcNow;
                    if ((now - lastReport).TotalMilliseconds >= 120)
                    {
                        var elapsed = Math.Max(0.1, (now - startTime).TotalSeconds);
                        progress.Report(new DownloadProgress(totalRead, totalBytes, totalRead / elapsed));
                        lastReport = now;
                    }
                }

                var totalElapsed = Math.Max(0.1, (DateTime.UtcNow - startTime).TotalSeconds);
                progress.Report(new DownloadProgress(totalRead, totalBytes, totalRead / totalElapsed));
            }

            if (File.Exists(destinationZipPath))
                File.Delete(destinationZipPath);

            File.Move(partPath, destinationZipPath);
        }
    }

    private static string ExtractConfirmUrl(string html, string fileId)
    {
        var actionMatch = Regex.Match(
            html,
            @"<form[^>]*id=""download-form""[^>]*action=""([^""]+)""",
            RegexOptions.IgnoreCase);

        var actionUrl = actionMatch.Success
            ? WebUtility.HtmlDecode(actionMatch.Groups[1].Value)
            : "https://drive.usercontent.google.com/download";

        var query = HttpUtility.ParseQueryString(string.Empty);
        var inputMatches = Regex.Matches(
            html,
            @"<input[^>]*type=""hidden""[^>]*name=""([^""]+)""[^>]*value=""([^""]*)""",
            RegexOptions.IgnoreCase);

        foreach (Match match in inputMatches)
        {
            var name = WebUtility.HtmlDecode(match.Groups[1].Value);
            var value = WebUtility.HtmlDecode(match.Groups[2].Value);
            query[name] = value;
        }

        if (query.Count == 0)
        {
            query["id"] = fileId;
            query["export"] = "download";
            query["confirm"] = "t";
        }

        var separator = actionUrl.Contains('?') ? "&" : "?";
        return $"{actionUrl}{separator}{query}";
    }
}
