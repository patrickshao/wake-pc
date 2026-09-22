using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WakePcBridge;

public sealed class BridgeConfig
{
    private static readonly Regex Hex64 = new(
        "^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex PairingCodePattern = new(
        "^[0-9]{6}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex MacAddressPattern = new(
        "^[0-9A-F]{2}(?::[0-9A-F]{2}){5}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("secret_hex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string SecretHex { get; init; } = string.Empty;

    [JsonPropertyName("credentials_protected")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string ProtectedCredentials { get; init; } = string.Empty;

    [JsonPropertyName("sleep_delay_ms")]
    public int SleepDelayMs { get; init; }

    [JsonPropertyName("expected_wired_mac")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string ExpectedWiredMac { get; init; } = string.Empty;

    [JsonPropertyName("expected_ipv4_address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string ExpectedIpv4Address { get; init; } = string.Empty;

    [JsonPropertyName("pairing_code_hash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string PairingCodeHash { get; init; } = string.Empty;

    [JsonPropertyName("pairing_expires_utc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long PairingExpiresUtc { get; init; }

    [JsonPropertyName("pairing_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string PairingCode { get; init; } = string.Empty;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WakePcSleepBridge",
        "config.json");

    public static BridgeConfig LoadOrCreate(string path, out bool created)
    {
        ValidateAbsolutePath(path);
        if (File.Exists(path))
        {
            created = false;
            return Load(path);
        }

        string directory = Path.GetDirectoryName(path)
            ?? throw new ConfigException("Configuration directory is unavailable.");
        try
        {
            Directory.CreateDirectory(directory);
            BridgeConfig config = CreateNew();
            string temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
            WriteConfig(temporaryPath, config);
            try
            {
                File.Move(temporaryPath, path, overwrite: false);
                created = true;
                return config;
            }
            catch (IOException) when (File.Exists(path))
            {
                File.Delete(temporaryPath);
                created = false;
                return Load(path);
            }
        }
        catch (ConfigException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException(
                $"Configuration could not be created: {exception.Message}",
                exception);
        }
    }

    public static BridgeConfig Load(string path)
    {
        ValidateAbsolutePath(path);

        try
        {
            string json = File.ReadAllText(path);
            BridgeConfig? config = JsonSerializer.Deserialize<BridgeConfig>(
                json,
                JsonOptions);
            return config?.Validate()
                ?? throw new ConfigException("Configuration is empty.");
        }
        catch (ConfigException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ConfigException(
                $"Configuration could not be read: {exception.Message}",
                exception);
        }
    }

    public BridgeConfig Validate()
    {
        if (Port is < 1024 or > 65535)
        {
            throw new ConfigException("Port must be between 1024 and 65535.");
        }

        bool hasPlaintextSecret = SecretHex.Length != 0;
        bool hasProtectedSecret = ProtectedCredentials.Length != 0;
        if (hasPlaintextSecret && hasProtectedSecret)
        {
            throw new ConfigException(
                "Configuration cannot contain both protected and legacy credentials.");
        }

        if (hasPlaintextSecret && !Hex64.IsMatch(SecretHex))
        {
            throw new ConfigException(
                "secret_hex must be exactly 64 lowercase hexadecimal characters.");
        }

        if (hasProtectedSecret)
        {
            try
            {
                byte[] protectedBytes = Convert.FromBase64String(ProtectedCredentials);
                if (protectedBytes.Length is < 32 or > 16_384)
                {
                    throw new ConfigException(
                        "credentials_protected has an invalid size.");
                }
            }
            catch (FormatException exception)
            {
                throw new ConfigException(
                    "credentials_protected must be valid base64.",
                    exception);
            }
        }

        if (SleepDelayMs is < 0 or > 10_000)
        {
            throw new ConfigException(
                "sleep_delay_ms must be between 0 and 10000.");
        }

        if ((ExpectedWiredMac.Length == 0) !=
            (ExpectedIpv4Address.Length == 0))
        {
            throw new ConfigException(
                "Expected wired MAC and IPv4 address must either both be set or both be empty.");
        }

        if (ExpectedWiredMac.Length != 0 &&
            !MacAddressPattern.IsMatch(ExpectedWiredMac))
        {
            throw new ConfigException(
                "expected_wired_mac must use uppercase colon notation.");
        }

        if (ExpectedIpv4Address.Length != 0 &&
            (!IPAddress.TryParse(ExpectedIpv4Address, out IPAddress? expectedAddress) ||
             expectedAddress.AddressFamily != AddressFamily.InterNetwork ||
             IPAddress.IsLoopback(expectedAddress)))
        {
            throw new ConfigException(
                "expected_ipv4_address must be a non-loopback IPv4 address.");
        }

        if (PairingCodeHash.Length != 0 && !Hex64.IsMatch(PairingCodeHash))
        {
            throw new ConfigException(
                "pairing_code_hash must be empty or exactly 64 lowercase hexadecimal characters.");
        }

        if (PairingExpiresUtc < 0)
        {
            throw new ConfigException("pairing_expires_utc cannot be negative.");
        }

        if ((PairingCodeHash.Length == 0) != (PairingExpiresUtc == 0))
        {
            throw new ConfigException(
                "Pairing hash and expiry must either both be set or both be empty.");
        }

        if (PairingCode.Length != 0)
        {
            if (!PairingCodePattern.IsMatch(PairingCode) ||
                PairingCodeHash.Length == 0)
            {
                throw new ConfigException(
                    "pairing_code must be empty or a six-digit active code.");
            }

            byte[] actualHash = SHA256.HashData(
                Encoding.ASCII.GetBytes(PairingCode));
            if (!CryptographicOperations.FixedTimeEquals(
                actualHash,
                Convert.FromHexString(PairingCodeHash)))
            {
                throw new ConfigException(
                    "pairing_code does not match pairing_code_hash.");
            }
        }

        if (hasProtectedSecret &&
            (PairingCodeHash.Length != 0 ||
             PairingExpiresUtc != 0 ||
             PairingCode.Length != 0))
        {
            throw new ConfigException(
                "Protected pairing state cannot also appear in plaintext.");
        }

        if (!hasPlaintextSecret && !hasProtectedSecret &&
            (PairingCodeHash.Length != 0 ||
             PairingExpiresUtc != 0 ||
             PairingCode.Length != 0))
        {
            throw new ConfigException(
                "Uninitialized configuration cannot contain pairing state.");
        }

        return this;
    }

    public BridgeConfig WithExpectedNetwork(string macAddress, string ipv4Address)
    {
        return new BridgeConfig
        {
            Port = Port,
            SecretHex = SecretHex,
            ProtectedCredentials = ProtectedCredentials,
            SleepDelayMs = SleepDelayMs,
            ExpectedWiredMac = macAddress,
            ExpectedIpv4Address = ipv4Address,
            PairingCodeHash = PairingCodeHash,
            PairingExpiresUtc = PairingExpiresUtc,
            PairingCode = PairingCode,
        }.Validate();
    }

    private static BridgeConfig CreateNew()
    {
        return new BridgeConfig
        {
            Port = 47653,
            SleepDelayMs = 750,
            PairingCodeHash = string.Empty,
        }.Validate();
    }

    internal static void SaveAtomically(string path, BridgeConfig config)
    {
        ValidateAbsolutePath(path);
        string temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            WriteConfig(temporaryPath, config);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void WriteConfig(string path, BridgeConfig config)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(config, JsonOptions);
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough);
        stream.Write(json);
        stream.Flush(flushToDisk: true);
    }

    private static void ValidateAbsolutePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ConfigException("Configuration path must be absolute.");
        }
    }

    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };
}

public sealed class ConfigException : Exception
{
    public ConfigException(string message)
        : base(message)
    {
    }

    public ConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
