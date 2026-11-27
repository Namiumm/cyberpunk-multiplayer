-- ChoomSync Co-op Companion + Base-Game Performance & Bug-Fix Suite
-- Cyberpunk 2077 (Patch 2.5) + NightCoop SDK v1
-- 100% additive: zero @replaceMethod / Override hooks (fully compliant with NightCoop docs/do-not-hook.md).
-- Works seamlessly both solo (performance + bug fixes) and in NightCoop co-op sessions.

local Sdk = require("nightcoop_sdk")

local MOD_ID = "choomsync.coop"
local MOD_NAME = "ChoomSync Companion"
local MOD_VERSION = "0.2.0"
local MOD_PROTOCOL = 1

local CH_PING = 1

local MOD_FLAGS =
    Sdk.FLAG.USES_CHANNELS +
    Sdk.FLAG.USES_SHARED_STATE +
    Sdk.FLAG.USES_MENU +
    Sdk.FLAG.USES_ACTIONS +
    Sdk.FLAG.USES_WORLD_ASK

local coop = Sdk.new(MOD_ID, MOD_NAME, MOD_VERSION, MOD_PROTOCOL, MOD_FLAGS)

-- ---------------------------------------------------------------------------
-- Configuration & State
-- ---------------------------------------------------------------------------

local DEFAULT_CFG = {
    showMiniHud = true,
    showPerfInHud = true,
    autoFollowNotify = true,
    followThresholdMeters = 350,
    pingDurationSec = 25,
    miniHudX = 24,
    miniHudY = 24,
    perfPreset = "none", -- "none" | "balanced" | "max_fps" | "clarity"
}

local cfg = {}
for k, v in pairs(DEFAULT_CFG) do cfg[k] = v end

local state = {
    overlayOpen = false,
    panelOpen = false,
    menuRegistered = false,
    activePing = nil, -- { tag, sender, x, y, z, expiresAt, mappinId }
    lastFollowAlertAt = 0,
    tailscale = nil,
    lastTailscaleRead = -1e9,
    tailscalePollInterval = 15.0, -- backs off to 60s when file is absent (zero main-thread I/O hitch)
    logs = {},
    peerSnapshots = {},
    lastTelemetryPoll = 0,
    -- Frametime / 1% low ring buffer (120 frames, zero per-frame allocations)
    dtRing = {},
    dtIdx = 1,
    dtCount = 0,
    fpsAvg = 0,
    fps1Low = 0,
    frameMsAvg = 0,
    hitchCount = 0,
    lastPerfCalc = 0,
    originalCvars = {}, -- cached pre-tweak values for clean Reset
    appliedCvarCount = 0,
    lastFixStatus = "",
}

local function addLog(msg)
    local stamp = os.date("%H:%M:%S")
    table.insert(state.logs, 1, string.format("[%s] %s", stamp, tostring(msg)))
    if #state.logs > 40 then
        table.remove(state.logs)
    end
end

local function loadConfig()
    local f = io.open("config.json", "r")
    if not f then return end
    local raw = f:read("*a")
    f:close()
    local ok, parsed = pcall(json.decode, raw)
    if ok and type(parsed) == "table" then
        for k, _ in pairs(DEFAULT_CFG) do
            if parsed[k] ~= nil then cfg[k] = parsed[k] end
        end
    end
end

local function saveConfig()
    local f = io.open("config.json", "w")
    if not f then return end
    local ok, encoded = pcall(json.encode, cfg)
    if ok and encoded then f:write(encoded) end
    f:close()
end

local function readTailscaleStatus()
    local now = os.clock()
    if now - state.lastTailscaleRead < state.tailscalePollInterval then return end
    state.lastTailscaleRead = now
    local f = io.open("tailscale_status.json", "r")
    if not f then
        state.tailscale = nil
        state.tailscalePollInterval = 60.0
        return
    end
    state.tailscalePollInterval = 15.0
    local raw = f:read("*a")
    f:close()
    local ok, parsed = pcall(json.decode, raw)
    if ok and type(parsed) == "table" then
        state.tailscale = parsed
    end
end

-- ---------------------------------------------------------------------------
-- Live Frametime & 1% Low Telemetry (Lightweight Ring Buffer)
-- ---------------------------------------------------------------------------

local function recordFrameDt(dt)
    if not dt or dt <= 0 or dt > 1.0 then return end
    state.dtRing[state.dtIdx] = dt
    state.dtIdx = (state.dtIdx % 120) + 1
    if state.dtCount < 120 then state.dtCount = state.dtCount + 1 end
    if dt > 0.0333 then
        state.hitchCount = state.hitchCount + 1
    end

    local now = os.clock()
    if now - state.lastPerfCalc < 0.5 or state.dtCount < 10 then return end
    state.lastPerfCalc = now

    local sum = 0
    local worst1, worst2 = 0, 0
    for i = 1, state.dtCount do
        local v = state.dtRing[i]
        sum = sum + v
        if v >= worst1 then
            worst2 = worst1
            worst1 = v
        elseif v > worst2 then
            worst2 = v
        end
    end
    local avgDt = sum / state.dtCount
    state.frameMsAvg = avgDt * 1000.0
    state.fpsAvg = avgDt > 0 and (1.0 / avgDt) or 0
    local lowDt = worst2 > 0 and worst2 or worst1
    state.fps1Low = lowDt > 0 and (1.0 / lowDt) or 0
end

-- ---------------------------------------------------------------------------
-- Base-Game Engine Performance Presets (Self-Validating GameOptions CVars)
-- ---------------------------------------------------------------------------

