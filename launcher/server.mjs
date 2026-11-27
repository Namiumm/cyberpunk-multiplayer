// ChoomSync Tailscale Co-op Launcher for Cyberpunk 2077 (Patch 2.5) + NightCoop
// Zero external npm dependencies. Local UI binds strictly to 127.0.0.1 with a per-launch CSRF token.
// Optional Tailscale Peer Sync is OFF by default, binds strictly to 100.x.x.x (CGNAT 100.64.0.0/10),
// and authenticates every request with a shared pairing secret via crypto.timingSafeEqual.

import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import crypto from "node:crypto";
import { execFile, spawn } from "node:child_process";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";

const execFileAsync = promisify(execFile);
const __dirname = path.dirname(fileURLToPath(import.meta.url));

const LOCAL_HOST = "127.0.0.1";
const LOCAL_PORT = Number(process.env.CHOOMSYNC_PORT || 47890);
const PEER_PORT = 47891;
const SESSION_TOKEN = crypto.randomBytes(24).toString("hex");

// Known official Patch 2.5 hashes & references (never redistributes closed-source binaries)
const EXPECTED = {
  gameProductVersion: "2.5",
  nightCoopVersion: "0.2.2-beta",
  nightCoopUrl: "https://nightcoop.net/download.html",
  cetVersion: "v1.38.1",
  cetUrl: "https://github.com/maximegmd/CyberEngineTweaks/releases/latest",
  redscriptVersion: "v1.0.2",
  redscriptUrl: "https://github.com/jac3km4/redscript/releases/latest",
};

const CONFIG_PATH = path.join(__dirname, "launcher-config.json");

function loadLauncherConfig() {
  try {
    return JSON.parse(fs.readFileSync(CONFIG_PATH, "utf8"));
  } catch {
    return {
      gameDir: "",
      selectedPeerIp: "",
      pairingSecret: crypto.randomBytes(12).toString("base64url"),
      maxBackups: 8,
    };
  }
}

function saveLauncherConfig(cfg) {
  fs.writeFileSync(CONFIG_PATH, JSON.stringify(cfg, null, 2), "utf8");
}

let launcherCfg = loadLauncherConfig();

// ---------------------------------------------------------------------------
// Security & Validation Helpers
// ---------------------------------------------------------------------------

function isTailscaleIpv4(ip) {
  if (typeof ip !== "string") return false;
  const cleaned = ip.replace(/^::ffff:/, "");
  const parts = cleaned.split(".").map(Number);
  if (parts.length !== 4 || parts.some((n) => !Number.isInteger(n) || n < 0 || n > 255)) return false;
  // RFC 6598 CGNAT range used by Tailscale: 100.64.0.0/10
  return parts[0] === 100 && parts[1] >= 64 && parts[1] <= 127;
}

function safeEqual(a, b) {
  if (typeof a !== "string" || typeof b !== "string") return false;
  const ba = Buffer.from(a, "utf8");
  const bb = Buffer.from(b, "utf8");
  if (ba.length !== bb.length) return false;
  return crypto.timingSafeEqual(ba, bb);
}

function readJsonBody(req, maxBytes = 16384) {
  return new Promise((resolve, reject) => {
    let size = 0;
    const chunks = [];
    req.on("data", (c) => {
      size += c.length;
      if (size > maxBytes) {
        req.destroy();
        reject(new Error("Payload too large"));
        return;
      }
      chunks.push(c);
    });
    req.on("end", () => {
      try {
        const raw = Buffer.concat(chunks).toString("utf8").trim();
        resolve(raw ? JSON.parse(raw) : {});
      } catch {
        reject(new Error("Invalid JSON"));
      }
    });
    req.on("error", reject);
  });
}

function sendJson(res, status, data) {
  const body = JSON.stringify(data);
  res.writeHead(status, {
    "Content-Type": "application/json; charset=utf-8",
    "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff",
  });
  res.end(body);
}

function sha256File(filePath) {
  if (!fs.existsSync(filePath)) return null;
  const hash = crypto.createHash("sha256");
  hash.update(fs.readFileSync(filePath));
  return hash.digest("hex");
}

// ---------------------------------------------------------------------------
// Cyberpunk 2077 Auto-Discovery & Manifest Inspection
// ---------------------------------------------------------------------------

