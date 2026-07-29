using MelonLoader;
using HarmonyLib;
using UnityEngine;
using Il2CppMoonClient;
using Il2CppROGameLibs;
using Vector3 = UnityEngine.Vector3;
using System.IO;
using System.Text;
using System.Reflection;

namespace ROCSpeedHack
{
    public class MainMod : MelonMod
    {
        public static float runSpeed = 9f;
        public static bool unlockCameraDistance = false;
        public static bool showAllHealthBars = false;
        public static MelonLogger.Instance logger;
        public static bool runSpeedEnabled = true;
        public static bool showPlayerEsp = false;
        public static bool logResourceHashes = false;
        public static bool logButtonClicks = false;
        private static bool showRunSpeed = true;
        private string lastSceneName = string.Empty;
        private static Rect windowRect = new Rect(20, 44, 260, 0);
        private static GUIStyle espStyle;
        private static string runSpeedInput = runSpeed.ToString("0.0");
        private static bool showMonsterDropdown = false;
        private static System.Collections.Generic.List<string> mapMonsterNames = new System.Collections.Generic.List<string>();
        private static int mapMonsterListSceneId = -1;
        private const int FlyWingsItemId = 2031003;
        // Populated live by HUDPatch in Patches.cs (a Harmony postfix on
        // MMonsterHUDComponent.IsTarget, which the game calls every frame per actually-visible
        // monster HUD) - a far more complete/reliable monster list than MEntityMgr.GetMEntities(),
        // which only tracks a small combat-relevance-filtered subset. Keyed by entity UID with a
        // last-seen timestamp so stale (no longer visible) entries can be filtered out by callers.
        public static readonly System.Collections.Generic.Dictionary<ulong, (MEntity entity, float lastSeen)> liveMonsterSightings
            = new System.Collections.Generic.Dictionary<ulong, (MEntity, float)>();
        private const float MonsterSightingFreshness = 1.0f;

        private static System.Collections.Generic.List<MEntity> GetLiveMonsters()
        {
            var result = new System.Collections.Generic.List<MEntity>();
            float now = Time.time;
            foreach (var kv in liveMonsterSightings)
            {
                if (now - kv.Value.lastSeen <= MonsterSightingFreshness && kv.Value.entity != null)
                {
                    result.Add(kv.Value.entity);
                }
            }
            return result;
        }

        // Real per-map monster list, sourced from MSceneMgr:GetMonsIdsBySceneId(sceneId) -
        // the same API the game's own Map/MapInfor screen uses to populate its monster list
        // panel (MapWindow.lua's showCurrentSceneMonsterInfoPanel/setMonsterInfo). Unlike
        // liveMonsterSightings (a "seen" list built from visible HUDs), this reflects every
        // monster species that can spawn on the current map, whether or not one is nearby
        // right now. DoLuaString has no return value, so results are written to a temp file
        // and read back immediately after the call returns (same pattern as DebugAutoBuy).
        private static void RefreshMapMonsterList()
        {
            const string debugFile = "ROGHack_mapmonsters.txt";
            string script =
                "local f = io.open('" + debugFile + "', 'w')\n" +
                "local sceneId = MScene.SceneID\n" +
                "local ids = MSceneMgr:GetMonsIdsBySceneId(sceneId)\n" +
                "local entityTable = TableUtil.GetEntityTable()\n" +
                "f:write(tostring(sceneId) .. '\\n')\n" +
                "for i = 0, ids.Count - 1 do\n" +
                "  local row = entityTable.GetRowById(ids[i])\n" +
                "  if row ~= nil then\n" +
                "    f:write(tostring(row.Name) .. '\\n')\n" +
                "  end\n" +
                "end\n" +
                "f:close()\n";

            File.WriteAllText(debugFile, "");
            MLuaClientHelper.DoLuaString(script);

            if (!File.Exists(debugFile))
            {
                logger.Msg("[FlywingLock] Map monster list probe failed: debug file not created.");
                return;
            }

            string[] lines = File.ReadAllLines(debugFile);
            mapMonsterNames = new System.Collections.Generic.List<string>();
            if (lines.Length > 0 && int.TryParse(lines[0], out int sceneId))
            {
                mapMonsterListSceneId = sceneId;
                for (int i = 1; i < lines.Length; i++)
                {
                    if (!string.IsNullOrEmpty(lines[i]))
                    {
                        mapMonsterNames.Add(lines[i]);
                    }
                }
            }
            mapMonsterNames.Sort();
        }

