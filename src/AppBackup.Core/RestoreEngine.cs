namespace AppBackup.Core;

public static class RestoreEngine
{
    public static async Task RunAsync(string backupFolder, IReadOnlyList<AppEntry> apps, RestoreOptions options,
        IProgress<ScanProgress>? progress, Action<string> log, CancellationToken ct)
    {
        var selected = apps.Where(a => a.Include && (a.RestoreInstall || a.RestoreData)).ToList();
        var wingetOk = selected.Any(a => a.RestoreInstall && a.WingetId != null) && await WingetService.IsAvailableAsync();
        if (selected.Any(a => a.RestoreInstall && a.WingetId != null) && !wingetOk)
            log("winget isn't available — install \"App Installer\" from the Microsoft Store, or apps will use stored installers only.");

        var manual = new List<AppEntry>();
        for (var i = 0; i < selected.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var app = selected[i];
            progress?.Report(new((double)i / selected.Count, $"Restoring {app.Name} ({i + 1} of {selected.Count})"));
            log($"• {app.Name}");

            var installed = true;
            if (app.RestoreInstall)
            {
                app.Status = "Installing…";
                var (ok, message) = await InstallAsync(backupFolder, app, options, wingetOk, log, ct);
                installed = ok;
                app.Status = message;
                log("  " + message);
                if (!ok && app.InstallMethod == "manual") manual.Add(app);
            }

            if (app.RestoreData && app.HasData)
            {
                if (!installed) log("  (restoring settings anyway, so they're in place when you install it)");
                var failed = await RestoreDataAsync(backupFolder, app, options, log, ct);
                app.Status = (app.RestoreInstall ? app.Status + "; " : "") +
                             (failed == 0 ? "settings restored" : $"settings restored ({failed} files skipped)");
            }
        }

        progress?.Report(new(1, "Restore finished."));
        if (manual.Count > 0)
        {
            log("These apps need to be installed by hand (no winget id or installer was saved):");
            foreach (var a in manual) log($"  - {a.Name}{(a.HelpUrl != null ? "  " + a.HelpUrl : "")}");
        }
        log("Done. Some apps may need a restart of Windows before they work properly.");
    }

    private static async Task<(bool Ok, string Message)> InstallAsync(string backupFolder, AppEntry app, RestoreOptions options,
        bool wingetOk, Action<string> log, CancellationToken ct)
    {
        var installer = app.InstallerPath != null ? Path.Combine(backupFolder, app.InstallerPath) : null;
        var hasInstaller = installer != null && File.Exists(installer);
        var useWinget = app.WingetId != null && wingetOk && !(options.PreferStoredInstaller && hasInstaller);

        if (useWinget)
        {
            var r = await WingetService.InstallAsync(app, options.SilentWinget, l => log("    " + l), ct);
            if (r.Ok || !hasInstaller) return r;
            log($"  {r.Message}; trying the saved installer instead");
        }

        if (hasInstaller)
        {
            try
            {
                var code = await RunInstallerFileAsync(installer!, app.InstallerArgs, ct);
                return code is 0 or 3010 or 1641
                    ? (true, code == 0 ? "Installed from saved installer" : "Installed (restart needed)")
                    : (false, $"Installer exited with code {code}");
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return (false, "Install cancelled (admin prompt declined)");
            }
        }

        return (false, app.HelpUrl != null ? $"Install manually: {app.HelpUrl}" : "Install manually (no installer saved)");
    }

    private static Task<int> RunInstallerFileAsync(string file, string? args, CancellationToken ct)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        switch (ext)
        {
            case ".msi":
                return ProcessRunner.RunInstallerAsync("msiexec.exe",
                    $"/i \"{file}\" {(string.IsNullOrWhiteSpace(args) ? "/passive /norestart" : args)}", ct);
            case ".msix":
            case ".msixbundle":
            case ".appx":
            case ".appxbundle":
                return ProcessRunner.RunAsync("powershell.exe",
                        $"-NoProfile -ExecutionPolicy Bypass -Command \"Add-AppxPackage -Path '{file.Replace("'", "''")}'\"", ct)
                    .ContinueWith(t => t.Result.ExitCode, ct);
            default:
                return ProcessRunner.RunInstallerAsync(file, args ?? "", ct);
        }
    }

    /// <summary>Copies settings back to where the app expects them. Returns the number of files that failed.</summary>
    public static async Task<int> RestoreDataAsync(string backupFolder, AppEntry app, RestoreOptions options,
        Action<string> log, CancellationToken ct)
    {
        var failed = 0;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        foreach (var d in app.Data)
        {
            var src = Path.Combine(backupFolder, "data", app.BackupFolder, d.BackupName);
            var target = d.ExpandedPath;
            if (!Directory.Exists(src))
            {
                log($"  ! nothing saved for {d.Path}");
                continue;
            }

            var stats = new CopyStats();
            await Task.Run(() =>
            {
                if (d.Kind == DataKind.File)
                {
                    var file = Directory.GetFiles(src).FirstOrDefault();
                    if (file == null) return;
                    if (options.KeepExistingAsBackup && File.Exists(target))
                        TryCopy(target, $"{target}.pre-restore-{stamp}");
                    FileCopier.CopyFile(new FileInfo(file), target, stats, log);
                }
                else
                {
                    if (options.KeepExistingAsBackup && Directory.Exists(target) &&
                        Directory.EnumerateFileSystemEntries(target).Any())
                    {
                        // Move what a fresh install created out of the way; if the app has it open, merge instead.
                        try { Directory.Move(target, $"{target}.pre-restore-{stamp}"); }
                        catch { log($"  (couldn't move the existing {Path.GetFileName(target)} folder aside — merging into it)"); }
                    }
                    FileCopier.CopyTree(src, target, skipCaches: false, stats, log, ct);
                }
            }, ct);
            failed += stats.Failed;
            log($"  settings: {d.Path} — {stats.Copied} files" + (stats.Failed > 0 ? $", {stats.Failed} failed" : ""));
            if (stats.FailedFiles.Any(f => f.Contains("Program Files", StringComparison.OrdinalIgnoreCase)))
                log("  (files under Program Files need the app to be run as administrator)");
        }

        foreach (var r in app.Registry)
        {
            var file = Path.Combine(backupFolder, "registry", app.BackupFolder, r.BackupFile);
            if (!File.Exists(file)) continue;
            var res = await ProcessRunner.RunAsync("reg", $"import \"{file}\"", ct);
            if (res.ExitCode == 0) log($"  registry: {r.Key}");
            else { failed++; log($"  ! couldn't import {r.Key}"); }
        }
        return failed;
    }

    private static void TryCopy(string from, string to)
    {
        try { File.Copy(from, to, overwrite: true); } catch { }
    }
}
