# Sleep Bridge design and threat model

Sleep Bridge is an optional Windows companion for Wake PC. It provides one remote
action: request normal Windows sleep after a gaming session. It is not a general
remote shell, process manager, or PC-control service.

## User flow

1. Run the Windows Setup.exe once on the gaming PC.
2. Setup installs and launches the signed-in-user notification-area app. Its finish
   screen points to the optional setup guide in the Start menu and tray; the guide
   does not open automatically.
3. Open **Setup & status…**. Its **Status** tab shows the MAC address and PC address
   needed by Decky; **How to set up** provides the short wired-network checklist.
4. The tray menu reports whether Sleep PC is enabled. Open **Setup & status…**, then
   choose **Pair for Sleep…** or **Pair another for Sleep…** to view or renew the
   code. In Wake PC, open the
   **Sleep PC** section, enter it, and select **Pair for Sleep**. A new
   client-specific credential transfers and is stored automatically.
5. **Sleep PC** then appears in the **Power** section.
6. Press **Sleep PC**, then **Confirm sleep** within eight seconds.
7. Wake PC sends an authenticated request and waits for the bridge port to become
   unreachable before showing **Offline**. This does not prove Windows slept;
   confirm that on the PC.

Repeat **Pair another for Sleep…** for additional clients. Each successful pairing
mints an independent device ID and 256-bit key, so pairing another client does not
change or disconnect credentials that already work.

Sleep Bridge does not call `LockWorkStation`. The target Windows account must be
configured to require sign-in after waking. The manual acceptance test verifies
that the lock screen is shown after every Sleep -> Wake cycle.

## Components and privileges

- `WakePcBridge.exe` is a self-contained .NET 10 Windows x64 single-file app using
  only Microsoft platform APIs. The runtime has no third-party NuGet package.
- Inno Setup 7 installs it under Program Files, creates its Add/Remove Programs
  entry, grants local users access to the application-specific ProgramData directory,
  and registers `supervise` in the standard machine Run key.
- `supervise` launches the same executable in `run` mode. It restarts that child
  after an unexpected failure. **Exit until next sign-in** writes a volatile
  current-session pause, so supervisor and runtime both exit until the next sign-in.
- Runtime modes refuse administrator and `SYSTEM` execution. Setup calls only the
  explicit elevated `configure-system` and `unconfigure-system` modes for HTTP.sys
  and Firewall lifecycle.
- The app has no console or main window. A local notification-area icon shows that
  the bridge is running and provides Setup & status plus one Advanced diagnostics
  action. Startup is silent; signing
  in does not display a toast or balloon. The tray reports only the paired-device
  count and never displays device
  IDs or keys.
- The firewall rule is limited to TCP port `47653`, Windows Private/Public profiles,
  and `LocalSubnet`; setup does not require a fixed Legion address or fragile adapter
  binding.
- Windows 11 classifies new networks as Public by default. Interactive setup explains
  the LocalSubnet exposure without requiring a redundant checkbox; silent setup
  requires `/TRUSTEDNETWORK=1`. Setup does not change the Windows network profile.
- The rule is port-scoped rather than process-scoped because `HttpListener` receives
  traffic through the kernel-mode HTTP.sys service.
- The bridge uses its own TCP port instead of Steam Remote Play's reserved ports;
  sharing a TCP listener is not possible, and Steam's process authorization does not
  authorize HTTP.sys.
- Decky uses Python's standard library for HTTP and HMAC; no new Python or Node
  dependency is introduced.
- The bridge uses the built-in Windows Forms
  `Application.SetSuspendState(PowerState.Suspend, false, false)` wrapper as the
  signed-in user: sleep rather than hibernate, non-forced, with wake events enabled.

## Pairing protocol

Endpoint: `POST /v1/pair`

The request has no body and carries the six-digit code in
`X-Wake-PC-Pairing-Code`. Updated clients identify pairing protocol `2` in
`X-Wake-PC-Protocol`. A client that is pairing again also sends its private
`X-Wake-PC-Replace-Device-ID`; the bridge rotates only that registry entry instead
of creating an orphan credential. A new client omits this header. While a code is
active, the bridge stores the code, its
SHA-256 hash, and expiry inside the same current-user DPAPI-protected payload as the
device registry. It accepts no more than ten incorrect attempts and removes the
active code and hash after the first successful exchange. Expired codes are
replaced from **Pair for Sleep…** or **Pair another for Sleep…**. A successful JSON
response contains a random device ID and a newly generated 256-bit key. Decky stores
both as mode `0600` configuration and never exposes either to the frontend. The
response also includes the nonsecret bridge version.

For upgrade-order compatibility, a headerless older client is treated as the
existing `primary` device. An updated new client sends protocol `2` without a
replacement ID, so the bridge knows to add an independent registry entry. This
allows either the Deck plugin or Windows bridge to be updated first.

```json
{
  "ok": true,
  "message": "Paired",
  "device_id": "0123456789abcdef0123456789abcdef",
  "secret_hex": "<64 lowercase hexadecimal characters>",
  "version": "1.0.0"
}
```

Pairing uses unencrypted HTTP on the approved local network. The short exposure
window, one-success limit, failed-attempt lockout, Private/Public profile, and
LocalSubnet firewall scope reduce but do not eliminate the risk of another local
device observing the exchange. The tray can issue a fresh short code without
replacing any existing device key.

