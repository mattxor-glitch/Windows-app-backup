using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AppBackup.Core;
using Microsoft.Win32;

namespace AppBackup.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<AppEntry> _apps = new();
    private readonly ObservableCollection<AppEntry> _restoreApps = new();
    private ICollectionView? _view;
    private CancellationTokenSource? _cts;
    private string? _loadedBackupFolder;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();

        AppsGrid.ItemsSource = _apps;
        _view = CollectionViewSource.GetDefaultView(_apps);
        _view.Filter = o => o is AppEntry a && Matches(a, SearchBox.Text);
        RestoreGrid.ItemsSource = _restoreApps;

        if (IsAdmin())
        {
            Title += " (Administrator)";
            ElevateButton.Visibility = Visibility.Collapsed;
        }

        DestBox.Text = SuggestDestination();
        DestBox.TextChanged += (_, _) => UpdateDestWarning();
        UpdateDestWarning();

        Loaded += (_, _) => OpenBackupNextToExe();
        Closing += OnClosing;
    }

    // ---------------------------------------------------------------- helpers

    private void Log(string line)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Log(line)); return; }
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool Matches(AppEntry a, string q) =>
        string.IsNullOrWhiteSpace(q) ||
        a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        (a.Publisher?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (a.WingetId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ScanButton.IsEnabled = !busy;
        BackupButton.IsEnabled = !busy && _apps.Count > 0;
        RestoreButton.IsEnabled = !busy && _restoreApps.Count > 0;
        CancelBackupButton.IsEnabled = busy;
        CancelRestoreButton.IsEnabled = busy;
        AppsGrid.IsEnabled = !busy;
        RestoreGrid.IsEnabled = !busy;
    }

    private void UpdateCount()
    {
        var ticked = _apps.Count(a => a.Include);
        CountText.Text = _apps.Count == 0 ? "" : $"{ticked} of {_apps.Count} apps ticked";
        EmptyHint.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string SuggestDestination()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var folder = Path.Combine("WindowsAppBackup", $"{Environment.MachineName}");
        try
        {
            // Prefer a USB drive, then any other disk, so the backup survives a Windows reinstall.
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady &&
                !d.RootDirectory.FullName.Equals(system, StringComparison.OrdinalIgnoreCase)).ToList();
            var pick = drives.FirstOrDefault(d => d.DriveType == DriveType.Removable)
                       ?? drives.FirstOrDefault(d => d.DriveType == DriveType.Fixed)
                       ?? drives.FirstOrDefault(d => d.DriveType == DriveType.Network);
            if (pick != null) return Path.Combine(pick.RootDirectory.FullName, folder);
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), folder);
    }

    private void UpdateDestWarning()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var root = "";
        try { root = Path.GetPathRoot(DestBox.Text.Trim()) ?? ""; } catch { }
        DestWarning.Visibility = root.Equals(system, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private BackupOptions CurrentBackupOptions() => new()
    {
        SkipCaches = SkipCachesBox.IsChecked == true,
        DownloadWingetInstallers = DownloadWingetBox.IsChecked == true,
    };

    private AppEntry? Selected => AppsGrid.SelectedItem as AppEntry;

    private void RefreshDetails()
    {
        DataList.Items.Refresh();
        RegList.Items.Refresh();
        Selected?.RefreshSummary();
    }

    // ---------------------------------------------------------------- header

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { MessageBox.Show(this, "Wait for the current job to finish first."); return; }
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            if (_loadedBackupFolder != null) psi.Arguments = $"\"{_loadedBackupFolder}\"";
            Process.Start(psi);
            Application.Current.Shutdown();
        }
        catch (Win32Exception) { /* user said no to UAC */ }
    }

    // ---------------------------------------------------------------- back up tab

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        _cts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p =>
        {
            BackupProgress.Value = p.Fraction;
            BackupStatus.Text = p.Text;
        });
        try
        {
            var apps = await Scanner.ScanAsync(CurrentBackupOptions(), progress, Log, _cts.Token);
            _apps.Clear();
            foreach (var a in apps)
            {
                a.PropertyChanged += (_, ev) => { if (ev.PropertyName == nameof(AppEntry.Include)) UpdateCount(); };
                _apps.Add(a);
            }
            var winget = apps.Count(a => a.WingetId != null);
            var installers = apps.Count(a => a.WingetId == null && a.InstallerPath != null);
            var manual = apps.Count(a => a.InstallMethod == "manual");
            Log($"Scan done: {winget} apps reinstall with winget, {installers} have an installer file, {manual} need a manual install.");
            if (manual > 0)
                Log("Tip: for apps marked \"manual\", click the app and Browse to its installer, or type a winget id.");
        }
        catch (OperationCanceledException) { Log("Scan cancelled."); }
        finally
        {
            SetBusy(false);
            UpdateCount();
            BackupStatus.Text = "";
            BackupProgress.Value = 0;
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var a in _view!.Cast<AppEntry>()) a.Include = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var a in _view!.Cast<AppEntry>()) a.Include = false;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();

    private void AppsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DetailsPanel.Visibility = Selected == null ? Visibility.Collapsed : Visibility.Visible;
        NewRegKey.Text = "";
    }

    private void BrowseInstaller_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        var dlg = new OpenFileDialog
        {
            Title = $"Installer for {Selected.Name}",
            Filter = "Installers (*.exe;*.msi;*.msix;*.msixbundle;*.appx)|*.exe;*.msi;*.msix;*.msixbundle;*.appx;*.appxbundle|All files (*.*)|*.*",
            InitialDirectory = InstallerLocator.DownloadsFolder() ?? "",
        };
        if (dlg.ShowDialog(this) == true) Selected.InstallerPath = dlg.FileName;
    }

    private void ClearInstaller_Click(object sender, RoutedEventArgs e)
    {
        if (Selected != null) Selected.InstallerPath = null;
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        var dlg = new OpenFolderDialog
        {
            Title = $"Folder with settings or data for {Selected.Name}",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        };
        if (dlg.ShowDialog(this) != true) return;
        DataLocator.AddPath(Selected, dlg.FolderName, "manual", CurrentBackupOptions());
        RefreshDetails();
    }

    private void AddFile_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        var dlg = new OpenFileDialog
        {
            Title = $"Settings file for {Selected.Name}",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        };
        if (dlg.ShowDialog(this) != true) return;
        DataLocator.AddPath(Selected, dlg.FileName, "manual", CurrentBackupOptions());
        RefreshDetails();
    }

    private void RemoveData_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null || (sender as FrameworkElement)?.Tag is not DataLocation d) return;
        Selected.Data.Remove(d);
        RefreshDetails();
    }

    private void RemoveReg_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null || (sender as FrameworkElement)?.Tag is not RegistryLocation r) return;
        Selected.Registry.Remove(r);
        RefreshDetails();
    }

    private void AddReg_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        var key = NewRegKey.Text.Trim().TrimEnd('\\');
        if (key.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase)) key = "HKCU\\" + key[18..];
        if (key.StartsWith("Computer\\", StringComparison.OrdinalIgnoreCase)) key = key[9..];
        if (key.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase)) key = "HKCU\\" + key[18..];
        if (!key.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Only keys under HKCU (your user settings) can be backed up, e.g. HKCU\\Software\\MyApp.",
                "Registry key", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!DataLocator.RegistryKeyExists(key))
        {
            MessageBox.Show(this, $"{key} doesn't exist on this PC.", "Registry key", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DataLocator.AddRegistry(Selected, key, "manual");
        NewRegKey.Text = "";
        RefreshDetails();
    }

    private void DataToggle_Click(object sender, RoutedEventArgs e) => Selected?.RefreshSummary();

    private void BrowseDest_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Where should the backup be saved?" };
        if (dlg.ShowDialog(this) == true)
            DestBox.Text = Path.Combine(dlg.FolderName, Path.GetFileName(dlg.FolderName)
                .Equals("WindowsAppBackup", StringComparison.OrdinalIgnoreCase) ? Environment.MachineName : "");
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var dest = DestBox.Text.Trim();
        if (dest.Length == 0) { MessageBox.Show(this, "Choose where to save the backup."); return; }
        try { dest = Path.GetFullPath(dest); }
        catch { MessageBox.Show(this, "That isn't a valid folder path."); return; }

        var selected = _apps.Where(a => a.Include).ToList();
        if (selected.Count == 0) { MessageBox.Show(this, "Tick at least one app."); return; }

        if (DestWarning.Visibility == Visibility.Visible &&
            MessageBox.Show(this, "The backup folder is on the Windows drive, so it would be lost if you reinstall Windows.\n\nBack up there anyway?",
                "Windows App Backup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        Log("Tip: close the apps you're backing up so their files aren't locked.");
        SetBusy(true);
        _cts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p => { BackupProgress.Value = p.Fraction; BackupStatus.Text = p.Text; });
        try
        {
            await BackupEngine.RunAsync(dest, selected, CurrentBackupOptions(), progress, Log, _cts.Token);
            if (MessageBox.Show(this, $"Backup saved to\n{dest}\n\nOpen the folder?", "Backup finished",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dest}\"") { UseShellExecute = true });
        }
        catch (OperationCanceledException) { Log("Backup cancelled. What was copied so far is kept; run it again to finish."); }
        catch (Exception ex)
        {
            Log("Backup failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Backup failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            BackupStatus.Text = "";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Log("Cancelling…");
    }

    // ---------------------------------------------------------------- restore tab

    /// <summary>If the exe is run from a backup folder (it copies itself there), open that backup straight away.</summary>
    private void OpenBackupNextToExe()
    {
        var args = Environment.GetCommandLineArgs();
        var candidate = args.Length > 1 ? args[1] : Path.GetDirectoryName(Environment.ProcessPath);
        if (candidate != null && File.Exists(Path.Combine(candidate, BackupManifest.FileName)))
        {
            BackupFolderBox.Text = candidate;
            LoadBackup(candidate);
            Tabs.SelectedItem = RestoreTab;
        }
    }

    private void BrowseBackup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose the backup folder (the one with manifest.json)" };
        if (dlg.ShowDialog(this) != true) return;
        BackupFolderBox.Text = dlg.FolderName;
        LoadBackup(dlg.FolderName);
    }

    private void LoadBackup_Click(object sender, RoutedEventArgs e) => LoadBackup(BackupFolderBox.Text.Trim());

    private void LoadBackup(string folder)
    {
        try
        {
            // Accept the parent folder too, if it has exactly one backup inside.
            if (!File.Exists(Path.Combine(folder, BackupManifest.FileName)) && Directory.Exists(folder))
            {
                var inner = Directory.GetDirectories(folder)
                    .Where(d => File.Exists(Path.Combine(d, BackupManifest.FileName))).ToList();
                if (inner.Count == 1) { folder = inner[0]; BackupFolderBox.Text = folder; }
            }

            var manifest = BackupEngine.LoadManifest(folder);
            _restoreApps.Clear();
            foreach (var a in manifest.Apps)
            {
                a.Include = true;
                a.RestoreInstall = a.InstallMethod != "manual";
                a.RestoreData = a.HasData;
                a.Status = "";
                _restoreApps.Add(a);
            }
            _loadedBackupFolder = folder;
            BackupInfo.Text = $"Backup of {manifest.ComputerName} from {manifest.CreatedUtc.ToLocalTime():MMMM d, yyyy h:mm tt} — {manifest.Apps.Count} apps.";
            Log($"Opened backup {folder} ({manifest.Apps.Count} apps).");
            RestoreButton.IsEnabled = !_busy && _restoreApps.Count > 0;
        }
        catch (FileNotFoundException)
        {
            MessageBox.Show(this, "That folder doesn't have a manifest.json, so it isn't a backup made by this app.",
                "Not a backup folder", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't read the backup: " + ex.Message, "Restore", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var a in _restoreApps) a.Include = true;
    }

    private void RestoreNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var a in _restoreApps) a.Include = false;
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedBackupFolder == null) return;
        var selected = _restoreApps.Where(a => a.Include && (a.RestoreInstall || a.RestoreData)).ToList();
        if (selected.Count == 0) { MessageBox.Show(this, "Tick at least one app."); return; }

        var needsAdmin = selected.Any(a => a.RestoreData && a.Data.Any(d =>
            d.Path.StartsWith("%PROGRAMFILES", StringComparison.OrdinalIgnoreCase) ||
            d.Path.StartsWith("%PROGRAMDATA%", StringComparison.OrdinalIgnoreCase)));
        if (needsAdmin && !IsAdmin() &&
            MessageBox.Show(this, "Some settings go into Program Files or ProgramData, which needs administrator rights.\n\n" +
                    "Restart as administrator first? (Choose No to continue; those files may be skipped.)",
                "Restore", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            Elevate_Click(sender, e);
            return;
        }

        SetBusy(true);
        _cts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p => { RestoreProgress.Value = p.Fraction; RestoreStatus.Text = p.Text; });
        var options = new RestoreOptions
        {
            SilentWinget = SilentBox.IsChecked == true,
            PreferStoredInstaller = PreferStoredBox.IsChecked == true,
            KeepExistingAsBackup = KeepExistingBox.IsChecked == true,
        };
        try
        {
            await RestoreEngine.RunAsync(_loadedBackupFolder, selected, options, progress, Log, _cts.Token);
            MessageBox.Show(this, "Restore finished. Check the Status column and the log for anything that needs attention.",
                "Restore finished", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { Log("Restore cancelled."); }
        catch (Exception ex)
        {
            Log("Restore failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Restore failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            RestoreStatus.Text = "";
        }
    }

    // ---------------------------------------------------------------- log & closing

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { FileName = $"WindowsAppBackup-{DateTime.Now:yyyyMMdd-HHmm}.log", Filter = "Log (*.log)|*.log" };
        if (dlg.ShowDialog(this) == true) File.WriteAllText(dlg.FileName, LogBox.Text);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        if (MessageBox.Show(this, "A job is still running. Stop it and close?", "Windows App Backup",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            _cts?.Cancel();
        else
            e.Cancel = true;
    }
}
