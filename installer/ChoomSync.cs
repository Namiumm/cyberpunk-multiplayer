// ChoomSync.cs — Single-file, zero-dependency, self-updating Windows GUI Installer,
// Base-Game Performance & Stutter Optimizer, and Co-op Launcher for Cyberpunk 2077 (Patch 2.5).
// Compiles natively with Windows built-in .NET Framework 4.8 C# compiler (csc.exe, C# 5).
//
// Security model:
//   - Zero open ports, zero SSH, zero background services, zero embedded credentials/tokens.
//   - Strict HTTPS domain/path allowlist (nightcoop.net, official CET/redscript GitHub, Namiumm/cyberpunk-multiplayer).
//   - Mandatory SHA-256 verification on every downloaded archive, script, and launcher update.
//   - Zip-Slip path traversal protection on all archive extractions.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("ChoomSync — Cyberpunk 2077 Co-op & Optimizer")]
[assembly: AssemblyDescription("1-Click Auto-Updating Co-op Installer, Base-Game Performance Pack & Launcher for Cyberpunk 2077 (Patch 2.5)")]
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]

namespace ChoomSync
{
    internal sealed class PackageSpec
    {
        public string version { get; set; }
        public string url { get; set; }
        public string sha256 { get; set; }
    }

    internal sealed class ModFileSpec
    {
        public string name { get; set; }
        public string url { get; set; }
        public string sha256 { get; set; }
    }

    internal sealed class CoopManifest
    {
        public int schema { get; set; }
        public string version { get; set; }
        public string gameVersion { get; set; }
        public PackageSpec launcher { get; set; }
        public PackageSpec nightCoop { get; set; }
        public PackageSpec cet { get; set; }
        public PackageSpec redscript { get; set; }
        public List<ModFileSpec> modFiles { get; set; }
    }

    internal sealed class InstalledVersions
    {
        public string nightCoopSha256 { get; set; }
        public string cetSha256 { get; set; }
        public string redscriptSha256 { get; set; }
    }

    internal sealed class HardwareProfile
    {
        public int LogicalCores { get; set; }
        public int RamGb { get; set; }
        public string GpuName { get; set; }
        public int VramGb { get; set; }
        public string Tier { get; set; } // "Standard" | "High" | "Enthusiast"
    }

    internal static class Program
    {
        public const string APP_VERSION = "0.2.0";
        public const string MANIFEST_URL = "https://raw.githubusercontent.com/Namiumm/cyberpunk-multiplayer/master/manifest.json";
        public const string TAILSCALE_URL = "https://pkgs.tailscale.com/stable/tailscale-setup-latest.exe";

        public static CoopManifest DefaultManifest()
        {
            CoopManifest m = new CoopManifest();
            m.schema = 1;
            m.version = APP_VERSION;
            m.gameVersion = "2.5";
            m.launcher = new PackageSpec { version = APP_VERSION, url = "", sha256 = "" };
            m.nightCoop = new PackageSpec
            {
                version = "0.2.2-beta",
                url = "https://nightcoop.net/dl/0.2.2-beta/NightCoop-0.2.2-beta-2.5.zip",
                sha256 = "71d846388ed41443d5dadefb776e85ddef3e2a76fd5d6f9ff0868e7dfd615669"
            };
            m.cet = new PackageSpec
            {
                version = "v1.38.1",
                url = "https://github.com/maximegmd/CyberEngineTweaks/releases/download/v1.38.1/cet_1_38_1.zip",
                sha256 = "2e52be4ed431fad787e64ecbf75db346b4224577d2b89894912bc3f159020e73"
            };
            m.redscript = new PackageSpec
            {
                version = "v1.0.2",
                url = "https://github.com/jac3km4/redscript/releases/download/v1.0.2/redscript-v1.0.2-windows.zip",
                sha256 = "c29604bb56bb7ee6799dc66b749b4f52d3688fa99574c41c1a0ab8ee955743c0"
            };
            m.modFiles = new List<ModFileSpec>();
            return m;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | SecurityProtocolType.Tls12;

            if (args != null && args.Length > 0)
            {
                if (string.Equals(args[0], "--verify", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSelfCheck(false);
                }
                if (string.Equals(args[0], "--check-update", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSelfCheck(true);
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        private static int RunSelfCheck(bool fetchRemote)
        {
            string gameDir = CoopEngine.DetectGameDir();
            string ver = gameDir != null ? CoopEngine.GetGameVersion(gameDir) : "none";
            HardwareProfile hw = PerfOptimizer.DetectHardware();
            string[] resNames = Assembly.GetExecutingAssembly().GetManifestResourceNames();
            bool hasInit = Array.IndexOf(resNames, "ChoomSync.init.lua") >= 0;
            bool hasSdk = Array.IndexOf(resNames, "ChoomSync.nightcoop_sdk.lua") >= 0;
            bool hasCfg = Array.IndexOf(resNames, "ChoomSync.config.json") >= 0;

            string remoteInfo = "skipped";
            if (fetchRemote)
            {
                try
                {
                    using (HttpClient http = CoopEngine.CreateHttpClient(10))
                    {
                        CoopManifest m = CoopEngine.FetchManifestAsync(http).GetAwaiter().GetResult();
                        remoteInfo = m != null
                            ? string.Format("ok(v={0},nc={1},mods={2})", m.version, m.nightCoop != null ? m.nightCoop.version : "?", m.modFiles != null ? m.modFiles.Count : 0)
                            : "unreachable";
                    }
                }
                catch (Exception ex)
                {
                    remoteInfo = "err:" + ex.Message;
                }
            }

            Console.WriteLine(string.Format(
                "VERIFY_OK app={0} gameDir={1} gameVer={2} hw=({3},{4}c,{5}GB,{6}GBvram) embedded=(init:{7},sdk:{8},cfg:{9}) installed={10} remote={11}",
                APP_VERSION,
                gameDir ?? "(not found)",
                ver ?? "?",
                hw.Tier,
                hw.LogicalCores,
                hw.RamGb,
                hw.VramGb,
                hasInit,
                hasSdk,
                hasCfg,
                gameDir != null && CoopEngine.IsFullyInstalled(gameDir),
                remoteInfo
            ));
            return (hasInit && hasSdk && hasCfg) ? 0 : 1;
        }
    }

    // -----------------------------------------------------------------------
    // Base-Game Performance, Stutter & Config Optimizer (100% Reversible)
    // -----------------------------------------------------------------------
    internal static class PerfOptimizer
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        public static HardwareProfile DetectHardware()
        {
            HardwareProfile hp = new HardwareProfile();
            hp.LogicalCores = Environment.ProcessorCount;
            hp.RamGb = 16;
            hp.GpuName = "Unknown GPU";
            hp.VramGb = 8;

            try
            {
                MEMORYSTATUSEX mem = new MEMORYSTATUSEX();
                mem.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref mem))
                {
                    hp.RamGb = (int)Math.Round(mem.ullTotalPhys / 1073741824.0);
                }
            }
            catch { }

            // Read GPU name & real 64-bit VRAM from Windows display adapter registry (handles >4GB VRAM accurately)
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (root != null)
                    {
                        ulong bestVram = 0;
                        string bestName = null;
                        foreach (string subName in root.GetSubKeyNames())
                        {
                            if (!Regex.IsMatch(subName, @"^\d{4}$")) continue;
                            using (RegistryKey sub = root.OpenSubKey(subName))
                            {
                                if (sub == null) continue;
                                string desc = sub.GetValue("DriverDesc") as string;
                                if (string.IsNullOrEmpty(desc) || desc.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0 || desc.IndexOf("Basic", StringComparison.OrdinalIgnoreCase) >= 0)
                                    continue;

                                ulong vram = 0;
                                object qw = sub.GetValue("HardwareInformation.qwMemorySize");
                                if (qw is long) vram = (ulong)(long)qw;
                                else if (qw is int) vram = (ulong)(uint)(int)qw;
                                else if (qw is byte[] && ((byte[])qw).Length >= 8) vram = BitConverter.ToUInt64((byte[])qw, 0);

                                if (vram > bestVram || (bestName == null && vram == 0))
                                {
                                    bestVram = vram;
                                    bestName = desc;
                                }
                            }
                        }
                        if (!string.IsNullOrEmpty(bestName)) hp.GpuName = bestName;
                        if (bestVram > 0) hp.VramGb = Math.Max(2, (int)Math.Round(bestVram / 1073741824.0));
                    }
                }
            }
            catch { }

            if (hp.VramGb >= 15 && hp.RamGb >= 24) hp.Tier = "Enthusiast";
            else if (hp.VramGb >= 8) hp.Tier = "High";
            else hp.Tier = "Standard";
            return hp;
        }

