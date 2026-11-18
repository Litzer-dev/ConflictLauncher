using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ConflictLauncher.Services;
using Microsoft.Win32;

namespace ConflictLauncher;

public partial class MainWindow : Window
{
    private const string GoogleDriveFileId = "1aQgQGGzxRmQYyN-Fx4N6uNlk3Ch2I4sQ";

    private readonly LauncherConfig _config;
    private readonly GoogleDriveDownloader _downloader = new();
    private readonly GameInstallerService _installer = new();
    private readonly ModUpdaterService _updater = new();

    private CancellationTokenSource? _cts;
    private GitHubReleaseInfo? _latestRelease;
    private bool _isBusy;
    private bool _suppressCheckEvents;

    private static readonly Brush GreenText = new SolidColorBrush(Color.FromRgb(52, 211, 153));
    private static readonly Brush AmberText = new SolidColorBrush(Color.FromRgb(251, 191, 36));
    private static readonly Brush RedText = new SolidColorBrush(Color.FromRgb(248, 113, 113));

    public MainWindow()
    {
        InitializeComponent();
        _config = LauncherConfig.Load();
        InstallPathBox.Text = _config.InstallDirectory;
        Loaded += MainWindow_Loaded;
        RefreshStatus();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _latestRelease = await _updater.CheckLatestReleaseAsync();
        RefreshStatus();
    }

