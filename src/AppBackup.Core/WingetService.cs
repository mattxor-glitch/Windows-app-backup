using System.Text.Json;

namespace AppBackup.Core;

public sealed record WingetPackage(string Id, string? Name, string? Version, string Source);

/// <summary>Talks to winget (the Windows Package Manager), which ships with Windows 10/11.</summary>
public static class WingetService
{
    private const string Common = "--accept-source-agreements --disable-interactivity";

    // winget "success-ish" exit codes.
    private const int AlreadyInstalled = unchecked((int)0x8A150061);
    private const int NoApplicableUpgrade = unchecked((int)0x8A15002B);
    private const int RebootRequiredToFinish = unchecked((int)0x8A150109);
    private const int RebootInitiated = unchecked((int)0x8A15010A);

    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            var r = await ProcessRunner.RunAsync("winget", "--version");
            return r.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Installed packages winget knows how to reinstall. Combines `winget export` (reliable ids) with
    /// `winget list` (names, so we can match them to the installed-programs list).
    /// </summary>
    public static async Task<List<WingetPackage>> GetInstalledAsync(Action<string>? log, CancellationToken ct)
    {
        var byId = new Dictionary<string, WingetPackage>(StringComparer.OrdinalIgnoreCase);

        var tmp = Path.Combine(Path.GetTempPath(), $"appbackup-winget-{Guid.NewGuid():N}.json");
        try
        {
            log?.Invoke("Asking winget which installed apps it can reinstall…");
            await ProcessRunner.RunAsync("winget", $"export -o \"{tmp}\" --include-versions {Common}", ct);
            if (File.Exists(tmp))
            {
                foreach (var p in ParseExport(await File.ReadAllTextAsync(tmp, ct)))
                    byId[p.Id] = p;
            }
        }
        finally { try { File.Delete(tmp); } catch { } }

        try
        {
            var list = await ProcessRunner.RunAsync("winget", $"list {Common}", ct);
            foreach (var p in ParseList(list.Output))
            {
                if (byId.TryGetValue(p.Id, out var existing))
                    byId[p.Id] = existing with { Name = p.Name, Version = existing.Version ?? p.Version };
                else if (p.Source is "winget" or "msstore")
                    byId[p.Id] = p;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log?.Invoke("winget list failed: " + ex.Message); }

        log?.Invoke($"winget can reinstall {byId.Count} of your apps.");
        return byId.Values.ToList();
    }

    public static List<WingetPackage> ParseExport(string json)
    {
        var result = new List<WingetPackage>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Sources", out var sources)) return result;
        foreach (var src in sources.EnumerateArray())
        {
            var sourceName = src.TryGetProperty("SourceDetails", out var d) && d.TryGetProperty("Name", out var n)
                ? n.GetString() ?? "winget" : "winget";
            if (!src.TryGetProperty("Packages", out var pkgs)) continue;
            foreach (var p in pkgs.EnumerateArray())
            {
                var id = p.TryGetProperty("PackageIdentifier", out var i) ? i.GetString() : null;
                var ver = p.TryGetProperty("Version", out var v) ? v.GetString() : null;
                if (!string.IsNullOrEmpty(id)) result.Add(new WingetPackage(id, null, ver, sourceName));
            }
        }
        return result;
    }

    /// <summary>Parses the table `winget list` prints (column positions come from the header line).</summary>
    public static List<WingetPackage> ParseList(string output)
    {
        var result = new List<WingetPackage>();
        var lines = output.Split('\n').Select(ProcessRunner.CleanConsoleLine).ToList();
        var sep = lines.FindIndex(l => l.Length > 20 && l.Trim().All(c => c == '-'));
        if (sep < 1) return result;
        var header = lines[sep - 1];

        // Column headers are localized; Name/Id/Version are always the first three columns.
        var cols = new List<int>();
        for (var i = 0; i < header.Length; i++)
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' ')) cols.Add(i);
        if (cols.Count < 3) return result;
        int idCol = cols[1], verCol = cols[2];
        var sourceCol = cols.Count >= 5 ? cols[^1] : -1;

