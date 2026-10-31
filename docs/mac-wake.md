# Mac command

Requires Python 3.9 or later and a Windows PC with wired Ethernet and Wake-on-LAN
enabled. Both devices must be on the same trusted LAN.

## Install

From the source checkout:

```sh
python3 mac/install.py
~/.local/bin/wake-pc setup
```

Enter the Windows PC's MAC and IP addresses from **Wake PC Companion → Setup &
status → Status**. Reserve the PC's IP in your router.

The installer copies the command to `~/.local/bin/wake-pc` and its standalone
runtime to `~/.local/share/wake-pc`. It needs no pip dependencies or background
service. Add `~/.local/bin` to PATH, or use the full path in agents and Shortcuts.
The installed command does not depend on keeping the source checkout.

## Use

```sh
wake-pc wake           # Send wake packets and wait up to 45 seconds
wake-pc --wait 90 wake # Allow more time to resume
wake-pc --wait 0 wake  # Send only
wake-pc status         # Check reachability without waking
wake-pc pair           # Enter the Windows companion's six-digit code
wake-pc sleep --yes    # Send one authenticated sleep request
```

Wake and status never prompt. The default command is wake; `launch` is a
compatibility alias. Setup and pairing are interactive. Agents should use an
already configured client. Sleep requires both a saved pairing and `--yes`;
use it only when the user has authorized putting the PC to sleep. Failed sleep
requests are not automatically retried.

To pair, open **Setup & status → Pair for Sleep…** on Windows, then run
`wake-pc pair`. Each client gets its own credential, preserving existing clients.
Re-pairing replaces this Mac's credential. Pair only on a trusted LAN: the initial
code exchange uses local HTTP.

Settings and credentials stay in
`~/Library/Application Support/Wake PC/config.json` (mode 600 on Mac).
Installation and removal preserve them. `--config-dir /path` selects an isolated
configuration. Never commit this file or include it in an agent's output.

## Results

- Packets sent means a local send succeeded, not that the PC woke.
- A reachable bridge or Steam port confirms a service, not streaming readiness.
- An authenticated bridge response verifies the paired service.
- Sleep accepted means Windows accepted the request; actual sleep is unverified.

Exit codes: `0` success, `2` no response within the wake/status check,
`1` configuration or request failure, `130` cancelled. A rejected credential is
an error, not an indication that the PC is asleep.

If wake gets no response, check Ethernet, Wake-on-LAN settings, the saved IP,
and LAN broadcast connectivity. The PC may be awake with Steam and the companion
stopped. The companion starts at Windows sign-in. Wake from full shutdown depends
on the hardware and must be tested separately.

## Update or remove

```sh
python3 "$HOME/.local/share/wake-pc/mac/install.py" --uninstall
# To update, then run from the new checkout:
python3 mac/install.py
```

The installer preserves unrelated commands and directories. `--prefix /path`
supports isolated installation tests. If the recorded Python executable moves,
reinstall. For Finder, the installed `mac/Wake PC.command` runs wake and waits for
Return before closing; agents should use the CLI instead.