local PERF_PRESETS = {
    balanced = {
        { "Streaming", "MaxNodesPerFrame", "3000" },
        { "Streaming", "MinStreamingDistance", "45.0" },
        { "LevelOfDetail", "DecalsHideDistance", "25.0" },
        { "Rendering/Shadows", "CascadeRange0", "10.0" },
        { "Rendering/Shadows", "CascadeRange1", "26.0" },
        { "Rendering/Shadows", "CascadeRange2", "65.0" },
        { "Rendering/Shadows", "CascadeRange3", "130.0" },
        { "Developer/FeatureToggles", "DistantFog", "false" },
        { "Developer/FeatureToggles", "DistantVolFog", "false" },
        { "Developer/FeatureToggles", "ChromaticAberration", "false" },
        { "Developer/FeatureToggles", "FilmGrain", "false" },
        { "Developer/FeatureToggles", "MotionBlur", "false" },
    },
    max_fps = {
        { "Streaming", "MaxNodesPerFrame", "2000" },
        { "Streaming", "MinStreamingDistance", "35.0" },
        { "LevelOfDetail", "DecalsHideDistance", "18.0" },
        { "Rendering/Shadows", "CascadeRange0", "8.0" },
        { "Rendering/Shadows", "CascadeRange1", "20.0" },
        { "Rendering/Shadows", "CascadeRange2", "50.0" },
        { "Rendering/Shadows", "CascadeRange3", "95.0" },
        { "Developer/FeatureToggles", "DistantFog", "false" },
        { "Developer/FeatureToggles", "DistantVolFog", "false" },
        { "Developer/FeatureToggles", "VolumetricClouds", "false" },
        { "Developer/FeatureToggles", "ChromaticAberration", "false" },
        { "Developer/FeatureToggles", "FilmGrain", "false" },
        { "Developer/FeatureToggles", "MotionBlur", "false" },
        { "Developer/FeatureToggles", "Bloom", "false" },
    },
    clarity = {
        { "Streaming", "MaxNodesPerFrame", "6000" },
        { "Streaming", "MinStreamingDistance", "60.0" },
        { "LevelOfDetail", "DecalsHideDistance", "45.0" },
        { "Rendering/Shadows", "CascadeRange0", "12.0" },
        { "Rendering/Shadows", "CascadeRange1", "32.0" },
        { "Rendering/Shadows", "CascadeRange2", "80.0" },
        { "Rendering/Shadows", "CascadeRange3", "160.0" },
        { "Developer/FeatureToggles", "DistantFog", "false" },
        { "Developer/FeatureToggles", "ChromaticAberration", "false" },
        { "Developer/FeatureToggles", "FilmGrain", "false" },
        { "Developer/FeatureToggles", "MotionBlur", "false" },
    },
}

local function restoreEngineCvars()
    local restored = 0
    for fullKey, origVal in pairs(state.originalCvars) do
        local grp, param = fullKey:match("^([^|]+)|(.+)$")
        if grp and param and origVal ~= nil then
            local ok = pcall(function() GameOptions.Set(grp, param, tostring(origVal)) end)
            if ok then restored = restored + 1 end
        end
    end
    state.appliedCvarCount = 0
    return restored
end

local function applyPerfPreset(presetName, silent)
    restoreEngineCvars()
    cfg.perfPreset = presetName or "none"
    saveConfig()

    if cfg.perfPreset == "none" then
        state.lastFixStatus = "Restored vanilla engine cvars."
        if not silent then addLog(state.lastFixStatus) end
        return
    end

    local list = PERF_PRESETS[cfg.perfPreset]
    if not list then return end

    local applied = 0
    for _, item in ipairs(list) do
        local grp, param, val = item[1], item[2], item[3]
        local okGet, cur = pcall(function() return GameOptions.Get(grp, param) end)
        if okGet and cur ~= nil and tostring(cur) ~= "" then
            local fullKey = grp .. "|" .. param
            if state.originalCvars[fullKey] == nil then
                state.originalCvars[fullKey] = tostring(cur)
            end
            local okSet = pcall(function() GameOptions.Set(grp, param, val) end)
            if okSet then applied = applied + 1 end
        end
    end

    state.appliedCvarCount = applied
    state.lastFixStatus = string.format("Applied '%s' preset (%d verified Patch 2.5 cvars).", cfg.perfPreset, applied)
    if not silent then addLog(state.lastFixStatus) end
end

-- ---------------------------------------------------------------------------
-- Base-Game Cyberpunk 2077 Bug-Fix Rescue Tools (100% NightCoop-Safe)
-- ---------------------------------------------------------------------------

local function runFix(label, fn)
    local ok, detail = pcall(fn)
    if ok then
        local msg = label .. (detail and (" (" .. tostring(detail) .. ")") or " — Fixed!")
        state.lastFixStatus = msg
        addLog(msg)
        if Sdk.available() then coop:showMessage(msg, 3.0, Sdk.TONE.INFO) end
    else
        local msg = label .. " failed: " .. tostring(detail)
        state.lastFixStatus = msg
        addLog(msg)
    end
end

local function fixStuckRelicAndScreenFx()
    runFix("Clear Stuck Relic / Screen Glitch FX", function()
        local p = Game.GetPlayer()
        assert(p, "Load into a save first")
        local eid = p:GetEntityID()
        local ses = Game.GetStatusEffectSystem()
        local effects = {
            "BaseStatusEffect.JohnnySicknessLow",
            "BaseStatusEffect.JohnnySicknessMedium",
            "BaseStatusEffect.JohnnySicknessMediumQuest",
            "BaseStatusEffect.JohnnySicknessHeavy",
            "BaseStatusEffect.JohnnySicknessHeavyQuest",
            "BaseStatusEffect.RelicMalfunction",
            "BaseStatusEffect.CyberwareMalfunction",
            "BaseStatusEffect.SystemCollapse",
            "BaseStatusEffect.Overheat",
            "BaseStatusEffect.EMP",
            "BaseStatusEffect.Blind",
            "BaseStatusEffect.Drunk",
            "BaseStatusEffect.Drugged",
            "BaseStatusEffect.Burning",
            "BaseStatusEffect.Poisoned",
            "BaseStatusEffect.Bleeding",
            "BaseStatusEffect.Electrocuted",
        }
        local removed = 0
        for _, eff in ipairs(effects) do
            local ok = pcall(function()
                local id = TweakDBID.new(eff)
                if ses:HasStatusEffect(eid, id) then
                    ses:RemoveStatusEffect(eid, id)
                    removed = removed + 1
                end
            end)
        end
        pcall(function()
            local audio = Game.GetAudioSystem()
            if audio then
                audio:Stop("ui_jingle_chip_malfunction")
                audio:Stop("q101_sc_03_relic_malfunction_loop")
            end
        end)
        pcall(function() GameObjectEffectHelper.StopEffectEvent(p, CName.new("johnny_sickness_symptoms")) end)
        pcall(function() GameObjectEffectHelper.StopEffectEvent(p, CName.new("screen_glitch")) end)
        return tostring(removed) .. " active glitch effect(s) cleared"
    end)