        for (var li = sep + 1; li < lines.Count; li++)
        {
            var line = lines[li];
            if (line.Length <= verCol) continue;
            string Cut(int start, int end) =>
                start >= line.Length ? "" : line[start..Math.Min(end, line.Length)].Trim();

            var name = Cut(0, idCol).TrimEnd('…').Trim();
            var id = Cut(idCol, verCol);
            // Ids never contain spaces; if the name overflowed into the Id column, re-split.
            if (id.Contains(' ')) id = id.Split(' ')[^1];
            var ver = Cut(verCol, cols.Count > 3 ? cols[3] : line.Length).Split(' ')[0];
            var source = sourceCol > 0 ? Cut(sourceCol, line.Length) : "";
            if (string.IsNullOrEmpty(id) || id.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(new WingetPackage(id, name, ver, source));
        }
        return result;
    }

    /// <summary>Attaches winget ids to the scanned apps; returns winget packages that matched nothing (e.g. Store apps).</summary>
    public static List<WingetPackage> Match(List<AppEntry> apps, List<WingetPackage> packages)
    {
        var unmatched = new List<WingetPackage>();
        foreach (var pkg in packages)
        {
            var app = FindMatch(apps, pkg);
            if (app != null && app.WingetId == null)
            {
                app.WingetId = pkg.Id;
                app.WingetSource = pkg.Source;
            }
            else if (app == null) unmatched.Add(pkg);
        }
        return unmatched;
    }

    private static AppEntry? FindMatch(List<AppEntry> apps, WingetPackage pkg)
    {
        if (!string.IsNullOrEmpty(pkg.Name))
        {
            var n = AppScanner.Norm(pkg.Name);
            var exact = apps.FirstOrDefault(a => a.WingetId == null && AppScanner.Norm(a.Name) == n);
            if (exact != null) return exact;
            // winget truncates long names with "…".
            if (n.Length >= 8)
            {
                var prefix = apps.FirstOrDefault(a => a.WingetId == null && AppScanner.Norm(a.Name).StartsWith(n));
                if (prefix != null) return prefix;
            }
        }
        // Fall back to the id: "Notepad++.Notepad++" → "notepad++", "VideoLAN.VLC" → "vlc".
        var idWords = pkg.Id.Split('.', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                         .SelectMany(p => AppScanner.Keywords(p)).ToList();
        if (idWords.Count == 0) return null;
        return apps.FirstOrDefault(a =>
        {
            if (a.WingetId != null) return false;
            var words = AppScanner.Keywords(a.Name);
            return words.Count > 0 && idWords.All(w => words.Contains(w)) && words.Count <= idWords.Count + 2;
        });
    }

    public static async Task<(bool Ok, string Message)> InstallAsync(AppEntry app, bool silent, Action<string>? log, CancellationToken ct)
    {
        var src = string.IsNullOrEmpty(app.WingetSource) ? "" : $" --source {app.WingetSource}";
        var mode = silent ? " --silent" : " --interactive";
        var args = $"install --id \"{app.WingetId}\" --exact{src}{mode} --accept-package-agreements {Common}";
        var r = await ProcessRunner.RunAsync("winget", args, ct, log);
        return r.ExitCode switch
        {
            0 => (true, "Installed with winget"),
            AlreadyInstalled or NoApplicableUpgrade => (true, "Already installed"),
            RebootRequiredToFinish or RebootInitiated => (true, "Installed (restart needed)"),
            _ => (false, $"winget failed (code 0x{r.ExitCode:X8})"),
        };
    }

    /// <summary>Saves the app's installer into a folder so it can be reinstalled offline.</summary>
    public static async Task<string?> DownloadAsync(string id, string folder, Action<string>? log, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var r = await ProcessRunner.RunAsync("winget",
            $"download --id \"{id}\" --exact --source winget -d \"{folder}\" --accept-package-agreements {Common}", ct, log);
        if (r.ExitCode != 0) return null;
        return Directory.EnumerateFiles(folder)
            .Where(f => !f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
    }
}
