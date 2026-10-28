using System.Text.RegularExpressions;

namespace AppBackup.Core;

public sealed record ScanProgress(double Fraction, string Text);

/// <summary>Builds the list of apps shown on the Back up tab.</summary>
public static class Scanner
{
    /// <summary>Runtimes, drivers and Windows parts: apps pull these in themselves, so they start unticked.</summary>
    private static readonly Regex NotUsuallyWanted = new(
        @"Visual C\+\+|\.NET (Runtime|Desktop Runtime|Framework|SDK|Host)|ASP\.NET|Windows (Software Development Kit|SDK|Driver)|" +
        @"WebView2|Update Health Tools|Windows PC Health Check|Microsoft Edge Update|Driver|Chipset|Redistributable|" +
        @"Runtime$|Bonjour|Teams Machine-Wide Installer|Microsoft Update Health|vs_|Windows App Runtime|Intel\(R\)|NVIDIA|Realtek|AMD Software",
        RegexOptions.IgnoreCase);

    public static async Task<List<AppEntry>> ScanAsync(BackupOptions options, IProgress<ScanProgress>? progress,
        Action<string>? log, CancellationToken ct)
    {
        progress?.Report(new(0, "Reading installed programs…"));
        var apps = await Task.Run(AppScanner.ScanInstalled, ct);
        log?.Invoke($"Found {apps.Count} installed programs.");

        if (await WingetService.IsAvailableAsync())
        {
            progress?.Report(new(0.1, "Checking winget…"));
            try
            {
                var pkgs = await WingetService.GetInstalledAsync(log, ct);
                var unmatched = WingetService.Match(apps, pkgs);
                foreach (var p in unmatched)
                {
                    apps.Add(new AppEntry
                    {
                        Name = string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name!,
                        Version = p.Version,
                        WingetId = p.Id,
                        WingetSource = p.Source,
                    });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.Invoke("winget check failed: " + ex.Message); }
        }
        else
        {
            log?.Invoke("winget isn't available on this PC — apps will need their installer files. " +
                        "Install \"App Installer\" from the Microsoft Store to enable winget.");
        }

        apps = apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < apps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var app = apps[i];
            progress?.Report(new(0.3 + 0.7 * i / Math.Max(1, apps.Count), $"Looking for data: {app.Name}"));

            app.BackupFolder = UniqueFolder(app.Name, used);
            await Task.Run(() =>
            {
                DataLocator.Locate(app, options);
                if (app.WingetId == null) app.InstallerPath = InstallerLocator.Find(app);
            }, ct);
            app.Include = !NotUsuallyWanted.IsMatch(app.Name);
            app.RefreshSummary();
        }

        progress?.Report(new(1, $"{apps.Count} apps found."));
        return apps;
    }

    public static string UniqueFolder(string name, HashSet<string> used)
    {
        var b = PathTokens.SafeName(name);
        var n = b;
        for (var i = 2; !used.Add(n); i++) n = $"{b}_{i}";
        return n;
    }
}
