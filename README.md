# Conflict: Global Terror — SquadMod Launcher

A single-file, self-contained Windows WPF launcher (`.NET 10`, `win-x64`) that automates installing **Conflict: Global Terror (PC, 2005)**, applying **SquadMod** + **dgVoodoo**, checking for mod updates via GitHub Releases, and launching the game.

Because the `.NET` runtime and all mod/dgVoodoo files are bundled directly inside `dist/ConflictLauncher.exe`, you only need to send your friend **one `.exe` file**—no extra folders, archives, or runtime installers required.

---

## Table of Contents
1. [Features](#features)
2. [How the Automated Install Works](#how-the-automated-install-works)
3. [How Mod Updates Work](#how-mod-updates-work)
4. [How to Publish a New Mod Update (For Maintainers)](#how-to-publish-a-new-mod-update-for-maintainers)
5. [System Checks & Co-op Options](#system-checks--co-op-options)
6. [File Locations & Persistence](#file-locations--persistence)
7. [Project Structure](#project-structure)
8. [Building from Source](#building-from-source)

---

## Features
- **One-click full setup**: Downloads the game repack (~1.6 GB), extracts it, runs the installer silently to your chosen folder, and installs SquadMod + dgVoodoo automatically.
- **Zero external dependencies**: Compiled as a self-contained single-file executable (`PublishSingleFile=true`, `SelfContained=true`) with all 7 mod files embedded as assembly resources.
- **GitHub Releases auto-updater**: Checks `Litzer-dev/cgt-squadmod` on startup, compares `tag_name` against `mod_version.txt`, and offers one-click mod updates while preserving your `Steam=` setting.
- **Existing install detection**: Point the launcher at an already-installed `ConflictGlobal.exe` to install/reinstall SquadMod, toggle Steam Virtual LAN vs. plain LAN, or launch the game directly.
- **DirectPlay check**: Detects whether Windows DirectPlay (`dplayx.dll`) is enabled and provides a 1-click button to enable it.

---

## How the Automated Install Works

When the user picks an install folder (default: `C:\Games\Conflict Global Terror`) and clicks **DOWNLOAD & INSTALL GAME + SQUADMOD**, `MainWindow.RunFullInstallAsync` runs a 4-stage pipeline:

### Step 1 — Download (`GoogleDriveDownloader.cs`)
- Fetches the game archive from Google Drive file ID `1aQgQGGzxRmQYyN-Fx4N6uNlk3Ch2I4sQ` (`Conflict-Global-Terror_Win_EN_Magipack-Repack.zip`, ~1.6 GB).
- **Virus-scan bypass handling**: Because Google Drive serves an HTML confirmation page for files too large to scan, `GoogleDriveDownloader` sends the initial `GET` to `https://drive.google.com/uc?export=download&id=...` with a `CookieContainer`. When `text/html` is returned, it parses `<form id="download-form" action="...">` and all hidden `<input>` fields (`id`, `export`, `confirm`, `uuid`) and issues a second `GET` to `drive.usercontent.google.com/download`.
- **Safe streaming**: Writes to `%LocalAppData%\ConflictLauncher\temp\Conflict-Global-Terror_Win_EN_Magipack-Repack.zip.part` in 64 KB chunks while reporting downloaded MB, total MB, percentage, speed (MB/s), and ETA. Once complete, it renames `.part` to `.zip`.
- **Resume/cache check**: Before downloading, `GameInstallerService.IsValidZip` checks if a valid cached `.zip` (>100 MB, readable `ZipArchive`) already exists in `temp/` and skips re-downloading if so.

### Step 2 — Extract (`GameInstallerService.ExtractZipAsync`)
- Unpacks the `.zip` into `%LocalAppData%\ConflictLauncher\temp\extracted` with real-time percentage progress and zip-slip path traversal validation.

### Step 3 — Silent Installer (`GameInstallerService.RunSilentInstallerAsync`)
- Locates the extracted Inno Setup executable (`Conflict - Global Terror_Repack_Setup.exe`).
- Launches it with `UseShellExecute = true` (so Windows handles UAC elevation if needed) and Inno Setup's unattended command-line flags:
  ```text
  /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR="<chosen_install_folder>"
  ```
  - `/VERYSILENT` + `/SUPPRESSMSGBOXES` + `/SP-`: hides the wizard and suppresses confirmation/message boxes.
  - `/NORESTART`: prevents any reboot prompt.
  - `/DIR="..."`: installs directly into the folder chosen in the launcher.
- Awaits `proc.WaitForExitAsync` and verifies `ExitCode == 0`.
- Calls `GameInstallerService.FindGameExecutable` to locate `ConflictGlobal.exe` in the target folder (or up to 2 subdirectories deep).

### Step 4 — Automatic Mod Deployment & Cleanup (`GameInstallerService.InstallModFiles`)
- Extracts all 7 embedded resources (`ConflictLauncher.ModFiles.*`) directly into the folder containing `ConflictGlobal.exe`, overwriting existing files as required by SquadMod:
  - `dinput8.dll` (SquadMod proxy DLL)
  - `squadmod.ini` (SquadMod configuration)
  - `steam_api.dll` (Valve 32-bit Steamworks redistributable)
  - `D3D9.dll` (dgVoodoo 2.82 graphics wrapper)
  - `dgVoodoo.conf` (dgVoodoo settings)
  - `dgVoodooCpl.exe` (dgVoodoo control panel)
  - `README.txt` (written as `SquadMod_README.txt` so it does not overwrite any game readme)
- If a newer release is available on GitHub (`_latestRelease`), it also applies that release automatically on top of the base files and writes `mod_version.txt`.
- Deletes `%LocalAppData%\ConflictLauncher\temp` so the ~3.2 GB of temporary archive/installer files are reclaimed.

---

## How Mod Updates Work

Mod update checking and patching are handled by `Services/ModUpdaterService.cs`:

1. **Startup Check (`MainWindow_Loaded`)**:
   - Immediately after the window opens, the launcher sends **one** asynchronous `GET` request to:
     ```text
     https://api.github.com/repos/Litzer-dev/cgt-squadmod/releases/latest
     ```
   - Uses `User-Agent: ConflictLauncher/1.0` and `Accept: application/vnd.github+json`.
   - If the repository has no published releases yet (`404 Not Found`) or the user is offline, the check fails silently without interrupting the UI.

2. **Version Comparison (`ModUpdaterService.IsNewerVersion`)**:
   - Reads `mod_version.txt` from the game folder (next to `ConflictGlobal.exe`).
   - Compares the local version string against the GitHub release's `tag_name` (e.g., `v1.4.2`):
     - Strips any leading `v` or `V`.
     - If both parse as `System.Version` (e.g., `1.4.2 > 1.4.1`), performs a numeric version comparison.
     - Otherwise falls back to a case-insensitive string inequality check.
     - If `mod_version.txt` does not exist yet (e.g., first time updating an existing install), the remote release is treated as newer.

3. **Applying an Update (`ModUpdaterService.DownloadAndApplyUpdateAsync`)**:
   - When an update is detected, **SquadMod + dgVoodoo** status turns amber (`Update available: <tag> (installed: <local>)`) and an **Update to `<tag>`** button appears.
   - Clicking it downloads the release assets from `browser_download_url`:
     - **If a `.zip` asset is present**: Downloads the first `.zip` asset to `%TEMP%`, extracts it into the game folder (overwriting existing files), and automatically strips a single top-level wrapper folder (such as `release/` or `squadmod/`) if all files in the zip sit inside one.
     - **If no `.zip` asset is present**: Downloads every attached release asset directly into the game folder.
   - Any `README.txt` in the release is saved as `SquadMod_README.txt`.
   - **Settings preservation**: If `squadmod.ini` already existed before the update, the user's `Steam=1` / `Steam=0` setting is preserved after the update finishes.
   - Writes the new `tag_name` into `mod_version.txt` in the game folder.

---

## How to Publish a New Mod Update (For Maintainers)

To push a mod update to everyone using `ConflictLauncher.exe`:

1. Go to **`https://github.com/Litzer-dev/cgt-squadmod/releases`** and click **Draft a new release**.
2. Create a new tag following semantic versioning, e.g. **`v1.0.0`**, **`v1.0.1`**, **`v1.4.2`** (do **not** mark it as a pre-release, because `/releases/latest` only returns full releases).
3. Attach either:
   - **Option A (Recommended):** A single `.zip` file containing the updated mod files (e.g. `dinput8.dll`, `squadmod.ini`, `steam_api.dll`, etc., either at the zip root or inside a single `release/` folder), **OR**
   - **Option B:** The individual files (`dinput8.dll`, `squadmod.ini`, `steam_api.dll`, etc.) directly as release assets.
4. Click **Publish release**.

The next time your friend opens `ConflictLauncher.exe`, it will automatically detect the new `tag_name` and show the **Update to `vX.Y.Z`** button.

---

## System Checks & Co-op Options
- **Play Co-op over Steam (Virtual LAN)**: Toggle switch in the status card that reads and writes `Steam=1` or `Steam=0` in `squadmod.ini`.
- **DirectPlay Check**: Checks for `dplayx.dll` in `SysWOW64` / `System32`. If missing, displays an **Enable DirectPlay** button that runs `dism.exe /online /Enable-Feature /FeatureName:DirectPlay /All /NoRestart` with administrator elevation.
- **Reinstall Mod**: Re-copies the embedded `ModFiles/` into the selected game folder.
- **Open Folder**: Opens the detected game directory in Windows Explorer.

---

## File Locations & Persistence
| Item | Path |
|---|---|
| Launcher config | `%LocalAppData%\ConflictLauncher\config.json` |
| Temporary download & extract | `%LocalAppData%\ConflictLauncher\temp\` (deleted after install) |
| Installed mod version | `<GameFolder>\mod_version.txt` |
| Mod configuration | `<GameFolder>\squadmod.ini` |
| Mod README | `<GameFolder>\SquadMod_README.txt` |

---

## Project Structure
```text
ConflictLauncher/
├── ConflictLauncher.csproj          # .NET 10 WPF single-file self-contained project
├── App.xaml / App.xaml.cs           # Tactical dark theme, button/toggle/progress styles
├── MainWindow.xaml / .cs            # Borderless UI, 4-step pipeline, status & actions
├── Services/
│   ├── LauncherConfig.cs            # Persists InstallDirectory in LocalAppData
│   ├── GoogleDriveDownloader.cs     # Streams repack zip + handles virus-scan form
│   ├── GameInstallerService.cs      # Zip extraction, Inno Setup silent install, mod copy
│   └── ModUpdaterService.cs         # GitHub Releases check, mod_version.txt & patching
├── ModFiles/                        # Embedded into ConflictLauncher.exe at build time
│   ├── D3D9.dll
│   ├── dgVoodoo.conf
│   ├── dgVoodooCpl.exe
│   ├── dinput8.dll
│   ├── README.txt
│   ├── squadmod.ini
│   └── steam_api.dll
└── dist/
    └── ConflictLauncher.exe         # Final standalone executable to share
```

---

## Building from Source
Requires the **.NET 10 SDK** (`net10.0-windows`):

```powershell
# Debug / Release build
dotnet build ConflictLauncher.csproj -c Release

# Publish standalone single-file executable to dist/
dotnet publish ConflictLauncher.csproj -c Release -o dist
```