## Sleep request protocol

Endpoint: `POST /v1/sleep`

Required headers:

- `X-Wake-PC-Device-ID`: `primary` for the migrated/first client, otherwise the
  32-character identifier returned by pairing.
- `X-Wake-PC-Timestamp`: Unix time in seconds.
- `X-Wake-PC-Nonce`: URL-safe random value generated for one request.
- `X-Wake-PC-Signature`: lowercase hexadecimal HMAC-SHA256.

The bridge selects the client-specific HMAC key using the device ID. The key is the
32-byte value represented by the returned 64-character hexadecimal string. The
canonical UTF-8 message remains:

```text
POST\n/v1/sleep\n<TIMESTAMP>\n<NONCE>\n
```

The bridge accepts a timestamp within 60 seconds, remembers accepted nonces for
two minutes, compares signatures in constant time, and returns HTTP 202 before
initiating sleep. A full nonce cache rejects new requests until entries expire;
it never evicts a live nonce. After pairing, sleep requests never transmit the key itself.

## Version status protocol

Endpoint: `POST /v1/status`

The request uses the same device ID, timestamp, nonce, and HMAC headers as the sleep
route, with `/v1/status` in the canonical message. It returns the nonsecret bridge
version without invoking a power action. Decky uses it to display the installed
Windows version and to confirm authenticated bridge reachability. A `404` from an
older paired bridge is treated as compatible but unable to report its version.

Protocol test vector:

```text
key:       000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f
timestamp: 1784304000
nonce:     AQIDBAUGBwgJCgsMDQ4PEA
signature: daf5287d35424fcadab946bd2fa473f9af70c836eef68064d51c72d43b2f0683
```

## Threat model

Controls cover:

- Unauthorized LAN clients: port-specific LocalSubnet firewall scope plus HMAC
  authentication for every power action.
- Pairing-code guessing: 30-minute expiration, ten-attempt lockout, and one
  successful exchange.
- Captured-request replay: timestamp window and nonce cache.
- Accidental controller input: two-press confirmation with an eight-second window.
- Arbitrary command execution: the listener has three fixed routes, accepts no
  request body or command argument, and exposes only pairing, authenticated version
  status, and normal sleep.
- Pairing-key disclosure at rest: Windows encrypts the per-device credential
  registry, active pairing code, hash, and expiry together with user-scoped DPAPI.
  The shared ProgramData JSON contains only ciphertext and nonsecret runtime
  settings. Decky stores its device ID and key as mode `0600`.
- Broad network exposure: installer creates only one port/profile-scoped LocalSubnet
  firewall rule, shows an interactive local-network disclosure, and requires
  `/TRUSTEDNETWORK=1` for unattended installation.
- Router reconfiguration: the tray and secret-free diagnostic report list current
  IPv4/prefix, gateway, DHCP state, and bridge target. Decky points users there when
  the saved PC address is no longer reachable; changing the address does not require
  pairing again. The report also includes the baseline health result and
  expected/current IPv4 addresses while omitting all MAC addresses.
- Network baseline: Wake setup accepts only a physical wired adapter, never Wi-Fi or
  a virtual adapter. After explicit confirmation, its expected MAC and IPv4 address
  are stored as nonsecret local configuration. The tray and guide compare them with
  live state and flag an address change, missing adapter, or missing gateway. The
  MAC is not written to logs or diagnostic JSON, and the app neither collects router
  credentials nor attempts to modify the router.

Residual risks:

- An administrator compromise on either device can recover that client's key or
  replace pairing state.
- Another local Windows account can read, replace, or delete the shared ciphertext.
  DPAPI prevents that account from decrypting the credential or forging modified
  pairing contents, but it can still cause denial of service. An administrator or a
  process running as the paired Windows user remains able to recover device keys.
- A hostile LAN client can cause denial of service even though it cannot authenticate.
- Nonce replay history and failed pairing-attempt counts are held in memory. A
  companion restart resets them; a captured signed request still within its
  timestamp window may be replayed after a restart. Pairing codes still expire
  and remain consumed after successful pairing across restarts.
- A one-time pairing response contains the new device key over unencrypted local
  HTTP. A device able to observe traffic during that brief exchange could obtain it;
  request a new tray code and re-pair that device to rotate its credential if this
  is suspected.
- Authenticated sleep requests are not encrypted. They contain no key or sensitive
  body, but timing and endpoint metadata remain visible on the LAN.
- A Windows driver or policy can still reject sleep. Decky therefore verifies the
  bridge becomes unreachable rather than treating HTTP 202 alone as completion.

## Manual acceptance

Run at least five complete cycles:

1. Wake the PC from Decky.
2. Start and end a Steam Remote Play session.
3. Press **Sleep PC**, then **Confirm sleep**.
4. Confirm the PC actually sleeps and Decky reports the companion is no longer reachable.
5. Wake the PC again and confirm Windows displays the lock/sign-in screen.

Also verify an incorrect short code is rejected, a used code cannot pair again, ten
bad attempts lock pairing, the Sleep button requires its second confirmation press,
and Wake remains functional with Sleep Bridge stopped.