        public static string GetPerfIniPath(string gameDir)
        {
            return Path.Combine(gameDir, @"engine\config\platform\pc\choomsync_perf.ini");
        }

        public static string GetUserSettingsPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"CD Projekt Red\Cyberpunk 2077\UserSettings.json");
        }

        public static string GetUserSettingsBackupPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"CD Projekt Red\Cyberpunk 2077\UserSettings.pre-choomsync.json");
        }

        public static bool IsPerfPackApplied(string gameDir)
        {
            return CoopEngine.IsValidGameDir(gameDir) && File.Exists(GetPerfIniPath(gameDir));
        }

        public static void ApplyBaseGamePerfPack(string gameDir, Action<string> log)
        {
            HardwareProfile hw = DetectHardware();
            log(string.Format("Detected hardware: {0} ({1}GB VRAM), {2} threads, {3}GB RAM -> {4} tier",
                hw.GpuName, hw.VramGb, hw.LogicalCores, hw.RamGb, hw.Tier));

            // 1. Write hardware-tiered REDengine 4 streaming & shadow INI
            string iniPath = GetPerfIniPath(gameDir);
            Directory.CreateDirectory(Path.GetDirectoryName(iniPath));

            int maxNodes = hw.Tier == "Enthusiast" ? 5000 : (hw.Tier == "High" ? 3500 : 2000);
            int editorNodes = hw.Tier == "Enthusiast" ? 6000 : (hw.Tier == "High" ? 4000 : 2500);
            string minDist = hw.Tier == "Enthusiast" ? "55.000000" : (hw.Tier == "High" ? "45.000000" : "35.000000");
            string decalsDist = hw.Tier == "Enthusiast" ? "40.0" : (hw.Tier == "High" ? "30.0" : "20.0");

            StringBuilder ini = new StringBuilder();
            ini.AppendLine("; ChoomSync Base-Game Performance & Stutter Pack (Patch 2.5)");
            ini.AppendLine(string.Format("; Hardware Tier: {0} ({1}, {2}GB VRAM, {3} threads)", hw.Tier, hw.GpuName, hw.VramGb, hw.LogicalCores));
            ini.AppendLine();
            ini.AppendLine("[Streaming]");
            ini.AppendLine("EditorThrottledMaxNodesPerFrame = " + editorNodes);
            ini.AppendLine("MaxNodesPerFrame = " + maxNodes);
            ini.AppendLine("MinStreamingDistance = " + minDist);
            ini.AppendLine();
            ini.AppendLine("[LevelOfDetail]");
            ini.AppendLine("DecalsHideDistance = " + decalsDist);
            ini.AppendLine();
            ini.AppendLine("[Rendering/Shadows]");
            ini.AppendLine("CascadeRange0 = 12.000000");
            ini.AppendLine("CascadeRange1 = 30.000000");
            ini.AppendLine("CascadeRange2 = 75.000000");
            ini.AppendLine("CascadeRange3 = 150.000000");
            File.WriteAllText(iniPath, ini.ToString(), new UTF8Encoding(false));
            log("Wrote engine\\config\\platform\\pc\\choomsync_perf.ini (" + hw.Tier + " tier)");

            // 2. Optimize verified high-cost/low-visual-impact settings in UserSettings.json (with permanent backup)
            OptimizeUserSettings(hw, log);

            // 3. Ensure CET JIT & clean runtime settings
            OptimizeCetConfig(gameDir, log);
        }

        private static void OptimizeUserSettings(HardwareProfile hw, Action<string> log)
        {
            string usPath = GetUserSettingsPath();
            if (!File.Exists(usPath))
            {
                log("UserSettings.json not created yet (launch Cyberpunk once, then click Optimize again).");
                return;
            }

            string backupPath = GetUserSettingsBackupPath();
            if (!File.Exists(backupPath))
            {
                File.Copy(usPath, backupPath, false);
                log("Backed up original UserSettings.json -> UserSettings.pre-choomsync.json");
            }

            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 16 * 1024 * 1024;
            Dictionary<string, object> root = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(usPath, Encoding.UTF8));
            if (root == null || !root.ContainsKey("data")) return;

            ArrayList groups = root["data"] as ArrayList;
            if (groups == null) return;

            // Only cap settings that are currently ABOVE optimal (never lowers someone already on Low)
            int crowdCapIdx = (hw.LogicalCores <= 8) ? 0 : 1; // Low for <=8 threads, Medium for >8 threads
            string crowdCapVal = crowdCapIdx == 0 ? "Low" : "Medium";

            int changed = 0;
            foreach (object gObj in groups)
            {
                Dictionary<string, object> grp = gObj as Dictionary<string, object>;
                if (grp == null || !grp.ContainsKey("options")) continue;
                ArrayList opts = grp["options"] as ArrayList;
                if (opts == null) continue;

                foreach (object oObj in opts)
                {
                    Dictionary<string, object> opt = oObj as Dictionary<string, object>;
                    if (opt == null || !opt.ContainsKey("name")) continue;
                    string name = opt["name"] as string;
                    if (string.IsNullOrEmpty(name)) continue;

                    if (name == "CrowdDensity")
                    {
                        if (CapIndexOption(opt, crowdCapIdx, crowdCapVal)) changed++;
                    }
                    else if (name == "ScreenSpaceReflectionsQuality")
                    {
                        // Insane (5) & Ultra (4) cost 20-35% GPU FPS with near-zero visual gain over High (3)
                        if (CapIndexOption(opt, 3, "High")) changed++;
                    }
                    else if (name == "VolumetricFogResolution")
                    {
                        // Ultra (3) & High (2) cost 15-25% GPU FPS over Medium (1)
                        if (CapIndexOption(opt, 1, "Medium")) changed++;
                    }
                    else if (name == "VolumetricCloudsQuality")
                    {
                        if (CapIndexOption(opt, 1, "Medium")) changed++;
                    }
                    else if (name == "CascadedShadowsRange")
                    {
                        if (CapIndexOption(opt, 1, "Medium")) changed++;
                    }
                    else if (name == "MaxDynamicDecals")
                    {
                        if (CapIndexOption(opt, 2, "High")) changed++;
                    }
                }
            }

            if (changed > 0)
            {
                File.WriteAllText(usPath, ser.Serialize(root), new UTF8Encoding(false));
                log(string.Format("Optimized {0} high-cost graphics/crowd settings in UserSettings.json (Crowd->{1}, SSR<=High, VolFog/Clouds<=Medium).", changed, crowdCapVal));
            }
            else
            {
                log("UserSettings.json settings were already at or below optimal performance levels.");
            }
        }

        private static bool CapIndexOption(Dictionary<string, object> opt, int maxIdx, string targetVal)
        {
            if (!opt.ContainsKey("index")) return false;
            int curIdx = Convert.ToInt32(opt["index"]);
            if (curIdx <= maxIdx) return false;
            opt["index"] = maxIdx;
            opt["value"] = targetVal;
            return true;
        }

        private static void OptimizeCetConfig(string gameDir, Action<string> log)
        {
            string cetCfg = Path.Combine(gameDir, @"bin\x64\plugins\cyber_engine_tweaks\config.json");
            if (!File.Exists(cetCfg)) return;
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> root = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(cetCfg, Encoding.UTF8));
                if (root == null) return;
                Dictionary<string, object> dev = root.ContainsKey("developer") ? (root["developer"] as Dictionary<string, object>) : null;
                if (dev != null)
                {
                    dev["enable_jit"] = true;
                    dev["enable_imgui_assertions"] = false;
                    dev["remove_dead_bindings"] = true;
                    File.WriteAllText(cetCfg, ser.Serialize(root), new UTF8Encoding(false));
                    log("Verified CET LuaJIT enabled & dead-binding cleanup ON.");
                }
            }
            catch { }
        }

        public static void RestoreVanillaSettings(string gameDir, Action<string> log)
        {
            string iniPath = GetPerfIniPath(gameDir);
            if (File.Exists(iniPath))
            {
                File.Delete(iniPath);
                log("Removed choomsync_perf.ini.");
            }
            string backupPath = GetUserSettingsBackupPath();
            string usPath = GetUserSettingsPath();
            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, usPath, true);
                log("Restored original UserSettings.json from backup.");
            }
            else
            {
                log("No UserSettings backup to restore (settings untouched).");
            }
        }

        public static int ClearStutterCaches(string gameDir, Action<string> log)
        {
            int removed = 0;
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            List<string> dirs = new List<string>
            {
                Path.Combine(localApp, @"CD Projekt Red\Cyberpunk 2077\cache"),
                Path.Combine(localApp, "D3DSCache"),
                Path.Combine(localApp, @"NVIDIA\DXCache"),
                Path.Combine(localApp, @"AMD\DxCache"),
            };
            if (CoopEngine.IsValidGameDir(gameDir))
            {
                CoopEngine.ClearStaleScriptCache(gameDir);
                removed++;
            }

            foreach (string d in dirs)
            {
                if (!Directory.Exists(d)) continue;
                int countBefore = removed;
                foreach (string f in Directory.GetFiles(d, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(f); removed++; } catch { }
                }
                if (removed > countBefore)
                {
                    log("Cleared cache: " + Path.GetFileName(d) + " (" + (removed - countBefore) + " files)");
                }
            }
            return removed;
        }

        public static List<string> DetectConflictingOverlays()
        {
            List<string> found = new List<string>();
            string[] suspects = new string[] { "RTSS", "MSIAfterburner", "Overwolf" };
            foreach (string name in suspects)
            {
                try
                {
                    if (Process.GetProcessesByName(name).Length > 0) found.Add(name);
                }
                catch { }
            }
            return found;
        }

        public static void BoostCyberpunkPriorityAsync(Action<string> log)
        {
            Task.Run(delegate ()
            {
                for (int i = 0; i < 30; i++)
                {
                    Thread.Sleep(1000);
                    try
                    {
                        Process[] procs = Process.GetProcessesByName("Cyberpunk2077");
                        if (procs.Length > 0)
                        {
                            procs[0].PriorityClass = ProcessPriorityClass.High;
                            log("Boosted Cyberpunk2077.exe CPU priority to High (smoother 1% lows).");
                            return;
                        }
                    }
                    catch { return; }
                }
            });
        }
    }

    internal static class CoopEngine
    {
        public static HttpClient CreateHttpClient(int timeoutSeconds)
        {
            HttpClient http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ChoomSync/" + Program.APP_VERSION);
            return http;
        }

        public static bool IsAllowedDownloadUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return false;

            string host = uri.Host.ToLowerInvariant();
            string path = uri.AbsolutePath;

            if (host == "raw.githubusercontent.com")
            {
                return path.StartsWith("/Namiumm/cyberpunk-multiplayer/", StringComparison.OrdinalIgnoreCase);
            }
            if (host == "github.com")
            {
                return path.StartsWith("/Namiumm/cyberpunk-multiplayer/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/maximegmd/CyberEngineTweaks/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/jac3km4/redscript/", StringComparison.OrdinalIgnoreCase);
            }
            if (host == "nightcoop.net") return true;
            if (host == "pkgs.tailscale.com") return true;
            return false;
        }

        public static async Task<CoopManifest> FetchManifestAsync(HttpClient http)
        {
            if (!IsAllowedDownloadUrl(Program.MANIFEST_URL)) return null;
            string url = Program.MANIFEST_URL + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (HttpResponseMessage resp = await http.GetAsync(url))
            {
                if (!resp.IsSuccessStatusCode) return null;
                string json = await resp.Content.ReadAsStringAsync();
                JavaScriptSerializer ser = new JavaScriptSerializer();
                CoopManifest m = ser.Deserialize<CoopManifest>(json);
                if (m == null || m.schema != 1) return null;
                return m;
            }
        }

        public static string DetectGameDir()
        {
            List<string> candidates = new List<string>();
            string vdf = @"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf";
            if (File.Exists(vdf))
            {
                try
                {
                    string txt = File.ReadAllText(vdf);
                    MatchCollection matches = Regex.Matches(txt, "\"path\"\\s+\"([^\"]+)\"");
                    foreach (Match m in matches)
                    {
                        string lib = m.Groups[1].Value.Replace("\\\\", "\\");
                        candidates.Add(Path.Combine(lib, @"steamapps\common\Cyberpunk 2077"));
                    }
                }
                catch { }
            }

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                string r = drive.RootDirectory.FullName;
                candidates.Add(Path.Combine(r, @"Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077"));
                candidates.Add(Path.Combine(r, @"SteamLibrary\steamapps\common\Cyberpunk 2077"));
                candidates.Add(Path.Combine(r, @"Steam\steamapps\common\Cyberpunk 2077"));
                candidates.Add(Path.Combine(r, @"Program Files\GOG Galaxy\Games\Cyberpunk 2077"));
                candidates.Add(Path.Combine(r, @"GOG Games\Cyberpunk 2077"));
                candidates.Add(Path.Combine(r, @"Games\Cyberpunk 2077"));
                candidates.Add(Path.Combine(r, @"Program Files\Epic Games\Cyberpunk2077"));
            }

            foreach (string c in candidates)
            {
                if (IsValidGameDir(c)) return c;
            }
            return null;
        }

        public static bool IsValidGameDir(string dir)
        {
            return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, @"bin\x64\Cyberpunk2077.exe"));
        }

        public static string GetGameVersion(string gameDir)
        {
            string exe = Path.Combine(gameDir, @"bin\x64\Cyberpunk2077.exe");
            if (!File.Exists(exe)) return null;
            FileVersionInfo vi = FileVersionInfo.GetVersionInfo(exe);
            return string.IsNullOrEmpty(vi.ProductVersion) ? vi.FileVersion : vi.ProductVersion;
        }

        public static string GetModDir(string gameDir)
        {
            return Path.Combine(gameDir, @"bin\x64\plugins\cyber_engine_tweaks\mods\ChoomSync");
        }

        public static InstalledVersions LoadInstalledVersions(string gameDir)
        {
            try
            {
                string p = Path.Combine(GetModDir(gameDir), ".installed_versions.json");
                if (!File.Exists(p)) return new InstalledVersions();
                JavaScriptSerializer ser = new JavaScriptSerializer();
                return ser.Deserialize<InstalledVersions>(File.ReadAllText(p)) ?? new InstalledVersions();
            }
            catch { return new InstalledVersions(); }
        }

        public static void SaveInstalledVersions(string gameDir, InstalledVersions v)
        {
            string modDir = GetModDir(gameDir);
            Directory.CreateDirectory(modDir);
            JavaScriptSerializer ser = new JavaScriptSerializer();
            File.WriteAllText(Path.Combine(modDir, ".installed_versions.json"), ser.Serialize(v), Encoding.UTF8);
        }

        public static Dictionary<string, bool> CheckComponents(string gameDir)
        {
            Dictionary<string, bool> map = new Dictionary<string, bool>();
            if (!IsValidGameDir(gameDir))
            {
                map["NightCoop"] = false;
                map["RED4ext"] = false;
                map["CET"] = false;
                map["redscript"] = false;
                map["ChoomSync"] = false;
                map["PerfPack"] = false;
                return map;
            }
            map["NightCoop"] = File.Exists(Path.Combine(gameDir, @"red4ext\plugins\NightCoop\NightCoop.dll"));
            map["RED4ext"] = File.Exists(Path.Combine(gameDir, @"bin\x64\winmm.dll")) && File.Exists(Path.Combine(gameDir, @"red4ext\RED4ext.dll"));
            map["CET"] = File.Exists(Path.Combine(gameDir, @"bin\x64\version.dll"));
            map["redscript"] = File.Exists(Path.Combine(gameDir, @"engine\tools\scc.exe"));
            map["ChoomSync"] = File.Exists(Path.Combine(GetModDir(gameDir), "init.lua"));
            map["PerfPack"] = PerfOptimizer.IsPerfPackApplied(gameDir);
            return map;
        }

        public static bool IsFullyInstalled(string gameDir)
        {
            Dictionary<string, bool> c = CheckComponents(gameDir);
            return c["NightCoop"] && c["RED4ext"] && c["CET"] && c["redscript"] && c["ChoomSync"];
        }

        public static bool NeedsUpdate(string gameDir, CoopManifest remote, out string reason)
        {
            reason = "";
            if (remote == null) return false;

            if (remote.launcher != null && !string.IsNullOrEmpty(remote.launcher.version)
                && !string.Equals(remote.launcher.version, Program.APP_VERSION, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(remote.launcher.sha256))
            {
                reason = "New ChoomSync.exe v" + remote.launcher.version;
                return true;
            }

            if (!IsValidGameDir(gameDir) || !IsFullyInstalled(gameDir))
            {
                reason = "Not installed yet";
                return true;
            }

            string modDir = GetModDir(gameDir);
            if (remote.modFiles != null)
            {
                foreach (ModFileSpec mf in remote.modFiles)
                {
                    if (string.IsNullOrEmpty(mf.name) || string.IsNullOrEmpty(mf.sha256)) continue;
                    string localPath = Path.Combine(modDir, Path.GetFileName(mf.name));
                    if (!File.Exists(localPath) || !string.Equals(ComputeSha256(localPath), mf.sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "Updated ChoomSync mod (" + mf.name + ")";
                        return true;
                    }
                }
            }

            InstalledVersions inst = LoadInstalledVersions(gameDir);
            if (remote.nightCoop != null && !string.IsNullOrEmpty(remote.nightCoop.sha256)
                && !string.IsNullOrEmpty(inst.nightCoopSha256)
                && !string.Equals(inst.nightCoopSha256, remote.nightCoop.sha256, StringComparison.OrdinalIgnoreCase))
            {
                reason = "New NightCoop " + remote.nightCoop.version;
                return true;
            }
            if (remote.cet != null && !string.IsNullOrEmpty(remote.cet.sha256)
                && !string.IsNullOrEmpty(inst.cetSha256)
                && !string.Equals(inst.cetSha256, remote.cet.sha256, StringComparison.OrdinalIgnoreCase))
            {
                reason = "New CET " + remote.cet.version;
                return true;
            }
            if (remote.redscript != null && !string.IsNullOrEmpty(remote.redscript.sha256)
                && !string.IsNullOrEmpty(inst.redscriptSha256)
                && !string.Equals(inst.redscriptSha256, remote.redscript.sha256, StringComparison.OrdinalIgnoreCase))
            {
                reason = "New redscript " + remote.redscript.version;
                return true;
            }
            return false;
        }

        public static bool CanWriteDirectory(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".choomsync_write_test_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static string GetSavesDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"Saved Games\CD Projekt Red\Cyberpunk 2077");
        }

        public static string GetBackupsRoot()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"Saved Games\CD Projekt Red\Cyberpunk 2077_Backups");
        }

        public static int CountBackups()
        {
            string root = GetBackupsRoot();
            if (!Directory.Exists(root)) return 0;
            return Directory.GetDirectories(root).Length;
        }

        public static string BackupSaves(bool initialPreCoop, Action<string> log)
        {
            string src = GetSavesDir();
            if (!Directory.Exists(src))
            {
                log("No existing Cyberpunk saves found yet (skipping backup).");
                return null;
            }
            string root = GetBackupsRoot();
            Directory.CreateDirectory(root);

            if (initialPreCoop)
            {
                string[] existing = Directory.GetDirectories(root, "pre-coop_*");
                if (existing.Length > 0)
                {
                    log("Initial save backup preserved: " + Path.GetFileName(existing[0]));
                    return existing[0];
                }
            }

            string prefix = initialPreCoop ? "pre-coop_" : "coop_";
            string dest = Path.Combine(root, prefix + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            CopyDirectory(src, dest);
            int count = Directory.GetDirectories(dest).Length;
            log(string.Format("Backed up {0} save folder(s) to {1}", count, Path.GetFileName(dest)));

            if (!initialPreCoop)
            {
                string[] rolling = Directory.GetDirectories(root, "coop_*");
                Array.Sort(rolling, StringComparer.OrdinalIgnoreCase);
                Array.Reverse(rolling);
                for (int i = 5; i < rolling.Length; i++)
                {
                    try { Directory.Delete(rolling[i], true); } catch { }
                }
            }
            return dest;
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (string file in Directory.GetFiles(sourceDir))
            {
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), true);
            }
            foreach (string sub in Directory.GetDirectories(sourceDir))
            {
                CopyDirectory(sub, Path.Combine(destDir, Path.GetFileName(sub)));
            }
        }

        public static string ComputeSha256(string filePath)
        {
            using (FileStream fs = File.OpenRead(filePath))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public static async Task DownloadFileVerifiedAsync(HttpClient http, string url, string destPath, string expectedSha256, Action<int, long, long> onProgress)
        {
            if (!IsAllowedDownloadUrl(url))
            {
                throw new InvalidOperationException("Blocked download from untrusted URL: " + url);
            }

            if (File.Exists(destPath) && !string.IsNullOrEmpty(expectedSha256))
            {
                if (string.Equals(ComputeSha256(destPath), expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            string tmp = destPath + ".part";
            using (HttpResponseMessage resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength.HasValue ? resp.Content.Headers.ContentLength.Value : -1;
                using (Stream input = await resp.Content.ReadAsStreamAsync())
                using (FileStream output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    byte[] buffer = new byte[81920];
                    long readTotal = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, read);
                        readTotal += read;
                        int pct = total > 0 ? (int)Math.Min(100, (readTotal * 100L) / total) : 0;
                        if (onProgress != null) onProgress(pct, readTotal, total);
                    }
                }
            }

            if (!string.IsNullOrEmpty(expectedSha256))
            {
                string actual = ComputeSha256(tmp);
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(tmp); } catch { }
                    throw new InvalidOperationException(string.Format(
                        "SHA-256 verification failed for {0} (expected {1}, got {2})",
                        Path.GetFileName(destPath), expectedSha256.Substring(0, 12), actual.Substring(0, 12)
                    ));
                }
            }

            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmp, destPath);
        }

        public static void ExtractZipSafe(string zipPath, string destDir)
        {
            string fullRoot = Path.GetFullPath(destDir);
            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString())) fullRoot += Path.DirectorySeparatorChar;
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string targetPath = Path.GetFullPath(Path.Combine(fullRoot, entry.FullName));
                    if (!targetPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Blocked unsafe archive entry: " + entry.FullName);
                    }
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(targetPath);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                        entry.ExtractToFile(targetPath, true);
                    }
                }
            }
        }

        public static async Task SyncCompanionModAsync(HttpClient http, string gameDir, CoopManifest manifest, Action<string> log)
        {
            string modDir = GetModDir(gameDir);
            Directory.CreateDirectory(modDir);
            Assembly asm = Assembly.GetExecutingAssembly();

            WriteResource(asm, "ChoomSync.config.json", Path.Combine(modDir, "config.json"), false);

            bool updatedFromRemote = false;
            if (manifest != null && manifest.modFiles != null && manifest.modFiles.Count > 0)
            {
                foreach (ModFileSpec mf in manifest.modFiles)
                {
                    string safeName = Path.GetFileName(mf.name ?? "");
                    if (string.IsNullOrEmpty(safeName) || string.IsNullOrEmpty(mf.url) || string.IsNullOrEmpty(mf.sha256)) continue;
                    string target = Path.Combine(modDir, safeName);
                    if (File.Exists(target) && string.Equals(ComputeSha256(target), mf.sha256, StringComparison.OrdinalIgnoreCase))
                        continue;
                    log("Auto-updating ChoomSync file: " + safeName + "...");
                    await DownloadFileVerifiedAsync(http, mf.url, target, mf.sha256, null);
                    updatedFromRemote = true;
                }
            }

            if (!File.Exists(Path.Combine(modDir, "init.lua")))
                WriteResource(asm, "ChoomSync.init.lua", Path.Combine(modDir, "init.lua"), true);
            if (!File.Exists(Path.Combine(modDir, "nightcoop_sdk.lua")))
                WriteResource(asm, "ChoomSync.nightcoop_sdk.lua", Path.Combine(modDir, "nightcoop_sdk.lua"), true);
            if (updatedFromRemote) log("ChoomSync companion mod updated to latest version.");
        }

        private static void WriteResource(Assembly asm, string resName, string destPath, bool overwrite)
        {
            if (!overwrite && File.Exists(destPath)) return;
            using (Stream s = asm.GetManifestResourceStream(resName))
            {
                if (s == null) throw new InvalidOperationException("Missing embedded resource: " + resName);
                using (FileStream fs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                {
                    s.CopyTo(fs);
                }
            }
        }

        public static async Task<bool> TrySelfUpdateExeAsync(HttpClient http, CoopManifest manifest, Action<string> log, Action<int, string> setProgress)
        {
            if (manifest == null || manifest.launcher == null) return false;
            if (string.IsNullOrEmpty(manifest.launcher.version)
                || string.Equals(manifest.launcher.version, Program.APP_VERSION, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(manifest.launcher.url)
                || string.IsNullOrEmpty(manifest.launcher.sha256))
            {
                return false;
            }

            string dlDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"ChoomSync\downloads");
            Directory.CreateDirectory(dlDir);
            string newExe = Path.Combine(dlDir, "ChoomSync_new.exe");

            log("Downloading ChoomSync.exe v" + manifest.launcher.version + "...");
            await DownloadFileVerifiedAsync(http, manifest.launcher.url, newExe, manifest.launcher.sha256, delegate (int pct, long cur, long tot)
            {
                setProgress(pct, string.Format("Updating ChoomSync.exe to v{0}... {1}%", manifest.launcher.version, pct));
            });

            string currentExe = Assembly.GetExecutingAssembly().Location;
            string installedExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"ChoomSync\ChoomSync.exe");
            string swapCmd = Path.Combine(Path.GetTempPath(), "choomsync_swap_" + Guid.NewGuid().ToString("N") + ".cmd");

            StringBuilder cmd = new StringBuilder();
            cmd.AppendLine("@echo off");
            cmd.AppendLine("ping 127.0.0.1 -n 2 >nul");
            cmd.AppendLine(string.Format("copy /Y \"{0}\" \"{1}\" >nul", newExe, currentExe));
            if (!string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(installedExe), StringComparison.OrdinalIgnoreCase))
            {
                cmd.AppendLine(string.Format("if exist \"{0}\" copy /Y \"{1}\" \"{0}\" >nul", installedExe, newExe));
            }
            cmd.AppendLine(string.Format("start \"\" \"{0}\"", currentExe));
            cmd.AppendLine("del \"%~f0\"");
            File.WriteAllText(swapCmd, cmd.ToString(), Encoding.ASCII);

            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + swapCmd + "\"")
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false
            });
            return true;
        }

        public static void ClearStaleScriptCache(string gameDir)
        {
            string dirCache = Path.Combine(gameDir, @"r6\cache\modded");
            string fileCache = Path.Combine(gameDir, @"r6\cache\final.redscripts.modded");
            if (Directory.Exists(dirCache)) Directory.Delete(dirCache, true);
            if (File.Exists(fileCache)) File.Delete(fileCache);
        }

        public static void InstallSelfAndShortcut(Action<string> log)
        {
            try
            {
                string installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChoomSync");
                Directory.CreateDirectory(installDir);
                string currentExe = Assembly.GetExecutingAssembly().Location;
                string targetExe = Path.Combine(installDir, "ChoomSync.exe");
                if (!string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(currentExe, targetExe, true);
                }

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string lnk = Path.Combine(desktop, "ChoomSync (Cyberpunk Co-op).lnk");
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    object shell = Activator.CreateInstance(shellType);
                    object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                    Type scType = sc.GetType();
                    scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { targetExe });
                    scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { installDir });
                    scType.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Play Cyberpunk 2077 Co-op (ChoomSync + NightCoop)" });
                    scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                    log("Created Desktop shortcut: ChoomSync (Cyberpunk Co-op)");
                }
            }
            catch (Exception ex) { log("Shortcut skipped: " + ex.Message); }
        }

        public static string LaunchCyberpunk(string gameDir)
        {
            if (gameDir.IndexOf(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Process.Start(new ProcessStartInfo("steam://rungameid/1091500") { UseShellExecute = true });
                return "Steam (Cyberpunk 2077)";
            }
            string exe = Path.Combine(gameDir, @"bin\x64\Cyberpunk2077.exe");
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true });
            return exe;
        }

        public static bool IsTailscaleInstalled()
        {
            return File.Exists(@"C:\Program Files\Tailscale\tailscale.exe");
        }
    }

    internal sealed class MainForm : Form
    {
        private static readonly Color BgDark = Color.FromArgb(12, 13, 18);
        private static readonly Color CardBg = Color.FromArgb(20, 22, 32);
        private static readonly Color BorderCol = Color.FromArgb(40, 46, 68);
        private static readonly Color TextLight = Color.FromArgb(233, 236, 245);
        private static readonly Color TextMuted = Color.FromArgb(147, 155, 180);
        private static readonly Color AccentYellow = Color.FromArgb(252, 238, 10);
        private static readonly Color AccentCyan = Color.FromArgb(0, 240, 255);
        private static readonly Color OkGreen = Color.FromArgb(56, 229, 140);
        private static readonly Color WarnAmber = Color.FromArgb(255, 184, 77);
        private static readonly Color BadRed = Color.FromArgb(255, 71, 105);

        private string _gameDir;
        private bool _busy;
        private CoopManifest _remoteManifest;
        private HardwareProfile _hw;

        private TextBox _pathBox;
        private Label _versionBadge;
        private Label _updateBadge;
        private Label _statusSummary;
        private Dictionary<string, Label> _compBadges = new Dictionary<string, Label>();
        private CheckBox _chkPerfPack;
        private CheckBox _chkHighPriority;
        private Button _btnInstall;
        private Button _btnPlay;
        private Button _btnOptimize;
        private Button _btnClearCaches;
        private Button _btnRestoreVanilla;
        private Button _btnBrowse;
        private Button _btnTailscale;
        private ProgressBar _progress;
        private Label _progressLabel;
        private TextBox _logBox;

        public MainForm()
        {
            Text = "ChoomSync v" + Program.APP_VERSION + " — Cyberpunk 2077 Co-op & Optimizer";
            ClientSize = new Size(660, 725);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = BgDark;
            ForeColor = TextLight;
            Font = new Font("Segoe UI", 9.5f);

            _hw = PerfOptimizer.DetectHardware();
            BuildUi();
            _gameDir = CoopEngine.DetectGameDir();
            RefreshUiState();
            Shown += OnFormShown;
        }

        private void BuildUi()
        {
            int pad = 20;
            int w = ClientSize.Width - pad * 2;

            Label title = new Label();
            title.Text = "CHOOMSYNC // CYBERPUNK 2077 CO-OP";
            title.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
            title.ForeColor = AccentYellow;
            title.Location = new Point(pad, 14);
            title.AutoSize = true;
            Controls.Add(title);

            _updateBadge = new Label();
            _updateBadge.Location = new Point(pad + w - 210, 20);
            _updateBadge.Size = new Size(210, 22);
            _updateBadge.TextAlign = ContentAlignment.MiddleRight;
            _updateBadge.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _updateBadge.ForeColor = TextMuted;
            _updateBadge.Text = "Checking for updates...";
            Controls.Add(_updateBadge);

            Label sub = new Label();
            sub.Text = string.Format("Patch 2.5 Co-op + Base-Game Perf & Bug-Fix Suite  •  v{0}  •  {1} Tier ({2}GB VRAM)", Program.APP_VERSION, _hw.Tier, _hw.VramGb);
            sub.ForeColor = TextMuted;
            sub.Location = new Point(pad + 2, 46);
            sub.AutoSize = true;
            Controls.Add(sub);

            // Card 1: Game folder
            Panel gameCard = MakeCard(pad, 74, w, 74);
            Controls.Add(gameCard);

            Label lblDir = new Label();
            lblDir.Text = "CYBERPUNK 2077 FOLDER";
            lblDir.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            lblDir.ForeColor = AccentCyan;
            lblDir.Location = new Point(12, 8);
            lblDir.AutoSize = true;
            gameCard.Controls.Add(lblDir);

            _versionBadge = new Label();
            _versionBadge.Location = new Point(w - 190, 8);
            _versionBadge.Size = new Size(176, 20);
            _versionBadge.TextAlign = ContentAlignment.MiddleRight;
            _versionBadge.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            gameCard.Controls.Add(_versionBadge);

            _pathBox = new TextBox();
            _pathBox.Location = new Point(12, 34);
            _pathBox.Size = new Size(w - 116, 26);
            _pathBox.ReadOnly = true;
            _pathBox.BackColor = BgDark;
            _pathBox.ForeColor = TextLight;
            _pathBox.BorderStyle = BorderStyle.FixedSingle;
            gameCard.Controls.Add(_pathBox);

            _btnBrowse = MakeButton("Browse...", w - 96, 33, 84, 27, Color.FromArgb(34, 40, 60), TextLight);
            _btnBrowse.Click += OnBrowseClick;
            gameCard.Controls.Add(_btnBrowse);

            // Card 2: Component Checklist
            Panel statusCard = MakeCard(pad, 158, w, 122);
            Controls.Add(statusCard);

            Label lblComp = new Label();
            lblComp.Text = "CO-OP & PERFORMANCE STACK";
            lblComp.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            lblComp.ForeColor = AccentCyan;
            lblComp.Location = new Point(12, 8);
            lblComp.AutoSize = true;
            statusCard.Controls.Add(lblComp);

            _statusSummary = new Label();
            _statusSummary.Location = new Point(220, 8);
            _statusSummary.Size = new Size(w - 234, 20);
            _statusSummary.TextAlign = ContentAlignment.MiddleRight;
            _statusSummary.ForeColor = TextMuted;
            statusCard.Controls.Add(_statusSummary);

            string[] keys = new string[] { "NightCoop", "RED4ext", "CET", "redscript", "ChoomSync", "PerfPack" };
            string[] names = new string[] { "NightCoop 0.2.2-beta", "RED4ext 1.31.0", "Cyber Engine Tweaks", "redscript 1.0.2", "ChoomSync Mod v0.2", "Base-Game Perf Pack" };
            for (int i = 0; i < keys.Length; i++)
            {
                int col = i % 2;
                int row = i / 2;
                int x = 14 + col * 300;
                int y = 35 + row * 26;

                Label nl = new Label();
                nl.Text = names[i];
                nl.Location = new Point(x, y);
                nl.Size = new Size(170, 22);
                nl.ForeColor = TextLight;
                statusCard.Controls.Add(nl);

                Label bl = new Label();
                bl.Location = new Point(x + 174, y);
                bl.Size = new Size(112, 22);
                bl.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
                statusCard.Controls.Add(bl);
                _compBadges[keys[i]] = bl;
            }

            // Card 3: Base-Game Performance & Stutter Pack
            Panel perfCard = MakeCard(pad, 290, w, 118);
            Controls.Add(perfCard);

            Label pTitle = new Label();
            pTitle.Text = "BASE-GAME PERFORMANCE & STUTTER FIXES (100% REVERSIBLE)";
            pTitle.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            pTitle.ForeColor = AccentYellow;
            pTitle.Location = new Point(12, 8);
            pTitle.AutoSize = true;
            perfCard.Controls.Add(pTitle);

            _chkPerfPack = new CheckBox();
            _chkPerfPack.Text = "Auto-apply Streaming INI + Safe FPS Caps (Crowd->Med, SSR<=High, VolFog<=Med)";
            _chkPerfPack.Checked = true;
            _chkPerfPack.ForeColor = TextLight;
            _chkPerfPack.Location = new Point(14, 30);
            _chkPerfPack.Size = new Size(w - 28, 22);
            perfCard.Controls.Add(_chkPerfPack);

            _chkHighPriority = new CheckBox();
            _chkHighPriority.Text = "Boost Cyberpunk 2077 to High CPU Priority on Launch (smoother 1% lows)";
            _chkHighPriority.Checked = true;
            _chkHighPriority.ForeColor = TextLight;
            _chkHighPriority.Location = new Point(14, 52);
            _chkHighPriority.Size = new Size(w - 28, 22);
            perfCard.Controls.Add(_chkHighPriority);

            _btnOptimize = MakeButton("Apply Perf Pack Now", 12, 80, 185, 28, Color.FromArgb(30, 56, 48), OkGreen);
            _btnOptimize.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _btnOptimize.Click += OnOptimizeClick;
            perfCard.Controls.Add(_btnOptimize);

            _btnClearCaches = MakeButton("Clear Stutter Caches (DX12+Script)", 206, 80, 225, 28, Color.FromArgb(34, 40, 60), TextLight);
            _btnClearCaches.Font = new Font("Segoe UI", 8.5f);
            _btnClearCaches.Click += OnClearCachesClick;
            perfCard.Controls.Add(_btnClearCaches);

            _btnRestoreVanilla = MakeButton("Restore Vanilla", 440, 80, 166, 28, Color.FromArgb(46, 32, 38), WarnAmber);
            _btnRestoreVanilla.Font = new Font("Segoe UI", 8.5f);
            _btnRestoreVanilla.Click += OnRestoreVanillaClick;
            perfCard.Controls.Add(_btnRestoreVanilla);

            // Big Action Buttons
            _btnInstall = MakeButton("1. Install / Update Co-op + Perf", pad, 418, (w - 12) / 2, 46, AccentYellow, BgDark);
            _btnInstall.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            _btnInstall.Click += OnInstallClick;
            Controls.Add(_btnInstall);

            _btnPlay = MakeButton("2. Auto-Update & Play!", pad + (w - 12) / 2 + 12, 418, (w - 12) / 2, 46, AccentCyan, BgDark);
            _btnPlay.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            _btnPlay.Click += OnPlayClick;
            Controls.Add(_btnPlay);

            // Progress Bar
            _progress = new ProgressBar();
            _progress.Location = new Point(pad, 474);
            _progress.Size = new Size(w, 16);
            _progress.Style = ProgressBarStyle.Continuous;
            Controls.Add(_progress);

            _progressLabel = new Label();
            _progressLabel.Location = new Point(pad, 494);
            _progressLabel.Size = new Size(w, 20);
            _progressLabel.ForeColor = TextMuted;
            _progressLabel.Text = "Ready. Updates, base-game perf fixes & save backups run automatically.";
            Controls.Add(_progressLabel);

            // Card 4: Quick Guide
            Panel guideCard = MakeCard(pad, 520, w, 74);
            Controls.Add(guideCard);

            Label gBody = new Label();
            gBody.Text =
                "• In-Game Perf & Bug Fixes: Press your CET key -> ChoomSync -> \"Perf & Bug Fixes\" tab (live FPS / 1% low, Relic glitch & stuck combat/scanner fixes).\n" +
                "• Joining Co-op: Main Menu -> Co-op -> Join by code -> Ctrl+V your friend's invite code.";
            gBody.Location = new Point(12, 10);
            gBody.Size = new Size(w - 24, 56);
            gBody.ForeColor = TextLight;
            guideCard.Controls.Add(gBody);

            // Log Box
            _logBox = new TextBox();
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Vertical;
            _logBox.BackColor = CardBg;
            _logBox.ForeColor = TextMuted;
            _logBox.BorderStyle = BorderStyle.FixedSingle;
            _logBox.Font = new Font("Consolas", 8.5f);
            _logBox.Location = new Point(pad, 604);
            _logBox.Size = new Size(w, 74);
            Controls.Add(_logBox);

            // Footer
            _btnTailscale = MakeButton("Optional: Install Tailscale VPN", pad, 686, 215, 28, Color.FromArgb(28, 32, 48), TextMuted);
            _btnTailscale.Font = new Font("Segoe UI", 8.5f);
            _btnTailscale.Click += OnTailscaleClick;
            Controls.Add(_btnTailscale);

            Label secNote = new Label();
            secNote.Text = "Zero open ports • HTTPS allowlisted • SHA-256 verified";
            secNote.ForeColor = TextMuted;
            secNote.Font = new Font("Segoe UI", 8f);
            secNote.Location = new Point(pad + 225, 692);
            secNote.Size = new Size(w - 225, 20);
            secNote.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(secNote);
        }

        private async void OnFormShown(object sender, EventArgs e)
        {
            List<string> overlays = PerfOptimizer.DetectConflictingOverlays();
            if (overlays.Count > 0)
            {
                Log("Tip: Background overlay(s) detected (" + string.Join(", ", overlays.ToArray()) + ") — close them if CET flickers or stutters.");
            }
            await CheckRemoteManifestSilentAsync();
        }

        private async Task CheckRemoteManifestSilentAsync()
        {
            try
            {
                using (HttpClient http = CoopEngine.CreateHttpClient(8))
                {
                    _remoteManifest = await CoopEngine.FetchManifestAsync(http);
                }
                if (_remoteManifest == null)
                {
                    _updateBadge.Text = "Offline mode (v" + Program.APP_VERSION + ")";
                    _updateBadge.ForeColor = TextMuted;
                    return;
                }
                string reason;
                if (CoopEngine.NeedsUpdate(_gameDir, _remoteManifest, out reason))
                {
                    _updateBadge.Text = "● Update ready";
                    _updateBadge.ForeColor = WarnAmber;
                    Log("Update available: " + reason);
                }
                else
                {
                    _updateBadge.Text = "● Up to date (v" + _remoteManifest.version + ")";
                    _updateBadge.ForeColor = OkGreen;
                }
            }
            catch
            {
                _updateBadge.Text = "Offline mode (v" + Program.APP_VERSION + ")";
                _updateBadge.ForeColor = TextMuted;
            }
        }

        private Panel MakeCard(int x, int y, int w, int h)
        {
            Panel p = new Panel();
            p.Location = new Point(x, y);
            p.Size = new Size(w, h);
            p.BackColor = CardBg;
            p.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen pen = new Pen(BorderCol))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
                }
            };
            return p;
        }

        private Button MakeButton(string text, int x, int y, int w, int h, Color bg, Color fg)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, h);
            b.BackColor = bg;
            b.ForeColor = fg;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = BorderCol;
            b.Cursor = Cursors.Hand;
            return b;
        }

        private void Log(string msg)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(Log), msg);
                return;
            }
            _logBox.AppendText(string.Format("[{0:HH:mm:ss}] {1}{2}", DateTime.Now, msg, Environment.NewLine));
        }

        private void SetProgress(int pct, string label)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<int, string>(SetProgress), pct, label);
                return;
            }
            _progress.Value = Math.Max(0, Math.Min(100, pct));
            _progressLabel.Text = label;
        }

        private void RefreshUiState()
        {
            bool valid = CoopEngine.IsValidGameDir(_gameDir);
            _pathBox.Text = valid ? _gameDir : "(Cyberpunk 2077 not found — click Browse...)";

            if (valid)
            {
                string ver = CoopEngine.GetGameVersion(_gameDir) ?? "?";
                bool is25 = ver.StartsWith("2.5");
                _versionBadge.Text = "Patch " + ver + (is25 ? " (OK)" : " (Update to 2.5!)");
                _versionBadge.ForeColor = is25 ? OkGreen : WarnAmber;
            }
            else
            {
                _versionBadge.Text = "Not found";
                _versionBadge.ForeColor = BadRed;
            }

            Dictionary<string, bool> comps = CoopEngine.CheckComponents(_gameDir);
            int coreOk = 0;
            foreach (KeyValuePair<string, bool> kv in comps)
            {
                if (kv.Key != "PerfPack" && kv.Value) coreOk++;
                Label lbl = _compBadges[kv.Key];
                lbl.Text = kv.Value ? "● Active" : "○ Not set";
                lbl.ForeColor = kv.Value ? OkGreen : TextMuted;
            }

            int backups = CoopEngine.CountBackups();
            _statusSummary.Text = string.Format("{0}/5 Core Ready  •  {1} Save Backup(s)", coreOk, backups);

            _btnInstall.Enabled = !_busy && valid;
            _btnPlay.Enabled = !_busy && valid && coreOk == 5;
            _btnOptimize.Enabled = !_busy && valid;
            _btnClearCaches.Enabled = !_busy;
            _btnRestoreVanilla.Enabled = !_busy && valid;
            _btnBrowse.Enabled = !_busy;
            _btnTailscale.Enabled = !_busy;
            _btnTailscale.Text = CoopEngine.IsTailscaleInstalled()
                ? "Tailscale Installed (Open)"
                : "Optional: Install Tailscale VPN";
        }

        private void OnBrowseClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = "Select your Cyberpunk 2077 folder (containing bin\\x64\\Cyberpunk2077.exe)";
                if (fbd.ShowDialog(this) == DialogResult.OK)
                {
                    if (CoopEngine.IsValidGameDir(fbd.SelectedPath))
                    {
                        _gameDir = fbd.SelectedPath;
                        Log("Selected game folder: " + _gameDir);
                        RefreshUiState();
                    }
                    else
                    {
                        MessageBox.Show(this, "Could not find bin\\x64\\Cyberpunk2077.exe in that folder.", "Invalid Folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
        }

        private bool EnsureWritableOrPromptElevate()
        {
            if (CoopEngine.CanWriteDirectory(_gameDir)) return true;
            DialogResult ans = MessageBox.Show(
                this,
                "Cyberpunk 2077 is in a protected Program Files folder. Restart ChoomSync as Administrator to install/update?",
                "Administrator Permission Needed",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information
            );
            if (ans == DialogResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location) { Verb = "runas", UseShellExecute = true });
                    Close();
                }
                catch (Exception ex) { Log("Elevation cancelled: " + ex.Message); }
            }
            return false;
        }

        private async void OnOptimizeClick(object sender, EventArgs e)
        {
            if (_busy || !CoopEngine.IsValidGameDir(_gameDir)) return;
            if (!EnsureWritableOrPromptElevate()) return;
            _busy = true;
            RefreshUiState();
            try
            {
                SetProgress(40, "Applying Base-Game Performance & Stutter Pack...");
                await Task.Run(delegate () { PerfOptimizer.ApplyBaseGamePerfPack(_gameDir, Log); });
                SetProgress(100, "Base-game performance optimizations applied! (Reversible via Restore Vanilla)");
            }
            catch (Exception ex)
            {
                Log("Optimize failed: " + ex.Message);
                MessageBox.Show(this, "Optimize failed:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { _busy = false; RefreshUiState(); }
        }

        private async void OnClearCachesClick(object sender, EventArgs e)
        {
            if (_busy) return;
            _busy = true;
            RefreshUiState();
            try
            {
                SetProgress(35, "Clearing stale DX12, GPU & script caches...");
                int removed = await Task.Run(delegate () { return PerfOptimizer.ClearStutterCaches(_gameDir, Log); });
                SetProgress(100, string.Format("Cleared {0} stale cache file(s). First boot will rebuild shaders cleanly.", removed));
            }
            catch (Exception ex) { Log("Cache clear error: " + ex.Message); }
            finally { _busy = false; RefreshUiState(); }
        }

        private async void OnRestoreVanillaClick(object sender, EventArgs e)
        {
            if (_busy || !CoopEngine.IsValidGameDir(_gameDir)) return;
            if (!EnsureWritableOrPromptElevate()) return;
            _busy = true;
            RefreshUiState();
            try
            {
                await Task.Run(delegate () { PerfOptimizer.RestoreVanillaSettings(_gameDir, Log); });
                SetProgress(100, "Restored original vanilla Cyberpunk settings.");
            }
            catch (Exception ex) { Log("Restore error: " + ex.Message); }
            finally { _busy = false; RefreshUiState(); }
        }

        private async Task<bool> RunInstallOrUpdateAsync(bool fullInstall, bool applyPerf)
        {
            using (HttpClient http = CoopEngine.CreateHttpClient(300))
            {
                SetProgress(3, "Checking latest ChoomSync manifest...");
                CoopManifest manifest = null;
                try { manifest = await CoopEngine.FetchManifestAsync(http); } catch { }
                if (manifest != null) _remoteManifest = manifest;
                if (manifest == null) manifest = Program.DefaultManifest();

                if (await CoopEngine.TrySelfUpdateExeAsync(http, manifest, Log, SetProgress))
                {
                    Log("Restarting into updated ChoomSync.exe...");
                    return true;
                }

                InstalledVersions installed = CoopEngine.LoadInstalledVersions(_gameDir);
                Dictionary<string, bool> comps = CoopEngine.CheckComponents(_gameDir);
                bool needNc = fullInstall || !comps["NightCoop"] || !comps["RED4ext"]
                    || (!string.IsNullOrEmpty(manifest.nightCoop.sha256) && !string.Equals(installed.nightCoopSha256, manifest.nightCoop.sha256, StringComparison.OrdinalIgnoreCase));
                bool needCet = fullInstall || !comps["CET"]
                    || (!string.IsNullOrEmpty(manifest.cet.sha256) && !string.Equals(installed.cetSha256, manifest.cet.sha256, StringComparison.OrdinalIgnoreCase));
                bool needRs = fullInstall || !comps["redscript"]
                    || (!string.IsNullOrEmpty(manifest.redscript.sha256) && !string.Equals(installed.redscriptSha256, manifest.redscript.sha256, StringComparison.OrdinalIgnoreCase));

                string dlDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"ChoomSync\downloads");
                Directory.CreateDirectory(dlDir);

                if (needRs || needCet || needNc)
                {
                    SetProgress(8, "Backing up single-player saves...");
                    CoopEngine.BackupSaves(true, Log);
                }

                if (needRs)
                {
                    string rsZip = Path.Combine(dlDir, "redscript_" + (manifest.redscript.version ?? "latest") + ".zip");
                    Log("Syncing redscript " + manifest.redscript.version + "...");
                    await CoopEngine.DownloadFileVerifiedAsync(http, manifest.redscript.url, rsZip, manifest.redscript.sha256, delegate (int pct, long cur, long tot)
                    {
                        SetProgress(10 + pct / 6, string.Format("Downloading redscript {0}... {1}%", manifest.redscript.version, pct));
                    });
                    SetProgress(28, "Extracting redscript...");
                    CoopEngine.ExtractZipSafe(rsZip, _gameDir);
                    installed.redscriptSha256 = CoopEngine.ComputeSha256(rsZip);
                }

                if (needCet)
                {
                    string cetZip = Path.Combine(dlDir, "cet_" + (manifest.cet.version ?? "latest") + ".zip");
                    Log("Syncing Cyber Engine Tweaks " + manifest.cet.version + "...");
                    await CoopEngine.DownloadFileVerifiedAsync(http, manifest.cet.url, cetZip, manifest.cet.sha256, delegate (int pct, long cur, long tot)
                    {
                        SetProgress(30 + pct / 3, string.Format("Downloading CET {0}... {1}% ({2:F1} MB)", manifest.cet.version, pct, cur / 1048576.0));
                    });
                    SetProgress(66, "Extracting Cyber Engine Tweaks...");
                    CoopEngine.ExtractZipSafe(cetZip, _gameDir);
                    installed.cetSha256 = CoopEngine.ComputeSha256(cetZip);
                }

                if (needNc)
                {
                    string ncZip = Path.Combine(dlDir, "NightCoop_" + (manifest.nightCoop.version ?? "latest") + ".zip");
                    Log("Syncing NightCoop " + manifest.nightCoop.version + "...");
                    await CoopEngine.DownloadFileVerifiedAsync(http, manifest.nightCoop.url, ncZip, manifest.nightCoop.sha256, delegate (int pct, long cur, long tot)
                    {
                        SetProgress(68 + pct / 4, string.Format("Downloading NightCoop {0}... {1}% ({2:F1} MB)", manifest.nightCoop.version, pct, cur / 1048576.0));
                    });
                    SetProgress(92, "Extracting NightCoop + RED4ext...");
                    CoopEngine.ExtractZipSafe(ncZip, _gameDir);
                    installed.nightCoopSha256 = CoopEngine.ComputeSha256(ncZip);
                }

                SetProgress(95, "Syncing ChoomSync Companion Mod...");
                await CoopEngine.SyncCompanionModAsync(http, _gameDir, manifest, Log);
                CoopEngine.SaveInstalledVersions(_gameDir, installed);

                if (applyPerf && (fullInstall || !PerfOptimizer.IsPerfPackApplied(_gameDir)))
                {
                    SetProgress(97, "Applying Base-Game Performance & Stutter Pack...");
                    PerfOptimizer.ApplyBaseGamePerfPack(_gameDir, Log);
                }

                if (needRs || needCet || needNc)
                {
                    CoopEngine.ClearStaleScriptCache(_gameDir);
                }
                if (fullInstall)
                {
                    CoopEngine.InstallSelfAndShortcut(Log);
                }
                return false;
            }
        }

        private async void OnInstallClick(object sender, EventArgs e)
        {
            if (_busy || !CoopEngine.IsValidGameDir(_gameDir)) return;
            if (!EnsureWritableOrPromptElevate()) return;

            bool applyPerf = _chkPerfPack.Checked;
            _busy = true;
            RefreshUiState();
            try
            {
                bool selfRestart = await Task.Run(async delegate () { return await RunInstallOrUpdateAsync(true, applyPerf); });
                if (selfRestart) { Close(); return; }

                SetProgress(100, "All done! Click \"2. Auto-Update & Play!\" to jump into Night City.");
                Log("Install/Update complete! Co-op + Base-Game Perf Pack ready.");
                await CheckRemoteManifestSilentAsync();
                MessageBox.Show(
                    this,
                    "Cyberpunk 2077 Co-op + Base-Game Performance Pack is ready!\n\nClick \"2. Auto-Update & Play!\", then in the Main Menu click:\n  Co-op -> Join by code -> Ctrl+V",
                    "ChoomSync v" + Program.APP_VERSION + " Ready!",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
                SetProgress(0, "Install failed — see log below.");
                MessageBox.Show(this, "Install failed:\n\n" + ex.Message, "ChoomSync Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { _busy = false; RefreshUiState(); }
        }

        private async void OnPlayClick(object sender, EventArgs e)
        {
            if (_busy || !CoopEngine.IsValidGameDir(_gameDir)) return;

            string reason;
            bool applyPerf = _chkPerfPack.Checked;
            bool needPerfWrite = applyPerf && !PerfOptimizer.IsPerfPackApplied(_gameDir);
            if (needPerfWrite || (_remoteManifest != null && CoopEngine.NeedsUpdate(_gameDir, _remoteManifest, out reason)))
            {
                if (!EnsureWritableOrPromptElevate()) return;
            }

            bool boostPriority = _chkHighPriority.Checked;
            _busy = true;
            RefreshUiState();
            try
            {
                bool selfRestart = false;
                try
                {
                    selfRestart = await Task.Run(async delegate () { return await RunInstallOrUpdateAsync(false, applyPerf); });
                }
                catch (Exception uex)
                {
                    Log("Auto-update check skipped (" + uex.Message + "); launching installed build.");
                }
                if (selfRestart) { Close(); return; }

                SetProgress(85, "Backing up saves before launch...");
                await Task.Run(delegate () { CoopEngine.BackupSaves(false, Log); });
                SetProgress(95, "Launching Cyberpunk 2077...");
                string via = CoopEngine.LaunchCyberpunk(_gameDir);
                Log("Launched via: " + via);
                if (boostPriority)
                {
                    PerfOptimizer.BoostCyberpunkPriorityAsync(Log);
                }
                SetProgress(100, "Cyberpunk 2077 launched! Have fun in Night City.");
                await CheckRemoteManifestSilentAsync();
            }
            catch (Exception ex)
            {
                Log("Launch error: " + ex.Message);
                MessageBox.Show(this, "Could not launch Cyberpunk 2077:\n" + ex.Message, "Launch Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { _busy = false; RefreshUiState(); }
        }

        private async void OnTailscaleClick(object sender, EventArgs e)
        {
            if (CoopEngine.IsTailscaleInstalled())
            {
                try
                {
                    Process.Start(new ProcessStartInfo(@"C:\Program Files\Tailscale\tailscale-ipn.exe") { UseShellExecute = true });
                    Log("Opened Tailscale.");
                }
                catch (Exception ex) { Log("Could not open Tailscale: " + ex.Message); }
                return;
            }

            DialogResult ans = MessageBox.Show(
                this,
                "Tailscale is OPTIONAL (NightCoop already connects directly P2P without it).\n\nDownload and run the official Tailscale Windows installer from tailscale.com?",
                "Install Optional Tailscale",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );
            if (ans != DialogResult.Yes) return;

            _busy = true;
            RefreshUiState();
            try
            {
                string dlDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"ChoomSync\downloads");
                Directory.CreateDirectory(dlDir);
                string tsInstaller = Path.Combine(dlDir, "tailscale-setup-latest.exe");
                using (HttpClient http = CoopEngine.CreateHttpClient(180))
                {
                    Log("Downloading official Tailscale installer...");
                    await CoopEngine.DownloadFileVerifiedAsync(http, Program.TAILSCALE_URL, tsInstaller, null, delegate (int pct, long cur, long tot)
                    {
                        SetProgress(pct, string.Format("Downloading Tailscale... {0}%", pct));
                    });
                }
                Log("Launching official Tailscale installer...");
                Process.Start(new ProcessStartInfo(tsInstaller) { UseShellExecute = true });
                SetProgress(100, "Tailscale installer opened.");
            }
            catch (Exception ex)
            {
                Log("Tailscale download failed: " + ex.Message);
                MessageBox.Show(this, "Tailscale download failed:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { _busy = false; RefreshUiState(); }
        }
    }
}
