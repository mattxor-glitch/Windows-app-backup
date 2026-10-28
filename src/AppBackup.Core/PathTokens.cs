namespace AppBackup.Core;

/// <summary>
/// Converts absolute paths to portable tokens (%APPDATA%\Foo) and back, so a backup made as
/// C:\Users\Matt restores correctly to C:\Users\matt2 or to a Documents folder that lives in OneDrive.
/// </summary>
public static class PathTokens
{
    private static List<(string Token, string Path)>? _map;

    private static List<(string Token, string Path)> Map => _map ??= Build();

    private static List<(string Token, string Path)> Build()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<(string, string)>
        {
            ("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            ("%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            ("%LOCALLOW%", string.IsNullOrEmpty(profile) ? "" : System.IO.Path.Combine(profile, "AppData", "LocalLow")),
            ("%DOCUMENTS%", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("%PICTURES%", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ("%PROGRAMDATA%", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            ("%PROGRAMFILES(X86)%", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
            ("%PROGRAMFILES%", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            ("%USERPROFILE%", profile),
        };
        // Longest real path first so %LOCALAPPDATA% wins over %USERPROFILE%.
        return list.Where(t => !string.IsNullOrEmpty(t.Item2))
                   .Select(t => (t.Item1, t.Item2.TrimEnd('\\', '/')))
                   .OrderByDescending(t => t.Item2.Length)
                   .ToList();
    }

    /// <summary>Lets tests (or a future "restore for another user" option) override the folder map.</summary>
    public static void Override(IEnumerable<(string Token, string Path)> map) =>
        _map = map.OrderByDescending(t => t.Path.Length).ToList();

    public static string Tokenize(string absolutePath)
    {
        foreach (var (token, path) in Map)
        {
            if (absolutePath.Equals(path, StringComparison.OrdinalIgnoreCase))
                return token;
            if (absolutePath.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase) ||
                absolutePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))
                return token + absolutePath[path.Length..];
        }
        return absolutePath;
    }

    public static string Expand(string tokenized)
    {
        foreach (var (token, path) in Map)
        {
            if (tokenized.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                return path + tokenized[token.Length..];
        }
        return Environment.ExpandEnvironmentVariables(tokenized);
    }

    /// <summary>Turns any path into something safe to use as one folder name.</summary>
    public static string SafeName(string s)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars().Concat(new[] { '%', '\\', '/', ':' }).ToHashSet();
        var chars = s.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var name = new string(chars).Trim('_', ' ', '.');
        while (name.Contains("__")) name = name.Replace("__", "_");
        if (name.Length > 80) name = name[..80];
        return string.IsNullOrEmpty(name) ? "item" : name;
    }
}
