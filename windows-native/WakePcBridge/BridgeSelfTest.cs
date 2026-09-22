using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WakePcBridge;

public static class BridgeSelfTest
{
    public static IReadOnlyList<string> RunPureTests()
    {
        var passed = new List<string>();
        Require(
            typeof(BridgeSelfTest).Assembly.EntryPoint?.IsDefined(typeof(STAThreadAttribute), false) == true,
            "The compiled executable entry point must preserve STAThread.");
        passed.Add("compiled STA entry point for clipboard access");
        byte[] key = Convert.FromHexString(
            "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        const long timestamp = 1784304000;
        const string nonce = "AQIDBAUGBwgJCgsMDQ4PEA";
        const string expected =
            "daf5287d35424fcadab946bd2fa473f9af70c836eef68064d51c72d43b2f0683";
        string actual = Convert.ToHexString(
            RequestAuthenticator.ComputeSignature(
                key,
                RequestAuthenticator.SleepPath,
                timestamp,
                nonce))
            .ToLowerInvariant();
        Require(actual == expected, "Published HMAC vector failed.");
        passed.Add("published HMAC vector");

        byte[] same = Convert.FromHexString(expected);
        byte[] different = same.ToArray();
        different[0] ^= 1;
        Require(CryptographicOperations.FixedTimeEquals(same, same), "Equal compare failed.");
        Require(
            !CryptographicOperations.FixedTimeEquals(same, different),
            "Unequal compare failed.");
        passed.Add("constant-time comparison");

        var auth = new RequestAuthenticator(key);
        Require(
            auth.Authenticate(
                RequestAuthenticator.SleepPath,
                timestamp.ToString(),
                nonce,
                expected,
                timestamp) ==
            AuthenticationResult.Accepted,
            "Valid request was rejected.");
        Require(
            auth.Authenticate(
                RequestAuthenticator.SleepPath,
                timestamp.ToString(),
                nonce,
                expected,
                timestamp) ==
            AuthenticationResult.Replay,
            "Replay was accepted.");
        Require(
            auth.Authenticate(
                RequestAuthenticator.SleepPath,
                (timestamp - 61).ToString(),
                nonce + "x",
                Convert.ToHexString(RequestAuthenticator.ComputeSignature(
                    key,
                    RequestAuthenticator.SleepPath,
                    timestamp - 61,
                    nonce + "x")).ToLowerInvariant(),
                timestamp) == AuthenticationResult.StaleTimestamp,
            "Stale timestamp was accepted.");
        Require(
            auth.Authenticate(
                RequestAuthenticator.SleepPath,
                timestamp.ToString(),
                "bad",
                expected,
                timestamp) ==
            AuthenticationResult.InvalidNonce,
            "Malformed nonce was accepted.");
        Require(auth.Count(timestamp + 121) == 0, "Nonce did not expire.");
        passed.Add("timestamp, nonce, replay, and expiry");

        var fullCache = new RequestAuthenticator(key);
        AuthenticationResult CheckCached(int index, long requestTime, long now)
        {
            string candidate = index.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
            string signature = Convert.ToHexString(RequestAuthenticator.ComputeSignature(
                key, RequestAuthenticator.StatusPath, requestTime, candidate)).ToLowerInvariant();
            return fullCache.Authenticate(RequestAuthenticator.StatusPath,
                requestTime.ToString(System.Globalization.CultureInfo.InvariantCulture),
                candidate, signature, now);
        }
        for (int index = 0; index < RequestAuthenticator.MaximumNonces; index++)
        {
            Require(CheckCached(index, timestamp, timestamp) == AuthenticationResult.Accepted,
                "Nonce cache rejected a request before reaching capacity.");
        }
        Require(CheckCached(RequestAuthenticator.MaximumNonces, timestamp, timestamp) ==
            AuthenticationResult.CacheFull, "A full nonce cache must fail closed.");
        Require(CheckCached(0, timestamp, timestamp) == AuthenticationResult.Replay,
            "Cache pressure evicted a live nonce and allowed replay.");
        Require(fullCache.Count(timestamp) == RequestAuthenticator.MaximumNonces,
            "Nonce cache exceeded its bound.");
        Require(CheckCached(RequestAuthenticator.MaximumNonces, timestamp + 121, timestamp + 121) ==
            AuthenticationResult.Accepted, "Expired nonce entries did not free capacity.");
        passed.Add("nonce cache saturation fails closed and recovers after expiry");

        string networkDetails = BridgeNetworkDiagnostics.FormatForDisplay(
            [new BridgeNetworkAddress(
                "Ethernet",
                "192.0.2.187",
                24,
                ["192.0.2.1"],
                true,
                "00:11:22:33:44:55",
                true)],
            47653);
        Require(
            networkDetails.Contains("192.0.2.187/24", StringComparison.Ordinal) &&
            networkDetails.Contains("192.0.2.1", StringComparison.Ordinal) &&
            networkDetails.Contains("192.0.2.187:47653", StringComparison.Ordinal) &&
            networkDetails.Contains("DHCP: Enabled", StringComparison.Ordinal),
            "Network diagnostic formatting omitted recovery details.");
        IReadOnlyList<BridgeNetworkAddress> detectedAddresses =
            BridgeNetworkDiagnostics.GetActiveIpv4();
        Require(
            detectedAddresses.Count <= 16 &&
            detectedAddresses.All(address =>
                System.Net.IPAddress.TryParse(address.Address, out _) &&
                address.PrefixLength is >= 0 and <= 32 &&
                address.Gateways.Count <= 4),
            "Active network diagnostic enumeration was invalid or unbounded.");
        passed.Add("network diagnostic formatting");

        var wiredAddress = new BridgeNetworkAddress(
            "Ethernet",
            "192.0.2.187",
            24,
            ["192.0.2.1"],
            true,
            "00:11:22:33:44:55",
            true);
        var wirelessAddress = new BridgeNetworkAddress(
            "Wi-Fi",
            "192.0.2.188",
            24,
            ["192.0.2.1"],
            true,
            "AA:BB:CC:DD:EE:FF",
            false);
        Require(
            BridgeSetupGuide.SelectWiredAddress(
                [wirelessAddress, wiredAddress]) == wiredAddress &&
            BridgeSetupGuide.SelectWiredAddress([wirelessAddress]) is null,
            "Setup guide accepted a non-wired adapter.");
        BridgeConfig unconfiguredNetwork = new BridgeConfig
        {
            Port = 47653,
            SleepDelayMs = 750,
        }.Validate();
        BridgeConfig configuredNetwork = unconfiguredNetwork.WithExpectedNetwork(
            wiredAddress.MacAddress,
            wiredAddress.Address);
        Require(
            BridgeSetupGuide.Assess(
                configuredNetwork,
                [wiredAddress]).Health == BridgeNetworkHealth.Healthy,
            "Matching saved wired setup was not healthy.");
        var changedAddress = wiredAddress with { Address = "192.0.2.199" };
        Require(
            BridgeSetupGuide.Assess(
                configuredNetwork,
                [changedAddress]).Health == BridgeNetworkHealth.IpChanged &&
            BridgeSetupGuide.Assess(
                configuredNetwork,
                [wirelessAddress]).Health ==
                BridgeNetworkHealth.AdapterUnavailable,
            "Saved wired setup changes were not detected.");
        string setupValues = BridgeSetupGuide.FormatSetupValues(
            configuredNetwork,
            wiredAddress,
            2);
        string setupSteps = BridgeSetupGuide.FormatSteps(wiredAddress);
        Require(
            setupValues.Contains("00:11:22:33:44:55", StringComparison.Ordinal) &&
            setupValues.Contains("192.0.2.187", StringComparison.Ordinal) &&
            setupValues.Contains("Enabled for 2 devices", StringComparison.Ordinal) &&
            setupSteps.Contains("Turn on Wake on LAN", StringComparison.Ordinal) &&
            setupSteps.Contains("address-reservation settings", StringComparison.Ordinal) &&
            setupSteps.Contains("Wake PC → Settings", StringComparison.Ordinal) &&
            setupSteps.Contains("Save changes", StringComparison.Ordinal) &&
            setupSteps.Contains("wake-pc setup", StringComparison.Ordinal) &&
            setupSteps.Contains("wake-pc pair", StringComparison.Ordinal) &&
            setupSteps.Contains("Pair for Sleep", StringComparison.Ordinal),
            "Setup guide omitted required first-run instructions.");
        passed.Add("interactive first-run setup guide");

        Rectangle compactBounds = BridgeSetupGuide.FitWindowToWorkingArea(
            new Rectangle(-640, 20, 640, 480),
            BridgeSetupGuide.ScaleForDpi(new Size(640, 560), 96),
            16);
        Require(
            compactBounds == new Rectangle(-624, 36, 608, 448),
            "Setup guide did not fit inside a compact or offset monitor.");
        Rectangle normalBounds = BridgeSetupGuide.FitWindowToWorkingArea(
            new Rectangle(0, 0, 1920, 1040),
            BridgeSetupGuide.ScaleForDpi(new Size(640, 560), 96),
            16);
        Require(
            normalBounds == new Rectangle(640, 240, 640, 560) &&
            BridgeSetupGuide.ScaleForDpi(new Size(640, 560), 144) ==
                new Size(960, 840),
            "Setup guide did not center at its preferred size.");
        passed.Add("setup guide screen fitting");

        string secret = Convert.ToHexString(key).ToLowerInvariant();
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"WakePcBridge-selftest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string configPath = Path.GetFullPath(
                Path.Combine(temporaryDirectory, "config.json"));
            string codeHash = Convert.ToHexString(
                SHA256.HashData(Encoding.ASCII.GetBytes("123456"))).ToLowerInvariant();
            var config = new BridgeConfig
            {
                Port = 47653,
                SecretHex = secret,
                SleepDelayMs = 750,
                ExpectedWiredMac = wiredAddress.MacAddress,
                ExpectedIpv4Address = wiredAddress.Address,
                PairingCodeHash = codeHash,
                PairingExpiresUtc = timestamp + 1800,
            };
            File.WriteAllText(
                configPath,
                JsonSerializer.Serialize(config, new JsonSerializerOptions
                {
                    WriteIndented = true,
                }));
            BridgeConfig loaded = BridgeConfig.Load(configPath);
            Require(loaded.SecretHex == secret, "Valid config failed.");
            string logPath = Path.Combine(temporaryDirectory, "sleep-bridge.log");
            File.WriteAllText(
                logPath,
                "Accepted authenticated sleep request from 192.0.2.1.");
            BridgeCredentials credentials = BridgeCredentials.LoadOrMigrate(
                configPath,
                logPath);
            string protectedJson = File.ReadAllText(configPath);
            BridgeDeviceCredential? migratedDevice = credentials.FindDevice(
                BridgeCredentials.PrimaryDeviceId);
            Require(
                migratedDevice?.SecretHex == secret &&
                credentials.DeviceCount == 1 &&
                credentials.Config.SecretHex.Length == 0 &&
                credentials.Config.ProtectedCredentials.Length > 0 &&
                credentials.Config.ExpectedWiredMac == wiredAddress.MacAddress &&
                credentials.Config.ExpectedIpv4Address == wiredAddress.Address &&
                credentials.PairedOnce &&
                !protectedJson.Contains(secret, StringComparison.Ordinal) &&
                !protectedJson.Contains(codeHash, StringComparison.Ordinal) &&
                !protectedJson.Contains("123456", StringComparison.Ordinal),
                "Legacy credential migration failed: " +
                $"legacy={credentials.Config.SecretHex.Length}, " +
                $"protected={credentials.Config.ProtectedCredentials.Length}, " +
                $"paired={credentials.PairedOnce}.");
            passed.Add("plaintext credential becomes primary device");

            var pairing = new PairingState(credentials, configPath);
            for (int attempt = 0; attempt < 9; attempt++)
            {
                Require(
                    pairing.TryPair("000000", timestamp, null, out _) ==
                    PairingResult.Incorrect,
                    "Pairing attempt did not fail.");
            }
            Require(
                pairing.TryPair("000000", timestamp, null, out _) ==
                    PairingResult.Locked,
                "Pairing did not lock after ten failures.");
            Require(
                pairing.TryPair("123456", timestamp, null, out _) ==
                    PairingResult.Locked,
                "Locked pairing was bypassed.");
            passed.Add("pairing format, hash, expiry, and lockout");

            BridgeCredentials rotated =
                BridgeCredentials.EnsureActivePairingCode(configPath);
            Require(
                pairing.TryPair(
                    rotated.PairingCode,
                    timestamp,
                    null,
                    out BridgeDeviceCredential? rotatedDevice) ==
                    PairingResult.Paired &&
                rotatedDevice is not null &&
                rotatedDevice.Id != BridgeCredentials.PrimaryDeviceId &&
                rotatedDevice.SecretHex != secret,
                "Rotating a pairing code did not reset its attempt lockout.");
            passed.Add("pairing rotation resets lockout");

            File.WriteAllText(
                configPath,
                JsonSerializer.Serialize(config));
            credentials = BridgeCredentials.LoadOrMigrate(configPath);
            pairing = new PairingState(credentials, configPath);
            Require(
                pairing.TryPair(
                    "123456",
                    timestamp,
                    null,
                    out BridgeDeviceCredential? pairedDevice) ==
                    PairingResult.Paired &&
                pairedDevice is not null &&
                pairedDevice.Id == BridgeCredentials.PrimaryDeviceId &&
                pairedDevice.SecretHex != secret,
                "Pairing success failed.");
            Require(
                pairing.TryPair("123456", timestamp, null, out _) ==
                    PairingResult.Gone,
                "Pairing succeeded more than once.");
            Require(
                BridgeCredentials.Load(configPath).PairingCodeHash.Length == 0 &&
                BridgeCredentials.Load(configPath).PairedOnce,
                "Pairing state was not atomically disabled.");
            passed.Add("one-success pairing disablement");

            BridgeCredentials additionalPairing =
                BridgeCredentials.EnsureActivePairingCode(configPath);
            var additionalPairingState = new PairingState(
                additionalPairing,
                configPath);
            BridgeDeviceCredential? additionalDevice = null;
            Require(
                additionalPairing.PairedOnce &&
                additionalPairingState.TryPair(
                    additionalPairing.PairingCode,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    null,
                    out additionalDevice) ==
                    PairingResult.Paired &&
                additionalDevice is not null &&
                additionalDevice.Id != pairedDevice!.Id &&
                additionalDevice.SecretHex != pairedDevice.SecretHex &&
                BridgeCredentials.Load(configPath)
                    .FindDevice(pairedDevice.Id)?.SecretHex ==
                    pairedDevice.SecretHex,
                "Additional pairing did not create an independent credential.");
            passed.Add("additional clients receive independent credentials");
            BridgeDeviceCredential firstPaired = pairedDevice
                ?? throw new InvalidOperationException(
                    "Primary device test credential is unavailable.");
            BridgeDeviceCredential secondPaired = additionalDevice
                ?? throw new InvalidOperationException(
                    "Additional device test credential is unavailable.");

            BridgeCredentials replacementPairing =
                BridgeCredentials.EnsureActivePairingCode(configPath);
            var replacementPairingState = new PairingState(
                replacementPairing,
                configPath);
            Require(
                replacementPairingState.TryPair(
                    replacementPairing.PairingCode,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    firstPaired.Id,
                    out BridgeDeviceCredential? replacementDevice) ==
                    PairingResult.Paired &&
                replacementDevice is not null &&
                replacementDevice.Id == firstPaired.Id &&
                replacementDevice.SecretHex != firstPaired.SecretHex &&
                BridgeCredentials.Load(configPath).DeviceCount == 2 &&
                BridgeCredentials.Load(configPath)
                    .FindDevice(secondPaired.Id)?.SecretHex ==
                    secondPaired.SecretHex,
                "Re-pairing did not rotate only the requesting device.");
            passed.Add("same-client pairing replaces only its credential");

            string protectedLegacyPath = Path.GetFullPath(
                Path.Combine(temporaryDirectory, "protected-legacy.json"));
            byte[] legacyPlaintext = JsonSerializer.SerializeToUtf8Bytes(new
            {
                secret_hex = secret,
                pairing_code_hash = string.Empty,
                pairing_expires_utc = 0,
                pairing_code = string.Empty,
                paired_once = true,
            });
            byte[] protectedLegacy = ProtectedData.Protect(
                legacyPlaintext,
                Encoding.UTF8.GetBytes(
                    "WakePcSleepBridge/credentials/v1"),
                DataProtectionScope.CurrentUser);
            CryptographicOperations.ZeroMemory(legacyPlaintext);
            File.WriteAllText(
                protectedLegacyPath,
                JsonSerializer.Serialize(new BridgeConfig
                {
                    Port = 47653,
                    ProtectedCredentials = Convert.ToBase64String(protectedLegacy),
                    SleepDelayMs = 750,
                }));
            BridgeCredentials protectedMigration =
                BridgeCredentials.LoadOrMigrate(protectedLegacyPath);
            Require(
                protectedMigration.DeviceCount == 1 &&
                protectedMigration.FindDevice(
                    BridgeCredentials.PrimaryDeviceId)?.SecretHex == secret,
                "Protected shared credential did not become the primary device.");
            passed.Add("protected credential becomes primary device");

            BridgeCredentials completed = BridgeCredentials.Load(configPath);
            byte[] tamperedBytes = Convert.FromBase64String(
                completed.Config.ProtectedCredentials);
            tamperedBytes[^1] ^= 1;
            var tampered = new BridgeConfig
            {
                Port = completed.Config.Port,
                ProtectedCredentials = Convert.ToBase64String(tamperedBytes),
                SleepDelayMs = completed.Config.SleepDelayMs,
                PairingCodeHash = string.Empty,
            };
            File.WriteAllText(configPath, JsonSerializer.Serialize(tampered));
            RequireThrows<ConfigException>(
                () => BridgeCredentials.Load(configPath),
                "Tampered pairing state was accepted.");
            passed.Add("protected pairing-state integrity");

            File.WriteAllText(
                configPath,
                """{"port":47653,"secret_hex":"bad","sleep_delay_ms":750,"pairing_code_hash":"","pairing_expires_utc":0}""");
            RequireThrows<ConfigException>(
                () => BridgeConfig.Load(configPath),
                "Invalid config was accepted.");
            passed.Add("strict configuration validation");

            string generatedPath = Path.GetFullPath(
                Path.Combine(temporaryDirectory, "generated.json"));
            BridgeConfig generated = BridgeConfig.LoadOrCreate(
                generatedPath,
                out bool created);
            BridgeCredentials generatedCredentials =
                BridgeCredentials.LoadOrMigrate(generatedPath);
            Require(
                created &&
                generated.SecretHex.Length == 0 &&
                generated.ProtectedCredentials.Length == 0 &&
                generatedCredentials.PairingCode.Length == 6 &&
                generatedCredentials.DeviceCount == 0 &&
                generatedCredentials.Config.ProtectedCredentials.Length > 0,
                "First-run configuration generation failed.");
            BridgeConfig existing = BridgeConfig.LoadOrCreate(
                generatedPath,
                out bool createdAgain);
            Require(
                !createdAgain &&
                existing.ProtectedCredentials ==
                    generatedCredentials.Config.ProtectedCredentials,
                "Existing first-run configuration was replaced.");
            passed.Add("first-run protected pairing generation");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }

        string sensitive =
            $"secret_hex={secret} nonce=abcdefghijklmnop signature={expected}";
        string redacted = BridgeLog.Redact(sensitive);
        Require(!redacted.Contains(secret, StringComparison.Ordinal), "Secret leaked.");
        Require(!redacted.Contains(nonce, StringComparison.Ordinal), "Nonce leaked.");
        Require(!redacted.Contains(expected, StringComparison.Ordinal), "Signature leaked.");
        passed.Add("log redaction");

        Require(
            BridgePauseState.IsPausedValueForSession(7, 7),
            "Current-session pause marker was ignored.");
        Require(
            !BridgePauseState.IsPausedValueForSession(7, 8) &&
            !BridgePauseState.IsPausedValueForSession("7", 7),
            "Stale or malformed pause marker was accepted.");
        passed.Add("current-session pause matching");

        using Stream? icon = typeof(BridgeSelfTest).Assembly
            .GetManifestResourceStream("WakePcBridge.ico");
        Require(icon is not null && icon.Length > 0, "Application icon is missing.");
        passed.Add("embedded application icon");
        return passed;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