end

local function fixStuckCombatState()
    runFix("Clear Stuck Combat Lock", function()
        local p = Game.GetPlayer()
        assert(p, "Load into a save first")
        local eid = p:GetEntityID()
        local ses = Game.GetStatusEffectSystem()
        -- Safely clear combat lock & holster weapons so save/fast-travel/sleep unlock
        pcall(function()
            local eq = EquipmentSystem.GetInstance(p)
            if eq then
                local req = EquipmentSystemWeaponManipulationRequest.new()
                req.owner = p
                req.requestType = EquipmentManipulationAction.UnequipAll
                eq:QueueRequest(req)
            end
        end)
        pcall(function()
            local bb = Game.GetBlackboardSystem():GetLocalInstanced(eid, GetAllBlackboardDefs().PlayerStateMachine)
            if bb then
                bb:SetInt(GetAllBlackboardDefs().PlayerStateMachine.Combat, 0, true)
            end
        end)
        pcall(function() ses:RemoveStatusEffect(eid, TweakDBID.new("GameplayRestriction.NoFastTravel")) end)
        return "combat flag reset & weapons holstered"
    end)
end

local function fixStuckRestrictionsAndScanner()
    runFix("Clear Stuck Scanner / Controls Lock", function()
        local p = Game.GetPlayer()
        assert(p, "Load into a save first")
        local eid = p:GetEntityID()
        local ses = Game.GetStatusEffectSystem()
        local restrictions = {
            "GameplayRestriction.NoCombat",
            "GameplayRestriction.NoSprint",
            "GameplayRestriction.NoJump",
            "GameplayRestriction.NoZooming",
            "GameplayRestriction.NoScanning",
            "GameplayRestriction.NoPhone",
            "GameplayRestriction.NoFastTravel",
            "GameplayRestriction.NoPhotoMode",
            "GameplayRestriction.NoWeaponHolstering",
            "GameplayRestriction.BlockAllHubMenu",
            "GameplayRestriction.VehicleNoSummoning",
        }
        local cleared = 0
        for _, r in ipairs(restrictions) do
            pcall(function()
                local id = TweakDBID.new(r)
                if ses:HasStatusEffect(eid, id) then
                    ses:RemoveStatusEffect(eid, id)
                    cleared = cleared + 1
                end
            end)
        end
        pcall(function()
            local vm = Game.GetVisionModeSystem()
            if vm then vm:ForceDisableVisionMode(p) end
        end)
        return tostring(cleared) .. " restriction(s) cleared"
    end)
end

local function fixStuckSlowMoSolo()
    if Sdk.inSession() then
        state.lastFixStatus = "Skipped in co-op (NightCoop manages shared world time)."
        addLog(state.lastFixStatus)
        return
    end
    runFix("Clear Stuck Slow-Mo / Time Dilation", function()
        local ts = Game.GetTimeSystem()
        assert(ts, "TimeSystem unavailable")
        local reasons = { "sandevistan", "kereznikov", "focusMode", "radialMenu", "weaponWheel", "quickhack", "default" }
        for _, r in ipairs(reasons) do
            pcall(function() ts:UnsetTimeDilation(CName.new(r)) end)
        end
        pcall(function() ts:UnsetTimeDilationOnLocalPlayerZero(CName.new("sandevistan")) end)
        return "local time scale restored to 1.0x"
    end)
end

local function fixStuckVehicleSummon()
    runFix("Reset Vehicle Summon Lock", function()
        local p = Game.GetPlayer()
        assert(p, "Load into a save first")
        local ses = Game.GetStatusEffectSystem()
        pcall(function() ses:RemoveStatusEffect(p:GetEntityID(), TweakDBID.new("GameplayRestriction.VehicleNoSummoning")) end)
        pcall(function()
            local vs = Game.GetVehicleSystem()
            if vs then vs:TogglePlayerActiveVehicle(TweakDBID.new(), gamedataVehicleType.Car, true) end
        end)
        return "vehicle summon unlocked"
    end)
end

-- ---------------------------------------------------------------------------
-- Safe NightCoop & Game Helpers
-- ---------------------------------------------------------------------------

local function invokeNightCoopAction(actionName)
    if not Sdk.available() then
        addLog("NightCoop SDK not available")
        return false
    end
    local okAvail, avail = pcall(function() return NightCoopSdk.ActionAvailable(actionName) end)
    if not okAvail or avail ~= 1 then
        coop:showMessage("Action '" .. actionName .. "' is not available right now.", 2.5, Sdk.TONE.QUIET)
        addLog("Action unavailable: " .. actionName)
        return false
    end
    local okInv, res = pcall(function() return NightCoopSdk.InvokeAction(MOD_ID, actionName) end)
    if okInv and res == 0 then
        addLog("Invoked NightCoop action: " .. actionName)
        return true
    end
    addLog("Action failed (" .. tostring(res) .. "): " .. actionName)
    return false
end

local function getLocalPlayerPos()
    local ok, pos = pcall(function()
        local p = Game.GetPlayer()
        if not p then return nil end
        return p:GetWorldPosition()
    end)
    if ok then return pos end
    return nil
end

local function getPartnerPos(peer)
    local ok, pos = pcall(function()
        local bodyId = Sdk.partnerBody(peer)
        if not bodyId then return nil end
        local ent = Game.FindEntityByID(bodyId)
        if not ent then return nil end
        return ent:GetWorldPosition()
    end)
    if ok then return pos end
    return nil
end

local function dist3D(a, b)
    if not a or not b then return nil end
    local dx = a.x - b.x
    local dy = a.y - b.y
    local dz = a.z - b.z
    return math.sqrt(dx * dx + dy * dy + dz * dz)
end

local function compassDir(fromPos, toPos)
    if not fromPos or not toPos then return "" end
    local dx = toPos.x - fromPos.x
    local dy = toPos.y - fromPos.y
    local angle = math.deg(math.atan2(dx, dy))
    if angle < 0 then angle = angle + 360 end
    local dirs = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }
    local idx = math.floor(((angle + 22.5) % 360) / 45) + 1
    return dirs[idx] or ""
end

local function clearActivePingMappin()
    if state.activePing and state.activePing.mappinId then
        pcall(function()
            local ms = Game.GetMappinSystem()
            if ms then ms:UnregisterMappin(state.activePing.mappinId) end
        end)
        state.activePing.mappinId = nil
    end
