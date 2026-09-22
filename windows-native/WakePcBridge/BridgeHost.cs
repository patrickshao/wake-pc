using System.Net;
using System.Text;
using System.Text.Json;

namespace WakePcBridge;

public sealed class BridgeHost : IAsyncDisposable
{
    private readonly BridgeConfig _config;
    private readonly HttpListener _listener;
    private readonly BridgeLog _log;
    private readonly PairingState _pairing;
    private readonly object _authenticatorGate = new();
    private readonly Dictionary<string, AuthenticatorEntry> _authenticators =
        new(StringComparer.Ordinal);
    private readonly IPowerController _powerController;
    private readonly Action _pairingCompleted;

    public BridgeHost(
        BridgeConfig config,
        BridgeCredentials credentials,
        string configPath,
        IPowerController powerController,
        BridgeLog log,
        string? listenerPrefix = null,
        Action? pairingCompleted = null)
    {
        _config = config;
        _powerController = powerController;
        _log = log;
        _pairing = new PairingState(credentials, configPath);
        foreach (BridgeDeviceCredential device in credentials.Devices)
        {
            _authenticators.Add(
                device.Id,
                new AuthenticatorEntry(
                    device.SecretHex,
                    new RequestAuthenticator(device.SecretBytes)));
        }
        _pairingCompleted = pairingCompleted ?? (() => { });
        _listener = new HttpListener
        {
            IgnoreWriteExceptions = true,
        };
        _listener.Prefixes.Add(
            listenerPrefix ?? FormattableString.Invariant($"http://+:{config.Port}/"));
    }

