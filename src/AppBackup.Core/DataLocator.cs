using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AppBackup.Core;

/// <summary>Works out where each app keeps its settings and data.</summary>
public static class DataLocator
{
    private sealed class KnownApp
    {
        public string Match { get; set; } = "";
        public string? WingetId { get; set; }
        public List<string>? Paths { get; set; }
        public List<string>? Registry { get; set; }
        public string? Note { get; set; }
    }

    private static readonly Lazy<List<(Regex Re, KnownApp App)>> Known = new(() =>
    {
        using var s = typeof(DataLocator).Assembly.GetManifestResourceStream("AppBackup.Core.KnownApps.json")!;
        var list = JsonSerializer.Deserialize<List<KnownApp>>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        return list.Select(k => (new Regex(k.Match, RegexOptions.IgnoreCase), k)).ToList();
    });

    /// <summary>Folders that hold many apps' data or Windows' own — never treat them as one app's data.</summary>
    private static readonly HashSet<string> SkipTopFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Packages", "Temp", "Programs", "CrashDumps", "D3DSCache", "NVIDIA", "NVIDIA Corporation",
        "Intel", "AMD", "Comms", "ConnectedDevicesPlatform", "Package Cache", "Microsoft Help", "SquirrelTemp",
        "PeerDistRepub", "PlaceholderTileLogoFolder", "Publishers", "VirtualStore", "History", "IconCache",
        "Windows", "WindowsApps", "Application Data", "Desktop", "Downloads", "Documents", "Music", "Pictures",
        "Videos", "Favorites", "Links", "Saved Games", "Searches", "Contacts", "OneDrive", "My Games",
        "regid.1991-06.com.microsoft",
        "ssh", "USOPrivate", "USOShared", "SoftwareDistribution", "Start Menu", "Templates", "Microsoft OneDrive",
        "Custom Office Templates", "WindowsPowerShell", "PowerShell", "Visual Studio Setup",
    };

    /// <summary>Shared publisher folders: never one app's data on their own, but look inside for Publisher\Product.</summary>
    private static readonly HashSet<string> PublisherOnlyFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Adobe", "Google", "Mozilla", "Apple", "Apple Computer", "Oracle", "Sun", "Java", "JetBrains",
    };

    private static readonly HashSet<string> SkipRegistry = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Classes", "Policies", "Wow6432Node", "AppDataLow", "Intel", "NVIDIA Corporation", "Google",
        "Mozilla", "Clients", "RegisteredApplications", "ODBC", "Khronos", "Chromium", "Adobe", "Apple Inc.",
        "AMD", "Realtek", "Dell", "HP", "Lenovo", "WinRAR SFX", "Wow6432", "IM Providers", "Netscape",
    };

    /// <summary>Fills app.Data / app.Registry (and winget id / notes from the known-apps list).</summary>
    public static void Locate(AppEntry app, BackupOptions options)
    {
        app.Data.Clear();
        app.Registry.Clear();

        var known = Known.Value.FirstOrDefault(k => k.Re.IsMatch(app.Name)).App;
        if (known != null)
        {
            app.Note = known.Note;
            if (app.WingetId == null && known.WingetId != null) app.WingetId = known.WingetId;
            foreach (var p in known.Paths ?? new())
                AddPath(app, p, "known", options);
            foreach (var r in known.Registry ?? new())
                if (RegistryKeyExists(r)) AddRegistry(app, r, "known");
        }
        else
        {
            ScanFolders(app, options);
            ScanRegistry(app);
        }
        app.RefreshSummary();
    }

    public static void AddPath(AppEntry app, string pathOrToken, string source, BackupOptions options)
    {
        var tokenized = PathTokens.Tokenize(PathTokens.Expand(pathOrToken));
        var expanded = PathTokens.Expand(tokenized);
        if (app.Data.Any(d => d.Path.Equals(tokenized, StringComparison.OrdinalIgnoreCase))) return;

        DataKind kind;
        if (Directory.Exists(expanded)) kind = DataKind.Folder;
        else if (File.Exists(expanded)) kind = DataKind.File;
        else return; // nothing there on this PC

        // Don't add a folder that's inside one we already have (or swallow ones we have).
        if (app.Data.Any(d => IsUnder(expanded, d.ExpandedPath))) return;
        app.Data.RemoveAll(d => IsUnder(d.ExpandedPath, expanded));

        var size = kind == DataKind.File ? new FileInfo(expanded).Length : FolderSize(expanded, options.SkipCaches);
        app.Data.Add(new DataLocation
        {
            Path = tokenized,
            Kind = kind,
            Source = source,
            SizeBytes = size,
            BackupName = UniqueBackupName(app, tokenized),
            // Huge folders found by guessing are probably not settings — leave them unticked.
            Include = source != "scan" || options.MaxLocationBytes <= 0 || size <= options.MaxLocationBytes,
        });
    }

    public static void AddRegistry(AppEntry app, string key, string source)
    {
        if (app.Registry.Any(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) return;
        app.Registry.Add(new RegistryLocation
        {
            Key = key,
            Source = source,
            BackupFile = PathTokens.SafeName(key.Replace("HKCU\\Software\\", "", StringComparison.OrdinalIgnoreCase)) + ".reg",
        });
    }

    private static string UniqueBackupName(AppEntry app, string tokenized)
    {
        var baseName = PathTokens.SafeName(tokenized);
        var name = baseName;
        for (var i = 2; app.Data.Any(d => d.BackupName == name); i++) name = $"{baseName}_{i}";
        return name;
    }

    private static bool IsUnder(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Names a folder for this app might have, in compact form ("obsstudio", "notepad++").</summary>
    public static HashSet<string> CandidateNames(AppEntry app)
    {
        var set = new HashSet<string>();
        var full = AppScanner.Norm(app.Name);
        var words = full.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var pubFirst = AppScanner.Keywords(app.Publisher).FirstOrDefault();

        void Add(IEnumerable<string> w)
        {
            var c = string.Concat(w);
            if (c.Length >= 3) set.Add(c);
        }

        Add(words);
        if (pubFirst != null && words.Count > 1 && words[0] == pubFirst)
        {
            var product = words.Skip(1).ToList();          // "Mozilla Firefox" → "firefox"
            // …but not "OBS Studio" → "studio": a lone generic word would match other apps' folders.
            if (AppScanner.Keywords(string.Join(' ', product)).Count > 0 && string.Concat(product).Length >= 4)
            {
                Add(product);
                if (product.Count > 2) Add(product.Take(2));
            }
        }
        if (words.Count > 2) Add(words.Take(2));
        if (words.Count > 1 && words[0].Length >= 5 && words[0] != pubFirst &&
            AppScanner.Keywords(words[0]).Count > 0) set.Add(words[0]);

        if (!string.IsNullOrEmpty(app.InstallLocation))
        {
            var leaf = AppScanner.Norm(Path.GetFileName(app.InstallLocation.TrimEnd('\\')));
            var generic = new[] { "bin", "app", "application", "program", "current", "x64", "x86", "" };
            if (!generic.Contains(leaf) && !Regex.IsMatch(leaf, @"^[\d .]+$")) Add(leaf.Split(' '));
        }
        return set;
    }

    private static string Compact(string folderName) => AppScanner.Norm(folderName).Replace(" ", "");

    private static void ScanFolders(AppEntry app, BackupOptions options)
    {
        var names = CandidateNames(app);
        if (names.Count == 0) return;
        var publisherNames = new HashSet<string>();
        if (!string.IsNullOrEmpty(app.Publisher))
        {
            publisherNames.Add(Compact(app.Publisher));
            var firstWord = AppScanner.Keywords(app.Publisher).FirstOrDefault();
            if (firstWord != null && firstWord.Length >= 3) publisherNames.Add(firstWord);
        }

        var roots = new[] { "%APPDATA%", "%LOCALAPPDATA%", "%LOCALLOW%", "%PROGRAMDATA%", "%DOCUMENTS%" };
        foreach (var root in roots)
        {
            var rootPath = PathTokens.Expand(root);
            foreach (var dir in SafeDirs(rootPath))
            {
                var leaf = Path.GetFileName(dir);
                if (SkipTopFolders.Contains(leaf)) continue;
                var c = Compact(leaf);
                if (names.Contains(c) && !PublisherOnlyFolders.Contains(leaf))
                {
                    AddPath(app, dir, "scan", options);
                }
                else if (publisherNames.Contains(c))
                {
                    // %APPDATA%\Publisher\Product
                    foreach (var sub in SafeDirs(dir))
                        if (names.Contains(Compact(Path.GetFileName(sub)))) AddPath(app, sub, "scan", options);
                }
            }
        }

        // Dot-folders in the user profile, e.g. C:\Users\Matt\.gradle
        var profile = PathTokens.Expand("%USERPROFILE%");
        foreach (var dir in SafeDirs(profile))
        {
            var leaf = Path.GetFileName(dir);
            if (leaf.StartsWith('.') && names.Contains(Compact(leaf.TrimStart('.'))))
                AddPath(app, dir, "scan", options);
        }
    }

    private static void ScanRegistry(AppEntry app)
    {
        var names = CandidateNames(app);
        var pub = string.IsNullOrEmpty(app.Publisher) ? null : Compact(app.Publisher);
        var pubWord = AppScanner.Keywords(app.Publisher).FirstOrDefault();
        try
        {
            using var sw = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software");
            if (sw == null) return;
            foreach (var name in sw.GetSubKeyNames())
            {
                if (SkipRegistry.Contains(name)) continue;
                var c = Compact(name);
                if (names.Contains(c))
                {
                    AddRegistry(app, $@"HKCU\Software\{name}", "scan");
                }
                else if (c.Length >= 3 && (c == pub || c == pubWord))
                {
                    using var pk = sw.OpenSubKey(name);
                    foreach (var sub in pk?.GetSubKeyNames() ?? Array.Empty<string>())
                        if (names.Contains(Compact(sub))) AddRegistry(app, $@"HKCU\Software\{name}\{sub}", "scan");
                }
            }
        }
        catch { }
    }

    public static bool RegistryKeyExists(string key)
    {
        if (!key.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key[5..]);
            return k != null;
        }
        catch { return false; }
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return Array.Empty<string>();
            return Directory.GetDirectories(path).Where(d =>
            {
                try { return !new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint); }
                catch { return false; }
            }).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    // ---- file copying rules shared by backup and size estimates ----

    private static readonly HashSet<string> CacheDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Caches", "cache2", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache", "GraphiteDawnCache",
        "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "CachedData", "CachedExtensions", "CachedExtensionVSIXs",
        "Crashpad", "Crash Reports", "CrashDumps", "crashes", "logs", "Logs", "log", "Temp", "tmp", "temp",
        "CacheStorage", "ScriptCache", "startupCache", "thumbnails", "jumpListCache", "OfflineCache",
        "Service Worker", "component_crx_cache", "optimization_guide_model_store", "Safe Browsing", "WebStorage",
        "extensions_crx_cache", "BrowserMetrics", "Snapshots", "SwReporter",
    };

    private static readonly string[] SkipFileExt = { ".tmp", ".dmp", ".log", ".etl", ".lock" };

    public static bool SkipDirectory(string name, bool skipCaches) =>
        skipCaches && CacheDirs.Contains(name);

    public static bool SkipFile(string name, bool skipCaches) =>
        skipCaches && SkipFileExt.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public static long FolderSize(string path, bool skipCaches)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var f in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if (f.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (f is DirectoryInfo d) { if (!SkipDirectory(d.Name, skipCaches)) stack.Push(d.FullName); }
                    else if (f is FileInfo fi && !SkipFile(fi.Name, skipCaches)) total += fi.Length;
                }
            }
            catch { }
        }
        return total;
    }
}