function detectGameDir() {
  if (launcherCfg.gameDir && fs.existsSync(path.join(launcherCfg.gameDir, "bin", "x64", "Cyberpunk2077.exe"))) {
    return launcherCfg.gameDir;
  }
  const candidates = [
    "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Cyberpunk 2077",
    "C:\\Program Files\\GOG Galaxy\\Games\\Cyberpunk 2077",
    "C:\\GOG Games\\Cyberpunk 2077",
    "C:\\Program Files\\Epic Games\\Cyberpunk2077",
    "D:\\SteamLibrary\\steamapps\\common\\Cyberpunk 2077",
    "E:\\SteamLibrary\\steamapps\\common\\Cyberpunk 2077",
  ];
  // Parse Steam libraryfolders.vdf if present
  const vdfPath = "C:\\Program Files (x86)\\Steam\\steamapps\\libraryfolders.vdf";
  if (fs.existsSync(vdfPath)) {
    try {
      const txt = fs.readFileSync(vdfPath, "utf8");
      for (const m of txt.matchAll(/"path"\s+"([^"]+)"/g)) {
        const lib = m[1].replace(/\\\\/g, "\\");
        candidates.unshift(path.join(lib, "steamapps", "common", "Cyberpunk 2077"));
      }
    } catch {
      // ignore
    }
  }
  for (const dir of candidates) {
    if (fs.existsSync(path.join(dir, "bin", "x64", "Cyberpunk2077.exe"))) {
      launcherCfg.gameDir = dir;
      saveLauncherConfig(launcherCfg);
      return dir;
    }
  }
  return null;
}

async function getExeVersion(exePath) {
  if (!fs.existsSync(exePath)) return null;
  try {
    const { stdout } = await execFileAsync(
      "powershell.exe",
      [
        "-NoProfile",
        "-Command",
        `(Get-Item -LiteralPath '${exePath.replace(/'/g, "''")}').VersionInfo | Select-Object FileVersion,ProductVersion | ConvertTo-Json -Compress`,
      ],
      { timeout: 6000 }
    );
    return JSON.parse(stdout.trim());
  } catch {
    return null;
  }
}

async function buildModManifest() {
  const gameDir = detectGameDir();
  if (!gameDir) {
    return { found: false, gameDir: null };
  }
  const exePath = path.join(gameDir, "bin", "x64", "Cyberpunk2077.exe");
  const nightCoopDll = path.join(gameDir, "red4ext", "plugins", "NightCoop", "NightCoop.dll");
  const red4extDll = path.join(gameDir, "bin", "x64", "winmm.dll");
  const cetDll = path.join(gameDir, "bin", "x64", "version.dll");
  const sccExe = path.join(gameDir, "engine", "tools", "scc.exe");
  const choomSyncLua = path.join(gameDir, "bin", "x64", "plugins", "cyber_engine_tweaks", "mods", "ChoomSync", "init.lua");
  const hooksLog = path.join(gameDir, "red4ext", "plugins", "NightCoop", "logs", "nightcoop-hooks.txt");
  const serversTxt = path.join(gameDir, "red4ext", "plugins", "NightCoop", "servers.txt");

  const exeVersion = await getExeVersion(exePath);
  const hooksWarnings = fs.existsSync(hooksLog)
    ? fs.readFileSync(hooksLog, "utf8").split(/\r?\n/).filter((l) => l.trim().length > 0).slice(0, 20)
    : [];

  return {
    found: true,
    hostName: os.hostname(),
    gameDir,
    exeVersion,
    components: {
      nightCoop: { installed: fs.existsSync(nightCoopDll), sha256: sha256File(nightCoopDll) },
      red4ext: { installed: fs.existsSync(red4extDll), sha256: sha256File(red4extDll) },
      cet: { installed: fs.existsSync(cetDll), sha256: sha256File(cetDll) },
      redscript: { installed: fs.existsSync(sccExe), sha256: sha256File(sccExe) },
      choomSync: { installed: fs.existsSync(choomSyncLua), sha256: sha256File(choomSyncLua) },
    },
    serversTxt: fs.existsSync(serversTxt) ? fs.readFileSync(serversTxt, "utf8") : "",
    hooksWarnings,
    expected: EXPECTED,
    checkedAt: new Date().toISOString(),
  };
}

// ---------------------------------------------------------------------------
// Tailscale Diagnostics & In-Game Telemetry Bridge
// ---------------------------------------------------------------------------

function findTailscaleExe() {
  const winPath = "C:\\Program Files\\Tailscale\\tailscale.exe";
  if (fs.existsSync(winPath)) return winPath;
  return "tailscale";
}

