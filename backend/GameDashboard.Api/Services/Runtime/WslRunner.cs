using System.Diagnostics;
using System.Text;

namespace GameDashboard.Api.Services.Runtime;

/// <summary>
/// Thin wrapper over wsl.exe invocations so RuntimeSetupService's decision
/// logic can be unit-tested against a mock. Windows-only; callers guard with
/// OperatingSystem.IsWindows().
/// </summary>
public interface IWslRunner
{
    /// <summary>
    /// Runs `wsl.exe {arguments}` and captures its output. Never throws on a
    /// non-zero exit code — that's a result, not an exception. Throws only
    /// when wsl.exe itself cannot be started (not a Windows box).
    /// </summary>
    Task<WslResult> RunAsync(string arguments, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Runs an elevated command (UAC prompt) without output capture — used for
    /// `wsl --install`, which needs admin rights. Returns the exit code, or
    /// null when the user cancelled the UAC prompt.
    /// </summary>
    Task<int?> RunElevatedAsync(string arguments, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Spawns `wsl.exe {arguments}` as a detached, hidden process that is
    /// never waited on and outlives this backend — used for the keep-alive
    /// session that pins the runtime VM (see RuntimeSetupService).
    /// </summary>
    void StartDetached(string arguments);
}

public sealed record WslResult(int ExitCode, string Output);

public sealed class WslRunner : IWslRunner
{
    public async Task<WslResult> RunAsync(string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // wsl.exe writes UTF-16LE to its output pipes; reading it as the
            // default codepage yields interleaved NUL characters that break
            // string matching ("g\0a\0m\0e...").
            StandardOutputEncoding = Encoding.Unicode,
            StandardErrorEncoding = Encoding.Unicode,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start wsl.exe.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        var output = (await stdoutTask) + (await stderrTask);
        return new WslResult(process.ExitCode, output);
    }

    public void StartDetached(string arguments)
    {
        // No job object, no redirects, never waited on: the child survives
        // this backend exiting, which is what lets game servers keep running
        // after the dashboard app is closed.
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "wsl.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    public async Task<int?> RunElevatedAsync(string arguments, TimeSpan timeout, CancellationToken ct)
    {
        // UseShellExecute + runas triggers the UAC prompt; output cannot be
        // captured in this mode, so callers re-check state afterwards.
        var startInfo = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
        };

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start wsl.exe.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // the user declined the UAC prompt
        }

        using (process)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw;
            }

            return process.ExitCode;
        }
    }
}
