using System.ComponentModel;
using System.Diagnostics;

namespace WakePcBridge;

internal static class SystemRegistration
{
    public const string FirewallRuleName = "Wake PC Sleep Bridge";
    public const string LegacyTaskName = "Wake PC Sleep Bridge";

    public static int Configure(string configPath)
    {
        RequireAdministrator();
        BridgeSelfTest.RunPureTests();
        StopOtherBridgeProcesses();
        DeleteLegacyScheduledTask();

        BridgeConfig config = BridgeConfig.LoadOrCreate(
            configPath,
            out _);
        DeleteNetworkRegistration(config.Port);
        try
        {
            RunNetsh(
                [
                    "http",
                    "add",
                    "urlacl",
                    $"url=http://+:{config.Port}/",
                    "sddl=D:(A;;GX;;;BU)",
                ]);
            RunNetsh(
                [
                    "advfirewall",
                    "firewall",
                    "add",
                    "rule",
                    $"name={FirewallRuleName}",
                    "dir=in",
                    "action=allow",
                    "enable=yes",
                    "protocol=TCP",
                    $"localport={config.Port}",
                    "remoteip=LocalSubnet",
                    "profile=private,public",
                ]);
            VerifyNetworkRegistration(config.Port);
            DeleteLegacyRuntimeFiles(configPath);
            return 0;
        }
        catch
        {
            DeleteNetworkRegistration(config.Port);
            throw;
        }
    }

    public static int Unconfigure(string configPath)
    {
        RequireAdministrator();
        StopOtherBridgeProcesses();
        DeleteLegacyScheduledTask();
        int port = 47653;
        try
        {
            if (File.Exists(configPath))
            {
                port = BridgeConfig.Load(configPath).Port;
            }
        }
        catch (ConfigException)
        {
            // Cleanup should remain possible even if configuration is damaged.
        }

        DeleteNetworkRegistration(port);
        return 0;
    }

    private static void VerifyNetworkRegistration(int port)
    {
        RunNetsh(
            [
                "http",
                "show",
                "urlacl",
                $"url=http://+:{port}/",
            ]);
        RunNetsh(
            [
                "advfirewall",
                "firewall",
                "show",
                "rule",
                $"name={FirewallRuleName}",
                "verbose",
            ]);
    }

    private static void DeleteNetworkRegistration(int port)
    {
        RunNetsh(
            [
                "advfirewall",
                "firewall",
                "delete",
                "rule",
                $"name={FirewallRuleName}",
            ],
            ignoreFailure: true);
        RunNetsh(
            [
                "http",
                "delete",
                "urlacl",
                $"url=http://+:{port}/",
            ],
            ignoreFailure: true);
    }

    private static void DeleteLegacyScheduledTask()
    {
        RunNative(
            Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            [
                "/Delete",
                "/TN",
                LegacyTaskName,
                "/F",
            ],
            ignoreFailure: true);
    }

    private static void StopOtherBridgeProcesses()
    {
        int currentProcessId = Environment.ProcessId;
        Process[] processes = Process.GetProcessesByName("WakePcBridge");
        foreach (Process process in processes)
        {
            using (process)
            {
                if (process.Id == currentProcessId)
                {
                    continue;
                }

                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or Win32Exception or
                    NotSupportedException)
                {
                }
            }
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            Process[] remaining = Process.GetProcessesByName("WakePcBridge");
            bool allStopped;
            try
            {
                allStopped = remaining.All(
                    process => process.Id == currentProcessId);
            }
            finally
            {
                foreach (Process process in remaining)
                {
                    process.Dispose();
                }
            }

            if (allStopped)
            {
                return;
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException(
            "Another Wake PC Sleep Bridge process could not be stopped.");
    }

    private static void DeleteLegacyRuntimeFiles(string configPath)
    {
        string directory = Path.GetDirectoryName(configPath)
            ?? throw new ConfigException("Configuration directory is unavailable.");
        foreach (string name in new[]
        {
            "WakePcBridge.exe",
            "sleep-bridge.ps1",
            "run-sleep-bridge.vbs",
            "WakePcBridge.error.txt",
        })
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void RunNetsh(
        IReadOnlyList<string> arguments,
        bool ignoreFailure = false) =>
        RunNative(
            Path.Combine(Environment.SystemDirectory, "netsh.exe"),
            arguments,
            ignoreFailure);

    private static void RunNative(
        string executable,
        IReadOnlyList<string> arguments,
        bool ignoreFailure)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} could not be started.");
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(standardOutput, standardError);
        if (process.ExitCode == 0 || ignoreFailure)
        {
            return;
        }

        string detail = string.Join(
            " ",
            new[] { standardOutput.Result, standardError.Result }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => BridgeLog.Redact(value.Trim())));
        if (detail.Length > 1_000)
        {
            detail = detail[^1_000..];
        }

        throw new InvalidOperationException(
            $"{Path.GetFileName(executable)} failed with exit code " +
            $"{process.ExitCode}: {detail}");
    }

    private static void RequireAdministrator()
    {
        if (!Program.IsElevated())
        {
            throw new InvalidOperationException(
                "System registration requires administrator privileges.");
        }
    }
}
