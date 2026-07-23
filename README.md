# StreamDeckRelay

Created by [Crowd Control](https://crowdcontrol.live) / Warp World.

Windows system-tray app that bridges the Elgato Stream Deck **MCP Actions**
endpoint across the network, so a two-PC streaming setup can use Stream Deck
integrations (Crowd Control, MCP clients, ...) from the PC that doesn't have
the Stream Deck attached.

## How creators use it

Run the same exe on **both** PCs. No configuration needed in the common case:

1. On launch, the app auto-detects its role: if the local Elgato MCP pipe
   exists (Stream Deck app running with MCP Actions enabled) it becomes the
   **host** and shares the pipe over TCP; otherwise it becomes the **client**.
2. The client finds the host automatically via UDP broadcast on the LAN.
3. The client recreates the pipe `\\.\pipe\elgato-mcp-streamdeck` locally, so
   apps on that PC see the Stream Deck as if it were attached.

Tray menu:

- **Status** line + color-coded icon (green = relaying, yellow = searching)
- **Mode** — Automatic / Host / Client override
- **Set host address...** — manual IP for networks where broadcast discovery
  is blocked (VLANs, some VPNs)
- **Start with Windows** — adds/removes an entry under `HKCU\...\Run`
- **Show log...** — recent relay activity

## Technical notes

- The Stream Deck app exposes its MCP tools over a local-only endpoint:
  named pipe `\\.\pipe\elgato-mcp-streamdeck` (Windows), unix socket
  `/tmp/elgato-mcp-streamdeck.sock` (macOS). The protocol is newline-delimited
  JSON, so the relay tunnels raw bytes and never parses traffic — it works
  with any present or future tools on the pipe.
- Each local connection on the client PC gets its own dedicated pipe
  connection on the host PC (TCP port 18675 by default; allow it through the
  host's firewall). Discovery uses UDP port 18676.
- Settings persist in `%APPDATA%\StreamDeckRelay\settings.json`.
- Single-instance: launching a second copy just exits.

## Build

```
dotnet build
# single-file exe for distribution:
dotnet publish -c Release -r win-x64
```

## Limitations

- Windows-only GUI (the relay core itself is portable if a mac build is ever
  needed).
- No authentication/encryption — intended for trusted LANs only.
- Auto-detection runs at startup (and when switching Mode back to Automatic).
  If the Stream Deck app is installed later on a PC already running as client,
  switch Mode manually or restart the relay.
