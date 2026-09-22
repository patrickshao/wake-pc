using System.Text;

namespace WakePcBridge;

public sealed class BridgeLog
{
    public const long MaximumBytes = 1024 * 1024;
    private readonly string _path;
    private readonly object _gate = new();

    public BridgeLog(string path)
    {
        _path = path;
    }

    public void Write(string message)
    {
        try
        {
            lock (_gate)
            {
                RotateIfNeeded();
                string safe = Redact(message);
                File.AppendAllText(
                    _path,
                    $"{DateTimeOffset.UtcNow:O} {safe}{Environment.NewLine}",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Logging cannot bring down the one-purpose listener.
        }
    }

    public static string Redact(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        string redacted = System.Text.RegularExpressions.Regex.Replace(
            message,
            "(?i)(secret_hex|pairing(?:-|_)code|signature|nonce)\\s*[:=]\\s*\\S+",
            "$1=[REDACTED]",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted,
            "(?<![0-9A-Fa-f])[0-9a-fA-F]{64}(?![0-9A-Fa-f])",
            "[REDACTED-64HEX]",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return redacted.Replace('\r', ' ').Replace('\n', ' ');
    }

    private void RotateIfNeeded()
    {
        var file = new FileInfo(_path);
        if (!file.Exists || file.Length < MaximumBytes)
        {
            return;
        }

        string first = _path + ".1";
        string second = _path + ".2";
        File.Delete(second);
        if (File.Exists(first))
        {
            File.Move(first, second);
        }

        File.Move(_path, first);
    }
}
