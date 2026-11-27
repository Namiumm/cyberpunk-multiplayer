# ChoomSync — Cyberpunk 2077 (Patch 2.5) Tailscale Co-op Suite

A security-hardened, customizable 2-player co-op launcher and additive companion mod built on top of **NightCoop (0.2.2-beta)** + **NightCoop SDK v1** + **Cyber Engine Tweaks (1.38.1)** + **redscript (1.0.2)** for **Cyberpunk 2077 Patch 2.5**.

## Architecture

- **`mod/cet/ChoomSync/` (`v0.2.0`)** — Additive in-game companion + base-game performance & bug-fix suite built on the official MIT-licensed NightCoop SDK (`nightcoop_sdk.lua`). **Zero forbidden hooks** (100% compliant with NightCoop's `docs/do-not-hook.md`):
  - **Base-Game Performance & Bug Fixes (`Perf & Bug Fixes` tab):** Live FPS / 1% Low / >33ms hitch counter, self-validating Patch 2.5 engine presets (`Balanced`, `Max FPS`, `Visual Clarity`, `Reset Vanilla`), and 1-click fixes for vanilla Cyberpunk 2077 bugs (stuck Relic/screen glitch FX, stuck combat lock, stuck scanner/controls restrictions, stuck vehicle summon, and solo stuck slow-mo).
  - Native menu actions in **`Esc → Co-op → Mods…`** (Toggle Panel, Ping Location, Catch Up to Host, Sync Save / Unstick NPC, Fix Stuck Glitch/Combat, Time & Weather requests).
  - Compact in-game **Co-op Mini-HUD** (role, live FPS/1% low, partner distance & compass direction, health bar, combat/vehicle/scene/downed badges, live Tailscale `Direct` vs `DERP` ping).
  - **Tactical Pings** (`REGROUP`, `ENEMY`, `LOOT`, `RIDE`) with temporary HUD map markers over NightCoop's encrypted SDK channel.
  - **Safe Guest Catch-Up & Far-Drift Alerts** when the host fast-travels far away.
- **`installer/ChoomSync.cs` (`ChoomSync.exe` `v0.2.0`)** — Single-file, zero-dependency, self-updating Windows GUI app:
  - **Auto-updates** ChoomSync Lua scripts, NightCoop/CET/redscript versions, and itself via SHA-256 verified `manifest.json`.
  - **Hardware-Aware Base-Game Performance & Stutter Pack** (100% reversible): writes GPU-VRAM-tiered `engine\config\platform\pc\choomsync_perf.ini`, caps high-cost/low-visual-impact settings in `UserSettings.json` (with `UserSettings.pre-choomsync.json` backup), clears stale Patch 2.5 DX12/GPU/redscript stutter caches, detects conflicting overlays (RTSS/Afterburner/Overwolf), and boosts `Cyberpunk2077.exe` to High CPU priority on launch.
- **`launcher/`** — Zero-dependency Node.js Tailscale Launcher (`launch.bat` / `server.mjs`):
  - Local web UI bound **strictly to `127.0.0.1`** with a per-launch random CSRF token.
  - Automatic Cyberpunk 2077 save backups before every co-op launch.
  - Patch 2.5 mod stack verification + stale `r6\cache\modded` cleaner + `nightcoop-hooks.txt` overlap monitor.
  - Live Tailscale diagnostics (`Direct` vs `DERP` + RTT) pushed into the in-game HUD.
  - **Opt-in Tailscale Peer Sync** (OFF by default; binds strictly to `100.x.x.x` CGNAT with shared pairing secret) for Host↔Guest mod hash parity checks and 1-click invite clipboard relay.
- **`scripts/setup-coop.ps1`** — Reproducible setup script for your friend's PC (fetches official binaries directly from `nightcoop.net` & GitHub with SHA-256 verification, never redistributes closed-source DLLs).

## Quick Start (For You & Your Friend — 1-Click Auto-Updating EXE)

1. Download **`ChoomSync.exe`** from the [latest GitHub Release](https://github.com/Namiumm/cyberpunk-multiplayer/releases/latest) (zero dependencies, 123 KB, runs on any Windows 10/11 PC).
2. Double-click `ChoomSync.exe` → click **`1. Install / Update Co-op`** (backs up saves, installs SHA-256 verified NightCoop `0.2.2-beta` + CET `1.38.1` + redscript `1.0.2` + ChoomSync companion mod, and creates a Desktop shortcut).
3. Click **`2. Auto-Update & Play!`** — automatically checks `manifest.json` for any new ChoomSync/NightCoop updates, backs up saves, and launches Cyberpunk 2077.
4. First time CET opens, bind a key for the CET overlay (e.g. `~` or `Insert`) and optionally bind ChoomSync quick hotkeys (`Toggle Panel`, `Quick Ping`, `Catch Up`) in CET → Bindings.
5. In Cyberpunk's Main Menu or Pause Menu → **Co-op**: Host starts a session (`Copy the invite`), Guest joins via `Join by code` (`Ctrl+V`).

*(Power users can also run the Tailscale web dashboard via `launcher\launch.bat` or CLI setup via `scripts\setup-coop.ps1`.)*