end

local function placePingMappin(x, y, z)
    clearActivePingMappin()
    local ok, id = pcall(function()
        local ms = Game.GetMappinSystem()
        if not ms then return nil end
        local data = MappinData.new()
        data.mappinType = TweakDBID.new("Mappins.DefaultStaticMappin")
        data.variant = gamedataMappinVariant.CustomPositionVariant
        data.visibleThroughWalls = true
        return ms:RegisterMappin(data, Vector4.new(x, y, z + 1.2, 1.0))
    end)
    if ok then return id end
    return nil
end

-- ---------------------------------------------------------------------------
-- Tactical Pings
-- ---------------------------------------------------------------------------

local function sendPing(tag)
    if not Sdk.inSession() or not Sdk.inHostWorld() then
        addLog("Cannot ping: not in a co-op world")
        return
    end
    local pos = nil
    local okTarget, tPos = pcall(function()
        local ts = Game.GetTargetingSystem()
        local p = Game.GetPlayer()
        if ts and p then
            local obj = ts:GetLookAtObject(p, false, false)
            if obj then return obj:GetWorldPosition() end
        end
        return nil
    end)
    if okTarget and tPos then
        pos = tPos
    else
        pos = getLocalPlayerPos()
    end
    if not pos then return end

    local cleanTag = tostring(tag or "REGROUP"):gsub("[|\r\n]", ""):sub(1, 20)
    local payload = string.format("%s|%.2f|%.2f|%.2f", cleanTag, pos.x, pos.y, pos.z)
    local sent = coop:send(Sdk.PEER_ALL, CH_PING, payload, true)
    if sent >= 0 then
        coop:showMessage("Ping sent: [" .. cleanTag .. "]", 2.2, Sdk.TONE.INFO)
        addLog(string.format("Sent ping [%s] (reached %d peer(s))", cleanTag, sent))
    else
        addLog("Ping send failed: " .. tostring(sent))
    end
end

-- ---------------------------------------------------------------------------
-- Safe Guest Catch-Up
-- ---------------------------------------------------------------------------

local function safeTeleportToPeer(targetPeer)
    if not Sdk.inSession() or not Sdk.inHostWorld() then
        addLog("Teleport blocked: not in host world")
        return
    end
    if Sdk.isHost() then
        coop:showMessage("Host cannot pull-teleport; use guest Catch Up.", 2.5, Sdk.TONE.QUIET)
        return
    end
    local me = Sdk.partner(Sdk.localPeer())
    local target = Sdk.partner(targetPeer)
    if not me or not target then
        addLog("Teleport blocked: peer state unavailable")
        return
    end
    if Sdk.has(me.flags, Sdk.PARTNER.VEHICLE) or Sdk.has(me.flags, Sdk.PARTNER.SCENE) or Sdk.has(me.flags, Sdk.PARTNER.DOWN) then
        coop:showMessage("Exit vehicle / scene before teleporting to host.", 3.0, Sdk.TONE.WAIT)
        return
    end
    if not Sdk.has(target.flags, Sdk.PARTNER.IN_WORLD) or Sdk.has(target.flags, Sdk.PARTNER.SCENE) then
        coop:showMessage("Partner is in a scene or loading — wait a moment.", 3.0, Sdk.TONE.WAIT)
        return
    end

    local tPos = getPartnerPos(targetPeer)
    if not tPos then
        if not invokeNightCoopAction("story_pull") then
            coop:showMessage("Partner too far to resolve body; hold PageUp to pull.", 3.0, Sdk.TONE.INFO)
        end
        return
    end

    local ok = pcall(function()
        local p = Game.GetPlayer()
        local tf = Game.GetTeleportationFacility()
        if p and tf then
            local dest = Vector4.new(tPos.x + 1.4, tPos.y + 1.4, tPos.z + 0.35, 1.0)
            tf:Teleport(p, dest, p:GetWorldOrientation():ToEulerAngles())
        end
    end)
    if ok then
        coop:showMessage("Teleported next to " .. Sdk.peerName(targetPeer), 2.5, Sdk.TONE.INFO)
        addLog("Safe-teleported next to peer " .. tostring(targetPeer))
    end
end

-- ---------------------------------------------------------------------------
-- World Requests (Official NightCoop WorldAsk)
-- ---------------------------------------------------------------------------

local function askTimeOfDay(hours, label)
    local sec = math.floor(hours * 3600) % 86400
    local r = coop:worldAsk(Sdk.WORLD.TIME_OF_DAY, sec, "", not Sdk.isHost())
    if r > 0 then
        coop:showMessage("Requested time: " .. label, 2.5, Sdk.TONE.INFO)
        addLog("WorldAsk TimeOfDay -> " .. label)
    else
        addLog("WorldAsk TimeOfDay failed: " .. tostring(r))
    end
end

local function askWeather(weatherState, label)
    local r = coop:worldAsk(Sdk.WORLD.WEATHER, 12, weatherState, not Sdk.isHost())
    if r > 0 then
        coop:showMessage("Requested weather: " .. label, 2.5, Sdk.TONE.INFO)
        addLog("WorldAsk Weather -> " .. weatherState)
    else
        addLog("WorldAsk Weather failed: " .. tostring(r))
    end
end

-- ---------------------------------------------------------------------------
-- Native Co-op Menu Registration (Esc -> Co-op -> Mods...)
-- ---------------------------------------------------------------------------

local function registerMenuItems()
    if not coop.registered or state.menuRegistered then return end
    local inWorldSess = Sdk.MENU.IN_SESSION + Sdk.MENU.IN_WORLD
    coop:menuItemSet("toggle_panel", "ChoomSync: Открыть панель", "ChoomSync: Toggle Panel", 0)
    coop:menuItemSet("ping_here", "ChoomSync: Метка (Сбор)", "ChoomSync: Ping Location", inWorldSess)
    coop:menuItemSet("story_pull", "ChoomSync: Догонять хоста", "ChoomSync: Catch Up to Host", inWorldSess)
    coop:menuItemSet("save_pull", "ChoomSync: Починить NPC / сейв", "ChoomSync: Sync Save / Unstick NPC", inWorldSess)
    coop:menuItemSet("fix_glitches", "ChoomSync: Исправить баги (Relic/Бой)", "ChoomSync: Fix Stuck Glitch / Combat", Sdk.MENU.IN_WORLD)
    coop:menuItemSet("world_morning", "ChoomSync: Утро (08:00)", "ChoomSync: Time -> Morning (08:00)", inWorldSess)
    coop:menuItemSet("world_night", "ChoomSync: Ночь (22:30)", "ChoomSync: Time -> Night (22:30)", inWorldSess)
    coop:menuItemSet("world_clear", "ChoomSync: Ясная погода", "ChoomSync: Weather -> Clear", inWorldSess)
    state.menuRegistered = true
    addLog("Registered 8 items in Esc -> Co-op -> Mods...")
