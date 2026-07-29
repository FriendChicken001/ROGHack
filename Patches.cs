using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppMoonClient;
using Il2CppMoonCommonLib;

namespace ROCSpeedHack
{
    public static class Patches
    {

        [HarmonyPatch(typeof(Il2CppMoonClient.MEntity))]
        [HarmonyPatch("RunSpeed")]
        [HarmonyPatch(new System.Type[] { typeof(Il2CppMoonClient.MEntity) })]
        [HarmonyPatch(MethodType.Getter)]
        public static class RunPatch
        {
            [HarmonyPostfix]
            static void Postfix(ref float __result, ref Il2CppMoonClient.MEntity __instance)
            {
                if (__instance.IsPlayer && __instance.IsLoaded && MainMod.runSpeedEnabled)
                {
                    __result = MainMod.runSpeed;
                }
            }
        }

        [HarmonyPatch(typeof(MMonsterHUDComponent))]
        [HarmonyPatch(nameof(MMonsterHUDComponent.IsTarget))]
        [HarmonyPatch(MethodType.Normal)]
        public static class HUDPatch
        {
            [HarmonyPostfix]
            static void Postfix(ref bool __result, MMonsterHUDComponent __instance)
            {
                if (MainMod.showAllHealthBars)
                {
                    __result = true;
                }

                // Called every frame per actually-visible monster HUD, regardless of the toggle
                // above - a much more complete live monster list than MEntityMgr.GetMEntities().
                Il2CppMoonClient.MEntity entity = __instance?.Entity;
                if (entity != null)
                {
                    MainMod.liveMonsterSightings[entity.UID] = (entity, UnityEngine.Time.time);
                }
            }
        }



        [HarmonyPatch(typeof(Il2CppMoonClient.MCameraConfig))]
        [HarmonyPatch("MaxCameraDis")]
        [HarmonyPatch(new System.Type[] { typeof(Il2CppMoonClient.MCameraConfig) })]
        [HarmonyPatch(MethodType.Getter)]

        public static class FOVPatch
        {
            [HarmonyPostfix]
            static void Postfix(ref float __result, ref Il2CppMoonClient.MCameraConfig __instance)
            {
                if (MainMod.unlockCameraDistance)
                {
                    __result = __result * 3;
                }
            }
        }

        // Debug-only: logs every resource-location string hashed via MResLoader.Hash so we can
        // discover the real path string used for a given Lua module (e.g. "PropMgr"), since the
        // BLOCK.blockinfo asset index is keyed by this exact hash and we don't know the source
        // path convention blind. Gated behind MainMod.logResourceHashes, off by default.
        [HarmonyPatch(typeof(Il2CppMoonClient.MResLoader))]
        [HarmonyPatch("Hash")]
        [HarmonyPatch(new System.Type[] { typeof(string), typeof(string) })]
        public static class ResLoaderHashPatch
        {
            [HarmonyPrefix]
            static void Prefix(string location, string ext)
            {
                if (MainMod.logResourceHashes && location != null &&
                    (location.Contains("PropMgr") || location.Contains("ModuleMgr") || location.Contains(".lua")))
                {
                    MainMod.logger.Msg($"[ResHash] MResLoader.Hash location='{location}' ext='{ext}'");
                }
            }
        }

        // Lowest-level hash primitives - catches the call regardless of which higher-level
        // wrapper (MResLoader.Hash, GetLocationHash, or a Lua-specific loader) is used.
        [HarmonyPatch(typeof(Il2CppMoonCommonLib.MCommonFunctions))]
        [HarmonyPatch("GetHash")]
        [HarmonyPatch(new System.Type[] { typeof(string) })]
        public static class GetHashPatch
        {
            [HarmonyPrefix]
            static void Prefix(string str)
            {
                if (MainMod.logResourceHashes && str != null &&
                    (str.Contains("PropMgr") || str.Contains("ModuleMgr") || str.Contains(".lua")))
                {
                    MainMod.logger.Msg($"[ResHash] GetHash str='{str}'");
                }
            }
        }

        [HarmonyPatch(typeof(Il2CppMoonCommonLib.MCommonFunctions))]
        [HarmonyPatch("GetLowerHash")]
        [HarmonyPatch(new System.Type[] { typeof(string) })]
        public static class GetLowerHashPatch
        {
            [HarmonyPrefix]
            static void Prefix(string str)
            {
                if (MainMod.logResourceHashes && str != null &&
                    (str.Contains("PropMgr") || str.Contains("ModuleMgr") || str.Contains(".lua")))
                {
                    MainMod.logger.Msg($"[ResHash] GetLowerHash str='{str}'");
                }
            }
        }

        // Debug-only: logs the full hierarchy path of every real UI button click, so we can
        // identify the actual "Auto" toggle button by having the user click it manually once
        // and reading the name back from the log, instead of guessing GameObject names blind.
        [HarmonyPatch(typeof(UnityEngine.UI.Button))]
        [HarmonyPatch(nameof(UnityEngine.UI.Button.OnPointerClick))]
        public static class ButtonClickLogPatch
        {
            [HarmonyPostfix]
            static void Postfix(UnityEngine.UI.Button __instance)
            {
                if (!MainMod.logButtonClicks || __instance == null) return;
                string path = __instance.name;
                UnityEngine.Transform t = __instance.transform;
                while (t.parent != null)
                {
                    t = t.parent;
                    path = t.name + "/" + path;
                }
                MainMod.logger.Msg($"[ButtonClick] '{__instance.name}' path={path}");
            }
        }

        // Our overlay is legacy IMGUI (OnGUI/GUILayout.Window), which predates uGUI/EventSystem
        // and isn't raycast-aware - clicks on it fall through to the game world underneath
        // (movement, item use, etc). Patching EventSystem.IsPointerOverGameObject() didn't work -
        // the game's own click handling doesn't consult it at all. The real entry point is
        // MKeyboard.Update(), which reads raw Input.GetMouseButtonDown/Up/Button every frame and
        // converts it into the game's internal "touch" event (MakeFakeTouch) - everything
        // downstream (movement, skill casting) reacts to that synthetic touch, not the raw input.
        // Skipping this method entirely while the mouse is over our window blocks click-through
        // at the earliest possible point, before any game system ever sees the click.
        [HarmonyPatch(typeof(MKeyboard))]
        [HarmonyPatch(nameof(MKeyboard.Update))]
        [HarmonyPatch(new System.Type[] { })]
        public static class BlockClickThroughPatch
        {
            [HarmonyPrefix]
            static bool Prefix()
            {
                return !MainMod.isMouseOverModUI;
            }
        }
    }
}
