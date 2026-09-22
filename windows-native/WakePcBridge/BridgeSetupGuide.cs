using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WakePcBridge;

public enum BridgeNetworkHealth
{
    NotConfigured,
    Healthy,
    IpChanged,
    AdapterUnavailable,
    GatewayMissing,
}

public sealed record BridgeNetworkAssessment(
    BridgeNetworkHealth Health,
    BridgeNetworkAddress? Current,
    string Summary,
    string Detail);

public static class BridgeSetupGuide
{
    private static readonly Size PreferredWindowSize = new(640, 560);

    public static int Run(string configPath)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Show(configPath);
        return 0;
    }

    public static void Show(string configPath)
    {
        // Start-menu and tray entry points share one window per configuration/session.
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(configPath).ToUpperInvariant())));
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset,
            $"Local\\WakePcSleepBridge.SetupActivate.{identity}");
        using var instance = new Mutex(false,
            $"Local\\WakePcSleepBridge.SetupWindow.{identity}", out bool firstWindow);
        if (!firstWindow)
        {
            activate.Set();
            return;
        }

        BridgeCredentials.LoadOrMigrate(configPath);
        using var form = new SetupGuideForm(configPath);
        using var activationTimer = new System.Windows.Forms.Timer { Interval = 100 };
        activationTimer.Tick += (_, _) =>
        {
            if (!activate.WaitOne(0)) return;
            if (form.WindowState == FormWindowState.Minimized)
                form.WindowState = FormWindowState.Normal;
            form.BringToFront();
            form.Activate();
        };
        activationTimer.Start();
        form.ShowDialog();
    }

    internal static BridgeNetworkAddress? SelectWiredAddress(
        IReadOnlyList<BridgeNetworkAddress> addresses) => addresses
            .Where(address => address.IsWired && address.MacAddress.Length != 0)
            .OrderByDescending(address => address.Gateways.Count > 0)
            .ThenBy(address => address.Address.StartsWith(
                "169.254.",
                StringComparison.Ordinal))
            .ThenBy(address => address.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    internal static BridgeNetworkAssessment Assess(
        BridgeConfig config,
        IReadOnlyList<BridgeNetworkAddress> addresses)
    {
        BridgeNetworkAddress? current;
        if (config.ExpectedWiredMac.Length == 0)
        {
            current = SelectWiredAddress(addresses);
            return current is null
                ? new BridgeNetworkAssessment(
                    BridgeNetworkHealth.AdapterUnavailable,
                    null,
                    "Connect this PC by Ethernet",
                    "Wake PC needs the target PC to have a wired Ethernet connection. Connect it, then refresh.")
                : new BridgeNetworkAssessment(
                    BridgeNetworkHealth.NotConfigured,
                    current,
                    "Save this PC's network details",
                    "We found the wired connection. Save it so Wake PC can warn you if this PC's address changes.");
        }

        current = addresses.FirstOrDefault(address =>
            address.IsWired &&
            string.Equals(
                address.MacAddress,
                config.ExpectedWiredMac,
                StringComparison.Ordinal) &&
            string.Equals(
                address.Address,
                config.ExpectedIpv4Address,
                StringComparison.Ordinal));
        if (current is not null)
        {
            return current.Gateways.Count == 0
                ? new BridgeNetworkAssessment(
                    BridgeNetworkHealth.GatewayMissing,
                    current,
                    "Ethernet needs attention",
                    "Windows cannot reach the router over Ethernet. Check the cable and network connection, then refresh.")
                : new BridgeNetworkAssessment(
                    BridgeNetworkHealth.Healthy,
                    current,
                    "Network details match",
                    "This PC still matches the wired connection saved in Wake PC.");
        }

        current = addresses
            .Where(address =>
                address.IsWired &&
                string.Equals(
                    address.MacAddress,
                    config.ExpectedWiredMac,
                    StringComparison.Ordinal))
            .OrderBy(address => address.Address.StartsWith(
                "169.254.",
                StringComparison.Ordinal))
            .ThenByDescending(address => address.Gateways.Count > 0)
            .FirstOrDefault();
        if (current is null)
        {
            current = SelectWiredAddress(addresses);
            return new BridgeNetworkAssessment(
                BridgeNetworkHealth.AdapterUnavailable,
                current,
                "Saved Ethernet connection not found",
                current is null
                    ? "Reconnect the Ethernet cable, then refresh."
                    : "A different wired connection is active. Save it only if this is the connection you now want to use.");
        }

        return new BridgeNetworkAssessment(
            BridgeNetworkHealth.IpChanged,
            current,
            "PC address changed",
            $"This PC now uses {current.Address}. Update your clients and router, then confirm it here.");
    }

    internal static string FormatSetupValues(
        BridgeConfig config,
        BridgeNetworkAddress? address,
        int deviceCount)
    {
        string pairing = deviceCount switch
        {
            0 => "Not enabled",
            1 => "Enabled for 1 device",
            _ => $"Enabled for {deviceCount} devices",
        };
        return
            $"MAC address:  {ValueOrUnavailable(address?.MacAddress)}\r\n" +
            $"IP address:   {ValueOrUnavailable(address?.Address)}\r\n" +
            $"Sleep PC:     {pairing}";
    }

    internal static string FormatSteps(BridgeNetworkAddress? address)
    {
        string macAddress = address?.MacAddress ?? "the MAC address shown on Status";
        string pcAddress = address?.Address ?? "the IP address shown on Status";
        return
            "1. Connect this PC by Ethernet\r\n" +
            "Wake PC sends the wake signal over your local network. The PC being woken must use wired Ethernet.\r\n\r\n" +
            "2. Turn on Wake on LAN\r\n" +
            "Enable Wake on LAN for this PC and its Ethernet adapter. The exact setting name depends on the PC.\r\n\r\n" +
            "3. Keep this PC's address stable\r\n" +
            $"In your router's address-reservation settings, reserve {pcAddress} for {macAddress}.\r\n\r\n" +
            "4. Add the PC on your other device\r\n" +
            "Decky: open Wake PC → Settings, enter the MAC and IP addresses, then choose Save changes.\r\n" +
            "Mac: run wake-pc setup and enter the same addresses.\r\n\r\n" +
            "5. Save and test\r\n" +
            "Save this PC here, put it to sleep, and try Wake PC.\r\n\r\n" +
            "Optional: enable Sleep PC\r\n" +
            "Choose Pair for Sleep. Enter the code in Decky, or run wake-pc pair on your Mac.";
    }

    internal static Rectangle FitWindowToWorkingArea(
        Rectangle workingArea,
        Size preferredSize,
        int margin)
    {
        int safeMargin = Math.Max(0, Math.Min(
            margin,
            Math.Min(workingArea.Width, workingArea.Height) / 4));
        int availableWidth = Math.Max(1, workingArea.Width - (safeMargin * 2));
        int availableHeight = Math.Max(1, workingArea.Height - (safeMargin * 2));
        int width = Math.Min(preferredSize.Width, availableWidth);
        int height = Math.Min(preferredSize.Height, availableHeight);
        int left = workingArea.Left + ((workingArea.Width - width) / 2);
        int top = workingArea.Top + ((workingArea.Height - height) / 2);
        return new Rectangle(left, top, width, height);
    }

    internal static Size ScaleForDpi(Size logicalSize, int dpi)
    {
        int safeDpi = Math.Clamp(dpi, 96, 768);
        return new Size(
            (int)Math.Round(logicalSize.Width * safeDpi / 96d),
            (int)Math.Round(logicalSize.Height * safeDpi / 96d));
    }

    private static string ValueOrUnavailable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Not saved" : value;

    private sealed class SetupGuideForm : Form
    {
        private readonly string _configPath;
        private readonly Label _healthSummary;
        private readonly Label _healthDetail;
        private readonly TextBox _values;
        private readonly Label _actionStatus;
        private readonly RichTextBox _steps;
        private readonly Button _saveNetwork;
        private readonly Button _pairDevice;
        private BridgeConfig _config = null!;
        private BridgeNetworkAssessment _assessment = null!;
        private int _deviceCount;

        public SetupGuideForm(string configPath)
        {
            _configPath = configPath;

            Text = "Wake PC setup & status";
            StartPosition = FormStartPosition.CenterScreen;
            Size = PreferredWindowSize;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            var outer = new TableLayoutPanel
            {
                ColumnCount = 1,
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
            };
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var content = new TableLayoutPanel
            {
                ColumnCount = 1,
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var tabs = new TabControl
            {
                Dock = DockStyle.Fill,
            };
            var statusTab = new TabPage("Status")
            {
                UseVisualStyleBackColor = true,
            };
            var setupTab = new TabPage("How to set up")
            {
                Padding = new Padding(16),
                UseVisualStyleBackColor = true,
            };
            tabs.TabPages.Add(statusTab);
            tabs.TabPages.Add(setupTab);

            var title = new Label
            {
                AutoSize = true,
                Font = new Font(Font.FontFamily, 16, FontStyle.Bold),
                Text = "Wake PC",
                Margin = new Padding(0, 0, 0, 8),
            };
            var intro = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                Text = "Wake needs Ethernet. Pairing enables Sleep PC.",
                Margin = new Padding(0, 0, 0, 14),
            };
            _healthSummary = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                Font = new Font(Font.FontFamily, 11, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 4),
            };
            _healthDetail = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 12),
            };
            _values = new TextBox
            {
                BackColor = SystemColors.Window,
                Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 9.5f),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                TabStop = true,
                WordWrap = true,
                Height = 92,
                Margin = new Padding(0, 0, 0, 8),
            };
            _actionStatus = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0),
            };
            _steps = new RichTextBox
            {
                BackColor = SystemColors.Window,
                BorderStyle = BorderStyle.None,
                DetectUrls = false,
                Dock = DockStyle.Fill,
                Font = new Font(Font.FontFamily, 10),
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                TabStop = true,
                Margin = new Padding(0),
            };

            content.Controls.Add(title);
            content.Controls.Add(intro);
            content.Controls.Add(_healthSummary);
            content.Controls.Add(_healthDetail);
            content.Controls.Add(_values);
            content.Controls.Add(_actionStatus);
            statusTab.Controls.Add(content);
            setupTab.Controls.Add(_steps);

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 12, 0, 0),
                WrapContents = true,
            };
            var close = new Button
            {
                AutoSize = true,
                DialogResult = DialogResult.OK,
                Text = "Close",
            };
            var refresh = new Button
            {
                AutoSize = true,
                Text = "Refresh",
            };
            refresh.Click += (_, _) => RefreshState();
            _pairDevice = new Button
            {
                AutoSize = true,
                Text = "Pair for Sleep…",
            };
            _pairDevice.Click += (_, _) => PairDevice();
            _saveNetwork = new Button
            {
                AutoSize = true,
                Text = "Save this PC…",
            };
            _saveNetwork.Click += (_, _) => SaveCurrentNetwork();
            var copy = new Button
            {
                AutoSize = true,
                Text = "Copy values",
            };
            copy.Click += (_, _) => CopySetupValues();
            buttons.Controls.Add(close);
            buttons.Controls.Add(refresh);
            buttons.Controls.Add(_pairDevice);
            buttons.Controls.Add(_saveNetwork);
            buttons.Controls.Add(copy);

            outer.Controls.Add(tabs, 0, 0);
            outer.Controls.Add(buttons, 0, 1);
            Controls.Add(outer);
            CancelButton = close;
            RefreshState();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Rectangle workingArea = Screen.FromControl(this).WorkingArea;
            MinimumSize = FitWindowToWorkingArea(
                workingArea, ScaleForDpi(new Size(480, 420), DeviceDpi), margin: 16).Size;
            Bounds = FitWindowToWorkingArea(
                workingArea,
                ScaleForDpi(PreferredWindowSize, DeviceDpi),
                margin: 16);
        }

        private void RefreshState()
        {
            BridgeCredentials credentials = BridgeCredentials.Load(_configPath);
            _config = credentials.Config;
            _deviceCount = credentials.DeviceCount;
            IReadOnlyList<BridgeNetworkAddress> addresses =
                BridgeNetworkDiagnostics.GetActiveIpv4();
            _assessment = Assess(_config, addresses);
            _healthSummary.Text = _assessment.Summary;
            _healthSummary.ForeColor = HealthColor(_assessment.Health);
            _healthDetail.Text = _assessment.Detail;
            _values.Text = FormatSetupValues(
                _config,
                _assessment.Current,
                _deviceCount);
            _steps.Text = FormatSteps(_assessment.Current);
            _pairDevice.Text = _deviceCount == 0
                ? "Pair for Sleep…"
                : "Pair another for Sleep…";
            _saveNetwork.Enabled = _assessment.Current is not null &&
                _assessment.Health != BridgeNetworkHealth.Healthy;
            _saveNetwork.Text = _assessment.Health == BridgeNetworkHealth.IpChanged
                ? "Use new address…"
                : _assessment.Health == BridgeNetworkHealth.Healthy
                    ? "This PC is saved"
                    : "Save this PC…";
        }

        private void SaveCurrentNetwork()
        {
            BridgeNetworkAddress? current = _assessment.Current;
            if (current is null)
            {
                return;
            }

            string message = _assessment.Health == BridgeNetworkHealth.IpChanged
                ? $"Use {current.Address} as this PC's new address?\n\nUpdate your clients and router too."
                : $"Save this PC using {current.MacAddress} and {current.Address}?";
            if (MessageBox.Show(
                this,
                message,
                "Save this PC?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }

            BridgeCredentials.SaveExpectedNetwork(
                _configPath,
                current.MacAddress,
                current.Address);
            _actionStatus.Text = "Saved.";
            RefreshState();
        }

        private void CopySetupValues()
        {
            try
            {
                Clipboard.SetText(_values.Text);
                _actionStatus.Text = "Values copied.";
            }
            catch (ExternalException)
            {
                _actionStatus.Text = "Windows could not copy the values. Select and copy them manually.";
            }
        }

        private void PairDevice()
        {
            try
            {
                BridgeCredentials credentials =
                    BridgeCredentials.EnsureActivePairingCode(_configPath);
                DateTimeOffset expires = DateTimeOffset
                    .FromUnixTimeSeconds(credentials.PairingExpiresUtc)
                    .ToLocalTime();
                MessageBox.Show(
                    this,
                    $"Pairing code: {credentials.PairingCode}\n\n" +
                    "Decky: Wake PC → Sleep PC → Pair for Sleep.\n" +
                    "Mac: run wake-pc pair.\n\n" +
                    $"This one-time code expires at {expires:t}.",
                    "Pair Wake PC",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                RefreshState();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                ConfigException or InvalidOperationException)
            {
                MessageBox.Show(
                    this,
                    "A pairing code could not be created.\n\n" +
                    BridgeLog.Redact(exception.Message),
                    "Wake PC Companion",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private static Color HealthColor(BridgeNetworkHealth health) => health switch
        {
            BridgeNetworkHealth.Healthy => Color.DarkGreen,
            BridgeNetworkHealth.NotConfigured => Color.DarkGoldenrod,
            BridgeNetworkHealth.IpChanged => Color.DarkOrange,
            _ => Color.DarkRed,
        };
    }
}
