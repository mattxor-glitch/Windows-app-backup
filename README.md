# Windows App Backup

Simple app that collects app installers and tries to back up the app data, then restores both to a new computer or after reinstalling the operating system.

It's a single `WindowsAppBackup.exe` (no install, no .NET needed) for Windows 10 and 11.

## Download

Every push to `main` builds the exe. Open the **Actions** tab → latest **Build** run → **Artifacts** → `WindowsAppBackup`. Tagging a version (`git tag v1.0.0 && git push --tags`) also attaches the exe to a GitHub Release.

## Back up

1. Run `WindowsAppBackup.exe` and press **Scan this PC**.
2. Every installed app is listed with how it can be reinstalled and what settings were found. Runtimes and drivers start unticked.
3. Click an app to check or change the details on the right:
   - **winget id** – reinstalls the latest version from the internet. Filled in automatically for apps winget knows.
   - **Installer file** – copied into the backup. Found automatically for MSI-installed apps (Windows keeps the original `.msi`) and for matching installers in your Downloads folder. Use **Browse…** for anything else.
   - **Settings & data folders / Registry settings** – what gets copied. Add or remove folders, files and `HKCU` keys.
4. Pick where to save (a USB or second drive is suggested, since a Windows reinstall wipes C:) and press **Back up**.

Close the apps you're backing up first so their files aren't locked. Running the backup again into the same folder only copies files that changed.

Tick **Also save installers for winget apps** to store installers for everything, so restore works without internet (uses `winget download`).

## Restore

1. On the new PC, run the `WindowsAppBackup.exe` that the backup copied into its own folder. The backup opens on the **Restore** tab automatically. (Or run any copy and **Browse…** to the backup folder.)
2. Untick anything you don't want. **Install** reinstalls the app; **Settings** copies its data back.
3. Press **Restore**. Each app is installed (winget first, falling back to the saved installer), then its settings are copied back. Existing settings are kept as `*.pre-restore-<date>` copies unless you untick that option.

Settings under Program Files or ProgramData need **Restart as administrator**.

## What's in a backup folder

```
manifest.json          list of apps, how to install each, where their data goes
installers\<app>\      collected installer files
data\<app>\            copied settings folders and files
registry\<app>\        exported .reg files
winget-packages.json   for "winget import -i winget-packages.json" without this app
WindowsAppBackup.exe   copy of the app, for restoring
README.txt
```

Paths are stored with tokens like `%APPDATA%\Notepad++`, so a backup from `C:\Users\Matt` restores fine to a different username or a Documents folder in OneDrive.

## Limits worth knowing

- **Browser passwords** (Chrome, Edge, Brave) are encrypted to your Windows account and won't open on a new install. Use the browser's sync. Bookmarks, extensions and history do come across.
- **Licences/activation** for paid software usually aren't in the settings folder; you may need to sign in or re-enter a key.
- Data folders are found by matching app names to folders in AppData, ProgramData, Documents and HKCU\Software, plus a built-in list of common apps (`src/AppBackup.Core/KnownApps.json`). Check what was found for apps you care about. Folders over 5 GB found by guessing start unticked.
- Caches, logs and temp files are skipped by default.
- Things like `.ssh` keys aren't picked up automatically; add them with **Add folder…** if you want them.

## Building locally

Needs the .NET 8 SDK on Windows:

```
dotnet publish src/AppBackup.App/AppBackup.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

`src/AppBackup.Core` holds the scanning/backup/restore logic; `src/AppBackup.App` is the WPF window. Add apps to `KnownApps.json` to teach it where their settings live.