    private string? ResolvedGameExe => GameInstallerService.FindGameExecutable(InstallPathBox.Text.Trim());

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdatePipelineStep(int activeStep)
    {
        (Border pill, TextBlock label, int step)[] nodes =
        [
            (Step1Pill, Step1Label, 1),
            (Step2Pill, Step2Label, 2),
            (Step3Pill, Step3Label, 3),
            (Step4Pill, Step4Label, 4)
        ];

        foreach (var (pill, label, step) in nodes)
        {
            if (activeStep == 5 || step < activeStep)
            {
                pill.Background = new SolidColorBrush(Color.FromRgb(12, 42, 34));
                pill.BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                label.Foreground = GreenText;
            }
            else if (step == activeStep)
            {
                pill.Background = new SolidColorBrush(Color.FromRgb(45, 31, 14));
                pill.BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                label.Foreground = AmberText;
            }
            else
            {
                pill.Background = new SolidColorBrush(Color.FromRgb(26, 34, 46));
                pill.BorderBrush = new SolidColorBrush(Color.FromRgb(42, 54, 73));
                label.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139));
            }
        }
    }

    private void RefreshStatus()
    {
        var gameExe = ResolvedGameExe;
        var gameInstalled = gameExe is not null;
        var gameDir = gameExe is not null ? Path.GetDirectoryName(gameExe)! : InstallPathBox.Text.Trim();
        var modInstalled = gameInstalled && GameInstallerService.IsModInstalled(gameDir);
        var directPlay = GameInstallerService.IsDirectPlayEnabled();

        GameStatusText.Text = gameInstalled
            ? $"Installed ({gameDir})"
            : "Not installed at selected path";
        GameStatusText.Foreground = gameInstalled ? GreenText : RedText;
        GameStatusDot.Fill = gameInstalled ? GreenText : RedText;

        var localVersion = modInstalled ? ModUpdaterService.GetInstalledVersion(gameDir) : null;
        var hasUpdate = modInstalled
            && _latestRelease is not null
            && ModUpdaterService.IsNewerVersion(localVersion, _latestRelease.TagName);

        if (modInstalled && hasUpdate && _latestRelease is not null)
        {
            var currentLabel = localVersion ?? "unversioned";
            ModStatusText.Text = $"Update available: {_latestRelease.TagName} (installed: {currentLabel})";
            ModStatusText.Foreground = AmberText;
            ModStatusDot.Fill = AmberText;
            UpdateModButton.Content = $"Update to {_latestRelease.TagName}";
            UpdateModButton.Visibility = Visibility.Visible;
        }
        else
        {
            var verSuffix = localVersion is not null ? $" ({localVersion})" : "";
            ModStatusText.Text = modInstalled
                ? $"Installed{verSuffix} (dinput8.dll + Steam + dgVoodoo)"
                : gameInstalled ? "Not installed yet" : "Waiting for game install";
            ModStatusText.Foreground = modInstalled ? GreenText : AmberText;
            ModStatusDot.Fill = modInstalled ? GreenText : AmberText;
            UpdateModButton.Visibility = Visibility.Collapsed;
        }

        DirectPlayStatusText.Text = directPlay ? "Enabled" : "Not detected (recommended for 2005 game)";
        DirectPlayStatusText.Foreground = directPlay ? GreenText : AmberText;
        DirectPlayStatusDot.Fill = directPlay ? GreenText : AmberText;
        EnableDirectPlayButton.Visibility = directPlay ? Visibility.Collapsed : Visibility.Visible;

        ModOptionsPanel.Visibility = modInstalled ? Visibility.Visible : Visibility.Collapsed;
        if (modInstalled)
        {
            _suppressCheckEvents = true;
            SteamModeCheckBox.IsChecked = GameInstallerService.GetSteamMode(gameDir);
            _suppressCheckEvents = false;
        }

        if (!_isBusy)
        {
            if (gameInstalled && modInstalled)
            {
                PrimaryActionButton.Content = "LAUNCH CONFLICT: GLOBAL TERROR";
                PrimaryActionButton.Style = (Style)FindResource("PlayButton");
                StepText.Text = "Ready for deployment!";
                ProgressDetailText.Text = "Game and SquadMod are installed and configured.";
                MainProgressBar.Value = 100;
                UpdatePipelineStep(5);
            }
            else if (gameInstalled)
            {
                PrimaryActionButton.Content = "INSTALL SQUADMOD ONLY";
                PrimaryActionButton.Style = (Style)FindResource("PrimaryButton");
                StepText.Text = "Game found — SquadMod is not installed yet.";
                ProgressDetailText.Text = "Click below to copy SquadMod and dgVoodoo files into the game folder.";
                MainProgressBar.Value = 75;
                UpdatePipelineStep(4);
            }
            else
            {
                PrimaryActionButton.Content = "DOWNLOAD & INSTALL GAME + SQUADMOD";
                PrimaryActionButton.Style = (Style)FindResource("PrimaryButton");
                StepText.Text = "Ready. Choose an install folder and click Install to begin.";
                ProgressDetailText.Text = "Downloads ~1.6 GB repack, extracts, runs silent setup, and installs SquadMod.";
                MainProgressBar.Value = 0;
                UpdatePipelineStep(0);
            }
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        InstallPathBox.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        PrimaryActionButton.IsEnabled = !busy;
        UpdateModButton.IsEnabled = !busy;
        ReinstallModButton.IsEnabled = !busy;
        SteamModeCheckBox.IsEnabled = !busy;
        EnableDirectPlayButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InstallPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _isBusy)
            return;

        _config.InstallDirectory = InstallPathBox.Text.Trim();
        _config.Save();
        RefreshStatus();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Conflict: Global Terror Install Folder",
            InitialDirectory = Directory.Exists(InstallPathBox.Text) ? InstallPathBox.Text : @"C:\"
        };

        if (dialog.ShowDialog() == true)
        {
            InstallPathBox.Text = dialog.FolderName;
            _config.InstallDirectory = dialog.FolderName;
            _config.Save();
            RefreshStatus();
        }
    }

    private async void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        var gameExe = ResolvedGameExe;
        if (gameExe is not null && GameInstallerService.IsModInstalled(Path.GetDirectoryName(gameExe)!))
        {
            LaunchGame(gameExe);
            return;
        }

        if (gameExe is not null)
        {
            await InstallModIntoAsync(Path.GetDirectoryName(gameExe)!);
            return;
        }

        await RunFullInstallAsync();
    }

    private async Task RunFullInstallAsync()
    {
        var targetDir = InstallPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            MessageBox.Show("Please choose an install location first.", "Install Location Required",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _config.InstallDirectory = targetDir;
        _config.Save();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);

        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ConflictLauncher",
            "temp");
        var zipPath = Path.Combine(cacheRoot, "Conflict-Global-Terror_Win_EN_Magipack-Repack.zip");
        var extractDir = Path.Combine(cacheRoot, "extracted");

        try
        {
            // Step 1: Download
            UpdatePipelineStep(1);
            if (GameInstallerService.IsValidZip(zipPath))
            {
                StepText.Text = "Step 1/4: Found cached game archive, skipping download.";
                MainProgressBar.IsIndeterminate = false;
                MainProgressBar.Value = 100;
            }
            else
            {
                StepText.Text = "Step 1/4: Downloading Conflict: Global Terror...";
                MainProgressBar.IsIndeterminate = false;
                MainProgressBar.Value = 0;

                var dlProgress = new Progress<DownloadProgress>(p =>
                {
                    var mbRead = p.BytesReceived / (1024.0 * 1024.0);
                    var mbTotal = (p.TotalBytes ?? 1_725_793_128L) / (1024.0 * 1024.0);
                    var pct = Math.Min(100.0, mbRead / mbTotal * 100.0);
                    var speedMb = p.BytesPerSecond / (1024.0 * 1024.0);

                    var etaText = "";
                    if (p.BytesPerSecond > 1024 && p.TotalBytes is long total && total > p.BytesReceived)
                    {
                        var remainingSec = (total - p.BytesReceived) / p.BytesPerSecond;
                        var ts = TimeSpan.FromSeconds(remainingSec);
                        etaText = $"  •  ETA {ts:mm\\:ss}";
                    }

                    MainProgressBar.Value = pct;
                    ProgressDetailText.Text = $"{mbRead:F1} MB / {mbTotal:F1} MB ({pct:F1}%)  •  {speedMb:F2} MB/s{etaText}";
                });

                await _downloader.DownloadFileAsync(GoogleDriveFileId, zipPath, dlProgress, ct);
            }

            // Step 2: Extract
            UpdatePipelineStep(2);
            StepText.Text = "Step 2/4: Extracting installer archive...";
            MainProgressBar.IsIndeterminate = false;
            MainProgressBar.Value = 0;
            ProgressDetailText.Text = "Unpacking setup files...";

            var extractProgress = new Progress<double>(pct =>
            {
                MainProgressBar.Value = pct;
                ProgressDetailText.Text = $"Extracting... {pct:F0}%";
            });
            await _installer.ExtractZipAsync(zipPath, extractDir, extractProgress, ct);

            // Step 3: Run silent installer
            UpdatePipelineStep(3);
            StepText.Text = "Step 3/4: Installing game automatically...";
            MainProgressBar.IsIndeterminate = true;
            ProgressDetailText.Text = $"Installing to {targetDir} (accept UAC prompt if shown)...";

            await _installer.RunSilentInstallerAsync(extractDir, targetDir, ct);

            // Step 4: Install SquadMod from GitHub Releases
            UpdatePipelineStep(4);
            MainProgressBar.IsIndeterminate = true;
            StepText.Text = "Step 4/4: Downloading & installing SquadMod...";
            ProgressDetailText.Text = "Fetching latest SquadMod release from GitHub...";

            var installedExe = GameInstallerService.FindGameExecutable(targetDir);
            var modTargetDir = installedExe is not null ? Path.GetDirectoryName(installedExe)! : targetDir;

            _latestRelease ??= await _updater.CheckLatestReleaseAsync(ct);
            if (_latestRelease is null || _latestRelease.Assets.Count == 0)
                throw new InvalidOperationException("Could not fetch SquadMod release from GitHub (Litzer-dev/cgt-squadmod).");

            var modStatus = new Progress<string>(msg => ProgressDetailText.Text = msg);
            await _updater.DownloadAndApplyUpdateAsync(_latestRelease, modTargetDir, modStatus, ct);
            _installer.InstallDefaultConfigFiles(modTargetDir, overwriteIni: false);

            // Cleanup temp files
            ProgressDetailText.Text = "Cleaning up temporary files...";
            try
            {
                if (Directory.Exists(cacheRoot))
                    Directory.Delete(cacheRoot, recursive: true);
            }
            catch
            {
                // Non-fatal if temp cleanup fails
            }

            UpdatePipelineStep(5);
            MainProgressBar.IsIndeterminate = false;
            MainProgressBar.Value = 100;
            StepText.Text = $"Done! Installed game and SquadMod ({_latestRelease.TagName}).";
            ProgressDetailText.Text = $"Installed at: {modTargetDir}";
        }
        catch (OperationCanceledException)
        {
            MainProgressBar.IsIndeterminate = false;
            UpdatePipelineStep(0);
            StepText.Text = "Installation cancelled.";
            ProgressDetailText.Text = "You can resume or restart anytime.";
        }
        catch (Exception ex)
        {
            MainProgressBar.IsIndeterminate = false;
            UpdatePipelineStep(0);
            StepText.Text = "Installation failed.";
            ProgressDetailText.Text = ex.Message;
            MessageBox.Show(ex.Message, "Install Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
            RefreshStatus();
        }
    }

    private async Task InstallModIntoAsync(string gameDir)
    {
        _cts = new CancellationTokenSource();
        SetBusy(true);
        UpdatePipelineStep(4);
        StepText.Text = "Installing SquadMod from GitHub...";
        MainProgressBar.IsIndeterminate = true;

        try
        {
            _latestRelease ??= await _updater.CheckLatestReleaseAsync(_cts.Token);
            if (_latestRelease is null || _latestRelease.Assets.Count == 0)
                throw new InvalidOperationException("Could not fetch SquadMod release from GitHub (Litzer-dev/cgt-squadmod).");

            var status = new Progress<string>(msg => ProgressDetailText.Text = msg);
            await _updater.DownloadAndApplyUpdateAsync(_latestRelease, gameDir, status, _cts.Token);
            _installer.InstallDefaultConfigFiles(gameDir, overwriteIni: false);

            MainProgressBar.IsIndeterminate = false;
            MainProgressBar.Value = 100;
            UpdatePipelineStep(5);
            StepText.Text = $"Installed SquadMod {_latestRelease.TagName}!";
            ProgressDetailText.Text = $"Installed into: {gameDir}";
        }
        catch (OperationCanceledException)
        {
            MainProgressBar.IsIndeterminate = false;
            StepText.Text = "Mod install cancelled.";
        }
        catch (Exception ex)
        {
            MainProgressBar.IsIndeterminate = false;
            StepText.Text = "Mod install failed.";
            ProgressDetailText.Text = ex.Message;
            MessageBox.Show($"Failed to install SquadMod:\n{ex.Message}",
                "Mod Install Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
            RefreshStatus();
        }
    }

    private void LaunchGame(string gameExe)
    {
        try
        {
            var gameDir = Path.GetDirectoryName(gameExe)!;
            Process.Start(new ProcessStartInfo
            {
                FileName = gameExe,
                WorkingDirectory = gameDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not launch game:\n{ex.Message}",
                "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StepText.Text = "Cancelling...";
        _cts?.Cancel();
    }

    private void SteamModeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressCheckEvents)
            return;

        var gameExe = ResolvedGameExe;
        if (gameExe is null)
            return;

        GameInstallerService.SetSteamMode(Path.GetDirectoryName(gameExe)!, SteamModeCheckBox.IsChecked == true);
    }

    private async void UpdateModButton_Click(object sender, RoutedEventArgs e)
    {
        var gameExe = ResolvedGameExe;
        if (gameExe is null || _latestRelease is null)
            return;

        var gameDir = Path.GetDirectoryName(gameExe)!;
        _cts = new CancellationTokenSource();
        SetBusy(true);
        UpdatePipelineStep(4);
        StepText.Text = $"Updating SquadMod to {_latestRelease.TagName}...";
        MainProgressBar.IsIndeterminate = true;

        try
        {
            var status = new Progress<string>(msg => ProgressDetailText.Text = msg);
            await _updater.DownloadAndApplyUpdateAsync(_latestRelease, gameDir, status, _cts.Token);
            MainProgressBar.IsIndeterminate = false;
            MainProgressBar.Value = 100;
            UpdatePipelineStep(5);
            StepText.Text = $"SquadMod updated to {_latestRelease.TagName}!";
            ProgressDetailText.Text = $"Saved {ModUpdaterService.VersionFileName} in {gameDir}";
        }
        catch (OperationCanceledException)
        {
            MainProgressBar.IsIndeterminate = false;
            StepText.Text = "Mod update cancelled.";
        }
        catch (Exception ex)
        {
            MainProgressBar.IsIndeterminate = false;
            StepText.Text = "Mod update failed.";
            ProgressDetailText.Text = ex.Message;
            MessageBox.Show($"Failed to update SquadMod:\n{ex.Message}",
                "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
            RefreshStatus();
        }
    }

    private async void ReinstallModButton_Click(object sender, RoutedEventArgs e)
    {
        var gameExe = ResolvedGameExe;
        if (gameExe is not null)
            await InstallModIntoAsync(Path.GetDirectoryName(gameExe)!);
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var gameExe = ResolvedGameExe;
        var dir = gameExe is not null ? Path.GetDirectoryName(gameExe)! : InstallPathBox.Text.Trim();
        if (Directory.Exists(dir))
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private async void EnableDirectPlayButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnableDirectPlayButton.IsEnabled = false;
            await _installer.EnableDirectPlayAsync();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not enable DirectPlay:\n{ex.Message}",
                "DirectPlay Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            EnableDirectPlayButton.IsEnabled = true;
        }
    }
}
