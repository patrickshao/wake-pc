# Wake PC Companion for Windows

Wake PC's main feature is waking this PC from another device. Wake works without a
Windows app. This optional companion adds **Sleep PC** and a simple setup/status
view. It starts quietly after sign-in.

## Install

Windows installers are unsigned hobby releases. Check the accompanying SHA-256
file before installing. Windows may display an unknown-publisher or reputation
warning; signing is not required to build or release this project.

1. Download and open `wake-pc-sleep-bridge-latest-setup.exe` from the
   [latest GitHub release](https://github.com/patrickshao/wake-pc/releases/latest).
2. Approve the one Windows administrator prompt.
3. Finish Setup. It uses the standard Program Files location automatically.
4. Open **Wake PC Setup & Status** from the Start menu, or
   right-click the Wake PC notification-area icon and choose **Setup & status…**.
5. Copy the MAC address and IP address from **Status** into **Wake PC → Settings**
   in Decky, or run `wake-pc setup` on your Mac.
6. Choose **Save changes** and test Wake PC.

Pairing is optional and only enables **Sleep PC**. Choose **Pair for Sleep…**, then
enter the six-digit code in Decky or run `wake-pc pair` on your Mac. Use **Pair another for Sleep…** for each additional
device. Codes expire after 30 minutes and work once. Updating in place preserves
saved setup and paired devices; do not uninstall first.

Setup installs the app under Program Files, keeps nonsecret settings, DPAPI-encrypted
credentials, and the log under ProgramData, adds an Add/Remove Programs entry, and
starts it for signed-in Windows users through the standard machine Run key. It
creates one HTTP.sys URL reservation and one inbound Windows Firewall rule for TCP
`47653`, restricted to the local subnet on Private and Public profiles.

Interactive setup has no separate checkbox or license agreement page. For unattended
deployment, trusted-network confirmation must be explicit:

```text
wake-pc-sleep-bridge-v1.0.0-setup.exe /VERYSILENT /NORESTART /TRUSTEDNETWORK=1
```

## Use

The notification-area tooltip includes the installed version, for example
**Wake PC Companion 1.0.0 — Running**. Right-click
the icon to:

- see whether Wake is ready and Sleep PC is enabled;
- open **Setup & status…**;
- open **Advanced diagnostics…** to create one support report; or
- exit the companion until the next Windows sign-in.

The setup guide uses separate **Status** and **How to set up** pages. The status page
shows only the MAC address, IP address, and Sleep PC state. It warns when the saved
wired connection disappears or the PC address changes.

After a router reset, open **Setup & status…**, enter the appropriate current PC
address in your clients, and recreate the router's DHCP reservation. Sleep pairing remains
valid because it is independent of the IP address.

A same-executable supervisor restarts the bridge after an unexpected runtime failure.
Choosing **Exit until next sign-in** records a volatile session pause, so the
supervisor exits too. Signing out or restarting Windows clears that pause and the
normal startup entry launches the bridge again.

The runtime refuses to run as Administrator or `SYSTEM`. Administrator access is
used only by Setup and Uninstall for Program Files, HTTP.sys, and Firewall changes.

## Files and logs

Program:

```text
C:\Program Files\Wake PC Sleep Bridge\WakePcBridge.exe
```

Configuration and log:

```text
C:\ProgramData\WakePcSleepBridge\config.json
C:\ProgramData\WakePcSleepBridge\sleep-bridge.log
```

The per-device keys and active pairing material are encrypted at rest with Windows
DPAPI for the signed-in user. The JSON file contains ciphertext, not plaintext keys
or the six-digit code. A new key transfers once during each short-code pairing;
later sleep requests contain a device selector, HMAC signature, timestamp, and
single-use nonce.

## Uninstall

Open **Settings → Apps → Installed apps**, find **Wake PC Companion**, and choose
**Uninstall**. The standard uninstaller removes the startup entry, firewall rule,
HTTP URL reservation, program, configuration, pairing key, diagnostic files, and
log. A reboot is not required.

## Troubleshooting

- **Sleep PC does not appear:** create a fresh code from the tray menu, pair it,
  then reopen Wake PC.
- **Sleep Bridge unavailable:** verify both devices are on the same local network
  and that the saved PC IP matches Windows.
- **Code rejected or expired:** choose **Pair for Sleep…** or **Pair another for
  Sleep…** and use the new code
  within 30 minutes. Ten incorrect attempts lock the current code.
- **Windows stays awake:** inspect `sleep-bridge.log` for `SetSuspendState` errors.
- **Windows resumes without a lock screen:** enable require-sign-in-after-wake in
  Windows. Sleep Bridge intentionally does not call a separate lock API.
