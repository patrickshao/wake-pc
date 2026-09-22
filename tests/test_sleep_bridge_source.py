import json
import re
import unittest
from pathlib import Path


class SleepBridgeSourceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.runtime_dir = Path("windows-native/WakePcBridge")
        self.sources = "\n".join(
            path.read_text(encoding="utf-8")
            for path in sorted(self.runtime_dir.glob("*.cs"))
        )
        self.project = (self.runtime_dir / "WakePcBridge.csproj").read_text(
            encoding="utf-8"
        )
        self.program = (self.runtime_dir / "Program.cs").read_text(encoding="utf-8")
        self.tray = (self.runtime_dir / "BridgeTrayIcon.cs").read_text(
            encoding="utf-8"
        )
        self.installer = Path("windows/WakePcSleepBridge.iss").read_text(
            encoding="utf-8"
        )
        self.build = Path("windows-native/scripts/build.ps1").read_text(
            encoding="utf-8"
        )
        self.packaging = Path("scripts/package.py").read_text(encoding="utf-8")
        self.package_validator = Path("scripts/validate_package.py").read_text(
            encoding="utf-8"
        )
        self.bootstrap = Path(
            "windows-native/scripts/bootstrap-inno.ps1"
        ).read_text(encoding="utf-8")
        self.workflow = Path(
            ".github/workflows/windows-sleep-bridge.yml"
        ).read_text(encoding="utf-8")
        self.icon_builder = Path(
            "windows-native/scripts/build-icon.py"
        ).read_text(encoding="utf-8")

    def test_runtime_is_hidden_native_net10_single_file(self) -> None:
        for marker in (
            "<OutputType>WinExe</OutputType>",
            "<TargetFramework>net10.0-windows</TargetFramework>",
            "<RuntimeIdentifier>win-x64</RuntimeIdentifier>",
            "<SelfContained>true</SelfContained>",
            "<PublishSingleFile>true</PublishSingleFile>",
            "<PublishTrimmed>false</PublishTrimmed>",
            "<TreatWarningsAsErrors>true</TreatWarningsAsErrors>",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.project)
        self.assertIn("NotifyIcon", self.sources)
        self.assertIn("Wake PC Companion {BridgeVersion.Current} — Running", self.sources)
        self.assertIn("Wake PC Companion · v{BridgeVersion.Current}", self.sources)
        self.assertIn(
            "<ApplicationIcon>Assets\\WakePcBridge.ico</ApplicationIcon>",
            self.project,
        )

    def test_application_icon_has_reproducible_tray_sizes(self) -> None:
        png = Path("windows-native/WakePcBridge/Assets/WakePcBridge.png").read_bytes()
        ico = Path("windows-native/WakePcBridge/Assets/WakePcBridge.ico").read_bytes()
        self.assertTrue(png.startswith(b"\x89PNG\r\n\x1a\n"))
        self.assertTrue(ico.startswith(b"\x00\x00\x01\x00"))
        self.assertEqual(int.from_bytes(ico[4:6], "little"), 9)
        for marker in (
            "ICON_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)",
            'Image.new("RGBA"',
            "Image.Resampling.LANCZOS",
            "bitmap_format=\"png\"",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.icon_builder)

    def test_supervisor_recovers_crashes_and_honors_exit(self) -> None:
        for marker in (
            '"start"',
            '"supervise"',
            "BridgeSupervisor.RunAsync",
            "WakePcSleepBridge.Supervisor",
            "WakePcSleepBridge.Runtime",
            "restarting",
            "BridgePauseState.IsPausedForCurrentSession()",
            "BridgePauseState.TryPauseForCurrentSession",
            "BridgePauseState.ClearForCurrentSession",
            "Exit until next sign-in…",
            "RegistryOptions.Volatile",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.sources)

    def test_runtime_has_fixed_protocol_and_authentication(self) -> None:
        for marker in (
            'path == "/v1/pair"',
            'SleepPath = "/v1/sleep"',
            'StatusPath = "/v1/status"',
            "HandleStatusAsync",
            "version: BridgeVersion.Current",
            "request.ContentLength64 != 0",
            'request.Headers["Content-Length"] != "0"',
            "request.Url?.Query",
            "CryptographicOperations.FixedTimeEquals",
            "MaximumNonces = 4096",
            "NonceLifetimeSeconds = 120",
            "Application.SetSuspendState(",
            "PowerState.Suspend",
            "force: false",
            "disableWakeEvent: false",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.sources)
        for dangerous in (
            "Invoke-Expression",
            "DownloadString",
            "DownloadFile",
            "LockWorkStation",
        ):
            with self.subTest(dangerous=dangerous):
                self.assertNotIn(dangerous, self.sources)

    def test_pairing_is_created_and_visible_from_the_tray(self) -> None:
        for marker in (
            "LoadOrCreate",
            "RandomNumberGenerator.GetBytes(32)",
            "RandomNumberGenerator",
            "pairing_code",
            "EnsureActivePairingCode",
            "Pair for Sleep…",
            "Pair another for Sleep…",
            "Advanced diagnostics…",
            "Setup && status…",
            "Advanced",
            "How to set up",
            "RichTextBox",
            "HighDpiMode.PerMonitorV2",
            "FitWindowToWorkingArea",
            "ScaleForDpi",
            '"setup-guide"',
            "Turn on Wake on LAN",
            "address-reservation settings",
            "Copy values",
            "Pair for Sleep",
            "Network: Details match",
            "Wake: Address changed",
            "Connect this PC by Ethernet",
            "expected_wired_mac",
            "expected_ipv4_address",
            "Use new address…",
            "SaveExpectedNetwork",
            "network_setup = new",
            "virtualMarkers",
            "Sleep: {deviceCount} devices paired",
            "Sleep PC:     {pairing}",
            "credentials_protected",
            "ProtectedData.Protect",
            "DataProtectionScope.CurrentUser",
            "plaintext credential becomes primary device",
            "protected credential becomes primary device",
            "additional clients receive independent credentials",
            "same-client pairing replaces only its credential",
            "X-Wake-PC-Device-ID",
            "X-Wake-PC-Replace-Device-ID",
            "X-Wake-PC-Protocol",
            "MaximumDevices = 64",
            "Pairing code:",
            "TryCompletePairing",
            "BridgeNetworkDiagnostics.GetActiveIpv4",
            "prefix_length",
            "default_gateways",
            "sleep_bridge_target",
            "network diagnostic formatting",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.sources)
        for duplicate_tray_action in (
            "PairDevice()",
            "Technical network details…",
            "Open support folder",
            "Create support report",
            "DropDownItems.Add",
        ):
            with self.subTest(duplicate_tray_action=duplicate_tray_action):
                self.assertNotIn(duplicate_tray_action, self.tray)
        self.assertNotIn("public required string SecretHex", self.sources)
        for startup_notification in (
            "ShowStartupBalloon",
            "ShowBalloonTip",
            "BalloonTipText",
        ):
            with self.subTest(startup_notification=startup_notification):
                self.assertNotIn(startup_notification, self.sources)
        for exposed_identifier in ("KeyId", "key_id", "key ID"):
            with self.subTest(exposed_identifier=exposed_identifier):
                self.assertNotIn(exposed_identifier, self.sources)
        diagnostic_report = self.program.split("var report = new", maxsplit=1)[1]
        self.assertNotIn("MacAddress", diagnostic_report)
        self.assertNotIn("ExpectedWiredMac", diagnostic_report)

    def test_system_registration_is_bounded_and_idempotent(self) -> None:
        for marker in (
            '"configure-system"',
            '"unconfigure-system"',
            '"netsh.exe"',
            '"urlacl"',
            '"sddl=D:(A;;GX;;;BU)"',
            '"remoteip=LocalSubnet"',
            '"profile=private,public"',
            "VerifyNetworkRegistration",
            "DeleteNetworkRegistration",
            "RequireAdministrator",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.sources)
        self.assertNotIn('"/Create"', self.sources)
        self.assertNotIn("Register-ScheduledTask", self.sources)
        self.assertNotIn("powershell.exe", self.sources.lower())

    def test_inno_installer_owns_standard_windows_lifecycle(self) -> None:
        for marker in (
            "AppId={{EE345E29-5C07-44F9-A41B-5826C96C6356}",
            "PrivilegesRequired=admin",
            "ArchitecturesAllowed=x64compatible",
            "DefaultDirName={autopf}\\Wake PC Sleep Bridge",
            '#define AppName "Wake PC Companion"',
            "Permissions: users-modify",
            "Software\\Microsoft\\Windows\\CurrentVersion\\Run",
            'ValueName: "WakePcSleepBridge"',
            "AfterInstall: ConfigureSystem",
            "unconfigure-system",
            'RunOnceId: "UnconfigureWakePcSleepBridge"',
            "CloseApplications=force",
            "RestartApplications=no",
            "RestartIfNeededByRun=no",
            "DisableDirPage=yes",
            "Optional Sleep PC and setup tools",
            "CreateOutputMsgPage",
            "/TRUSTEDNETWORK=1",
            'DestName: "LICENSE.txt"',
            "SignTool=wakepc",
            "SignedUninstaller=yes",
            "SignedUninstaller=no",
            "Wake PC Setup & Status",
            "FinishedLabel=Wake PC Companion is installed.",
            "StopInstalledBridgeForSetup",
            "NextButtonClick",
            "taskkill.exe",
            "/F /T /IM {#AppExeName}",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.installer)
        self.assertNotIn("powershell", self.installer.lower())
        self.assertNotIn("scheduledtask", self.installer.lower())
        self.assertNotIn("CreateInputOptionPage", self.installer)
        self.assertNotIn("TrustedNetworkPage.Values", self.installer)
        self.assertNotIn("LicenseFile=", self.installer)
        self.assertNotIn("Private Windows network profiles", self.installer)

    def test_build_outputs_versioned_setup_and_supports_signing(self) -> None:
        for marker in (
            "Inno Setup 7.0.2",
            "INNO_SETUP_COMPILER",
            "windows-native\\version.txt",
            "WakePcSleepBridge.iss",
            "wake-pc-sleep-bridge-v$version-setup",
            "SignToolCommand",
            "-p:Version=$version",
            "/Swakepc=$SignToolCommand",
            "Setup.exe SHA-256",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.build)

    def test_plugin_and_companion_versions_are_independent(self) -> None:
        plugin_version = json.loads(Path("package.json").read_text(encoding="utf-8"))[
            "version"
        ]
        companion_version = Path("windows-native/version.txt").read_text(
            encoding="utf-8"
        ).strip()
        self.assertRegex(plugin_version, r"^\d+\.\d+\.\d+$")
        self.assertRegex(companion_version, r"^\d+\.\d+\.\d+$")
        for marker in (
            "plugin_version =",
            "bridge_version =",
            'windows-native/version.txt',
            'wake-pc-v{plugin_version}.zip',
            'wake-pc-sleep-bridge-v{bridge_version}-setup.exe',
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.packaging)
        self.assertIn("validate_bridge(bridge_archive, bridge_version)", self.package_validator)
        self.assertNotIn(
            'validate_bridge(bridge_archive, metadata["version"])',
            self.package_validator,
        )

    def test_compiler_bootstrap_and_ci_are_pinned(self) -> None:
        for marker in (
            "innosetup-7.0.2-x64.exe",
            "5ad54ca3def786f8f4212552e54cc6d8d61329e2d24a1cfee0571d42c2684ff1",
            "Get-AuthenticodeSignature",
            "CN=Pyrsys B.V.",
            "/PORTABLE=1",
            "wake-pc-inno-{0}.exe",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.bootstrap)
        self.assertNotIn("GetTempFileName", self.bootstrap)
        uses = re.findall(r"^\s*uses:\s*(\S+)", self.workflow, re.MULTILINE)
        self.assertGreaterEqual(len(uses), 3)
        for action in uses:
            with self.subTest(action=action):
                self.assertRegex(action, r"^[^@]+@[0-9a-f]{40}$")
        self.assertIn("windows-latest", self.workflow)
        self.assertIn("global-json-file: global.json", self.workflow)
        sdk = json.loads(Path("global.json").read_text())["sdk"]
        self.assertEqual("10.0.302", sdk["version"])
        self.assertEqual("disable", sdk["rollForward"])
        self.assertIn("wake-pc-sleep-bridge-windows-unsigned", self.workflow)

    def test_powershell_distribution_path_was_removed(self) -> None:
        self.assertEqual([], list(Path("windows").glob("*.ps1")))
        self.assertEqual([], list(Path("windows").glob("*.cmd")))


if __name__ == "__main__":
    unittest.main()