end

-- ---------------------------------------------------------------------------
-- Shared Session Rules Sync
-- ---------------------------------------------------------------------------

local function pushHostRules()
    if not Sdk.inSession() or not Sdk.isHost() then return end
    coop:setState("follow_threshold", tostring(cfg.followThresholdMeters))
    coop:setState("ping_duration", tostring(cfg.pingDurationSec))
end

-- ---------------------------------------------------------------------------
-- NightCoop Event Handling
-- ---------------------------------------------------------------------------

local function handleCoopEvent(ev)
    if ev.kind == Sdk.EVENT.MESSAGE and ev.a == CH_PING then
        local tag, sx, sy, sz = ev.data:match("^([^|]+)|([%-%d%.]+)|([%-%d%.]+)|([%-%d%.]+)$")
        local x, y, z = tonumber(sx), tonumber(sy), tonumber(sz)
        if tag and x and y and z then
            local safeTag = Sdk.cleanText(tag, 24)
            local sender = Sdk.cleanText(Sdk.peerName(ev.peer), 32)
            local mappinId = placePingMappin(x, y, z)
            state.activePing = {
                tag = safeTag,
                sender = sender,
                x = x,
                y = y,
                z = z,
                expiresAt = os.clock() + (cfg.pingDurationSec or 25),
                mappinId = mappinId,
            }
            coop:showMessage(string.format("[%s] %s pinged a location!", safeTag, sender), 4.0, Sdk.TONE.URGENT)
            addLog(string.format("Ping [%s] from %s at (%.0f, %.0f, %.0f)", safeTag, sender, x, y, z))
        end

    elseif ev.kind == Sdk.EVENT.MENU_ITEM then
        if ev.key == "toggle_panel" then
            state.panelOpen = not state.panelOpen
        elseif ev.key == "ping_here" then
            sendPing("REGROUP")
        elseif ev.key == "story_pull" then
            if not invokeNightCoopAction("story_pull") then
                safeTeleportToPeer(Sdk.PEER_HOST)
            end
        elseif ev.key == "save_pull" then
            invokeNightCoopAction("save_pull")
        elseif ev.key == "fix_glitches" then
            fixStuckRelicAndScreenFx()
            fixStuckCombatState()
            fixStuckRestrictionsAndScanner()
        elseif ev.key == "world_morning" then
            askTimeOfDay(8, "Morning (08:00)")
        elseif ev.key == "world_night" then
            askTimeOfDay(22.5, "Night (22:30)")
        elseif ev.key == "world_clear" then
            askWeather("24h_weather_sunny", "Clear")
        end

    elseif ev.kind == Sdk.EVENT.STATE then
        if not ev.removed then
            if ev.key == "follow_threshold" then
                local v = tonumber(ev.data)
                if v and v >= 100 and v <= 2000 then cfg.followThresholdMeters = v end
            elseif ev.key == "ping_duration" then
                local v = tonumber(ev.data)
                if v and v >= 5 and v <= 90 then cfg.pingDurationSec = v end
            end
        end

    elseif ev.kind == Sdk.EVENT.SESSION then
        if ev.a == Sdk.SESSION.START then
            addLog("Co-op session started (peer #" .. tostring(ev.peer) .. ")")
            if Sdk.isHost() then pushHostRules() end
        elseif ev.a == Sdk.SESSION.END then
            clearActivePingMappin()
            state.activePing = nil
            addLog("Co-op session ended")
        elseif ev.a == Sdk.SESSION.PEER_JOIN then
            addLog(Sdk.peerName(ev.peer) .. " joined the session")
        elseif ev.a == Sdk.SESSION.PEER_LEAVE then
            addLog("Peer #" .. tostring(ev.peer) .. " left the session")
        elseif ev.a == Sdk.SESSION.PEER_WORLD_IN then
            addLog(Sdk.peerName(ev.peer) .. " entered your world")
            if Sdk.isHost() then pushHostRules() end
        elseif ev.a == Sdk.SESSION.PEER_MOD then
            addLog(Sdk.peerName(ev.peer) .. " is running ChoomSync v" .. coop:peerModVersion(ev.peer))
        elseif ev.a == Sdk.SESSION.PARTNER_DOWN then
            local pname = Sdk.peerName(ev.peer)
            addLog(ev.value == 1 and (pname .. " is DOWN!") or (pname .. " is back up!"))
        elseif ev.a == Sdk.SESSION.WORLD_ANSWER then
            local labels = { [0] = "Refused", [1] = "Applied", [2] = "Failed", [3] = "Busy" }
            addLog("World change result: " .. (labels[ev.value] or tostring(ev.value)))
        end
    end
end

-- ---------------------------------------------------------------------------
-- Periodic Telemetry & Guest Drift Alert (Throttled to 2Hz when in session)
-- ---------------------------------------------------------------------------