async function getTailscaleStatus() {
  const tsExe = findTailscaleExe();
  try {
    const { stdout } = await execFileAsync(tsExe, ["status", "--json"], { timeout: 6000 });
    const raw = JSON.parse(stdout);
    const selfIp = (raw.Self?.TailscaleIPs || []).find((ip) => isTailscaleIpv4(ip)) || "";
    const peers = Object.values(raw.Peer || {})
      .filter((p) => (p.TailscaleIPs || []).some((ip) => isTailscaleIpv4(ip)))
      .map((p) => {
        const ip = (p.TailscaleIPs || []).find((i) => isTailscaleIpv4(i)) || "";
        return {
          hostName: p.HostName || "",
          dnsName: (p.DNSName || "").replace(/\.$/, ""),
          ip,
          os: p.OS || "",
          online: Boolean(p.Online),
          direct: Boolean(p.CurAddr && p.CurAddr.length > 0),
          curAddr: p.CurAddr || "",
          relay: p.Relay || "",
        };
      })
      .sort((a, b) => Number(b.online) - Number(a.online) || a.hostName.localeCompare(b.hostName));

    let activePeer = peers.find((p) => p.ip === launcherCfg.selectedPeerIp) || peers.find((p) => p.online) || null;
    if (activePeer && activePeer.online) {
      try {
        const { stdout: pingOut } = await execFileAsync(tsExe, ["ping", "-c", "1", "--timeout=2s", activePeer.ip], {
          timeout: 3500,
        });
        const rttMatch = pingOut.match(/in\s+([\d.]+)\s*ms/i);
        if (rttMatch) activePeer = { ...activePeer, rttMs: Math.round(Number(rttMatch[1])) };
        if (/via\s+DERP/i.test(pingOut)) activePeer = { ...activePeer, direct: false };
        if (/via\s+\d+\.\d+\.\d+\.\d+:\d+/i.test(pingOut)) activePeer = { ...activePeer, direct: true };
      } catch {
        // ping timeout is non-fatal
      }
    }

    const result = {
      available: true,
      backendState: raw.BackendState || "Unknown",
      self: { hostName: raw.Self?.HostName || os.hostname(), ip: selfIp },
      peers,
      activePeer,
      updatedAt: new Date().toISOString(),
    };

    // Write lightweight JSON for in-game ChoomSync HUD if mod folder exists
    const gameDir = detectGameDir();
    if (gameDir) {
      const modDir = path.join(gameDir, "bin", "x64", "plugins", "cyber_engine_tweaks", "mods", "ChoomSync");
      if (fs.existsSync(modDir)) {
        try {
          fs.writeFileSync(
            path.join(modDir, "tailscale_status.json"),
            JSON.stringify({ self: result.self, activePeer: result.activePeer, updatedAt: result.updatedAt }),
            "utf8"
          );
        } catch {
          // ignore write races
        }
      }
    }
    return result;
  } catch (err) {
    return { available: false, error: err.message, peers: [], self: null, activePeer: null };
  }
}

// ---------------------------------------------------------------------------
// Save Backup, Cache Cleanup & Launching
// ---------------------------------------------------------------------------

function getSavesPaths() {
  const base = path.join(os.homedir(), "Saved Games", "CD Projekt Red");
  return {
    savesSrc: path.join(base, "Cyberpunk 2077"),
    backupRoot: path.join(base, "Cyberpunk 2077_Backups"),
  };
}

function listSaveBackups() {
  const { backupRoot } = getSavesPaths();
  if (!fs.existsSync(backupRoot)) return [];
  return fs
    .readdirSync(backupRoot, { withFileTypes: true })
    .filter((d) => d.isDirectory())
    .map((d) => {
      const full = path.join(backupRoot, d.name);
      const stat = fs.statSync(full);
      const count = fs.readdirSync(full, { withFileTypes: true }).filter((x) => x.isDirectory()).length;
      return { name: d.name, path: full, saveFolders: count, mtime: stat.mtime.toISOString() };
    })
    .sort((a, b) => b.mtime.localeCompare(a.mtime));
}