        private static bool flywingLockEnabled = false;
        private static string flywingLockTargetName = "";
        private static string flywingLockStatus = "";
        private static float flywingLockNextAttemptTime = 0f;
        private const float FlywingLockAttemptInterval = 1.5f;
        // Auto-Buy Favorited Trade Cards: buys every card the player has "Follow"-starred in
        // the Trade tab (Sweater/Trade panel), either on demand or automatically around the
        // market's known refresh times (12:05/16:05/20:05). Real API found via decompiling
        // ModuleMgr/TradeMgr.lua and UI/Trade/TradeHandler.lua: the favorited/"pre-buy" list is
        // ModuleData.TradeData.GetPreBuyList() (items with info.isFollow == true), and the real
        // buy call is TradeMgr.SendTradeBuyItemReq(notice, id, count, force, totalCost) - a plain
        // module function (dot syntax, no self), matching what TradeHandler.lua itself calls.
        private static bool tradeFavAutoBuyEnabled = false;
        private static string tradeFavBuyQty = "1";
        private static string tradeFavStatus = "";
        private static readonly System.TimeSpan[] TradeRefreshTimes = new System.TimeSpan[]
        {
            new System.TimeSpan(12, 5, 0),
            new System.TimeSpan(16, 5, 0),
            new System.TimeSpan(20, 5, 0),
        };
        // Stock at each refresh is limited and shared with every other player watching the same
        // slot, so a single request right at 12:05:00 easily loses a race to someone else's
        // client. Instead, start burst-firing a few seconds BEFORE the scheduled time (covers
        // clock skew/latency - requests that land before the server actually refreshes just fail
        // harmlessly) and keep firing for a while after it (server-authoritative either way).
        private const float TradeFavBurstLeadSeconds = 5f;
        private const float TradeFavBurstTrailSeconds = 20f;
        private const float TradeFavBurstIntervalSeconds = 1f;
        private static System.DateTime tradeFavLastFiredSlot = System.DateTime.MinValue;
        private static float tradeFavBurstEndTime = -1f;
        private static float tradeFavNextBurstAttemptTime = 0f;

        private void CheckTradeFavoriteAutoBuySchedule()
        {
            if (!tradeFavAutoBuyEnabled)
            {
                return;
            }

            if (Time.time < tradeFavBurstEndTime)
            {
                if (Time.time >= tradeFavNextBurstAttemptTime)
                {
                    tradeFavNextBurstAttemptTime = Time.time + TradeFavBurstIntervalSeconds;
                    BuyFavoritedTradeCards();
                }
                return;
            }

            System.DateTime now = System.DateTime.Now;
            foreach (System.TimeSpan slot in TradeRefreshTimes)
            {
                System.DateTime slotToday = now.Date + slot;
                double secondsUntilSlot = (slotToday - now).TotalSeconds;

                if (secondsUntilSlot <= TradeFavBurstLeadSeconds && secondsUntilSlot > -TradeFavBurstTrailSeconds)
                {
                    if (slotToday != tradeFavLastFiredSlot)
                    {
                        tradeFavLastFiredSlot = slotToday;
                        tradeFavBurstEndTime = Time.time + (float)secondsUntilSlot + TradeFavBurstTrailSeconds;
                        tradeFavNextBurstAttemptTime = 0f;
                    }
                    break;
                }
            }
        }

