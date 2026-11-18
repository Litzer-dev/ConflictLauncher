using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace ConflictLauncher.Services;

public sealed class GameInstallerService
{
    private const string ResourcePrefix = "ConflictLauncher.ModFiles.";

    public static IReadOnlyList<Process> FindRunningGameProcesses(string? gameDirectory)
    {
        var matches = new List<Process>();
        var seenIds = new HashSet<int>();

        foreach (var p in Process.GetProcessesByName("ConflictGlobal"))
        {
            if (seenIds.Add(p.Id))
                matches.Add(p);
        }

        if (!string.IsNullOrWhiteSpace(gameDirectory))
        {
            var fullDir = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var p in Process.GetProcesses())
            {
                if (seenIds.Contains(p.Id))
                    continue;
                try
                {
                    var exe = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exe) && exe.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase))
                    {
                        if (seenIds.Add(p.Id))
                            matches.Add(p);
                    }
                }
                catch
                {
                    // Ignore processes that deny MainModule access
                }
            }
        }

        return matches;
    }

    public static string? FindGameExecutable(string? rootDir)
    {
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
            return null;

        var direct = Path.Combine(rootDir, "ConflictGlobal.exe");
        if (File.Exists(direct))
            return direct;

        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(rootDir))
            {
                var candidate = Path.Combine(subDir, "ConflictGlobal.exe");
                if (File.Exists(candidate))
                    return candidate;

                foreach (var nestedDir in Directory.EnumerateDirectories(subDir))
                {
                    var nested = Path.Combine(nestedDir, "ConflictGlobal.exe");
                    if (File.Exists(nested))
                        return nested;
                }
            }
        }
        catch
        {
            // Ignore inaccessible subdirectories
        }

        return null;
    }

    public static bool IsModInstalled(string gameDirectory)
    {
        return File.Exists(Path.Combine(gameDirectory, "dinput8.dll"))
            && File.Exists(Path.Combine(gameDirectory, "squadmod.ini"))
            && File.Exists(Path.Combine(gameDirectory, "steam_api.dll"));
    }

    public static bool IsDirectPlayEnabled()
    {
        var sysWow = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "SysWOW64",
            "dplayx.dll");
        var sys32 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "dplayx.dll");
        return File.Exists(sysWow) || File.Exists(sys32);
    }

    public async Task EnableDirectPlayAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dism.exe",
            Arguments = "/online /Enable-Feature /FeatureName:DirectPlay /All /NoRestart",
            UseShellExecute = true,
            Verb = "runas"
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start DISM to enable DirectPlay.");
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0 && proc.ExitCode != 3010)
            throw new InvalidOperationException($"DISM exited with code {proc.ExitCode}.");
    }

    public async Task ExtractZipAsync(
        string zipPath,
        string extractDir,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, recursive: true);
            Directory.CreateDirectory(extractDir);

            using var archive = ZipFile.OpenRead(zipPath);
            var totalUncompressed = archive.Entries.Sum(e => Math.Max(1L, e.Length));
            long processedBytes = 0;

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var destinationPath = Path.GetFullPath(Path.Combine(extractDir, entry.FullName));
                if (!destinationPath.StartsWith(Path.GetFullPath(extractDir), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Unsafe zip entry path: {entry.FullName}");

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                using var entryStream = entry.Open();
                using var outStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);

                var buffer = new byte[65536];
                int read;
                while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    outStream.Write(buffer, 0, read);
                    processedBytes += read;
                    progress.Report(Math.Min(100.0, (double)processedBytes / totalUncompressed * 100.0));
                }
            }

            progress.Report(100.0);
        }, cancellationToken);
    }

    public async Task RunSilentInstallerAsync(
        string extractDir,
        string installDir,
        CancellationToken cancellationToken)
    {
        var setupExe = Directory
            .EnumerateFiles(extractDir, "*.exe", SearchOption.AllDirectories)
            .OrderByDescending(f => f.Contains("Setup", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault()
            ?? throw new FileNotFoundException("Could not find the game installer executable in the extracted archive.");

        Directory.CreateDirectory(installDir);

        var cleanDir = installDir.TrimEnd('\\', '/');
        var psi = new ProcessStartInfo
        {
            FileName = setupExe,
            Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=\"{cleanDir}\"",
            WorkingDirectory = Path.GetDirectoryName(setupExe)!,
            UseShellExecute = true
        };

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Administrator permission was cancelled by the user.", ex);
        }

        if (proc is null)
            throw new InvalidOperationException("Failed to start the game installer.");

        using (proc)
        {
            await proc.WaitForExitAsync(cancellationToken);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"Game installer exited with code {proc.ExitCode}.");
        }
    }

    public int InstallDefaultConfigFiles(string gameDirectory, bool overwriteIni = false)
    {
        Directory.CreateDirectory(gameDirectory);
        var assembly = Assembly.GetExecutingAssembly();
        var count = 0;

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                continue;

            var fileName = resourceName[ResourcePrefix.Length..];
            var targetName = fileName.Equals("README.txt", StringComparison.OrdinalIgnoreCase)
                ? "SquadMod_README.txt"
                : fileName;

            var targetPath = Path.Combine(gameDirectory, targetName);
            if (!overwriteIni && targetName.Equals("squadmod.ini", StringComparison.OrdinalIgnoreCase) && File.Exists(targetPath))
                continue;

            if (File.Exists(targetPath))
                File.SetAttributes(targetPath, FileAttributes.Normal);

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Missing embedded resource: {resourceName}");
            using var outFile = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.CopyTo(outFile);
            count++;
        }

        return count;
    }

    public static bool GetSteamMode(string gameDirectory)
    {
        var iniPath = Path.Combine(gameDirectory, "squadmod.ini");
        if (!File.Exists(iniPath))
            return true;

        foreach (var line in File.ReadAllLines(iniPath))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Steam=", StringComparison.OrdinalIgnoreCase))
                return trimmed["Steam=".Length..].Trim() == "1";
        }

        return true;
    }

    public static void SetSteamMode(string gameDirectory, bool enabled)
    {
        var iniPath = Path.Combine(gameDirectory, "squadmod.ini");
        if (!File.Exists(iniPath))
            return;

        var lines = File.ReadAllLines(iniPath);
        var updated = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().StartsWith("Steam=", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"Steam={(enabled ? "1" : "0")}";
                updated = true;
                break;
            }
        }

        if (!updated)
        {
            var list = lines.ToList();
            list.Add($"Steam={(enabled ? "1" : "0")}");
            lines = list.ToArray();
        }

        File.WriteAllLines(iniPath, lines);
    }

    public static bool IsValidZip(string zipPath)
    {
        try
        {
            if (!File.Exists(zipPath) || new FileInfo(zipPath).Length < 100_000_000)
                return false;

            using var archive = ZipFile.OpenRead(zipPath);
            return archive.Entries.Count > 0;
        }
        catch
        {
            return false;
        }
    }
}