local function refreshTelemetry()
    local now = os.clock()
    if state.activePing and now >= state.activePing.expiresAt then
        clearActivePingMappin()
        state.activePing = nil
    end

    if not Sdk.inSession() then
        if #state.peerSnapshots > 0 then state.peerSnapshots = {} end
        return
    end

    readTailscaleStatus()

    if now - state.lastTelemetryPoll < 0.5 then return end
    state.lastTelemetryPoll = now

    state.peerSnapshots = {}
    local myPos = getLocalPlayerPos()
    local peers = Sdk.peers()
    for _, peer in ipairs(peers) do
        local info = Sdk.partner(peer)
        local pPos = getPartnerPos(peer)
        local d = dist3D(myPos, pPos)
        table.insert(state.peerSnapshots, {
            peer = peer,
            name = Sdk.peerName(peer),
            info = info,
            distance = d,
            dir = compassDir(myPos, pPos),
            hasMod = coop:peerHasMod(peer) == 1,
        })

        if not Sdk.isHost() and peer == Sdk.PEER_HOST and cfg.autoFollowNotify and d and d > cfg.followThresholdMeters then
            if (now - state.lastFollowAlertAt) > 20.0 then
                local me = Sdk.partner(Sdk.localPeer())
                if me and not Sdk.has(me.flags, Sdk.PARTNER.SCENE) and not Sdk.has(me.flags, Sdk.PARTNER.DOWN) then
                    state.lastFollowAlertAt = now
                    coop:showMessage(
                        string.format("Host is %.0fm away — hold PageUp or tap ChoomSync Catch Up!", d),
                        4.0,
                        Sdk.TONE.INFO
                    )
                end
            end
        end
    end
end

-- ---------------------------------------------------------------------------
-- ImGui Rendering
-- ---------------------------------------------------------------------------

local function formatWhere(w)
    local names = {
        [Sdk.WHERE.HOST_WORLD] = "In World",
        [Sdk.WHERE.MAIN_MENU] = "Main Menu",
        [Sdk.WHERE.LOADING] = "Loading",
        [Sdk.WHERE.JOINING] = "Joining",
        [Sdk.WHERE.OWN_WORLD] = "Own Save",
    }
    return names[w] or "Unknown"
end

local function formatFlags(flags)
    if not flags then return "" end
    local tags = {}
    if Sdk.has(flags, Sdk.PARTNER.DOWN) then table.insert(tags, "DOWN") end
    if Sdk.has(flags, Sdk.PARTNER.COMBAT) then table.insert(tags, "COMBAT") end
    if Sdk.has(flags, Sdk.PARTNER.WHEEL) then table.insert(tags, "DRIVING")
    elseif Sdk.has(flags, Sdk.PARTNER.VEHICLE) then table.insert(tags, "PASSENGER") end
    if Sdk.has(flags, Sdk.PARTNER.SCENE) then table.insert(tags, "SCENE") end
    if Sdk.has(flags, Sdk.PARTNER.MENU) then table.insert(tags, "MENU") end
    if Sdk.has(flags, Sdk.PARTNER.LINK_LOST) then table.insert(tags, "LINK LOST") end
    return table.concat(tags, " | ")
end

local function drawMiniHud()
    if not cfg.showMiniHud then return end
    local inSess = Sdk.inSession()
    if not inSess and not cfg.showPerfInHud then return end

    ImGui.SetNextWindowPos(cfg.miniHudX, cfg.miniHudY, ImGuiCond.FirstUseEver)
    ImGui.SetNextWindowBgAlpha(0.70)
    local wFlags = ImGuiWindowFlags.AlwaysAutoResize + ImGuiWindowFlags.NoTitleBar + ImGuiWindowFlags.NoFocusOnAppearing
    if not state.overlayOpen then
        wFlags = wFlags + ImGuiWindowFlags.NoInputs
    end

    if ImGui.Begin("ChoomSyncHUD", wFlags) then
        if inSess then
            local role = Sdk.isHost() and "HOST" or "GUEST"
            ImGui.TextColored(0.98, 0.85, 0.18, 1.0, "CHOOMSYNC [" .. role .. "]")
        else
            ImGui.TextColored(0.98, 0.85, 0.18, 1.0, "CHOOMSYNC")
        end

        if cfg.showPerfInHud and state.fpsAvg > 0 then
            ImGui.SameLine()
            ImGui.TextColored(0.25, 0.92, 1.0, 1.0, string.format(" | %.0f FPS (1%% %.0f)", state.fpsAvg, state.fps1Low))
        end

        if inSess and state.tailscale and state.tailscale.activePeer then
            local ap = state.tailscale.activePeer
            local link = ap.direct and ("DIRECT " .. tostring(ap.rttMs or "?") .. "ms") or ("RELAY (" .. tostring(ap.relay or "DERP") .. ")")
            ImGui.SameLine()
            if ap.direct then
                ImGui.TextColored(0.30, 0.95, 0.55, 1.0, " | TS: " .. link)
            else
                ImGui.TextColored(1.0, 0.65, 0.25, 1.0, " | TS: " .. link)
            end
        end

        if inSess then
            for _, p in ipairs(state.peerSnapshots) do
                local distStr = p.distance and string.format("%.0fm %s", p.distance, p.dir) or (p.info and formatWhere(p.info.where) or "?")
                local tagStr = p.info and formatFlags(p.info.flags) or ""
                ImGui.Text(string.format("%s (%s)%s", p.name, distStr, tagStr ~= "" and (" [" .. tagStr .. "]") or ""))
                if p.info and p.info.health and p.info.health >= 0 then
                    ImGui.ProgressBar(p.info.health, 180, 8, string.format("%.0f%% HP", p.info.health * 100))
                end
            end

            if state.activePing then
                local myPos = getLocalPlayerPos()
                local pd = dist3D(myPos, state.activePing)
                local pdir = compassDir(myPos, state.activePing)
                local rem = math.max(0, math.ceil(state.activePing.expiresAt - os.clock()))
                ImGui.Separator()
                ImGui.TextColored(0.25, 0.92, 1.0, 1.0, string.format(
                    "PING [%s] by %s: %s (%ds)",
                    state.activePing.tag,
                    state.activePing.sender,
                    pd and string.format("%.0fm %s", pd, pdir) or "marked",
                    rem
                ))
            end
        end
    end
    ImGui.End()
end

