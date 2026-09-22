using System.Diagnostics;

namespace WakePcBridge;

internal static class BridgeSupervisor
{
    private const string MutexName = "WakePcSleepBridge.Supervisor";

    public static async Task<int> RunAsync(
        string configPath,
        bool resumeCurrentSession)
    {
        if (Program.IsElevated())
        {
            throw new InvalidOperationException(
                "The bridge runtime refuses to run with administrator privileges.");
        }

        if (resumeCurrentSession)
        {
            BridgePauseState.ClearForCurrentSession();
        }

        string directory = Path.GetDirectoryName(configPath)
            ?? throw new ConfigException("Configuration directory is unavailable.");
        Directory.CreateDirectory(directory);
        var log = new BridgeLog(Path.Combine(directory, "sleep-bridge.log"));
        using var mutex = new Mutex(
            initiallyOwned: true,
            MutexName,
            out bool createdNew);
        if (!createdNew)
        {
            log.Write("Duplicate bridge supervisor exited.");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cancellation.Cancel();

        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The bridge executable path is unavailable.");
        log.Write("Bridge supervisor started.");
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (BridgePauseState.IsPausedForCurrentSession())
                {
                    log.Write("Bridge supervisor honoring exit until next sign-in.");
                    return 0;
                }

                using var child = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = executable,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        ArgumentList =
                        {
                            "run",
                            "--config",
                            configPath,
                        },
                    },
                };
                if (!child.Start())
                {
                    throw new InvalidOperationException(
                        "The bridge runtime could not be started.");
                }

                try
                {
                    await child.WaitForExitAsync(cancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (cancellation.IsCancellationRequested)
                {
                    TryStop(child);
                    break;
                }

                if (BridgePauseState.IsPausedForCurrentSession())
                {
                    log.Write("Bridge exited by user until next sign-in.");
                    return 0;
                }

                log.Write(
                    $"Bridge runtime exited with code {child.ExitCode}; restarting.");
                await Task.Delay(
                    TimeSpan.FromSeconds(5),
                    cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            log.Write("Bridge supervisor stopped.");
            GC.KeepAlive(mutex);
        }

        return 0;
    }

    private static void TryStop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
        }
    }
}