function createSaveBackup() {
  const { savesSrc, backupRoot } = getSavesPaths();
  if (!fs.existsSync(savesSrc)) {
    return { ok: false, error: "Saves folder not found: " + savesSrc };
  }
  fs.mkdirSync(backupRoot, { recursive: true });
  const stamp = new Date().toISOString().replace(/[:.]/g, "-").slice(0, 19);
  const dest = path.join(backupRoot, `coop_${stamp}`);
  fs.cpSync(savesSrc, dest, { recursive: true, force: true });

  // Prune old rolling coop_* backups (never delete initial pre-coop_* backups)
  const rolling = listSaveBackups().filter((b) => b.name.startsWith("coop_"));
  const maxKeep = Math.max(2, Number(launcherCfg.maxBackups) || 8);
  for (const old of rolling.slice(maxKeep)) {
    fs.rmSync(old.path, { recursive: true, force: true });
  }
  const folders = fs.readdirSync(dest, { withFileTypes: true }).filter((x) => x.isDirectory()).length;
  return { ok: true, backupPath: dest, saveFolders: folders };
}

function clearModdedScriptCache() {
  const gameDir = detectGameDir();
  if (!gameDir) return { ok: false, error: "Game directory not found" };
  const targets = [
    path.join(gameDir, "r6", "cache", "modded"),
    path.join(gameDir, "r6", "cache", "final.redscripts.modded"),
  ];
  const removed = [];
  for (const t of targets) {
    if (fs.existsSync(t)) {
      fs.rmSync(t, { recursive: true, force: true });
      removed.push(t);
    }
  }
  return { ok: true, removed };
}

function writeClipboardWindows(text) {
  return new Promise((resolve, reject) => {
    const child = spawn("clip.exe", [], { windowsHide: true });
    child.on("error", reject);
    child.on("close", (code) => (code === 0 ? resolve() : reject(new Error("clip.exe exit " + code))));
    child.stdin.end(text, "utf8");
  });
}

function launchCyberpunk() {
  const gameDir = detectGameDir();
  if (!gameDir) return { ok: false, error: "Game directory not found" };
  // Use Steam protocol if inside Steam library; otherwise spawn exe directly
  if (gameDir.toLowerCase().includes("\\steamapps\\common\\")) {
    spawn("cmd.exe", ["/c", "start", "", "steam://rungameid/1091500"], { detached: true, stdio: "ignore", windowsHide: true }).unref();
    return { ok: true, method: "steam://rungameid/1091500" };
  }
  const exe = path.join(gameDir, "bin", "x64", "Cyberpunk2077.exe");
  spawn(exe, [], { cwd: path.dirname(exe), detached: true, stdio: "ignore" }).unref();
  return { ok: true, method: exe };
}

// ---------------------------------------------------------------------------
// Opt-In Tailscale Peer Sync Server (Binds ONLY to 100.x.x.x, OFF by default)
// ---------------------------------------------------------------------------

let peerServer = null;
let peerBindIp = null;
let lastReceivedInvite = null;

