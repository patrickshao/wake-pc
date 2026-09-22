using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WakePcBridge;

internal static class Program
{
    private static async Task<int> Main()
    {
        var results = new List<string>();
        try
        {
            results.AddRange(BridgeSelfTest.RunPureTests());
            results.AddRange(await RunLoopbackTestsAsync().ConfigureAwait(false));
            Console.WriteLine(
                JsonSerializer.Serialize(
                    new { ok = true, tests = results },
                    new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        ok = false,
                        tests = results,
                        error = BridgeLog.Redact(exception.ToString()),
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    private static async Task<IReadOnlyList<string>> RunLoopbackTestsAsync()
    {
        var passed = new List<string>();
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"WakePcBridge-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var cancellation = new CancellationTokenSource();
        try
        {
            int port = GetAvailablePort();
            string secret = "11" + new string('0', 62);
            string pairingCode = "123456";
            string pairingHash = Convert.ToHexString(
                SHA256.HashData(Encoding.ASCII.GetBytes(pairingCode)))
                .ToLowerInvariant();
            string configPath = Path.GetFullPath(Path.Combine(directory, "config.json"));
            WriteConfig(configPath, port, secret, pairingHash);
            string logPath = Path.Combine(directory, "sleep-bridge.log");
            BridgeCredentials credentials = BridgeCredentials.LoadOrMigrate(
                configPath,
                logPath);
            BridgeConfig config = credentials.Config;
            var power = new FakePowerController();
            bool pairingCallbackCalled = false;
            await using var host = new BridgeHost(
                config,
                credentials,
                configPath,
                power,
                new BridgeLog(logPath),
                $"http://127.0.0.1:{port}/",
                () => pairingCallbackCalled = true);
            Task hostTask = host.RunAsync(cancellation.Token);
            await WaitForListenerAsync(host, hostTask).ConfigureAwait(false);

            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                Timeout = TimeSpan.FromSeconds(5),
            };

            HttpResponseMessage wrongMethod = await client.GetAsync("v1/pair")
                .ConfigureAwait(false);
            Require(
                wrongMethod.StatusCode == HttpStatusCode.BadRequest,
                "Requests without explicit Content-Length: 0 must be rejected.");

            using HttpRequestMessage invalidProtocol = EmptyPost("v1/pair");
            invalidProtocol.Headers.Add("X-Wake-PC-Pairing-Code", pairingCode);
            invalidProtocol.Headers.Add("X-Wake-PC-Protocol", "99");
            Require(
                (await client.SendAsync(invalidProtocol).ConfigureAwait(false)).StatusCode ==
                    HttpStatusCode.BadRequest,
                "Invalid pairing protocol was accepted.");
            using HttpRequestMessage invalidReplacement = EmptyPost("v1/pair");
            invalidReplacement.Headers.Add(
                "X-Wake-PC-Pairing-Code",
                pairingCode);
            invalidReplacement.Headers.Add("X-Wake-PC-Protocol", "2");
            invalidReplacement.Headers.Add(
                "X-Wake-PC-Replace-Device-ID",
                "not-a-device");
            Require(
                (await client.SendAsync(invalidReplacement).ConfigureAwait(false))
                    .StatusCode == HttpStatusCode.BadRequest,
                "Invalid replacement device ID was accepted.");

            using HttpRequestMessage pair = EmptyPost("v1/pair");
            pair.Headers.Add("X-Wake-PC-Pairing-Code", pairingCode);
            pair.Headers.Add("X-Wake-PC-Protocol", "2");
            HttpResponseMessage paired = await client.SendAsync(pair).ConfigureAwait(false);
            Require(
                paired.StatusCode == HttpStatusCode.OK && pairingCallbackCalled,
                "Pairing failed or did not update local status.");
            string pairBody = await paired.Content.ReadAsStringAsync().ConfigureAwait(false);
            using JsonDocument pairDocument = JsonDocument.Parse(pairBody);
            string pairedDeviceId = pairDocument.RootElement
                .GetProperty("device_id").GetString() ?? string.Empty;
            string pairedSecret = pairDocument.RootElement
                .GetProperty("secret_hex").GetString() ?? string.Empty;
            string pairedBridgeVersion = pairDocument.RootElement
                .GetProperty("version").GetString() ?? string.Empty;
            Require(
                pairedDeviceId == BridgeCredentials.PrimaryDeviceId &&
                pairedSecret.Length == 64 &&
                pairedSecret != secret &&
                pairedBridgeVersion == BridgeVersion.Current,
                "First pairing did not mint a primary-device credential.");
            passed.Add("loopback pairing");

            using HttpRequestMessage usedPair = EmptyPost("v1/pair");
            usedPair.Headers.Add("X-Wake-PC-Pairing-Code", pairingCode);
            Require(
                (await client.SendAsync(usedPair).ConfigureAwait(false)).StatusCode ==
                HttpStatusCode.Gone,
                "Used pairing code was accepted.");
            passed.Add("one-use loopback pairing");

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            HttpResponseMessage status = await client.SendAsync(
                SignedStatus(
                    pairedDeviceId,
                    pairedSecret,
                    now,
                    "AQIDBAUGBwgJCgsMDQ4PEL")).ConfigureAwait(false);
            using JsonDocument statusDocument = JsonDocument.Parse(
                await status.Content.ReadAsStringAsync().ConfigureAwait(false));
            Require(
                status.StatusCode == HttpStatusCode.OK &&
                statusDocument.RootElement.GetProperty("version").GetString() ==
                    BridgeVersion.Current &&
                power.Calls == 0,
                "Authenticated status did not report the bridge version safely.");
            passed.Add("authenticated bridge version status");

            const string firstNonce = "AQIDBAUGBwgJCgsMDQ4PEA";
            HttpResponseMessage accepted = await client.SendAsync(
                SignedSleep(null, pairedSecret, now, firstNonce)).ConfigureAwait(false);
            Require(
                accepted.StatusCode == HttpStatusCode.Accepted,
                "Authenticated sleep was rejected.");
            Require(
                await power.WaitForCallsAsync(1).ConfigureAwait(false),
                "Fake power controller was not invoked.");
            passed.Add("authenticated loopback sleep with fake power");

            BridgeCredentials nextPairing =
                BridgeCredentials.EnsureActivePairingCode(configPath);
            using HttpRequestMessage pairAnother = EmptyPost("v1/pair");
            pairAnother.Headers.Add(
                "X-Wake-PC-Pairing-Code",
                nextPairing.PairingCode);
            pairAnother.Headers.Add("X-Wake-PC-Protocol", "2");
            HttpResponseMessage pairedAnother = await client
                .SendAsync(pairAnother).ConfigureAwait(false);
            using JsonDocument anotherDocument = JsonDocument.Parse(
                await pairedAnother.Content.ReadAsStringAsync().ConfigureAwait(false));
            string anotherDeviceId = anotherDocument.RootElement
                .GetProperty("device_id").GetString() ?? string.Empty;
            string anotherSecret = anotherDocument.RootElement
                .GetProperty("secret_hex").GetString() ?? string.Empty;
            Require(
                pairedAnother.StatusCode == HttpStatusCode.OK &&
                anotherDeviceId.Length == 32 &&
                anotherDeviceId != pairedDeviceId &&
                anotherSecret.Length == 64 &&
                anotherSecret != pairedSecret,
                "Additional pairing did not mint an independent credential.");
            Require(
                (await client.SendAsync(SignedSleep(
                    anotherDeviceId,
                    anotherSecret,
                    now,
                    "AQIDBAUGBwgJCgsMDQ4PEF")).ConfigureAwait(false)).StatusCode ==
                    HttpStatusCode.Accepted &&
                (await client.SendAsync(SignedSleep(
                    pairedDeviceId,
                    pairedSecret,
                    now,
                    "AQIDBAUGBwgJCgsMDQ4PEG")).ConfigureAwait(false)).StatusCode ==
                    HttpStatusCode.Accepted &&
                await power.WaitForCallsAsync(3).ConfigureAwait(false),
                "Per-device authentication did not preserve both clients.");
            passed.Add("independent multi-device authentication");

            HttpResponseMessage replay = await client.SendAsync(
                SignedSleep(pairedDeviceId, pairedSecret, now, firstNonce))
                .ConfigureAwait(false);
            Require(
                replay.StatusCode == HttpStatusCode.Unauthorized && power.Calls == 3,
                "Replay invoked power.");

            using HttpRequestMessage badSignature = SignedSleep(
                pairedDeviceId,
                pairedSecret,
                now,
                "AQIDBAUGBwgJCgsMDQ4PEB");
            badSignature.Headers.Remove("X-Wake-PC-Signature");
            badSignature.Headers.Add("X-Wake-PC-Signature", new string('0', 64));
            Require(
                (await client.SendAsync(badSignature).ConfigureAwait(false)).StatusCode ==
                HttpStatusCode.Unauthorized,
                "Bad signature was accepted.");
            Require(
                (await client.SendAsync(
                    SignedSleep(
                        pairedDeviceId,
                        pairedSecret,
                        now - 61,
                        "AQIDBAUGBwgJCgsMDQ4PEC"))
                    .ConfigureAwait(false)).StatusCode == HttpStatusCode.Unauthorized,
                "Stale request was accepted.");
            Require(
                (await client.SendAsync(
                    SignedSleep(
                        pairedDeviceId,
                        pairedSecret,
                        now,
                        "too-short"))
                    .ConfigureAwait(false)).StatusCode == HttpStatusCode.Unauthorized,
                "Malformed nonce was accepted.");
            Require(
                (await client.SendAsync(
                    SignedSleep(
                        anotherDeviceId,
                        pairedSecret,
                        now,
                        "AQIDBAUGBwgJCgsMDQ4PEH"))
                    .ConfigureAwait(false)).StatusCode == HttpStatusCode.Unauthorized,
                "A signature was accepted under the wrong device ID.");
            Require(power.Calls == 3, "Negative authentication invoked power.");
            passed.Add("negative authentication and replay");

            using HttpRequestMessage bodyRequest = new(HttpMethod.Post, "v1/sleep")
            {
                Content = new ByteArrayContent([1]),
            };
            Require(
                (await client.SendAsync(bodyRequest).ConfigureAwait(false)).StatusCode ==
                HttpStatusCode.BadRequest,
                "Request body was accepted.");
            using HttpRequestMessage queryRequest = EmptyPost("v1/sleep?command=anything");
            Require(
                (await client.SendAsync(queryRequest).ConfigureAwait(false)).StatusCode ==
                HttpStatusCode.BadRequest,
                "Query string was accepted.");
            using HttpRequestMessage wrongSleepMethod = new(HttpMethod.Put, "v1/sleep")
            {
                Content = new ByteArrayContent([]),
            };
            Require(
                (await client.SendAsync(wrongSleepMethod).ConfigureAwait(false)).StatusCode ==
                HttpStatusCode.MethodNotAllowed,
                "Wrong sleep method was accepted.");
            using HttpRequestMessage unknownRoute = EmptyPost("v1/unknown");
            Require(
                (await client.SendAsync(unknownRoute).ConfigureAwait(false)).StatusCode ==
                HttpStatusCode.NotFound,
                "Unknown route exposed functionality.");
            passed.Add("body, query, method, and route rejection");

            string concurrentNonce = "AQIDBAUGBwgJCgsMDQ4PED";
            Task<HttpResponseMessage>[] requests = Enumerable.Range(0, 12)
                .Select(_ => client.SendAsync(SignedSleep(
                    pairedDeviceId,
                    pairedSecret,
                    now,
                    concurrentNonce)))
                .ToArray();
            HttpResponseMessage[] responses = await Task.WhenAll(requests)
                .ConfigureAwait(false);
            Require(
                responses.Count(response => response.StatusCode == HttpStatusCode.Accepted) == 1,
                "Concurrent replay protection accepted more than one request.");
            Require(
                await power.WaitForCallsAsync(4).ConfigureAwait(false) && power.Calls == 4,
                "Concurrent replay protection invoked power more than once.");
            passed.Add("concurrent replay protection");

            await using var duplicate = new BridgeHost(
                config,
                BridgeCredentials.Load(configPath),
                configPath,
                new FakePowerController(),
                new BridgeLog(logPath),
                $"http://127.0.0.1:{port}/");
            await RequireThrowsAsync<HttpListenerException>(
                () => duplicate.RunAsync(CancellationToken.None),
                "Duplicate listener unexpectedly started.").ConfigureAwait(false);
            Require(
                (await client.SendAsync(
                    SignedSleep(
                        pairedDeviceId,
                        pairedSecret,
                        now,
                        "AQIDBAUGBwgJCgsMDQ4PEE"))
                    .ConfigureAwait(false)).StatusCode == HttpStatusCode.Accepted,
                "Duplicate listener disrupted active host.");
            passed.Add("duplicate listener isolation");

            BridgeCredentials replacementPairing =
                BridgeCredentials.EnsureActivePairingCode(configPath);
            using HttpRequestMessage replacePairing = EmptyPost("v1/pair");
            replacePairing.Headers.Add(
                "X-Wake-PC-Pairing-Code",
                replacementPairing.PairingCode);
            replacePairing.Headers.Add("X-Wake-PC-Protocol", "2");
            replacePairing.Headers.Add(
                "X-Wake-PC-Replace-Device-ID",
                pairedDeviceId);
            HttpResponseMessage replaced = await client
                .SendAsync(replacePairing).ConfigureAwait(false);
            using JsonDocument replacementDocument = JsonDocument.Parse(
                await replaced.Content.ReadAsStringAsync().ConfigureAwait(false));
            string replacementDeviceId = replacementDocument.RootElement
                .GetProperty("device_id").GetString() ?? string.Empty;
            string replacementSecret = replacementDocument.RootElement
                .GetProperty("secret_hex").GetString() ?? string.Empty;
            Require(
                replaced.StatusCode == HttpStatusCode.OK &&
                replacementDeviceId == pairedDeviceId &&
                replacementSecret.Length == 64 &&
                replacementSecret != pairedSecret &&
                (await client.SendAsync(SignedSleep(
                    replacementDeviceId,
                    replacementSecret,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    "AQIDBAUGBwgJCgsMDQ4PEI")).ConfigureAwait(false)).StatusCode ==
                    HttpStatusCode.Accepted &&
                (await client.SendAsync(SignedSleep(
                    pairedDeviceId,
                    pairedSecret,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    "AQIDBAUGBwgJCgsMDQ4PEJ")).ConfigureAwait(false)).StatusCode ==
                    HttpStatusCode.Unauthorized,
                "Same-client pairing did not replace its in-memory credential.");
            passed.Add("same-client loopback credential replacement");

            BridgeCredentials legacyClientPairing =
                BridgeCredentials.EnsureActivePairingCode(configPath);
            using HttpRequestMessage legacyClientPair = EmptyPost("v1/pair");
            legacyClientPair.Headers.Add(
                "X-Wake-PC-Pairing-Code",
                legacyClientPairing.PairingCode);
            HttpResponseMessage legacyClientReplaced = await client
                .SendAsync(legacyClientPair).ConfigureAwait(false);
            using JsonDocument legacyClientDocument = JsonDocument.Parse(
                await legacyClientReplaced.Content.ReadAsStringAsync()
                    .ConfigureAwait(false));
            string legacyClientDeviceId = legacyClientDocument.RootElement
                .GetProperty("device_id").GetString() ?? string.Empty;
            string legacyClientSecret = legacyClientDocument.RootElement
                .GetProperty("secret_hex").GetString() ?? string.Empty;
            Require(
                legacyClientReplaced.StatusCode == HttpStatusCode.OK &&
                legacyClientDeviceId == BridgeCredentials.PrimaryDeviceId &&
                legacyClientSecret != replacementSecret &&
                (await client.SendAsync(SignedSleep(
                    null,
                    legacyClientSecret,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    "AQIDBAUGBwgJCgsMDQ4PEK")).ConfigureAwait(false)).StatusCode ==
                    HttpStatusCode.Accepted,
                "Headerless older client did not replace the primary credential.");
            passed.Add("headerless older client replaces primary credential");

            cancellation.Cancel();
            await hostTask.ConfigureAwait(false);
            Require(!host.IsListening, "Listener did not stop cleanly.");
            passed.Add("graceful listener shutdown");

            string log = await File.ReadAllTextAsync(logPath).ConfigureAwait(false);
            Require(
                !log.Contains(secret, StringComparison.Ordinal) &&
                !log.Contains(pairedSecret, StringComparison.Ordinal) &&
                !log.Contains(anotherSecret, StringComparison.Ordinal) &&
                !log.Contains(replacementSecret, StringComparison.Ordinal) &&
                !log.Contains(legacyClientSecret, StringComparison.Ordinal),
                "Secret appeared in log.");
            Require(
                !log.Contains(firstNonce, StringComparison.Ordinal),
                "Nonce appeared in log.");
            passed.Add("loopback log secrecy");
            return passed;
        }
        finally
        {
            cancellation.Cancel();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static HttpRequestMessage EmptyPost(string path) =>
        new(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent([]),
        };

    private static HttpRequestMessage SignedSleep(
        string? deviceId,
        string secretHex,
        long timestamp,
        string nonce)
    {
        byte[] signature = RequestAuthenticator.ComputeSignature(
            Convert.FromHexString(secretHex),
            RequestAuthenticator.SleepPath,
            timestamp,
            nonce);
        HttpRequestMessage request = EmptyPost("v1/sleep");
        if (deviceId is not null)
        {
            request.Headers.Add("X-Wake-PC-Device-ID", deviceId);
        }
        request.Headers.Add("X-Wake-PC-Timestamp", timestamp.ToString());
        request.Headers.Add("X-Wake-PC-Nonce", nonce);
        request.Headers.Add(
            "X-Wake-PC-Signature",
            Convert.ToHexString(signature).ToLowerInvariant());
        return request;
    }

    private static HttpRequestMessage SignedStatus(
        string deviceId,
        string secretHex,
        long timestamp,
        string nonce)
    {
        byte[] signature = RequestAuthenticator.ComputeSignature(
            Convert.FromHexString(secretHex),
            RequestAuthenticator.StatusPath,
            timestamp,
            nonce);
        HttpRequestMessage request = EmptyPost("v1/status");
        request.Headers.Add("X-Wake-PC-Device-ID", deviceId);
        request.Headers.Add("X-Wake-PC-Timestamp", timestamp.ToString());
        request.Headers.Add("X-Wake-PC-Nonce", nonce);
        request.Headers.Add(
            "X-Wake-PC-Signature",
            Convert.ToHexString(signature).ToLowerInvariant());
        return request;
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForListenerAsync(
        BridgeHost host,
        Task hostTask)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!host.IsListening)
        {
            if (hostTask.IsCompleted)
            {
                await hostTask.ConfigureAwait(false);
                throw new InvalidOperationException(
                    "Loopback listener stopped during startup.");
            }

            await Task.Delay(20, timeout.Token).ConfigureAwait(false);
        }
    }

    private static void WriteConfig(
        string path,
        int port,
        string secret,
        string pairingHash)
    {
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new BridgeConfig
                {
                    Port = port,
                    SecretHex = secret,
                    SleepDelayMs = 0,
                    PairingCodeHash = pairingHash,
                    PairingExpiresUtc =
                        DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
                }));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task RequireThrowsAsync<TException>(
        Func<Task> action,
        string message)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private sealed class FakePowerController : IPowerController
    {
        private readonly SemaphoreSlim _called = new(0);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<bool> SleepAsync(
            int delayMilliseconds,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            _called.Release();
            return Task.FromResult(true);
        }

        public async Task<bool> WaitForCallsAsync(int expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Calls < expected)
            {
                await _called.WaitAsync(timeout.Token).ConfigureAwait(false);
            }

            return Calls >= expected;
        }
    }
}
