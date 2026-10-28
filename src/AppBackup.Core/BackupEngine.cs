using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppBackup.Core;

public static class BackupEngine
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public const string ToolExeName = "WindowsAppBackup.exe";

    public static async Task<BackupManifest> RunAsync(string destination, IReadOnlyList<AppEntry> apps, BackupOptions options,
        IProgress<ScanProgress>? progress, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        var manifest = new BackupManifest();
        var selected = apps.Where(a => a.Include).ToList();
        log($"Backing up {selected.Count} apps to {destination}");

        var totalFailed = 0;
        for (var i = 0; i < selected.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var app = selected[i];
            progress?.Report(new((double)i / selected.Count, $"Backing up {app.Name} ({i + 1} of {selected.Count})"));
            log($"• {app.Name}");

            // Saved copy of the entry, with installer path made relative to the backup folder.
            var saved = JsonSerializer.Deserialize<AppEntry>(JsonSerializer.Serialize(app, Json), Json)!;
            saved.Data = saved.Data.Where(d => d.Include).ToList();
            saved.Registry = saved.Registry.Where(r => r.Include).ToList();
            saved.InstallerPath = null;

            if (string.IsNullOrEmpty(saved.BackupFolder)) saved.BackupFolder = PathTokens.SafeName(app.Name);
            var installerDir = Path.Combine(destination, "installers", saved.BackupFolder);

            // --- installer ---
            try
            {
                if (app.InstallerPath != null && File.Exists(app.InstallerPath))
                {
                    var src = new FileInfo(app.InstallerPath);
                    // Cached MSIs have random names like 3f2a1.msi; give them a readable one.
                    var fileName = src.DirectoryName?.EndsWith(@"\Windows\Installer", StringComparison.OrdinalIgnoreCase) == true
                        ? PathTokens.SafeName(app.Name) + src.Extension
                        : src.Name;
                    var stats = new CopyStats();
                    await Task.Run(() => FileCopier.CopyFile(src, Path.Combine(installerDir, fileName), stats, log), ct);
                    if (stats.Failed == 0)
                    {
                        saved.InstallerPath = Path.Combine("installers", saved.BackupFolder, fileName);
                        log($"  installer: {fileName} ({Format.Bytes(src.Length)})");
                    }
                }
                else if (app.InstallerPath != null && File.Exists(Path.Combine(destination, app.InstallerPath)))
                {
                    saved.InstallerPath = app.InstallerPath; // already in this backup from a previous run
                }
                else if (app.WingetId != null && options.DownloadWingetInstallers &&
                         (app.WingetSource ?? "winget").Equals("winget", StringComparison.OrdinalIgnoreCase))
                {
                    log($"  downloading installer with winget ({app.WingetId})…");
                    var file = await WingetService.DownloadAsync(app.WingetId, installerDir, null, ct);
                    if (file != null)
                    {
                        saved.InstallerPath = Path.GetRelativePath(destination, file);
                        log($"  installer: {Path.GetFileName(file)} ({Format.Bytes(new FileInfo(file).Length)})");
                    }
                    else log("  ! winget couldn't download an installer (restore will install online)");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log($"  ! installer not saved: {ex.Message}"); }

            // --- data ---
            foreach (var d in saved.Data)
            {
                var src = d.ExpandedPath;
                var dst = Path.Combine(destination, "data", saved.BackupFolder, d.BackupName);
                var stats = new CopyStats();
                await Task.Run(() =>
                {
                    if (d.Kind == DataKind.File)
                    {
                        if (File.Exists(src)) FileCopier.CopyFile(new FileInfo(src), Path.Combine(dst, Path.GetFileName(src)), stats, log);
                    }
                    else if (Directory.Exists(src))
                    {
                        FileCopier.CopyTree(src, dst, options.SkipCaches, stats, log, ct);
                    }
                }, ct);
                totalFailed += stats.Failed;
                log($"  data: {d.Path} — {stats.Copied} copied, {stats.Unchanged} unchanged" +
                    (stats.Failed > 0 ? $", {stats.Failed} skipped" : ""));
            }

            // --- registry ---
            foreach (var r in saved.Registry.ToList())
            {
                var file = Path.Combine(destination, "registry", saved.BackupFolder, r.BackupFile);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var res = await ProcessRunner.RunAsync("reg", $"export \"{r.Key}\" \"{file}\" /y", ct);
                if (res.ExitCode == 0) log($"  registry: {r.Key}");
                else
                {
                    log($"  ! couldn't export {r.Key}");
                    saved.Registry.Remove(r);
                }
            }

            manifest.Apps.Add(saved);
            await SaveManifestAsync(destination, manifest); // save as we go so a cancelled backup is still usable
        }

        await WriteExtrasAsync(destination, manifest);
        progress?.Report(new(1, "Backup finished."));
        log(totalFailed == 0
            ? $"Done. Backup saved to {destination}"
            : $"Done, but {totalFailed} files were skipped (usually because an app was open). Close those apps and run the backup again — only changed files are copied.");
        return manifest;
    }

    public static async Task SaveManifestAsync(string destination, BackupManifest manifest)
    {
        var path = Path.Combine(destination, BackupManifest.FileName);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(manifest, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public static BackupManifest LoadManifest(string folder)
    {
        var path = Path.Combine(folder, BackupManifest.FileName);
        return JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path), Json)
               ?? throw new InvalidDataException("manifest.json is empty");
    }

    /// <summary>A winget import file and a copy of this tool, so the backup drive is self-contained.</summary>
    private static async Task WriteExtrasAsync(string destination, BackupManifest manifest)
    {
        var sources = manifest.Apps.Where(a => a.WingetId != null)
            .GroupBy(a => a.WingetSource ?? "winget")
            .Select(g => new
            {
                Packages = g.Select(a => new { PackageIdentifier = a.WingetId }).ToList(),
                SourceDetails = new
                {
                    Argument = g.Key == "msstore" ? "https://storeedgefd.dsx.mp.microsoft.com/v9.0" : "https://cdn.winget.microsoft.com/cache",
                    Identifier = g.Key == "msstore" ? "StoreEdgeFD" : "Microsoft.Winget.Source_8wekyb3d8bbwe",
                    Name = g.Key,
                    Type = g.Key == "msstore" ? "Microsoft.Rest" : "Microsoft.PreIndexed.Package",
                },
            }).ToList();
        var import = new
        {
            schema = "https://aka.ms/winget-packages.schema.2.0.json",
            CreationDate = DateTime.UtcNow.ToString("o"),
            Sources = sources,
            WinGetVersion = "1.0",
        };
        await File.WriteAllTextAsync(Path.Combine(destination, "winget-packages.json"),
            JsonSerializer.Serialize(import, new JsonSerializerOptions { WriteIndented = true }));

        var readme = $"""
            Windows App Backup — created {manifest.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} on {manifest.ComputerName}

            To restore on a new PC (or after reinstalling Windows):
              1. Run {ToolExeName} from this folder.
              2. Open the Restore tab, pick this folder, tick the apps you want and press Restore.

            Without the app: "winget import -i winget-packages.json" reinstalls the winget apps,
            installers\ has the installer files that were collected, data\ has each app's settings
            and registry\ has .reg files you can double-click to import.
            """;
        await File.WriteAllTextAsync(Path.Combine(destination, "README.txt"), readme);

        try
        {
            var self = Environment.ProcessPath;
            if (self != null && File.Exists(self) &&
                !Path.GetDirectoryName(self)!.Equals(destination.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileNameWithoutExtension(self).Contains("WindowsAppBackup", StringComparison.OrdinalIgnoreCase))
                File.Copy(self, Path.Combine(destination, ToolExeName), overwrite: true);
        }
        catch { /* not essential */ }
    }
}
