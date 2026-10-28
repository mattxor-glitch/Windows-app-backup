using System.Diagnostics;
using System.Text;

namespace AppBackup.Core;

public sealed record ProcessResult(int ExitCode, string Output);

public static class ProcessRunner
{
    /// <summary>Runs a console program hidden, capturing its output.</summary>
    public static async Task<ProcessResult> RunAsync(string file, string args, CancellationToken ct = default,
        Action<string>? onLine = null, string? workingDir = null)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDir != null) psi.WorkingDirectory = workingDir;

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var sb = new StringBuilder();
        void Handle(string? line)
        {
            if (line == null) return;
            line = CleanConsoleLine(line);
            lock (sb) sb.AppendLine(line);
            if (onLine != null && line.Trim().Length > 0 && !IsProgressNoise(line)) onLine(line);
        }
        p.OutputDataReceived += (_, e) => Handle(e.Data);
        p.ErrorDataReceived += (_, e) => Handle(e.Data);

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        p.WaitForExit(); // flush async readers
        return new ProcessResult(p.ExitCode, sb.ToString());
    }

    /// <summary>Starts an installer with a normal window (it may show UAC / its own wizard) and waits.</summary>
    public static async Task<int> RunInstallerAsync(string file, string args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = true, // lets Windows show the UAC prompt the installer asks for
            WorkingDirectory = Path.GetDirectoryName(file) ?? "",
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + file);
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }

    /// <summary>winget draws spinners with \r and \b; keep only what's finally visible.</summary>
    public static string CleanConsoleLine(string line)
    {
        var cr = line.LastIndexOf('\r');
        if (cr >= 0) line = line[(cr + 1)..];
        if (line.Contains('\b'))
        {
            var sb = new StringBuilder();
            foreach (var c in line)
            {
                if (c == '\b') { if (sb.Length > 0) sb.Length--; }
                else sb.Append(c);
            }
            line = sb.ToString();
        }
        return line.TrimEnd();
    }

    private static bool IsProgressNoise(string line)
    {
        var t = line.Trim();
        if (t.Length <= 2 && "-\\|/".Contains(t)) return true;
        // Download progress bars: "██████▒▒▒  12.0 MB / 40.0 MB"
        return t.Contains('█') || t.Contains('▒');
    }
}