async function startPeerSyncServer() {
  if (peerServer) return { ok: true, listening: `${peerBindIp}:${PEER_PORT}` };
  const ts = await getTailscaleStatus();
  if (!ts.available || !isTailscaleIpv4(ts.self?.ip)) {
    return { ok: false, error: "Tailscale IPv4 not available" };
  }
  const bindIp = ts.self.ip;
  return new Promise((resolve) => {
    const srv = http.createServer(async (req, res) => {
      const remoteIp = req.socket.remoteAddress || "";
      if (!isTailscaleIpv4(remoteIp)) {
        return sendJson(res, 403, { error: "Tailscale CGNAT peers only" });
      }
      const authHeader = String(req.headers["x-choomsync-pair"] || "");
      if (!safeEqual(authHeader, launcherCfg.pairingSecret)) {
        return sendJson(res, 401, { error: "Invalid pairing secret" });
      }
      if (req.method === "GET" && req.url === "/peer/manifest") {
        const m = await buildModManifest();
        return sendJson(res, 200, {
          hostName: m.hostName,
          exeVersion: m.exeVersion,
          components: m.components,
          checkedAt: m.checkedAt,
        });
      }
      if (req.method === "POST" && req.url === "/peer/invite") {
        try {
          const body = await readJsonBody(req, 1024);
          const code = String(body.code || "").trim();
          // Strict allowlist for NightCoop invite strings (alphanumeric, dashes, #, dots, colons, spaces)
          if (!code || code.length > 128 || !/^[A-Za-z0-9#.:_\-\s]+$/.test(code)) {
            return sendJson(res, 400, { error: "Invalid invite code format" });
          }
          await writeClipboardWindows(code);
          lastReceivedInvite = { code, fromIp: remoteIp.replace(/^::ffff:/, ""), receivedAt: new Date().toISOString() };
          return sendJson(res, 200, { ok: true, copiedToClipboard: true });
        } catch (err) {
          return sendJson(res, 400, { error: err.message });
        }
      }
      return sendJson(res, 404, { error: "Not found" });
    });

    srv.on("error", (err) => resolve({ ok: false, error: err.message }));
    srv.listen(PEER_PORT, bindIp, () => {
      peerServer = srv;
      peerBindIp = bindIp;
      resolve({ ok: true, listening: `${bindIp}:${PEER_PORT}` });
    });
  });
}

function stopPeerSyncServer() {
  if (peerServer) {
    peerServer.close();
    peerServer = null;
    peerBindIp = null;
  }
  return { ok: true, running: false };
}

function requestPeer(peerIp, method, urlPath, payload = null) {
  return new Promise((resolve, reject) => {
    if (!isTailscaleIpv4(peerIp)) {
      return reject(new Error("Target IP must be a Tailscale 100.64.0.0/10 address"));
    }
    const body = payload ? JSON.stringify(payload) : null;
    const req = http.request(
      {
        hostname: peerIp,
        port: PEER_PORT,
        path: urlPath,
        method,
        timeout: 4000,
        headers: {
          "X-ChoomSync-Pair": launcherCfg.pairingSecret,
          ...(body ? { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(body) } : {}),
        },
      },
      (res) => {
        const chunks = [];
        res.on("data", (c) => chunks.push(c));
        res.on("end", () => {
          try {
            const parsed = JSON.parse(Buffer.concat(chunks).toString("utf8"));
            if (res.statusCode >= 200 && res.statusCode < 300) resolve(parsed);
            else reject(new Error(parsed.error || `HTTP ${res.statusCode}`));
          } catch {
            reject(new Error(`HTTP ${res.statusCode}`));
          }
        });
      }
    );
    req.on("timeout", () => req.destroy(new Error("Peer timed out (is Peer Sync enabled on friend's launcher?)")));
    req.on("error", reject);
    if (body) req.write(body);
    req.end();
  });
}

// ---------------------------------------------------------------------------
// Local-Only Web UI Server (127.0.0.1 ONLY, Per-Launch Token Protected)
// ---------------------------------------------------------------------------

const indexHtml = fs.readFileSync(path.join(__dirname, "index.html"), "utf8");

function checkLocalAuth(req) {
  const host = String(req.headers.host || "");
  if (!host.startsWith("127.0.0.1:") && !host.startsWith("localhost:")) return false;
  const tok = String(req.headers["x-choomsync-token"] || "");
  return safeEqual(tok, SESSION_TOKEN);
}

const localServer = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://${LOCAL_HOST}:${LOCAL_PORT}`);

  if (req.method === "GET" && url.pathname === "/") {
    const qTok = url.searchParams.get("token") || "";
    if (!safeEqual(qTok, SESSION_TOKEN)) {
      res.writeHead(403, { "Content-Type": "text/plain" });
      res.end("Invalid or missing ChoomSync session token. Open via launch.bat.");
      return;
    }
    const html = indexHtml.replace("__CHOOMSYNC_TOKEN__", SESSION_TOKEN);
    res.writeHead(200, {
      "Content-Type": "text/html; charset=utf-8",
      "Cache-Control": "no-store",
      "Content-Security-Policy": "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'",
    });
    res.end(html);
    return;
  }

  if (!checkLocalAuth(req)) {
    return sendJson(res, 403, { error: "Unauthorized local API request" });
  }

  try {
    if (req.method === "GET" && url.pathname === "/api/status") {
      const [manifest, tailscale] = await Promise.all([buildModManifest(), getTailscaleStatus()]);
      return sendJson(res, 200, {
        manifest,
        tailscale,
        backups: listSaveBackups(),
        config: {
          gameDir: launcherCfg.gameDir,
          selectedPeerIp: launcherCfg.selectedPeerIp,
          pairingSecret: launcherCfg.pairingSecret,
        },
        peerSync: {
          running: Boolean(peerServer),
          listening: peerBindIp ? `${peerBindIp}:${PEER_PORT}` : null,
          lastReceivedInvite,
        },
      });
    }

    if (req.method === "POST" && url.pathname === "/api/config") {
      const body = await readJsonBody(req);
      if (typeof body.selectedPeerIp === "string" && (body.selectedPeerIp === "" || isTailscaleIpv4(body.selectedPeerIp))) {
        launcherCfg.selectedPeerIp = body.selectedPeerIp;
      }
      if (typeof body.pairingSecret === "string" && body.pairingSecret.length >= 8 && body.pairingSecret.length <= 64) {
        launcherCfg.pairingSecret = body.pairingSecret.trim();
      }
      saveLauncherConfig(launcherCfg);
      return sendJson(res, 200, { ok: true });
    }

    if (req.method === "POST" && url.pathname === "/api/backup") {
      return sendJson(res, 200, createSaveBackup());
    }

    if (req.method === "POST" && url.pathname === "/api/clear-cache") {
      return sendJson(res, 200, clearModdedScriptCache());
    }

    if (req.method === "POST" && url.pathname === "/api/servers-txt") {
      const body = await readJsonBody(req);
      const gameDir = detectGameDir();
      if (!gameDir) return sendJson(res, 400, { ok: false, error: "Game directory not found" });
      const dir = path.join(gameDir, "red4ext", "plugins", "NightCoop");
      if (!fs.existsSync(dir)) return sendJson(res, 400, { ok: false, error: "NightCoop folder not found" });
      const content = String(body.content || "").replace(/\r\n/g, "\n");
      fs.writeFileSync(path.join(dir, "servers.txt"), content, "utf8");
      return sendJson(res, 200, { ok: true });
    }

    if (req.method === "POST" && url.pathname === "/api/launch") {
      const body = await readJsonBody(req);
      let backup = null;
      if (body.backupFirst !== false) {
        backup = createSaveBackup();
      }
      await getTailscaleStatus(); // refresh tailscale_status.json for in-game HUD
      const launch = launchCyberpunk();
      return sendJson(res, 200, { ok: launch.ok, backup, launch });
    }

    if (req.method === "POST" && url.pathname === "/api/peer-sync/start") {
      return sendJson(res, 200, await startPeerSyncServer());
    }

    if (req.method === "POST" && url.pathname === "/api/peer-sync/stop") {
      return sendJson(res, 200, stopPeerSyncServer());
    }

    if (req.method === "POST" && url.pathname === "/api/peer/check") {
      const body = await readJsonBody(req);
      const ip = String(body.peerIp || launcherCfg.selectedPeerIp || "");
      const [localM, remoteM] = await Promise.all([buildModManifest(), requestPeer(ip, "GET", "/peer/manifest")]);
      const diffs = [];
      for (const key of ["nightCoop", "red4ext", "cet", "redscript", "choomSync"]) {
        const lh = localM.components?.[key]?.sha256 || null;
        const rh = remoteM.components?.[key]?.sha256 || null;
        if (lh !== rh) diffs.push({ component: key, local: lh ? lh.slice(0, 12) : "missing", remote: rh ? rh.slice(0, 12) : "missing" });
      }
      return sendJson(res, 200, { ok: true, inSync: diffs.length === 0, diffs, remote: remoteM });
    }

    if (req.method === "POST" && url.pathname === "/api/peer/send-invite") {
      const body = await readJsonBody(req);
      const ip = String(body.peerIp || launcherCfg.selectedPeerIp || "");
      const code = String(body.code || "").trim();
      const resp = await requestPeer(ip, "POST", "/peer/invite", { code });
      return sendJson(res, 200, resp);
    }

    return sendJson(res, 404, { error: "Unknown endpoint" });
  } catch (err) {
    return sendJson(res, 500, { error: err.message });
  }
});

// Keep in-game tailscale_status.json fresh every 10s while launcher runs
setInterval(() => {
  getTailscaleStatus().catch(() => {});
}, 10000);

localServer.listen(LOCAL_PORT, LOCAL_HOST, async () => {
  const url = `http://${LOCAL_HOST}:${LOCAL_PORT}/?token=${SESSION_TOKEN}`;
  console.log(`\n[ChoomSync] Launcher running locally at:\n  ${url}\n`);
  console.log("[ChoomSync] Bound strictly to 127.0.0.1 (no external ports open unless Peer Sync is enabled).");
  await getTailscaleStatus();
  if (!process.argv.includes("--no-open")) {
    spawn("cmd.exe", ["/c", "start", "", url], { detached: true, stdio: "ignore", windowsHide: true }).unref();
  }
});
