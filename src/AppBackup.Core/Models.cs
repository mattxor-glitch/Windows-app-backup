using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AppBackup.Core;

/// <summary>Small base class so the UI can bind straight to the model objects.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum DataKind { Folder, File }

/// <summary>A folder or file that holds an app's settings/data.</summary>
public sealed class DataLocation : Observable
{
    private bool _include = true;

    /// <summary>Path with environment tokens, e.g. %APPDATA%\Notepad++ — portable between PCs and usernames.</summary>
    public string Path { get; set; } = "";
    public DataKind Kind { get; set; } = DataKind.Folder;

    /// <summary>Where the guess came from: "known", "scan" or "manual".</summary>
    public string Source { get; set; } = "scan";

    public bool Include { get => _include; set => Set(ref _include, value); }

    /// <summary>Bytes found at scan time (informational).</summary>
    public long SizeBytes { get; set; }

    /// <summary>Folder name inside the backup's data\&lt;app&gt;\ directory.</summary>
    public string BackupName { get; set; } = "";

    [JsonIgnore] public string ExpandedPath => PathTokens.Expand(Path);
    [JsonIgnore] public string SizeText => Format.Bytes(SizeBytes);
}

public sealed class RegistryLocation : Observable
{
    private bool _include = true;

    /// <summary>Full key, e.g. HKCU\Software\SimonTatham. Only HKCU keys are portable.</summary>
    public string Key { get; set; } = "";
    public string Source { get; set; } = "scan";
    public bool Include { get => _include; set => Set(ref _include, value); }

    /// <summary>File name of the .reg export inside the backup.</summary>
    public string BackupFile { get; set; } = "";
}

/// <summary>One application: what it is, how to reinstall it, and where its data lives.</summary>
public sealed class AppEntry : Observable
{
    private bool _include;
    private string? _wingetId;
    private string? _installerPath;
    private string? _installerArgs;
    private bool _restoreInstall = true;
    private bool _restoreData = true;
    private string _status = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public string? InstallLocation { get; set; }
    public string? UninstallKey { get; set; }
    public string? MsiProductCode { get; set; }
    public string? HelpUrl { get; set; }
    public bool IsSystemComponent { get; set; }

    /// <summary>Advice shown to the user, e.g. that browser passwords need sync.</summary>
    public string? Note { get; set; }

    public bool Include { get => _include; set => Set(ref _include, value); }

    /// <summary>winget package id, e.g. Mozilla.Firefox. Preferred way to reinstall.</summary>
    public string? WingetId
    {
        get => _wingetId;
        set { if (Set(ref _wingetId, string.IsNullOrWhiteSpace(value) ? null : value.Trim())) OnPropertyChanged(nameof(InstallMethod)); }
    }

    public string? WingetSource { get; set; }

    /// <summary>
    /// During scanning: absolute path to an installer file on this PC.
    /// In a saved manifest: path relative to the backup folder (installers\...).
    /// </summary>
    public string? InstallerPath
    {
        get => _installerPath;
        set { if (Set(ref _installerPath, string.IsNullOrWhiteSpace(value) ? null : value.Trim())) OnPropertyChanged(nameof(InstallMethod)); }
    }

    /// <summary>Optional command-line arguments for the installer (e.g. /S for a silent NSIS install).</summary>
    public string? InstallerArgs { get => _installerArgs; set => Set(ref _installerArgs, value); }

    /// <summary>Folder name used for this app under installers\ and data\ in the backup.</summary>
    public string BackupFolder { get; set; } = "";

    public List<DataLocation> Data { get; set; } = new();
    public List<RegistryLocation> Registry { get; set; } = new();

    // Restore-side choices (not meaningful in the manifest itself).
    [JsonIgnore] public bool RestoreInstall { get => _restoreInstall; set => Set(ref _restoreInstall, value); }
    [JsonIgnore] public bool RestoreData { get => _restoreData; set => Set(ref _restoreData, value); }
    [JsonIgnore] public string Status { get => _status; set => Set(ref _status, value); }

    [JsonIgnore]
    public string InstallMethod =>
        WingetId != null && InstallerPath != null ? "winget + installer"
        : WingetId != null ? "winget"
        : InstallerPath != null ? "installer file"
        : "manual";

    [JsonIgnore] public bool HasData => Data.Count > 0 || Registry.Count > 0;

    [JsonIgnore]
    public string DataSummary
    {
        get
        {
            var folders = Data.Count(d => d.Include);
            var keys = Registry.Count(r => r.Include);
            if (folders == 0 && keys == 0) return "—";
            var size = Data.Where(d => d.Include).Sum(d => d.SizeBytes);
            var parts = new List<string>();
            if (folders > 0) parts.Add($"{folders} location{(folders == 1 ? "" : "s")} ({Format.Bytes(size)})");
            if (keys > 0) parts.Add($"{keys} reg key{(keys == 1 ? "" : "s")}");
            return string.Join(", ", parts);
        }
    }

    public void RefreshSummary()
    {
        OnPropertyChanged(nameof(DataSummary));
        OnPropertyChanged(nameof(HasData));
        OnPropertyChanged(nameof(InstallMethod));
    }
}

public sealed class BackupManifest
{
    public int FormatVersion { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string ComputerName { get; set; } = Environment.MachineName;
    public string UserName { get; set; } = Environment.UserName;
    public string OsVersion { get; set; } = Environment.OSVersion.VersionString;
    public string ToolVersion { get; set; } = typeof(BackupManifest).Assembly.GetName().Version?.ToString() ?? "";
    public List<AppEntry> Apps { get; set; } = new();

    public const string FileName = "manifest.json";
}

public sealed class BackupOptions
{
    /// <summary>Use `winget download` to store installers for winget apps so restore works offline.</summary>
    public bool DownloadWingetInstallers { get; set; }

    /// <summary>Skip cache/log/temp folders inside app data.</summary>
    public bool SkipCaches { get; set; } = true;

    /// <summary>Skip a single data location larger than this (0 = no limit).</summary>
    public long MaxLocationBytes { get; set; } = 5L * 1024 * 1024 * 1024;
}

public sealed class RestoreOptions
{
    /// <summary>Install winget apps silently (no installer windows).</summary>
    public bool SilentWinget { get; set; } = true;

    /// <summary>Prefer the stored installer file over winget when both exist (offline restore).</summary>
    public bool PreferStoredInstaller { get; set; }

    /// <summary>If the target folder already has data, move it aside to *.pre-restore-&lt;date&gt; before copying.</summary>
    public bool KeepExistingAsBackup { get; set; } = true;
}

public static class Format
{
    public static string Bytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b;
        var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.#} {u[i]}";
    }
}