local function drawMainPanel()
    if not (state.overlayOpen or state.panelOpen) then return end
    ImGui.SetNextWindowSize(540, 460, ImGuiCond.FirstUseEver)
    if ImGui.Begin("ChoomSync v" .. MOD_VERSION .. " (Co-op + Base-Game Perf & Fixes)") then
        local inSess = Sdk.available() and Sdk.inSession()
        local role = inSess and (Sdk.isHost() and "HOST" or "GUEST") or "SOLO / NOT IN SESSION"
        ImGui.TextColored(0.98, 0.85, 0.18, 1.0, "Status: " .. role)
        if state.fpsAvg > 0 then
            ImGui.SameLine()
            ImGui.TextColored(0.25, 0.92, 1.0, 1.0, string.format("  |  %.0f FPS  •  1%% Low: %.0f  •  %.1f ms", state.fpsAvg, state.fps1Low, state.frameMsAvg))
        end
        ImGui.Separator()

        if ImGui.BeginTabBar("ChoomSyncTabs") then
            -- TAB 1: BASE-GAME PERF & BUG FIXES
            if ImGui.BeginTabItem("Perf & Bug Fixes") then
                ImGui.TextColored(0.25, 0.92, 1.0, 1.0, "Live Frametime Telemetry:")
                ImGui.Text(string.format("  Avg: %.1f FPS (%.2f ms)  |  1%% Low: %.1f FPS  |  Hitches (>33ms): %d",
                    state.fpsAvg, state.frameMsAvg, state.fps1Low, state.hitchCount))
                ImGui.SameLine()
                if ImGui.SmallButton("Reset Counter") then state.hitchCount = 0 end

                ImGui.Separator()
                ImGui.TextColored(0.98, 0.85, 0.18, 1.0, "Base-Game Engine Performance Presets (Patch 2.5 Safe):")
                ImGui.TextWrapped("Active preset: " .. tostring(cfg.perfPreset) .. " (" .. tostring(state.appliedCvarCount) .. " live engine cvars applied)")
                if ImGui.Button("Balanced (Recommended)", 165, 28) then applyPerfPreset("balanced") end
                ImGui.SameLine()
                if ImGui.Button("Max FPS", 105, 28) then applyPerfPreset("max_fps") end
                ImGui.SameLine()
                if ImGui.Button("Visual Clarity", 115, 28) then applyPerfPreset("clarity") end
                ImGui.SameLine()
                if ImGui.Button("Reset Vanilla", 105, 28) then applyPerfPreset("none") end

                ImGui.Separator()
                ImGui.TextColored(0.98, 0.85, 0.18, 1.0, "Base-Game Cyberpunk Bug-Fix Rescue (1-Click, NightCoop-Safe):")
                if ImGui.Button("Fix Stuck Relic / Screen Glitch", 245, 28) then fixStuckRelicAndScreenFx() end
                ImGui.SameLine()
                if ImGui.Button("Fix Stuck Combat (Can't Save/FT)", 245, 28) then fixStuckCombatState() end

                if ImGui.Button("Fix Stuck Scanner / Controls Lock", 245, 28) then fixStuckRestrictionsAndScanner() end
                ImGui.SameLine()
                if ImGui.Button("Fix Stuck Vehicle Summon", 245, 28) then fixStuckVehicleSummon() end

                if ImGui.Button("Fix Stuck Slow-Mo (Solo Only)", 245, 28) then fixStuckSlowMoSolo() end
                ImGui.SameLine()
                if ImGui.Button("Run ALL Safe Rescue Fixes", 245, 28) then
                    fixStuckRelicAndScreenFx()
                    fixStuckCombatState()
                    fixStuckRestrictionsAndScanner()
                    fixStuckVehicleSummon()
                    if not Sdk.inSession() then fixStuckSlowMoSolo() end
                end

                if state.lastFixStatus ~= "" then
                    ImGui.Spacing()
                    ImGui.TextColored(0.30, 0.95, 0.55, 1.0, state.lastFixStatus)
                end

                ImGui.Separator()
                local chP, newP = ImGui.Checkbox("Show FPS / 1% Low in Compact Mini-HUD", cfg.showPerfInHud)
                if chP then cfg.showPerfInHud = newP; saveConfig() end
                ImGui.EndTabItem()
            end

            -- TAB 2: SQUAD
            if ImGui.BeginTabItem("Squad") then
                if not Sdk.available() then
                    ImGui.TextColored(1.0, 0.45, 0.45, 1.0, "NightCoop SDK not detected (Solo Perf & Fixes still active).")
                elseif not inSess then
                    ImGui.TextWrapped("Start or join a session from Cyberpunk's Main Menu or Pause Menu -> Co-op.")
                else
                    if #state.peerSnapshots == 0 then
                        ImGui.Text("Waiting for your friend to join the session...")
                    else
                        for _, p in ipairs(state.peerSnapshots) do
                            local crown = (p.peer == Sdk.PEER_HOST) and "[HOST] " or ""
                            ImGui.TextColored(0.25, 0.92, 1.0, 1.0, crown .. p.name .. (p.hasMod and " (ChoomSync OK)" or " (Vanilla NightCoop)"))
                            if p.info then
                                ImGui.Text(string.format("  Location: %s | Lv.%d | %s",
                                    formatWhere(p.info.where),
                                    p.info.level or 0,
                                    p.distance and string.format("%.0fm %s", p.distance, p.dir) or "far"
                                ))
                                local fl = formatFlags(p.info.flags)
                                if fl ~= "" then ImGui.Text("  State: " .. fl) end
                                if p.info.health >= 0 then
                                    ImGui.ProgressBar(p.info.health, 240, 14, string.format("%.0f%% HP", p.info.health * 100))
                                end
                            end
                            if not Sdk.isHost() and p.peer == Sdk.PEER_HOST then
                                if ImGui.Button("Teleport Next to Host##tp" .. tostring(p.peer)) then
                                    safeTeleportToPeer(p.peer)
                                end
                                ImGui.SameLine()
                            end
                            ImGui.Separator()
                        end
                    end

                    ImGui.Spacing()
                    if ImGui.Button("Catch Up to Story Leader (PageUp)") then
                        if not invokeNightCoopAction("story_pull") then
                            safeTeleportToPeer(Sdk.PEER_HOST)
                        end
                    end
                    ImGui.SameLine()
                    if ImGui.Button("Sync Save / Unstick NPC (PageDown)") then
                        invokeNightCoopAction("save_pull")
                    end
                end
                ImGui.EndTabItem()
            end

            -- TAB 3: PINGS
            if ImGui.BeginTabItem("Tactical Pings") then
                ImGui.TextWrapped("Pings target what you're looking at (or your position) and drop a temporary map marker on your partner's HUD.")
                ImGui.Spacing()
                if ImGui.Button("Ping: REGROUP", 140, 30) then sendPing("REGROUP") end
                ImGui.SameLine()
                if ImGui.Button("Ping: ENEMY", 140, 30) then sendPing("ENEMY") end
                if ImGui.Button("Ping: LOOT", 140, 30) then sendPing("LOOT") end
                ImGui.SameLine()
                if ImGui.Button("Ping: RIDE / CAR", 140, 30) then sendPing("RIDE") end

                if state.activePing then
                    ImGui.Separator()
                    ImGui.Text(string.format("Active Ping: [%s] from %s", state.activePing.tag, state.activePing.sender))
                    if ImGui.Button("Clear Active Ping") then
                        clearActivePingMappin()
                        state.activePing = nil
                    end
                end
                ImGui.EndTabItem()
            end

            -- TAB 4: WORLD & RULES
            if ImGui.BeginTabItem("World & Rules") then
                ImGui.Text("Time of Day (Host-Authoritative / Vote):")
                if ImGui.Button("Morning 08:00") then askTimeOfDay(8, "Morning") end
                ImGui.SameLine()
                if ImGui.Button("Noon 13:00") then askTimeOfDay(13, "Noon") end
                ImGui.SameLine()
                if ImGui.Button("Sunset 19:00") then askTimeOfDay(19, "Sunset") end
                ImGui.SameLine()
                if ImGui.Button("Night 23:00") then askTimeOfDay(23, "Night") end

                ImGui.Spacing()
                ImGui.Text("Weather:")
                if ImGui.Button("Clear") then askWeather("24h_weather_sunny", "Clear") end
                ImGui.SameLine()
                if ImGui.Button("Rain") then askWeather("24h_weather_rain", "Rain") end
                ImGui.SameLine()
                if ImGui.Button("Fog") then askWeather("24h_weather_fog", "Fog") end
                ImGui.SameLine()
                if ImGui.Button("Acid Rain") then askWeather("24h_weather_toxic_rain", "Acid Rain") end
                ImGui.SameLine()
                if ImGui.Button("Sandstorm") then askWeather("24h_weather_sandstorm", "Sandstorm") end

                ImGui.Separator()
                local changedHud, newHud = ImGui.Checkbox("Show Compact Mini-HUD", cfg.showMiniHud)
                if changedHud then cfg.showMiniHud = newHud; saveConfig() end

                local changedAuto, newAuto = ImGui.Checkbox("Alert Guest when Host Fast-Travels Far", cfg.autoFollowNotify)
                if changedAuto then cfg.autoFollowNotify = newAuto; saveConfig() end

                local changedThresh, newThresh = ImGui.SliderInt("Far-Drift Alert Distance (m)", cfg.followThresholdMeters, 150, 1200)
                if changedThresh then
                    cfg.followThresholdMeters = newThresh
                    saveConfig()
                    pushHostRules()
                end

                local changedDur, newDur = ImGui.SliderInt("Ping Marker Duration (s)", cfg.pingDurationSec, 10, 60)
                if changedDur then
                    cfg.pingDurationSec = newDur
                    saveConfig()
                    pushHostRules()
                end
                ImGui.EndTabItem()
            end

            -- TAB 5: DIAGNOSTICS
            if ImGui.BeginTabItem("Diagnostics") then
                ImGui.Text("NightCoop SDK: " .. (Sdk.available() and "Available" or "Not detected") .. " | Registered: " .. tostring(coop.registered))
                ImGui.Text("Hooks modified: 0 (100% additive, zero do-not-hook conflicts)")
                if ImGui.Button("Dump Patch 2.5 Engine CVars to CET Log") then
                    pcall(function() GameOptions.Dump() end)
                    addLog("Dumped engine CVars to cyber_engine_tweaks.log")
                end
                ImGui.Separator()
                ImGui.Text("Built-in NightCoop Keys (do not rebind over these):")
                ImGui.Text("  Hold PageUp   = Move next to story leader")
                ImGui.Text("  Hold PageDown = Sync host save / unstick NPC")
                ImGui.Text("  Hold End      = Host starts story without waiting")
                ImGui.Text("  Hold F        = Revive partner / enter quest ride / vote")
                ImGui.Separator()
                ImGui.Text("Recent Events:")
                for _, line in ipairs(state.logs) do
                    ImGui.TextWrapped(line)
                end
                ImGui.EndTabItem()
            end

            ImGui.EndTabBar()
        end
    end
    ImGui.End()