        private void BuyFavoritedTradeCards()
        {
            int qty = int.TryParse(tradeFavBuyQty, out int q) && q > 0 ? q : 1;
            const string debugFile = "ROGHack_tradebuy.txt";
            // ModuleData.TradeData / MgrMgr:GetMgr('TradeMgr') are global modules, independent of
            // the Sweater/Trade UI panel - confirmed live that GetPreBuyList() still returns the
            // player's followed items and SendTradeBuyItemReq still works after the Trade panel
            // has been fully closed (UIMgr:GetUI('Sweater') goes nil on close, but this data
            // survives that). So no UI panel needs to be open at all for this to work.
            string script =
                "local f = io.open('" + debugFile + "', 'w')\n" +
                "local tradeData = ModuleData.TradeData\n" +
                "local tradeMgr = MgrMgr:GetMgr('TradeMgr')\n" +
                "local list = tradeData.GetPreBuyList()\n" +
                "local n = 0\n" +
                "for i = 1, #list do\n" +
                "  local id = list[i].id\n" +
                "  local info = tradeData.GetTradeInfo(id)\n" +
                "  if info and info.isFollow then\n" +
                "    local price = math.floor((info.curPrice or 0) + 0.5)\n" +
                "    tradeMgr.SendTradeBuyItemReq(info.isNotice or false, id, " + qty + ", false, price)\n" +
                "    f:write('requested id=' .. tostring(id) .. ' qty=" + qty + " price=' .. tostring(price) .. '\\n')\n" +
                "    n = n + 1\n" +
                "  end\n" +
                "end\n" +
                "f:write('total requested=' .. tostring(n) .. '\\n')\n" +
                "f:close()\n";

            File.WriteAllText(debugFile, "");
            MLuaClientHelper.DoLuaString(script);

            if (File.Exists(debugFile))
            {
                string[] lines = File.ReadAllLines(debugFile);
                tradeFavStatus = lines.Length > 0 ? lines[lines.Length - 1] : "";
                foreach (string line in lines)
                {
                    logger.Msg("[TradeFavBuy] " + line);
                }
            }
        }

        private const string ConfigFileName = "ROGHack_config.txt";

        public override void OnInitializeMelon()
        {
            base.OnInitializeMelon();
            MainMod.logger = LoggerInstance;
            foreach (MethodBase based in HarmonyInstance.GetPatchedMethods())
            {
                logger.Msg($"PATCHED METHOD {based.Name} {based.FullDescription()}");
            }
            LoadConfig();
        }

        private void SaveConfig()
        {
            var lines = new System.Collections.Generic.List<string>
            {
                $"runSpeed={runSpeed}",
                $"runSpeedEnabled={runSpeedEnabled}",
                $"unlockCameraDistance={unlockCameraDistance}",
                $"showAllHealthBars={showAllHealthBars}",
                $"showPlayerEsp={showPlayerEsp}",
            };
            File.WriteAllLines(ConfigFileName, lines);
            logger.Msg("Config saved to " + ConfigFileName);
        }

