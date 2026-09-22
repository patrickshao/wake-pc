using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WakePcBridge;

public sealed class BridgeDeviceCredential
{
    internal BridgeDeviceCredential(string id, string secretHex)
    {
        Id = id;
        SecretHex = secretHex;
        SecretBytes = Convert.FromHexString(secretHex);
    }

    public string Id { get; }
    public string SecretHex { get; }
    public byte[] SecretBytes { get; }
}

public sealed class BridgeCredentials
{
    public const string PrimaryDeviceId = "primary";
    public const int MaximumDevices = 64;

    private static readonly object Gate = new();
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("WakePcSleepBridge/credentials/v1");
    private static readonly Regex Hex64 = new(
        "^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex DeviceIdPattern = new(
        "^(?:primary|[0-9a-f]{32})$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex PairingCodePattern = new(
        "^[0-9]{6}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly CredentialPayload _payload;

    private BridgeCredentials(BridgeConfig config, CredentialPayload payload)
    {
        Config = config;
        _payload = payload;
        Devices = payload.Devices
            .Select(device => new BridgeDeviceCredential(
                device.Id,
                device.SecretHex))
            .ToArray();
        PairingCodeHash = payload.PairingCodeHash;
        PairingExpiresUtc = payload.PairingExpiresUtc;
        PairingCode = payload.PairingCode;
    }

    public BridgeConfig Config { get; }
    public IReadOnlyList<BridgeDeviceCredential> Devices { get; }
    public int DeviceCount => Devices.Count;
    public bool PairedOnce => Devices.Count > 0;
    public string PairingCodeHash { get; }
    public long PairingExpiresUtc { get; }
    public string PairingCode { get; }

    public BridgeDeviceCredential? FindDevice(string? deviceId) =>
        IsValidDeviceId(deviceId)
            ? Devices.FirstOrDefault(device =>
                string.Equals(device.Id, deviceId, StringComparison.Ordinal))
            : null;

    public static bool IsValidDeviceId(string? deviceId) =>
        deviceId is not null && DeviceIdPattern.IsMatch(deviceId);

    public static BridgeCredentials LoadOrMigrate(
        string configPath,
        string? logPath = null)
    {
        lock (Gate)
        {
            BridgeConfig config = BridgeConfig.Load(configPath);
            if (config.ProtectedCredentials.Length != 0)
            {
                return FromProtectedConfig(config, configPath);
            }

            if (config.SecretHex.Length == 0)
            {
                CredentialPayload created = CreateFreshPayload();
                BridgeConfig createdConfig = ProtectConfig(config, created);
                BridgeConfig.SaveAtomically(configPath, createdConfig);
                return new BridgeCredentials(createdConfig, created);
            }

            bool paired = config.PairingCodeHash.Length == 0 ||
                LogShowsAuthenticatedUse(logPath);
            var payload = new CredentialPayload
            {
                Devices = paired
                    ? [new DevicePayload
                    {
                        Id = PrimaryDeviceId,
                        SecretHex = config.SecretHex,
                    }]
                    : [],
                PairingCodeHash = config.PairingCodeHash,
                PairingExpiresUtc = config.PairingExpiresUtc,
                PairingCode = config.PairingCode,
            }.Validate();
            BridgeConfig migratedConfig = ProtectConfig(config, payload);
            BridgeConfig.SaveAtomically(configPath, migratedConfig);
            return new BridgeCredentials(migratedConfig, payload);
        }
    }

    public static BridgeCredentials Load(string configPath)
    {
        lock (Gate)
        {
            return LoadWithoutLock(configPath);
        }
    }

    public static BridgeCredentials EnsureActivePairingCode(string configPath)
    {
        lock (Gate)
        {
            BridgeCredentials current = LoadWithoutLock(configPath);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (current.PairingCode.Length == 6 &&
                current.PairingExpiresUtc > now)
            {
                return current;
            }

            string pairingCode = RandomNumberGenerator
                .GetInt32(100_000, 1_000_000)
                .ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            var payload = new CredentialPayload
            {
                Devices = current._payload.Devices,
                PairingCodeHash = HashPairingCode(pairingCode),
                PairingExpiresUtc = DateTimeOffset.UtcNow
                    .AddMinutes(30)
                    .ToUnixTimeSeconds(),
                PairingCode = pairingCode,
            }.Validate();
            BridgeConfig updated = ProtectConfig(current.Config, payload);
            BridgeConfig.SaveAtomically(configPath, updated);
            return new BridgeCredentials(updated, payload);
        }
    }

    public static BridgeConfig SaveExpectedNetwork(
        string configPath,
        string macAddress,
        string ipv4Address)
    {
        lock (Gate)
        {
            BridgeConfig updated = BridgeConfig.Load(configPath)
                .WithExpectedNetwork(macAddress, ipv4Address);
            BridgeConfig.SaveAtomically(configPath, updated);
            return updated;
        }
    }

    public static bool TryCompletePairing(
        string configPath,
        string expectedPairingHash,
        string? replaceDeviceId,
        out BridgeCredentials credentials,
        out BridgeDeviceCredential? device)
    {
        lock (Gate)
        {
            BridgeCredentials current = LoadWithoutLock(configPath);
            if (!string.Equals(
                current.PairingCodeHash,
                expectedPairingHash,
                StringComparison.Ordinal))
            {
                credentials = current;
                device = null;
                return false;
            }

            BridgeDeviceCredential? replaced = current.FindDevice(replaceDeviceId);
            if (replaced is null && current.DeviceCount >= MaximumDevices)
            {
                throw new ConfigException(
                    "The maximum number of paired devices has been reached.");
            }

            string deviceId = replaced?.Id ?? (current.DeviceCount == 0
                ? PrimaryDeviceId
                : CreateUniqueDeviceId(current.Devices));
            var created = new DevicePayload
            {
                Id = deviceId,
                SecretHex = Convert.ToHexString(
                    RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            };
            var payload = new CredentialPayload
            {
                Devices = replaced is null
                    ? [.. current._payload.Devices, created]
                    : current._payload.Devices
                        .Select(existing =>
                            string.Equals(
                                existing.Id,
                                replaced.Id,
                                StringComparison.Ordinal)
                                ? created
                                : existing)
                        .ToArray(),
                PairingCodeHash = string.Empty,
                PairingExpiresUtc = 0,
                PairingCode = string.Empty,
            }.Validate();
            BridgeConfig updated = ProtectConfig(current.Config, payload);
            BridgeConfig.SaveAtomically(configPath, updated);
            credentials = new BridgeCredentials(updated, payload);
            device = credentials.FindDevice(deviceId)
                ?? throw new ConfigException(
                    "The paired device credential was not persisted.");
            return true;
        }
    }

    private static BridgeCredentials LoadWithoutLock(string configPath)
    {
        BridgeConfig config = BridgeConfig.Load(configPath);
        if (config.SecretHex.Length != 0)
        {
            throw new ConfigException(
                "The legacy bridge credentials have not been migrated yet.");
        }

        return FromProtectedConfig(config, configPath);
    }

    private static BridgeCredentials FromProtectedConfig(
        BridgeConfig config,
        string configPath)
    {
        byte[] protectedBytes = Convert.FromBase64String(
            config.ProtectedCredentials);
        byte[] plaintext;
        try
        {
            plaintext = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            throw new ConfigException(
                "The bridge credentials belong to a different Windows user or are damaged.",
                exception);
        }

        try
        {
            CredentialPayload payload;
            bool migrated;
            using (JsonDocument document = JsonDocument.Parse(plaintext))
            {
                migrated = !document.RootElement.TryGetProperty("devices", out _);
            }

            if (migrated)
            {
                LegacyCredentialPayload? legacy = JsonSerializer
                    .Deserialize<LegacyCredentialPayload>(plaintext, JsonOptions);
                payload = ConvertLegacyPayload(
                    legacy?.Validate()
                        ?? throw new ConfigException(
                            "The protected bridge credentials are empty."));
            }
            else
            {
                payload = JsonSerializer
                    .Deserialize<CredentialPayload>(plaintext, JsonOptions)
                    ?.Validate()
                    ?? throw new ConfigException(
                        "The protected bridge credentials are empty.");
            }

            if (migrated)
            {
                BridgeConfig updated = ProtectConfig(config, payload);
                BridgeConfig.SaveAtomically(configPath, updated);
                config = updated;
            }

            return new BridgeCredentials(config, payload);
        }
        catch (JsonException exception)
        {
            throw new ConfigException(
                "The protected bridge credentials are damaged.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static CredentialPayload ConvertLegacyPayload(
        LegacyCredentialPayload legacy) => new CredentialPayload
        {
            Devices = legacy.PairedOnce
                ? [new DevicePayload
                {
                    Id = PrimaryDeviceId,
                    SecretHex = legacy.SecretHex,
                }]
                : [],
            PairingCodeHash = legacy.PairingCodeHash,
            PairingExpiresUtc = legacy.PairingExpiresUtc,
            PairingCode = legacy.PairingCode,
        }.Validate();

    private static CredentialPayload CreateFreshPayload()
    {
        string pairingCode = RandomNumberGenerator
            .GetInt32(100_000, 1_000_000)
            .ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        return new CredentialPayload
        {
            Devices = [],
            PairingCodeHash = HashPairingCode(pairingCode),
            PairingExpiresUtc = DateTimeOffset.UtcNow
                .AddMinutes(30)
                .ToUnixTimeSeconds(),
            PairingCode = pairingCode,
        }.Validate();
    }

    private static string HashPairingCode(string pairingCode) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.ASCII.GetBytes(pairingCode)))
            .ToLowerInvariant();

    private static string CreateUniqueDeviceId(
        IReadOnlyList<BridgeDeviceCredential> existing)
    {
        while (true)
        {
            string candidate = Guid.NewGuid().ToString("N");
            if (!existing.Any(device =>
                string.Equals(device.Id, candidate, StringComparison.Ordinal)))
            {
                return candidate;
            }
        }
    }

    private static BridgeConfig ProtectConfig(
        BridgeConfig source,
        CredentialPayload payload)
    {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        try
        {
            string protectedCredentials = Convert.ToBase64String(
                ProtectedData.Protect(
                    plaintext,
                    Entropy,
                    DataProtectionScope.CurrentUser));
            return new BridgeConfig
            {
                Port = source.Port,
                ProtectedCredentials = protectedCredentials,
                SleepDelayMs = source.SleepDelayMs,
                ExpectedWiredMac = source.ExpectedWiredMac,
                ExpectedIpv4Address = source.ExpectedIpv4Address,
                PairingCodeHash = string.Empty,
                PairingExpiresUtc = 0,
                PairingCode = string.Empty,
            }.Validate();
        }
        catch (CryptographicException exception)
        {
            throw new ConfigException(
                "The bridge credentials could not be protected for the current Windows user.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static bool LogShowsAuthenticatedUse(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            return false;
        }

        try
        {
            return File.ReadLines(logPath).Any(line =>
                line.Contains(
                    "Completed one-time pairing",
                    StringComparison.Ordinal) ||
                line.Contains(
                    "Accepted authenticated sleep request",
                    StringComparison.Ordinal));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class DevicePayload
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("secret_hex")]
        public string SecretHex { get; init; } = string.Empty;
    }

    private sealed class CredentialPayload
    {
        [JsonPropertyName("devices")]
        public IReadOnlyList<DevicePayload> Devices { get; init; } = [];

        [JsonPropertyName("pairing_code_hash")]
        public string PairingCodeHash { get; init; } = string.Empty;

        [JsonPropertyName("pairing_expires_utc")]
        public long PairingExpiresUtc { get; init; }

        [JsonPropertyName("pairing_code")]
        public string PairingCode { get; init; } = string.Empty;

        public CredentialPayload Validate()
        {
            if (Devices.Count > MaximumDevices)
            {
                throw new ConfigException(
                    "The protected device registry is too large.");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var secrets = new HashSet<string>(StringComparer.Ordinal);
            foreach (DevicePayload device in Devices)
            {
                if (!DeviceIdPattern.IsMatch(device.Id) ||
                    !Hex64.IsMatch(device.SecretHex))
                {
                    throw new ConfigException(
                        "A protected device credential has an invalid format.");
                }

                if (!ids.Add(device.Id) || !secrets.Add(device.SecretHex))
                {
                    throw new ConfigException(
                        "The protected device registry contains a duplicate.");
                }
            }

            ValidatePairingState(
                PairingCodeHash,
                PairingExpiresUtc,
                PairingCode);
            return this;
        }
    }

    private sealed class LegacyCredentialPayload
    {
        [JsonPropertyName("secret_hex")]
        public string SecretHex { get; init; } = string.Empty;

        [JsonPropertyName("pairing_code_hash")]
        public string PairingCodeHash { get; init; } = string.Empty;

        [JsonPropertyName("pairing_expires_utc")]
        public long PairingExpiresUtc { get; init; }

        [JsonPropertyName("pairing_code")]
        public string PairingCode { get; init; } = string.Empty;

        [JsonPropertyName("paired_once")]
        public bool PairedOnce { get; init; }

        public LegacyCredentialPayload Validate()
        {
            if (!Hex64.IsMatch(SecretHex))
            {
                throw new ConfigException(
                    "The protected bridge secret has an invalid format.");
            }

            ValidatePairingState(
                PairingCodeHash,
                PairingExpiresUtc,
                PairingCode);
            return this;
        }
    }

    private static void ValidatePairingState(
        string pairingCodeHash,
        long pairingExpiresUtc,
        string pairingCode)
    {
        if (pairingCodeHash.Length != 0 && !Hex64.IsMatch(pairingCodeHash))
        {
            throw new ConfigException(
                "The protected pairing hash has an invalid format.");
        }

        if (pairingExpiresUtc < 0 ||
            (pairingCodeHash.Length == 0) != (pairingExpiresUtc == 0))
        {
            throw new ConfigException(
                "The protected pairing expiry is invalid.");
        }

        if (pairingCode.Length == 0)
        {
            return;
        }

        if (!PairingCodePattern.IsMatch(pairingCode) ||
            pairingCodeHash.Length == 0)
        {
            throw new ConfigException(
                "The protected pairing code has an invalid format.");
        }

        byte[] actualHash = SHA256.HashData(
            Encoding.ASCII.GetBytes(pairingCode));
        if (!CryptographicOperations.FixedTimeEquals(
            actualHash,
            Convert.FromHexString(pairingCodeHash)))
        {
            throw new ConfigException(
                "The protected pairing code does not match its hash.");
        }
    }

    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };
}
