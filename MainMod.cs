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
        private static string autoBuyItemId = "";
        private static string autoBuyMinCount = "";
        private static string autoBuyQty = "";
        private static bool showItemDropdown = false;
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
        private static readonly (string Name, int Id)[] KnownItems = new (string, int)[]
        {
            ("Red Potion", 2010001),
            ("Orange Potion", 2010002),
            ("White Potion", 2010003),
            ("Blue Potion", 2010005),
            ("Condensed Blue Potion", 2010015),
            ("Panacea", 2010014),
            ("Poison Bottle", 2020005),
            ("Blue Gemstone", 2020002),
        };
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
            string script =
                "local f = io.open('" + debugFile + "', 'w')\n" +
                "local ui = UIMgr:GetUI('Sweater')\n" +
                "local trade = ui and ui.handlers and ui.handlers.Trade\n" +
                "if trade == nil or not trade.isInited then\n" +
                "  f:write('ERROR: Trade panel not initialized - open Sweater/Trade at least once this session\\n')\n" +
                "else\n" +
                "  local list = trade.tradeData.GetPreBuyList()\n" +
                "  local n = 0\n" +
                "  for i = 1, #list do\n" +
                "    local id = list[i].id\n" +
                "    local info = trade.tradeData.GetTradeInfo(id)\n" +
                "    if info and info.isFollow then\n" +
                "      local price = math.floor((info.curPrice or 0) + 0.5)\n" +
                "      trade.tradeMgr.SendTradeBuyItemReq(info.isNotice or false, id, " + qty + ", false, price)\n" +
                "      f:write('requested id=' .. tostring(id) .. ' qty=" + qty + " price=' .. tostring(price) .. '\\n')\n" +
                "      n = n + 1\n" +
                "    end\n" +
                "  end\n" +
                "  f:write('total requested=' .. tostring(n) .. '\\n')\n" +
                "end\n" +
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
            if (GUILayout.Button("Dump Item Table"))
            {
                runLuaFile("dumpitems.lua", Encoding.UTF8.GetString(Properties.Resources.dumpitems));
                logger.Msg("Dumped item table to ROGHack_items.txt");
            }

            GUILayout.Space(8);
            GUILayout.Label("Auto-Buy Shop Item");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Item ID", GUILayout.Width(60));
            autoBuyItemId = GUILayout.TextField(autoBuyItemId);
            if (GUILayout.Button(showItemDropdown ? "▲" : "▼", GUILayout.Width(25)))
            {
                showItemDropdown = !showItemDropdown;
            }
            GUILayout.EndHorizontal();
            if (showItemDropdown)
            {
                foreach (var item in KnownItems)
                {
                    if (GUILayout.Button($"{item.Name} ({item.Id})"))
                    {
                        autoBuyItemId = item.Id.ToString();
                        showItemDropdown = false;
                    }
                }
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Min Qty", GUILayout.Width(60));
            autoBuyMinCount = GUILayout.TextField(autoBuyMinCount);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Buy Qty", GUILayout.Width(60));
            autoBuyQty = GUILayout.TextField(autoBuyQty);
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Start Auto-Buy"))
            {
                StartAutoBuy(autoBuyItemId, autoBuyMinCount, autoBuyQty);
            }
            if (GUILayout.Button("Stop Auto-Buy"))
            {
                StartAutoBuy(autoBuyItemId, "0", "0");
            }
            if (GUILayout.Button("Debug AutoBuy"))
            {
                DebugAutoBuy();
            }
            logResourceHashes = GUILayout.Toggle(logResourceHashes, "Log Resource Hashes (PropMgr search)");
            logButtonClicks = GUILayout.Toggle(logButtonClicks, "Log Button Clicks (find UI names)");

            GUILayout.Space(8);
            if (GUILayout.Button("Debug BagModel"))
            {
                DebugBagModel();
            }

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
            GUILayout.Label("Open the Trade tab at least once this session first, and star (Follow) the cards you want.");
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

        private void StartAutoBuy(string itemId, string minCount, string buyQty)
        {
            if (!int.TryParse(itemId, out int parsedItemId))
            {
                logger.Msg($"Auto-Buy: invalid item id '{itemId}'");
                return;
            }

            int parsedMinCount = int.TryParse(minCount, out int mc) ? mc : 0;
            int parsedBuyQty = int.TryParse(buyQty, out int bq) ? bq : 0;

            // Bare "AutoBuyItem == nil" / "AutoBuyItem(...)" don't reliably see the global -
            // this game's Lua sandbox silently drops plain global writes. autobuy.lua defines
            // it via rawset(_G, ...), so check/invoke through rawget to match.
            if (!File.Exists("autobuy.lua"))
            {
                File.WriteAllText("autobuy.lua", Encoding.UTF8.GetString(Properties.Resources.autobuy));
            }
            string script = File.ReadAllText("autobuy.lua");
            File.WriteAllText("ROGHack_debug.txt", "");
            MLuaClientHelper.DoLuaString(
                $"if rawget(_G, 'AutoBuyItem') == nil then\n{script}\nend\n" +
                $"rawget(_G, 'AutoBuyItem')({parsedItemId}, {parsedMinCount}, {parsedBuyQty})");
            logger.Msg($"Auto-Buy: item {parsedItemId}, minQty {parsedMinCount}, buyQty {parsedBuyQty}");
            if (File.Exists("ROGHack_debug.txt"))
            {
                foreach (var line in File.ReadAllLines("ROGHack_debug.txt"))
                {
                    logger.Msg("[AutoBuyDebug] " + line);
                }
            }
        }

        // Diagnostic helper for the "AutoBuyItem nil" bug: writes results to a file
        // (since DoLuaString has no return value) and echoes them through the normal
        // MelonLoader log so they can be read/copied the same way as any other log line.
        private void DebugAutoBuy()
        {
            const string debugFile = "ROGHack_debug.txt";
            const string logHelper =
                "local function _dbglog(...) local f=io.open('" + debugFile + "','a') local p={} " +
                "for i=1,select('#',...) do p[i]=tostring(select(i,...)) end " +
                "f:write(table.concat(p,' ')..'\\n') f:close() end\n";

            File.WriteAllText(debugFile, "");

            // Confirmed by an earlier run: bare "X = value" global assignment is a no-op
            // for later reads, even within the same chunk. These tests narrow down why,
            // and what persistence mechanism (if any) actually survives.
            MLuaClientHelper.DoLuaString(logHelper + "local TestBaz = 999\n_dbglog('local inline', TestBaz)");

            MLuaClientHelper.DoLuaString(logHelper +
                "rawset(_G, 'TestBar', 456)\n_dbglog('rawset inline', rawget(_G, 'TestBar'))");
            MLuaClientHelper.DoLuaString(logHelper + "_dbglog('rawset cross-call', rawget(_G, 'TestBar'))");

            MLuaClientHelper.DoLuaString(logHelper +
                "TableUtil.__ROGHack_test = 777\n_dbglog('table field inline', TableUtil.__ROGHack_test)");
            MLuaClientHelper.DoLuaString(logHelper + "_dbglog('table field cross-call', TableUtil.__ROGHack_test)");

            if (File.Exists(debugFile))
            {
                foreach (var line in File.ReadAllLines(debugFile))
                {
                    logger.Msg("[AutoBuyDebug] " + line);
                }
            }
            else
            {
                logger.Msg("[AutoBuyDebug] debug file was never created - io.open itself may be failing");
            }
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

        // Data.BagModel / item-use are Lua-only constructs with no C# footprint at all (confirmed
        // via reflection - no BagModel/MAttrItem type exists in Il2CppMoonClient.dll), so this
        // has to be explored empirically in Lua rather than guessed from C# signatures.
        private void DebugBagModel()
        {
            const string debugFile = "ROGHack_debug.txt";
            File.WriteAllText(debugFile, "");

            string script = @"
local f = io.open('ROGHack_debug.txt', 'a')
local function log(...)
   local p = {}
   for i=1,select('#',...) do p[i]=tostring(select(i,...)) end
   f:write(table.concat(p, ' ') .. '\n')
end

log('type(Data.BagModel)=', type(Data.BagModel))

local function dumpFields(obj, prefix, depth)
   if depth > 2 then return end
   local ok, err = pcall(function()
      for k, v in pairs(obj) do
         log(prefix .. tostring(k) .. ' = ' .. tostring(v))
         if type(v) == 'table' then
            dumpFields(v, prefix .. tostring(k) .. '.', depth + 1)
         end
      end
   end)
   if not ok then log(prefix .. '<pairs failed: ' .. tostring(err) .. '>') end
end

dumpFields(Data.BagModel, '', 0)

-- probe common candidate methods for enumerating/using items
local candidates = {
   {'GetAllItems'}, {'GetItems'}, {'GetBagItems'}, {'GetBagItemList'},
   {'RequestUseItem'}, {'UseItem'}, {'ReqUseItem'},
}
for _, c in ipairs(candidates) do
   local name = c[1]
   log('Data.BagModel.' .. name .. ' = ', tostring(Data.BagModel[name]))
end

local mgrNames = {'ItemMgr', 'BagMgr', 'PropMgr', 'BackpackMgr'}
for _, name in ipairs(mgrNames) do
   local ok, mgr = pcall(function() return MgrMgr:GetMgr(name) end)
   log('MgrMgr:GetMgr(' .. name .. ') ok=', tostring(ok), ' value=', tostring(mgr))
   if ok and type(mgr) == 'table' then
      log('--- fields of ' .. name .. ' ---')
      dumpFields(mgr, name .. '.', 0)
      log('--- likely use-item candidates in ' .. name .. ' ---')
      local ok2, err2 = pcall(function()
         for k, v in pairs(mgr) do
            local lk = string.lower(tostring(k))
            if string.find(lk, 'use') or string.find(lk, 'consume') then
               log(name .. '.' .. tostring(k) .. ' = ' .. tostring(v))
            end
         end
      end)
      if not ok2 then log(name .. ' <candidate scan failed: ' .. tostring(err2) .. '>') end
   end
end

f:close()
";
            MLuaClientHelper.DoLuaString(script);

            if (File.Exists(debugFile))
            {
                foreach (var line in File.ReadAllLines(debugFile))
                {
                    logger.Msg("[BagDebug] " + line);
                }
            }
        }

    }
}
