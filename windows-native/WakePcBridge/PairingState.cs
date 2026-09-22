using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WakePcBridge;

public enum PairingResult
{
    Paired,
    Incorrect,
    Gone,
    Locked,
    InvalidFormat,
}

public sealed class PairingState
{
    private static readonly Regex CodePattern = new(
        "^[0-9]{6}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly object _gate = new();
    private readonly string _configPath;
    private string _hashHex;
    private long _expiresUtc;
    private int _failedAttempts;

    public PairingState(BridgeCredentials credentials, string configPath)
    {
        _configPath = configPath;
        _hashHex = credentials.PairingCodeHash;
        _expiresUtc = credentials.PairingExpiresUtc;
    }

    public PairingResult TryPair(
        string? code,
        long now,
        string? replaceDeviceId,
        out BridgeDeviceCredential? device)
    {
        device = null;
        if (code is null || !CodePattern.IsMatch(code))
        {
            return PairingResult.InvalidFormat;
        }

        lock (_gate)
        {
            BridgeCredentials current = BridgeCredentials.Load(_configPath);
            if (!string.Equals(
                _hashHex,
                current.PairingCodeHash,
                StringComparison.Ordinal))
            {
                _failedAttempts = 0;
            }
            _hashHex = current.PairingCodeHash;
            _expiresUtc = current.PairingExpiresUtc;
            if (_hashHex.Length == 0 || _expiresUtc == 0 || now > _expiresUtc)
            {
                return PairingResult.Gone;
            }

            if (_failedAttempts >= 10)
            {
                return PairingResult.Locked;
            }

            byte[] actual = SHA256.HashData(Encoding.ASCII.GetBytes(code));
            byte[] expected = Convert.FromHexString(_hashHex);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                _failedAttempts++;
                return _failedAttempts >= 10
                    ? PairingResult.Locked
                    : PairingResult.Incorrect;
            }

            if (!BridgeCredentials.TryCompletePairing(
                _configPath,
                _hashHex,
                replaceDeviceId,
                out BridgeCredentials completed,
                out device))
            {
                _hashHex = completed.PairingCodeHash;
                _expiresUtc = completed.PairingExpiresUtc;
                _failedAttempts = 0;
                return PairingResult.Gone;
            }

            _hashHex = string.Empty;
            _expiresUtc = 0;
            return PairingResult.Paired;
        }
    }
}