        private void LoadConfig()
        {
            if (!File.Exists(ConfigFileName))
            {
                return;
            }

            foreach (string line in File.ReadAllLines(ConfigFileName))
            {
                string[] parts = line.Split(new[] { '=' }, 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                string key = parts[0].Trim();
                string value = parts[1].Trim();

                switch (key)
                {
                    case "runSpeed":
                        if (float.TryParse(value, out float rs)) runSpeed = rs;
                        break;
                    case "runSpeedEnabled":
                        if (bool.TryParse(value, out bool rse)) runSpeedEnabled = rse;
                        break;
                    case "unlockCameraDistance":
                        if (bool.TryParse(value, out bool ucd)) unlockCameraDistance = ucd;
                        break;
                    case "showAllHealthBars":
                        if (bool.TryParse(value, out bool sahb)) showAllHealthBars = sahb;
                        break;
                    case "showPlayerEsp":
                        if (bool.TryParse(value, out bool spe)) showPlayerEsp = spe;
                        break;
                }
            }

            logger.Msg("Config loaded from " + ConfigFileName);
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            base.OnSceneWasLoaded(buildIndex, sceneName);
            if (lastSceneName == "GameEntry")
            {
                logger.Msg("Running hooks.lua");
                runLuaFile("hooks.lua", Encoding.UTF8.GetString(Properties.Resources.hooks));
            }
            lastSceneName = sceneName;
        }

        public override void OnUpdate()
        {
            base.OnUpdate();

            if (Input.GetKeyDown(KeyCode.Delete))
            {
                showRunSpeed = !showRunSpeed;
            }

            if (flywingLockEnabled)
            {
                UpdateFlywingLock();
            }

            CheckTradeFavoriteAutoBuySchedule();

            /*
            if (Input.GetKeyDown(KeyCode.Z))
            {
                autoSkipCutScenes = !autoSkipCutScenes;
                logger.Msg($"Cutscene autoskip set to {autoSkipCutScenes}");
            }
            if (autoSkipCutScenes)
            {
                if (MCutSceneMgr._instance.IsPlaying)
                {
                    MCutSceneMgr._instance.Skip();
                }
            }

            */
        }





        private void runLuaFile(string filename, string defaultContent)
        {
            if (File.Exists(filename))
            {
                MLuaClientHelper.DoLuaString(File.ReadAllText(filename));
            }
            else
            {
                File.WriteAllText(filename, defaultContent);
                runLuaFile(filename, defaultContent);
            }
        }

        // Read by the EventSystem.IsPointerOverGameObject Harmony patch (Patches.cs) so clicks on
        // this IMGUI overlay don't fall through to the game world underneath - IMGUI predates
        // uGUI/EventSystem and isn't raycast-aware on its own.
        public static bool isMouseOverModUI = false;

        public override void OnGUI()
        {
            if (showPlayerEsp)
            {
                DrawEsp();
            }

            // Always-visible toggle button (in addition to the Delete key) so the overlay can be
            // reopened after being hidden without needing to remember/press the keybind.
            Rect toggleButtonRect = new Rect(10, 10, 90, 24);
            bool mouseOverToggle = toggleButtonRect.Contains(Event.current.mousePosition);
            if (GUI.Button(toggleButtonRect, showRunSpeed ? "Hide ROGHack" : "Show ROGHack"))
            {
                showRunSpeed = !showRunSpeed;
            }

            if (!showRunSpeed)
            {
                isMouseOverModUI = mouseOverToggle;
                return;
            }

            isMouseOverModUI = windowRect.Contains(Event.current.mousePosition) || mouseOverToggle;
            windowRect = GUILayout.Window(20260729, windowRect, (System.Action<int>)DrawWindow, "ROGHack (Del to hide)");
        }

        private void DrawWindow(int windowId)
        {
            GUILayout.Label($"Run Speed: {runSpeed:0.0}");
            GUILayout.BeginHorizontal();
            runSpeed = GUILayout.HorizontalSlider(runSpeed, 0f, 30f);
            GUILayout.Label(runSpeed.ToString("0.0"), GUILayout.Width(35));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            runSpeedInput = GUILayout.TextField(runSpeedInput, GUILayout.Width(60));
            if (GUILayout.Button("Set", GUILayout.Width(40)))
            {
                if (float.TryParse(runSpeedInput, out float parsedRunSpeed))
                {
                    runSpeed = parsedRunSpeed;
                }
            }
            GUILayout.EndHorizontal();
            runSpeedEnabled = GUILayout.Toggle(runSpeedEnabled, "Run Speed Enabled");

            GUILayout.Space(8);
            unlockCameraDistance = GUILayout.Toggle(unlockCameraDistance, "Unlock Camera Distance");
            showAllHealthBars = GUILayout.Toggle(showAllHealthBars, "Show All Health Bars");
            showPlayerEsp = GUILayout.Toggle(showPlayerEsp, "Show Player Names (ESP)");

            GUILayout.Space(8);
            if (GUILayout.Button("Save Settings"))
            {
                SaveConfig();
            }

            GUILayout.Space(8);
            if (GUILayout.Button("Run Lua (inject.lua)"))
            {
                runLuaFile("inject.lua", Encoding.UTF8.GetString(Properties.Resources.inject));
            }

            GUILayout.Space(8);
            logResourceHashes = GUILayout.Toggle(logResourceHashes, "Log Resource Hashes (PropMgr search)");
            logButtonClicks = GUILayout.Toggle(logButtonClicks, "Log Button Clicks (find UI names)");

            GUILayout.Space(8);
            GUILayout.Label("Flywing Lock (auto-spam Fly Wings until target monster found)");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Target name:", GUILayout.Width(80));
            flywingLockTargetName = GUILayout.TextField(flywingLockTargetName);
            if (GUILayout.Button(showMonsterDropdown ? "▲" : "▼", GUILayout.Width(25)))
            {
                showMonsterDropdown = !showMonsterDropdown;
                if (showMonsterDropdown)
                {
                    RefreshMapMonsterList();
                }
            }
            GUILayout.EndHorizontal();
            if (showMonsterDropdown)
            {
                if (GUILayout.Button("Refresh (current map)"))
                {
                    RefreshMapMonsterList();
                }
                if (mapMonsterNames.Count == 0)
                {
                    GUILayout.Label("(no monsters found for this map)");
                }
                foreach (string name in mapMonsterNames)
                {
                    if (GUILayout.Button(name))
                    {
                        flywingLockTargetName = name;
                        showMonsterDropdown = false;
                    }
                }
            }
            GUILayout.BeginHorizontal();
            if (!flywingLockEnabled)
            {
                if (GUILayout.Button("Start Flywing Lock") && !string.IsNullOrEmpty(flywingLockTargetName))
                {
                    StopFightAuto();
                    flywingLockEnabled = true;
                    flywingLockNextAttemptTime = 0f;
                    flywingLockStatus = "Starting...";
                    logger.Msg($"[FlywingLock] Started, target='{flywingLockTargetName}'");
                }
            }
            else
            {
                if (GUILayout.Button("Stop Flywing Lock"))
                {
                    flywingLockEnabled = false;
                    flywingLockStatus = "Stopped by user.";
                    logger.Msg("[FlywingLock] Stopped by user.");
                }
            }
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(flywingLockStatus))
            {
                GUILayout.Label(flywingLockStatus);
            }

            GUILayout.Space(8);
            GUILayout.Label("Auto-Buy Favorited Trade Cards (Sweater/Trade 'Follow' list)");
            GUILayout.Label("Star (Follow) the cards you want in the Trade tab first - the Trade panel does not need to stay open.");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Buy Qty", GUILayout.Width(60));
            tradeFavBuyQty = GUILayout.TextField(tradeFavBuyQty);
            GUILayout.EndHorizontal();
            tradeFavAutoBuyEnabled = GUILayout.Toggle(tradeFavAutoBuyEnabled, "Auto-buy at refresh (12:05 / 16:05 / 20:05)");
            if (GUILayout.Button("Buy Favorites Now"))
            {
                BuyFavoritedTradeCards();
            }
            if (!string.IsNullOrEmpty(tradeFavStatus))
            {
                GUILayout.Label(tradeFavStatus);
            }

            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        private void DrawEsp()
        {
            if (espStyle == null)
            {
                espStyle = new GUIStyle();
                espStyle.fontSize = 14;
                espStyle.alignment = TextAnchor.MiddleCenter;
                espStyle.normal.textColor = Color.cyan;
            }

            Camera cam = Camera.main;
            MEntityMgr entityMgr = MEntityMgr.singleton;
            if (cam == null || entityMgr == null)
            {
                return;
            }

            foreach (MEntity entity in entityMgr.GetMEntities())
            {
                if (entity == null || !entity.IsLoaded || entity.IsDead || !entity.IsPlayer)
                {
                    continue;
                }

                Vector3 headPos = entity.Position + Vector3.up * entity.Height;
                Vector3 screenPos = cam.WorldToScreenPoint(headPos);
                if (screenPos.z <= 0f)
                {
                    continue;
                }

                Rect labelRect = new Rect(screenPos.x - 60, Screen.height - screenPos.y - 10, 120, 20);
                GUI.Label(labelRect, entity.Name, espStyle);
            }
        }

        // The real API, found by decompiling ModuleMgr/FightAutoMgr.lua (extracted from the
        // runtime Unzips/BYTES_BLOCK cache, same technique used for PropMgr.lua earlier): calling
        // FightAutoMgr.StartFightAuto(luaType, targetId) directly sets MPlayerInfo.IsAutoBattle =
        // true, adds targetId via MPlayerInfo:AddMonsterTarget(targetId), and dispatches
        // EventConst.Names.UpdateAutoBattleState - exactly what the "Auto" button chain of UI
        // clicks was trying (and failing) to reproduce. `luaType` is unused inside the function
        // body, safe to pass nil. `targetId` is the monster's numeric species/template Id (NOT
        // its TID or world-instance UID) - confirmed identical to entity.AttrMonster
        // .EntityTableData.Id, so it's read directly off the entity here with no dependency on
        // the Fight-Auto settings panel being open at all. Called via dot syntax deliberately
        // (`FightAutoMgr.StartFightAuto`, not `:`) - colon would silently break it exactly like
        // every other ModuleMgr function this session that turned out to need dot calls.
        private void StartFightAutoForMonster(MEntity entity)
        {
            int monsterId;
            try
            {
                monsterId = entity.AttrMonster.EntityTableData.Id;
            }
            catch (System.Exception ex)
            {
                logger.Msg($"[FlywingLock] Could not read monster Id: {ex.Message}");
                return;
            }

            MLuaClientHelper.DoLuaString(
                $"MgrMgr:GetMgr('FightAutoMgr').StartFightAuto(nil, {monsterId})");
            logger.Msg($"[FlywingLock] FightAutoMgr.StartFightAuto(nil, {monsterId}) sent.");
        }

        // FightAutoMgr.lua's CloseFightAuto(luaType) just sets MPlayerInfo.IsAutoBattle = false
        // (luaType unused, same as StartFightAuto). Called when starting Flywing Lock so the
        // character isn't stuck auto-fighting whatever's nearby while trying to Fly Wing around
        // looking for the real target.
        private void StopFightAuto()
        {
            MLuaClientHelper.DoLuaString("MgrMgr:GetMgr('FightAutoMgr').CloseFightAuto(nil)");
            logger.Msg("[FlywingLock] FightAutoMgr.CloseFightAuto(nil) sent.");
        }

        // Auto-spams the real Fly Wings consumable (client-side, server-authoritative teleport -
        // this does not bypass anything, it automates the same click a player would make) until
        // a monster whose name contains flywingLockTargetName is loaded nearby, then stops.
        // Gated behind an explicit Start button, never auto-runs - same convention as autobuy.lua.
        //
        // Uses PropMgr.RequestUseItemByItemId(itemId, isAutoUse, config) via DOT syntax (not
        // colon!) - this is the real, confirmed-working call the game's own UI uses internally.
        // Colon syntax silently passes PropMgr itself as a hidden first arg and breaks it.
        //
        // Individual attempts can silently no-op (no error, no teleport) if a transient player
        // state exclusion is active server-side - the interval-based retry loop handles this,
        // no need for the caller to guarantee any single attempt succeeds.
        private void UpdateFlywingLock()
        {
            MEntityMgr entityMgr = MEntityMgr.singleton;
            MPlayer player = entityMgr?.PlayerEntity;
            if (entityMgr == null || player == null)
            {
                return;
            }

            var liveMonsters = GetLiveMonsters();
            foreach (MEntity entity in liveMonsters)
            {
                if (entity == null || entity.IsDead)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(flywingLockTargetName) &&
                    entity.Name != null &&
                    entity.Name.IndexOf(flywingLockTargetName, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    flywingLockEnabled = false;
                    flywingLockStatus = $"Found '{entity.Name}' - locked target, starting Auto.";
                    logger.Msg($"[FlywingLock] Found target '{entity.Name}', locking + starting auto.");
                    try
                    {
                        bool selectResult = MSkillTargetMgr.singleton.OnSelectTarget(entity);
                        logger.Msg($"[FlywingLock] OnSelectTarget returned {selectResult}, GetLastTarget={MSkillTargetMgr.singleton.GetLastTarget()?.Name}");
                    }
                    catch (System.Exception ex)
                    {
                        logger.Msg($"[FlywingLock] OnSelectTarget failed: {ex.Message}");
                    }

                    StartFightAutoForMonster(entity);
                    return;
                }
            }

            if (Time.time < flywingLockNextAttemptTime)
            {
                return;
            }
            flywingLockNextAttemptTime = Time.time + FlywingLockAttemptInterval;

            // Debug: dump every monster the client currently sees, so mismatches between
            // the target name and the real entity.Name values are visible in the log.
            var seenMonsters = new StringBuilder();
            int monsterCount = 0;
            foreach (MEntity e in liveMonsters)
            {
                if (e == null || e.IsDead)
                {
                    continue;
                }
                monsterCount++;
                seenMonsters.Append(e.Name).Append("; ");
            }
            logger.Msg($"[FlywingLock] scan: {monsterCount} monster(s) visible (live HUD registry): {seenMonsters}");

            flywingLockStatus = $"Searching for '{flywingLockTargetName}'... using Fly Wings";
            MLuaClientHelper.DoLuaString(
                $"MgrMgr:GetMgr('PropMgr').RequestUseItemByItemId({FlyWingsItemId}, false, nil)");
        }

    }
}
