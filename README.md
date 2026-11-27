# ChoomSync — Cyberpunk 2077 (Patch 2.5) Tailscale Co-op Suite

A security-hardened, customizable 2-player co-op launcher and additive companion mod built on top of **NightCoop (0.2.2-beta)** + **NightCoop SDK v1** + **Cyber Engine Tweaks (1.38.1)** + **redscript (1.0.2)** for **Cyberpunk 2077 Patch 2.5**.

## Architecture

- **`mod/cet/ChoomSync/`** — Additive in-game companion mod built on the official MIT-licensed NightCoop SDK (`nightcoop_sdk.lua`). **Zero forbidden hooks** (100% compliant with NightCoop's `docs/do-not-hook.md`):
  - Native menu actions in **`Esc → Co-op → Mods…`** (Toggle Panel, Ping Location, Catch Up to Host, Sync Save / Unstick NPC, Time & Weather requests).
  - Compact in-game **Co-op Mini-HUD** (role, partner distance & compass direction, health bar, combat/vehicle/scene/downed badges, live Tailscale `Direct` vs `DERP` ping).
  - **Tactical Pings** (`REGROUP`, `ENEMY`, `LOOT`, `RIDE`) with temporary HUD map markers over NightCoop's encrypted SDK channel.
  - **Safe Guest Catch-Up & Far-Drift Alerts** when the host fast-travels far away.
  - **World Controls** (Time of Day & Weather requests via NightCoop's official `WorldAsk` vote/host flow).
- **`launcher/`** — Zero-dependency Node.js Tailscale Launcher (`launch.bat` / `server.mjs`):
  - Local web UI bound **strictly to `127.0.0.1`** with a per-launch random CSRF token.
  - Automatic Cyberpunk 2077 save backups before every co-op launch.
  - Patch 2.5 mod stack verification + stale `r6\cache\modded` cleaner + `nightcoop-hooks.txt` overlap monitor.
  - Live Tailscale diagnostics (`Direct` vs `DERP` + RTT) pushed into the in-game HUD.
  - **Opt-in Tailscale Peer Sync** (OFF by default; binds strictly to `100.x.x.x` CGNAT with shared pairing secret) for Host↔Guest mod hash parity checks and 1-click invite clipboard relay.
- **`scripts/setup-coop.ps1`** — Reproducible setup script for your friend's PC (fetches official binaries directly from `nightcoop.net` & GitHub with SHA-256 verification, never redistributes closed-source DLLs).

## Quick Start (For You & Your Friend)

1. Run `powershell -ExecutionPolicy Bypass -File .\scripts\setup-coop.ps1` (backs up saves, installs official NightCoop 0.2.2-beta + CET 1.38.1 + redscript 1.0.2 + ChoomSync).
2. Double-click **`launcher\launch.bat`** to open the ChoomSync Tailscale Launcher.
3. Click **Backup Saves & Launch Cyberpunk 2077**.
4. First time CET opens, bind a key for the CET overlay (e.g. `~` or `Insert`) and optionally bind ChoomSync quick hotkeys (`Toggle Panel`, `Quick Ping`, `Catch Up`) in CET → Bindings.
5. In Cyberpunk's Main Menu or Pause Menu → **Co-op**: Host starts a session (`Copy the invite`), Guest joins via `Join by code` (`Ctrl+V`).
