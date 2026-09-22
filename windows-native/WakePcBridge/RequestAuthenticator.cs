using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WakePcBridge;

public enum AuthenticationResult
{
    Accepted,
    InvalidTimestamp,
    StaleTimestamp,
    InvalidNonce,
    InvalidSignature,
    Replay,
    CacheFull,
}

public sealed class RequestAuthenticator
{
    public const string SleepPath = "/v1/sleep";
    public const string StatusPath = "/v1/status";
    public const int NonceLifetimeSeconds = 120;
    public const int MaximumNonces = 4096;

    private static readonly Regex NoncePattern = new(
        "^[A-Za-z0-9_-]{16,64}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex SignaturePattern = new(
        "^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly byte[] _secret;
    private readonly Dictionary<string, long> _acceptedNonces = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public RequestAuthenticator(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length != 32)
        {
            throw new ArgumentException("Secret must contain 32 bytes.", nameof(secret));
        }

        _secret = secret.ToArray();
    }

    public AuthenticationResult Authenticate(
        string path,
        string? timestampText,
        string? nonce,
        string? signatureHex,
        long now)
    {
        if (!long.TryParse(
                timestampText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long timestamp))
        {
            return AuthenticationResult.InvalidTimestamp;
        }

        if (timestamp < now - 60 || timestamp > now + 60)
        {
            return AuthenticationResult.StaleTimestamp;
        }

        if (nonce is null || !NoncePattern.IsMatch(nonce))
        {
            return AuthenticationResult.InvalidNonce;
        }

        if (signatureHex is null || !SignaturePattern.IsMatch(signatureHex))
        {
            return AuthenticationResult.InvalidSignature;
        }

        byte[] provided = Convert.FromHexString(signatureHex);
        if (path is not SleepPath and not StatusPath)
        {
            return AuthenticationResult.InvalidSignature;
        }

        byte[] expected = ComputeSignature(_secret, path, timestamp, nonce);
        if (!CryptographicOperations.FixedTimeEquals(provided, expected))
        {
            return AuthenticationResult.InvalidSignature;
        }

        lock (_gate)
        {
            ExpireNonces(now);
            if (_acceptedNonces.ContainsKey(nonce))
            {
                return AuthenticationResult.Replay;
            }

            if (_acceptedNonces.Count >= MaximumNonces)
            {
                // Never evict a live nonce: doing so would permit a captured request
                // to be replayed while its timestamp is still valid.
                return AuthenticationResult.CacheFull;
            }

            _acceptedNonces.Add(nonce, now);
        }

        return AuthenticationResult.Accepted;
    }

    public static byte[] ComputeSignature(
        byte[] secret,
        string path,
        long timestamp,
        string nonce)
    {
        if (path is not SleepPath and not StatusPath)
        {
            throw new ArgumentException("Unsupported authenticated path.", nameof(path));
        }

        string canonical = FormattableString.Invariant(
            $"POST\n{path}\n{timestamp}\n{nonce}\n");
        return HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(canonical));
    }

    public int Count(long now)
    {
        lock (_gate)
        {
            ExpireNonces(now);
            return _acceptedNonces.Count;
        }
    }

    private void ExpireNonces(long now)
    {
        foreach (string nonce in _acceptedNonces
                     .Where(pair => now - pair.Value > NonceLifetimeSeconds)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _acceptedNonces.Remove(nonce);
        }
    }
}