    public bool IsListening => _listener.IsListening;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _listener.Start();
            _log.Write($"Native bridge listening on TCP {_config.Port}.");
        }
        catch (Exception exception) when (
            exception is HttpListenerException or InvalidOperationException)
        {
            _log.Write($"Listener startup failed: {exception.Message}");
            throw;
        }

        using CancellationTokenRegistration registration =
            cancellationToken.Register(Stop);
        try
        {
            while (!cancellationToken.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    cancellationToken.IsCancellationRequested &&
                    exception is HttpListenerException or ObjectDisposedException)
                {
                    break;
                }

                await HandleAsync(context, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Stop();
            _log.Write("Native bridge stopped.");
        }
    }

    public void Stop()
    {
        try
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        _listener.Close();
        return ValueTask.CompletedTask;
    }

    private async Task HandleAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        HttpListenerRequest request = context.Request;
        string remoteIp = request.RemoteEndPoint?.Address.MapToIPv4().ToString()
            ?? "unknown";
        try
        {
            if (request.ContentLength64 != 0 ||
                request.Headers["Content-Length"] != "0" ||
                !string.IsNullOrEmpty(request.Headers["Transfer-Encoding"]) ||
                request.HasEntityBody)
            {
                _log.Write($"Rejected request body from {remoteIp}.");
                await SendJsonAsync(
                    context.Response,
                    400,
                    "Request body not allowed").ConfigureAwait(false);
                return;
            }

            if (!string.IsNullOrEmpty(request.Url?.Query))
            {
                _log.Write($"Rejected query string from {remoteIp}.");
                await SendJsonAsync(
                    context.Response,
                    400,
                    "Query not allowed").ConfigureAwait(false);
                return;
            }

            string path = request.Url?.AbsolutePath ?? string.Empty;
            if (path == "/v1/pair")
            {
                await HandlePairingAsync(context, remoteIp).ConfigureAwait(false);
                return;
            }

            if (path == RequestAuthenticator.SleepPath)
            {
                await HandleSleepAsync(
                    context,
                    remoteIp,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (path == RequestAuthenticator.StatusPath)
            {
                await HandleStatusAsync(context, remoteIp).ConfigureAwait(false);
                return;
            }

            _log.Write($"Rejected unknown route from {remoteIp}.");
            await SendJsonAsync(
                context.Response,
                404,
                "Not found").ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            _log.Write($"Request failure for {remoteIp}: {exception.Message}");
            if (context.Response.OutputStream.CanWrite)
            {
                try
                {
                    await SendJsonAsync(
                        context.Response,
                        500,
                        "Wake PC Companion error").ConfigureAwait(false);
                }
                catch (Exception responseException) when (
                    responseException is IOException or HttpListenerException or
                    ObjectDisposedException)
                {
                }
            }
        }
    }

    private async Task HandlePairingAsync(
        HttpListenerContext context,
        string remoteIp)
    {
        if (context.Request.HttpMethod != "POST")
        {
            await SendJsonAsync(
                context.Response,
                405,
                "Method not allowed").ConfigureAwait(false);
            return;
        }

        string? code = BoundedHeader(
            context.Request,
            "X-Wake-PC-Pairing-Code",
            maximumLength: 6);
        string? suppliedReplaceDeviceId =
            context.Request.Headers["X-Wake-PC-Replace-Device-ID"];
        string? replaceDeviceId = BoundedHeader(
            context.Request,
            "X-Wake-PC-Replace-Device-ID",
            maximumLength: 32);
        if (suppliedReplaceDeviceId is not null &&
            !BridgeCredentials.IsValidDeviceId(replaceDeviceId))
        {
            await SendJsonAsync(
                context.Response,
                400,
                "Invalid replacement device ID").ConfigureAwait(false);
            return;
        }

        string? suppliedProtocol = context.Request.Headers["X-Wake-PC-Protocol"];
        string? protocol = BoundedHeader(
            context.Request,
            "X-Wake-PC-Protocol",
            maximumLength: 1);
        if (suppliedProtocol is not null &&
            !string.Equals(protocol, "2", StringComparison.Ordinal))
        {
            await SendJsonAsync(
                context.Response,
                400,
                "Invalid pairing protocol").ConfigureAwait(false);
            return;
        }

        if (replaceDeviceId is null &&
            !string.Equals(protocol, "2", StringComparison.Ordinal))
        {
            replaceDeviceId = BridgeCredentials.PrimaryDeviceId;
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        PairingResult result = _pairing.TryPair(
            code,
            now,
            replaceDeviceId,
            out BridgeDeviceCredential? device);
        switch (result)
        {
            case PairingResult.Paired:
                _ = GetAuthenticator(device
                    ?? throw new ConfigException(
                        "Pairing completed without a device credential."));
                _log.Write($"Completed one-time pairing for {remoteIp}.");
                _pairingCompleted();
                await SendJsonAsync(
                    context.Response,
                    200,
                    "Paired",
                    device).ConfigureAwait(false);
                break;
            case PairingResult.Incorrect:
                _log.Write($"Rejected pairing attempt from {remoteIp}.");
                await SendJsonAsync(
                    context.Response,
                    401,
                    "Pairing rejected").ConfigureAwait(false);
                break;
            case PairingResult.Locked:
                _log.Write($"Pairing attempt limit reached for {remoteIp}.");
                await SendJsonAsync(
                    context.Response,
                    429,
                    "Pairing locked").ConfigureAwait(false);
                break;
            case PairingResult.Gone:
                await SendJsonAsync(
                    context.Response,
                    410,
                    "Pairing expired").ConfigureAwait(false);
                break;
            default:
                await SendJsonAsync(
                    context.Response,
                    400,
                    "Invalid pairing code format").ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleSleepAsync(
        HttpListenerContext context,
        string remoteIp,
        CancellationToken cancellationToken)
    {
        if (context.Request.HttpMethod != "POST")
        {
            await SendJsonAsync(
                context.Response,
                405,
                "Method not allowed").ConfigureAwait(false);
            return;
        }

        AuthenticationResult result = AuthenticateRequest(
            context.Request,
            RequestAuthenticator.SleepPath);
        if (result != AuthenticationResult.Accepted)
        {
            _log.Write(
                $"Rejected authenticated request from {remoteIp}: {result}.");
            await SendJsonAsync(
                context.Response,
                401,
                "Unauthorized").ConfigureAwait(false);
            return;
        }

        _log.Write($"Accepted authenticated sleep request from {remoteIp}.");
        await SendJsonAsync(
            context.Response,
            202,
            "Sleep accepted").ConfigureAwait(false);

        bool accepted = await _powerController.SleepAsync(
            _config.SleepDelayMs,
            cancellationToken).ConfigureAwait(false);
        _log.Write(accepted
            ? "Windows accepted the non-forced sleep request."
            : "Windows rejected the non-forced sleep request.");
    }

    private async Task HandleStatusAsync(
        HttpListenerContext context,
        string remoteIp)
    {
        if (context.Request.HttpMethod != "POST")
        {
            await SendJsonAsync(
                context.Response,
                405,
                "Method not allowed").ConfigureAwait(false);
            return;
        }

        AuthenticationResult result = AuthenticateRequest(
            context.Request,
            RequestAuthenticator.StatusPath);
        if (result != AuthenticationResult.Accepted)
        {
            _log.Write(
                $"Rejected authenticated status request from {remoteIp}: {result}.");
            await SendJsonAsync(
                context.Response,
                401,
                "Unauthorized").ConfigureAwait(false);
            return;
        }

        await SendJsonAsync(
            context.Response,
            200,
            "Wake PC Companion reachable",
            version: BridgeVersion.Current).ConfigureAwait(false);
    }

    private AuthenticationResult AuthenticateRequest(
        HttpListenerRequest request,
        string path)
    {
        string? suppliedDeviceId = request.Headers["X-Wake-PC-Device-ID"];
        string? deviceId = suppliedDeviceId is null
            ? BridgeCredentials.PrimaryDeviceId
            : BoundedHeader(request, "X-Wake-PC-Device-ID", 32);
        RequestAuthenticator? authenticator = GetAuthenticator(deviceId);
        return authenticator is null
            ? AuthenticationResult.InvalidSignature
            : authenticator.Authenticate(
                path,
                BoundedHeader(request, "X-Wake-PC-Timestamp", 20),
                BoundedHeader(request, "X-Wake-PC-Nonce", 64),
                BoundedHeader(request, "X-Wake-PC-Signature", 64),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private static string? BoundedHeader(
        HttpListenerRequest request,
        string name,
        int maximumLength)
    {
        string? value = request.Headers[name];
        return value is not null && value.Length <= maximumLength ? value : null;
    }

    private static async Task SendJsonAsync(
        HttpListenerResponse response,
        int statusCode,
        string message,
        BridgeDeviceCredential? device = null,
        string? version = null)
    {
        byte[] body = device is not null
            ? JsonSerializer.SerializeToUtf8Bytes(new
            {
                ok = true,
                message,
                device_id = device.Id,
                secret_hex = device.SecretHex,
                version = BridgeVersion.Current,
            })
            : version is not null
                ? JsonSerializer.SerializeToUtf8Bytes(new
                {
                    ok = statusCode is >= 200 and < 300,
                    message,
                    version,
                })
                : JsonSerializer.SerializeToUtf8Bytes(new
            {
                ok = statusCode is >= 200 and < 300,
                message,
            });
        if (body.Length > 1024)
        {
            throw new InvalidOperationException("Response exceeded the fixed size bound.");
        }

        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = body.Length;
        response.KeepAlive = false;
        try
        {
            await response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        }
        finally
        {
            response.OutputStream.Close();
            response.Close();
        }
    }

    private RequestAuthenticator GetAuthenticator(BridgeDeviceCredential device)
    {
        lock (_authenticatorGate)
        {
            if (_authenticators.TryGetValue(
                    device.Id,
                    out AuthenticatorEntry? existing) &&
                string.Equals(
                    existing.SecretHex,
                    device.SecretHex,
                    StringComparison.Ordinal))
            {
                return existing.Authenticator;
            }

            var authenticator = new RequestAuthenticator(device.SecretBytes);
            _authenticators[device.Id] = new AuthenticatorEntry(
                device.SecretHex,
                authenticator);
            return authenticator;
        }
    }

    private RequestAuthenticator? GetAuthenticator(string? deviceId)
    {
        if (deviceId is null)
        {
            return null;
        }

        lock (_authenticatorGate)
        {
            return _authenticators.TryGetValue(
                deviceId,
                out AuthenticatorEntry? entry)
                ? entry.Authenticator
                : null;
        }
    }

    private sealed record AuthenticatorEntry(
        string SecretHex,
        RequestAuthenticator Authenticator);
}
