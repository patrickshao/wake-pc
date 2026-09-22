using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace WakePcBridge;

public sealed class BridgeTrayIcon : IDisposable
{
    private readonly Thread _uiThread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly string _configPath;
    private readonly BridgeCredentials _credentials;
    private readonly Action _writeDiagnostics;
    private readonly Action _requestShutdown;
    private TrayApplicationContext? _context;

    public BridgeTrayIcon(
        string configPath,
        BridgeCredentials credentials,
        Action writeDiagnostics,
        Action requestShutdown)
    {
        _configPath = configPath;
        _credentials = credentials;
        _writeDiagnostics = writeDiagnostics;
        _requestShutdown = requestShutdown;
        _uiThread = new Thread(RunUi)
        {
            IsBackground = true,
            Name = "Wake PC Bridge notification icon",
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        _ready.Wait();
    }

    public void Dispose()
    {
        TrayApplicationContext? context = _context;
        if (context is not null)
        {
            context.RequestExit();
            _uiThread.Join(TimeSpan.FromSeconds(5));
        }

        _ready.Dispose();
    }

    public void MarkPaired() => _context?.MarkPaired();

    private void RunUi()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var context = new TrayApplicationContext(
            _configPath,
            _credentials,
            _writeDiagnostics,
            _requestShutdown);
        _context = context;
        _ready.Set();
        Application.Run(context);
        _context = null;
    }

    private sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _pairingItem;
        private readonly ToolStripMenuItem _networkHealthItem;
        private readonly ContextMenuStrip _menu;
        private readonly Icon _applicationIcon;
        private readonly string _configPath;
        private readonly string _dataDirectory;
        private readonly Action _writeDiagnostics;
        private readonly Action _requestShutdown;

        public TrayApplicationContext(
            string configPath,
            BridgeCredentials credentials,
            Action writeDiagnostics,
            Action requestShutdown)
        {
            _configPath = configPath;
            _dataDirectory = Path.GetDirectoryName(configPath)
                ?? throw new ConfigException(
                    "Configuration directory is unavailable.");
            _writeDiagnostics = writeDiagnostics;
            _requestShutdown = requestShutdown;
            _applicationIcon = LoadApplicationIcon();
            _statusItem = new ToolStripMenuItem(
                $"Wake PC Companion · v{BridgeVersion.Current}")
            {
                Enabled = false,
            };
            _pairingItem = new ToolStripMenuItem(
                PairingStatus(credentials.DeviceCount))
            {
                Enabled = false,
            };
            _networkHealthItem = new ToolStripMenuItem(
                NetworkHealthStatus(credentials.Config))
            {
                Enabled = false,
            };
            var setupGuide = new ToolStripMenuItem(
                "Setup && status…",
                image: null,
                (_, _) => ShowSetupGuide());
            var advancedDiagnostics = new ToolStripMenuItem(
                "Advanced diagnostics…",
                image: null,
                (_, _) => WriteAndOpenDiagnostics());
            var exit = new ToolStripMenuItem(
                "Exit until next sign-in…",
                image: null,
                (_, _) => ExitUntilNextSignIn());
            _menu = new ContextMenuStrip();
            _menu.Items.Add(_statusItem);
            _menu.Items.Add(_networkHealthItem);
            _menu.Items.Add(_pairingItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(setupGuide);
            _menu.Items.Add(advancedDiagnostics);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exit);
            _menu.CreateControl();
            _menu.Opening += (_, _) => RefreshMenuStatus();
            _icon = new NotifyIcon
            {
                Icon = _applicationIcon,
                Text = $"Wake PC Companion {BridgeVersion.Current} — Running",
                ContextMenuStrip = _menu,
                Visible = true,
            };
            _icon.DoubleClick += (_, _) => ShowSetupGuide();
        }

        public void RequestExit()
        {
            if (_menu.InvokeRequired)
            {
                _menu.BeginInvoke(RequestExit);
                return;
            }

            _icon.Visible = false;
            ExitThread();
        }

        public void MarkPaired()
        {
            if (_menu.InvokeRequired)
            {
                _menu.BeginInvoke(MarkPaired);
                return;
            }

            BridgeCredentials credentials = BridgeCredentials.Load(_configPath);
            _pairingItem.Text = PairingStatus(credentials.DeviceCount);
        }

        private void RefreshMenuStatus()
        {
            try
            {
                BridgeCredentials credentials = BridgeCredentials.Load(_configPath);
                _pairingItem.Text = PairingStatus(credentials.DeviceCount);
                _networkHealthItem.Text = NetworkHealthStatus(credentials.Config);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                ConfigException)
            {
                _networkHealthItem.Text = "Wake: Check unavailable";
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _icon.Visible = false;
                _menu.Dispose();
                _icon.Dispose();
                _applicationIcon.Dispose();
            }

            base.Dispose(disposing);
        }

        private void ExitUntilNextSignIn()
        {
            DialogResult confirmation = MessageBox.Show(
                "Sleep control will stop until your next Windows sign-in.\n\n" +
                "Wake-on-LAN will still work.",
                "Exit Wake PC Companion?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                return;
            }

            if (!BridgePauseState.TryPauseForCurrentSession(out string? error))
            {
                MessageBox.Show(
                    "Windows could not pause the bridge. It is still running.\n\n" +
                    error,
                    "Wake PC Companion",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            _statusItem.Text = "Bridge is exiting";
            _requestShutdown();
        }

        private void WriteAndOpenDiagnostics()
        {
            try
            {
                _writeDiagnostics();
                OpenPath(Path.Combine(
                    _dataDirectory,
                    "WakePcBridge.diagnose.json"));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                InvalidOperationException)
            {
                MessageBox.Show(
                    "Advanced diagnostics could not be created.",
                    "Wake PC Companion",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private static void OpenPath(string path)
        {
            if (!File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = true,
                    ArgumentList = { Path.GetDirectoryName(path) ?? path },
                });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
                ArgumentList = { $"/select,{path}" },
            });
        }

        private static Icon LoadApplicationIcon()
        {
            using Stream? stream = typeof(BridgeTrayIcon).Assembly
                .GetManifestResourceStream("WakePcBridge.ico");
            if (stream is null)
            {
                return (Icon)SystemIcons.Application.Clone();
            }

            using var source = new Icon(stream);
            return (Icon)source.Clone();
        }

        private void ShowSetupGuide()
        {
            try
            {
                BridgeSetupGuide.Show(_configPath);
                RefreshMenuStatus();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                ConfigException or InvalidOperationException)
            {
                MessageBox.Show(
                    "Wake PC setup could not be opened.\n\n" +
                    BridgeLog.Redact(exception.Message),
                    "Wake PC Companion",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private static string PairingStatus(int deviceCount) => deviceCount switch
        {
            0 => "Sleep: Not enabled",
            1 => "Sleep: 1 device paired",
            _ => $"Sleep: {deviceCount} devices paired",
        };

        private static string NetworkHealthStatus(BridgeConfig config)
        {
            BridgeNetworkAssessment assessment = BridgeSetupGuide.Assess(
                config,
                BridgeNetworkDiagnostics.GetActiveIpv4());
            return assessment.Health switch
            {
                BridgeNetworkHealth.Healthy => "Network: Details match",
                BridgeNetworkHealth.NotConfigured => "Wake: Setup needed",
                BridgeNetworkHealth.IpChanged => "Wake: Address changed",
                BridgeNetworkHealth.GatewayMissing => "Wake: Check Ethernet",
                _ => "Wake: Connect Ethernet",
            };
        }
    }
}
