using Microsoft.Win32;

namespace AppBackup.Core;

/// <summary>Looks for an installer file for an app that's already on this PC.</summary>
public static class InstallerLocator
{
    private static readonly string[] InstallerExt = { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle" };

    public static string? Find(AppEntry app)
    {
        // 1. MSI-installed apps: Windows keeps the original .msi.
        if (app.MsiProductCode != null)
        {
            var msi = AppScanner.FindCachedMsi(app.MsiProductCode);
            if (msi != null) return msi;
        }

        // 2. Something with a matching name in Downloads.
        var downloads = DownloadsFolder();
        if (downloads == null) return null;
        var names = DataLocator.CandidateNames(app).Where(n => n.Length >= 4).ToList();
        if (names.Count == 0) return null;

        return SafeFiles(downloads)
            .Where(f => InstallerExt.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f =>
            {
                var c = AppScanner.Norm(Path.GetFileNameWithoutExtension(f)).Replace(" ", "");
                // version numbers are stripped by Norm, so "Firefox Setup 131.0.exe" → "firefoxsetup"
                return names.Any(n => c.Contains(n));
            })
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();
    }

    public static string? DownloadsFolder()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (k?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string p)
            {
                p = Environment.ExpandEnvironmentVariables(p);
                if (Directory.Exists(p)) return p;
            }
        }
        catch { }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(fallback) ? fallback : null;
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        var result = new List<string>();
        try
        {
            result.AddRange(Directory.GetFiles(dir));
            foreach (var sub in Directory.GetDirectories(dir))
                try { result.AddRange(Directory.GetFiles(sub)); } catch { }
        }
        catch { }
        return result;
    }
}