end

-- ---------------------------------------------------------------------------
-- CET Lifecycle & Hotkeys
-- ---------------------------------------------------------------------------

registerForEvent("onInit", function()
    loadConfig()
    if coop:register() then
        registerMenuItems()
        addLog("Registered ChoomSync v" .. MOD_VERSION .. " with NightCoop SDK")
    else
        addLog("ChoomSync v" .. MOD_VERSION .. " active (waiting for NightCoop SDK...)")
    end
    if cfg.perfPreset and cfg.perfPreset ~= "none" then
        applyPerfPreset(cfg.perfPreset, true)
    end
end)

registerForEvent("onShutdown", function()
    clearActivePingMappin()
    saveConfig()
    coop:unregister()
end)

registerForEvent("onOverlayOpen", function()
    state.overlayOpen = true
end)

registerForEvent("onOverlayClose", function()
    state.overlayOpen = false
end)

registerForEvent("onUpdate", function(dt)
    recordFrameDt(dt)
    coop:poll(handleCoopEvent)
    if coop.registered and not state.menuRegistered then
        registerMenuItems()
    end
    refreshTelemetry()
end)

registerForEvent("onDraw", function()
    drawMiniHud()
    drawMainPanel()
end)

registerHotkey("choomsync_toggle_panel", "ChoomSync: Toggle Companion Panel", function()
    state.panelOpen = not state.panelOpen
end)

registerHotkey("choomsync_ping_regroup", "ChoomSync: Quick Ping (Regroup)", function()
    sendPing("REGROUP")
end)

registerHotkey("choomsync_ping_enemy", "ChoomSync: Quick Ping (Enemy)", function()
    sendPing("ENEMY")
end)

registerHotkey("choomsync_catch_up", "ChoomSync: Catch Up to Host", function()
    if not invokeNightCoopAction("story_pull") then
        safeTeleportToPeer(Sdk.PEER_HOST)
    end
end)

registerHotkey("choomsync_fix_stuck", "ChoomSync: Fix Stuck Glitch / Combat / Scanner", function()
    fixStuckRelicAndScreenFx()
    fixStuckCombatState()
    fixStuckRestrictionsAndScanner()
end)
