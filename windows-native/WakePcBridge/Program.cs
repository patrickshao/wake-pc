using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text.Json;

namespace WakePcBridge;

internal static class Program
{
    private const string Usage =
        "WakePcBridge.exe [start|supervise|run|setup-guide|diagnose] " +
        "[--config <absolute-path>] | configure-system | unconfigure-system | self-test";

    [STAThread]
    // Keep the CLR entry point synchronous so STAThread applies to the thread
    // that owns the setup window and its clipboard operations.
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "self-test")
            {
                return RunSelfTest();
            }

            string command = args.Length == 0 ? "start" : args[0];
            // Elevated commands read no configuration. Older uninstallers may still pass
            // --config, so extra arguments are ignored rather than rejected.
            if (command == "configure-system")
            {
                return SystemRegistration.Configure();
            }

            if (command == "unconfigure-system")
            {
                return SystemRegistration.Unconfigure();
            }

            string configPath;
            if (args.Length is 0 or 1)
            {
                configPath = BridgeConfig.DefaultPath;
            }
            else if (args.Length == 3 &&
                args[1] == "--config" &&
                Path.IsPathFullyQualified(args[2]))
            {
                configPath = args[2];
            }
            else
            {
                return WriteUsageFailure();
            }

            return command switch
            {
                "start" => BridgeSupervisor.RunAsync(
                    configPath,
                    resumeCurrentSession: true).GetAwaiter().GetResult(),
                "supervise" => BridgeSupervisor.RunAsync(
                    configPath,
                    resumeCurrentSession: false).GetAwaiter().GetResult(),
                "run" => RunBridgeAsync(configPath).GetAwaiter().GetResult(),
                "setup-guide" => RunSetupGuide(configPath),
                "diagnose" => RunDiagnostics(configPath),
                _ => WriteUsageFailure(),
            };
        }
        catch (Exception exception)
        {
            TryWriteFailure(args, exception);
            return 1;
        }
    }

    private static int RunSetupGuide(string configPath)
    {
        if (IsElevated())
        {
            throw new InvalidOperationException(
                "The setup guide refuses to run with administrator privileges.");
        }

        return BridgeSetupGuide.Run(configPath);
    }

    private static async Task<int> RunBridgeAsync(string configPath)
    {
        if (IsElevated())
        {
            throw new InvalidOperationException(
                "The bridge runtime refuses to run with administrator privileges.");
        }

        if (BridgePauseState.IsPausedForCurrentSession())
        {
            return 0;
        }

        BridgeConfig.LoadOrCreate(configPath, out _);
        string directory = Path.GetDirectoryName(configPath)
            ?? throw new ConfigException("Configuration directory is unavailable.");
        string logPath = Path.Combine(directory, "sleep-bridge.log");
        var log = new BridgeLog(logPath);
        if (configPath == BridgeConfig.DefaultPath)
        {
            BridgeConfig.RemoveLegacySharedDirectory(log);
        }

        BridgeCredentials credentials = BridgeCredentials.LoadOrMigrate(
            configPath,
            logPath);
        BridgeConfig config = credentials.Config;
        using var mutex = new Mutex(
            initiallyOwned: true,
            "WakePcSleepBridge.Runtime",
            out bool createdNew);
        if (!createdNew)
        {
            log.Write("Duplicate native bridge instance exited.");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cancellation.Cancel();
        log.Write($"Native bridge starting as {WindowsIdentity.GetCurrent().Name}.");
        using var trayIcon = new BridgeTrayIcon(
            configPath,
            credentials,
            () => WriteDiagnostics(configPath),
            cancellation.Cancel);
        await using var host = new BridgeHost(
            config,
            credentials,
            configPath,
            new WindowsPowerController(),
            log,
            pairingCompleted: trayIcon.MarkPaired);
        await host.RunAsync(cancellation.Token).ConfigureAwait(false);
        GC.KeepAlive(mutex);
        return 0;
    }

    private static int RunSelfTest()
    {
        string reportPath = Path.Combine(
            AppContext.BaseDirectory,
            "WakePcBridge.self-test.json");
        try
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                throw new InvalidOperationException("The executable entry thread must use STA for clipboard access.");
            }
            IReadOnlyList<string> tests = BridgeSelfTest.RunPureTests();
            File.WriteAllText(
                reportPath,
                JsonSerializer.Serialize(
                    new { ok = true, tests },
                    new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception exception)
        {
            File.WriteAllText(
                reportPath,
                JsonSerializer.Serialize(
                    new { ok = false, error = BridgeLog.Redact(exception.Message) },
                    new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    private static int RunDiagnostics(string configPath)
    {
        WriteDiagnostics(configPath);
        return 0;
    }

    private static void WriteDiagnostics(string configPath)
    {
        string reportPath = Path.Combine(
            Path.GetDirectoryName(configPath)
                ?? throw new ConfigException("Configuration directory is unavailable."),
            "WakePcBridge.diagnose.json");
        string logPath = Path.Combine(
            Path.GetDirectoryName(configPath)!,
            "sleep-bridge.log");
        BridgeCredentials credentials = BridgeCredentials.LoadOrMigrate(
            configPath,
            logPath);
        BridgeConfig config = credentials.Config;
        IReadOnlyList<BridgeNetworkAddress> networkAddresses =
            BridgeNetworkDiagnostics.GetActiveIpv4();
        BridgeNetworkAssessment networkAssessment = BridgeSetupGuide.Assess(
            config,
            networkAddresses);
        bool portInUse = IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == config.Port);
        string[] recentLogLines = File.Exists(logPath)
            ? File.ReadLines(logPath).TakeLast(20).Select(BridgeLog.Redact).ToArray()
            : [];
        var report = new
        {
            generated_utc = DateTimeOffset.UtcNow,
            bridge_version = BridgeVersion.Current,
            config_valid = true,
            credential_protected = config.ProtectedCredentials.Length != 0,
            pairing_completed = credentials.PairedOnce,
            paired_device_count = credentials.DeviceCount,
            port = config.Port,
            network_addresses = networkAddresses.Select(address => new
            {
                interface_name = address.InterfaceName,
                ipv4_address = address.Address,
                prefix_length = address.PrefixLength,
                default_gateways = address.Gateways,
                dhcp_enabled = address.DhcpEnabled,
                sleep_bridge_target = $"{address.Address}:{config.Port}",
            }),
            network_setup = new
            {
                status = networkAssessment.Health.ToString(),
                summary = networkAssessment.Summary,
                expected_ipv4_address = config.ExpectedIpv4Address,
                current_ipv4_address = networkAssessment.Current?.Address,
            },
            listener_port_in_use = portInUse,
            current_user = WindowsIdentity.GetCurrent().Name,
            current_sid = WindowsIdentity.GetCurrent().User?.Value,
            process_elevated = IsElevated(),
            process_id = Environment.ProcessId,
            executable = Environment.ProcessPath,
            startup =
                @"HKLM\Software\Microsoft\Windows\CurrentVersion\Run\" +
                "WakePcSleepBridge",
            url_acl = "Use installer validation output for the exact ACL entry.",
            firewall_rule = "Use installer validation output for the exact rule.",
            recent_log_lines = recentLogLines,
        };
        File.WriteAllText(
            reportPath,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(
            WindowsBuiltInRole.Administrator);
    }

    private static int WriteUsageFailure()
    {
        File.WriteAllText(
            Path.Combine(AppContext.BaseDirectory, "WakePcBridge.error.txt"),
            Usage);
        return 64;
    }

    private static void TryWriteFailure(string[] args, Exception exception)
    {
        try
        {
            string configPath = args.Length >= 3 && args[1] == "--config"
                ? args[2]
                : BridgeConfig.DefaultPath;
            // Elevated failures stay in Program Files, never in a user-controlled path.
            string directory = !IsElevated() && Path.IsPathFullyQualified(configPath)
                ? Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory
                : AppContext.BaseDirectory;
            File.WriteAllText(
                Path.Combine(directory, "WakePcBridge.error.txt"),
                BridgeLog.Redact(exception.ToString()));
        }
        catch (Exception writeException) when (
            writeException is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(writeException);
        }
    }
}
