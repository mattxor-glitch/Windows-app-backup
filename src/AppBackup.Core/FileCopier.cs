namespace AppBackup.Core;

public sealed class CopyStats
{
    public int Copied;
    public int Unchanged;
    public int Failed;
    public long Bytes;
    public List<string> FailedFiles { get; } = new();
}

public static class FileCopier
{
    /// <summary>
    /// Copies a folder tree. Files that are already identical (same size and time) are skipped, so running a
    /// backup again into the same folder only copies what changed. Locked/unreadable files are logged, not fatal.
    /// </summary>
    public static void CopyTree(string source, string dest, bool skipCaches, CopyStats stats, Action<string>? log,
        CancellationToken ct)
    {
        var stack = new Stack<(string Src, string Dst)>();
        stack.Push((source, dest));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (src, dst) = stack.Pop();
            FileSystemInfo[] entries;
            try
            {
                Directory.CreateDirectory(dst);
                entries = new DirectoryInfo(src).GetFileSystemInfos();
            }
            catch (Exception ex)
            {
                stats.Failed++;
                stats.FailedFiles.Add(src);
                log?.Invoke($"  ! Can't read folder {src}: {ex.Message}");
                continue;
            }

            foreach (var e in entries)
            {
                if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // junctions/symlinks
                if (e is DirectoryInfo d)
                {
                    if (!DataLocator.SkipDirectory(d.Name, skipCaches))
                        stack.Push((d.FullName, Path.Combine(dst, d.Name)));
                }
                else if (e is FileInfo f && !DataLocator.SkipFile(f.Name, skipCaches))
                {
                    CopyFile(f, Path.Combine(dst, f.Name), stats, log);
                }
            }
        }
    }

    public static void CopyFile(FileInfo src, string dst, CopyStats stats, Action<string>? log)
    {
        try
        {
            var target = new FileInfo(dst);
            if (target.Exists && target.Length == src.Length && target.LastWriteTimeUtc == src.LastWriteTimeUtc)
            {
                stats.Unchanged++;
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (target.Exists && target.IsReadOnly) target.IsReadOnly = false;
            src.CopyTo(dst, overwrite: true);
            File.SetLastWriteTimeUtc(dst, src.LastWriteTimeUtc);
            stats.Copied++;
            stats.Bytes += src.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stats.Failed++;
            stats.FailedFiles.Add(src.FullName);
            var reason = ex is UnauthorizedAccessException ? "access denied"
                : ex.HResult == unchecked((int)0x80070020) ? "in use — close the app and try again" : ex.Message;
            log?.Invoke($"  ! Skipped {src.FullName} ({reason})");
        }
    }
}
