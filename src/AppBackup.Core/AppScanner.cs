using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AppBackup.Core;

/// <summary>Reads the installed-programs list (the same data "Apps &amp; features" shows).</summary>
public static class AppScanner
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<AppEntry> ScanInstalled()
    {
        var found = new List<AppEntry>();
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, found);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, found);
        Read(RegistryHive.CurrentUser, RegistryView.Default, found);

        // The same app can be listed in several hives; keep the first of each name+version.
        return found
            .GroupBy(a => (Norm(a.Name), a.Version ?? ""))
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Read(RegistryHive hive, RegistryView view, List<AppEntry> into)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallPath);
            if (uninstall == null) return;

            foreach (var subName in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var k = uninstall.OpenSubKey(subName);
                    if (k == null) continue;
                    var app = FromKey(k, subName, hive);
                    if (app != null) into.Add(app);
                }
                catch { /* unreadable key — skip */ }
            }
        }
        catch { /* hive/view not available */ }
    }

    private static readonly Regex UpdateName = new(@"\b(KB\d{6,}|Security Update|Hotfix|Update for)\b", RegexOptions.IgnoreCase);

    private static AppEntry? FromKey(RegistryKey k, string subName, RegistryHive hive)
    {
        var name = (k.GetValue("DisplayName") as string)?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (Convert.ToInt32(k.GetValue("SystemComponent") ?? 0) == 1) return null;
        if (k.GetValue("ParentKeyName") != null) return null;               // patches of another product
        var releaseType = k.GetValue("ReleaseType") as string ?? "";
        if (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
            releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)) return null;
        if (UpdateName.IsMatch(name)) return null;

        var app = new AppEntry
        {
            Name = name,
            Version = (k.GetValue("DisplayVersion") as string)?.Trim(),
            Publisher = (k.GetValue("Publisher") as string)?.Trim(),
            InstallLocation = CleanPath(k.GetValue("InstallLocation") as string),
            HelpUrl = (k.GetValue("URLInfoAbout") as string ?? k.GetValue("HelpLink") as string)?.Trim(),
            UninstallKey = $"{(hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM")}\\{UninstallPath}\\{subName}",
        };

        if (Convert.ToInt32(k.GetValue("WindowsInstaller") ?? 0) == 1 && Guid.TryParse(subName, out _))
            app.MsiProductCode = subName;

        if (string.IsNullOrEmpty(app.InstallLocation))
        {
            // Fall back to the folder of the app's icon or uninstaller.
            var icon = CleanPath((k.GetValue("DisplayIcon") as string)?.Split(',')[0]);
            if (icon != null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !icon.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase))
                app.InstallLocation = System.IO.Path.GetDirectoryName(icon);
        }
        return app;
    }

    private static string? CleanPath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        p = p.Trim().Trim('"').TrimEnd('\\');
        return p.Length == 0 ? null : Environment.ExpandEnvironmentVariables(p);
    }

    /// <summary>
    /// For MSI-installed apps Windows keeps a copy of the original .msi in C:\Windows\Installer.
    /// Returns its path, or null.
    /// </summary>
    public static string? FindCachedMsi(string productCode)
    {
        var packed = PackGuid(productCode);
        if (packed == null) return null;
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var userData = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData");
            if (userData == null) return null;
            foreach (var sid in userData.GetSubKeyNames())
            {
                using var props = userData.OpenSubKey($@"{sid}\Products\{packed}\InstallProperties");
                var local = props?.GetValue("LocalPackage") as string;
                if (!string.IsNullOrEmpty(local) && File.Exists(local)) return local;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Converts {12345678-ABCD-...} to the "packed" form the Windows Installer registry uses.</summary>
    public static string? PackGuid(string guid)
    {
        if (!Guid.TryParse(guid, out var g)) return null;
        var s = g.ToString("N").ToUpperInvariant(); // 32 hex chars
        static string Rev(string x) => new(x.Reverse().ToArray());
        var sb = new System.Text.StringBuilder();
        sb.Append(Rev(s[..8])).Append(Rev(s[8..12])).Append(Rev(s[12..16]));
        for (var i = 16; i < 32; i += 2) sb.Append(s[i + 1]).Append(s[i]);
        return sb.ToString();
    }

    /// <summary>Lower-cases and strips version numbers, bitness and punctuation for fuzzy matching.</summary>
    public static string Norm(string s)
    {
        s = s.ToLowerInvariant();
        s = Regex.Replace(s, @"\(.*?\)", " ");
        s = Regex.Replace(s, @"\b(x64|x86|64-bit|32-bit|amd64|arm64|version|v?\d+(\.\d+)+)\b", " ");
        s = Regex.Replace(s, @"[^a-z0-9+]+", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "inc", "llc", "ltd", "corp", "corporation", "co", "gmbh", "software", "technologies",
        "app", "application", "desktop", "client", "windows", "win", "edition", "free", "pro", "professional",
        "update", "setup", "installer", "tools", "tool", "suite", "studio", "x64", "x86", "en", "us", "user", "machine",
    };

    /// <summary>Meaningful words in a name, for matching folders/installers/winget ids.</summary>
    public static List<string> Keywords(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? new List<string>()
            : Norm(s).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                     .Where(w => w.Length >= 3 && !StopWords.Contains(w))
                     .Distinct()
                     .ToList();
}
