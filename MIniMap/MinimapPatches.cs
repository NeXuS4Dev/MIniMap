using System;
using System.Reflection;
using BepInEx.Logging;
using GameNetcodeStuff;
using HarmonyLib;

namespace MIniMap
{
    /// <summary>
    /// Applies every Harmony patch individually instead of Harmony.PatchAll().
    ///
    /// PatchAll() aborts the ENTIRE patch set if a single game method is missing or
    /// renamed after a game update, leaving the mod half-dead with no useful log.
    /// Applying patches one-by-one means a future game update can only knock out the
    /// specific feature a method belongs to, and we log exactly what was skipped.
    /// </summary>
    internal static class MinimapPatches
    {
        public static void Apply(Harmony harmony, ManualLogSource logger)
        {
            int applied = 0;

            // Minimap overlay creation + hotkey handling.
            applied += TryPatch(harmony, logger,
                typeof(PlayerControllerB), "ConnectClientToPlayerObject",
                postfix: GetMethod(typeof(MinimapPatch), nameof(MinimapPatch.CreateMinimap)));
            applied += TryPatch(harmony, logger,
                typeof(PlayerControllerB), "Update",
                postfix: GetMethod(typeof(MinimapPatch), nameof(MinimapPatch.HandleHotkeys)));

            // Map camera behaviour (zoom, auto-rotate, icon correction, keep-alive).
            applied += TryPatch(harmony, logger,
                typeof(ManualCameraRenderer), "Update",
                postfix: GetMethod(typeof(ManualCameraRendererPatch), nameof(ManualCameraRendererPatch.MapCameraLogic)));

            // Replace the game's radar-target cycling with our filtered cycling.
            applied += TryPatch(harmony, logger,
                typeof(ManualCameraRenderer), "SwitchRadarTargetForward",
                prefix: GetMethod(typeof(MinimapPatch), nameof(MinimapPatch.BlockOriginalSwitch)));

            // v80/v81: updateMapTarget() became a coroutine and is only reachable through
            // the entry points below (plus SwitchRadarTargetForward above). Swapping the
            // radar target remotely/externally now goes through these two methods, so we
            // gate them instead of blocking updateMapTarget itself. Blocking the coroutine
            // directly makes Unity throw ArgumentNullException in StartCoroutine(null) -
            // that was the v81 breakage of the old prefix on updateMapTarget.
            applied += TryPatch(harmony, logger,
                typeof(ManualCameraRenderer), "SwitchRadarTargetAndSync",
                prefix: GetMethod(typeof(MinimapPatch), nameof(MinimapPatch.BlockSyncedTargetSwitch)));
            applied += TryPatch(harmony, logger,
                typeof(ManualCameraRenderer), "SwitchRadarTargetClientRpc",
                prefix: GetMethod(typeof(MinimapPatch), nameof(MinimapPatch.BlockSyncedTargetSwitch)));

            logger.LogInfo($"Minimap patches applied successfully: {applied}/6.");
        }

        private static HarmonyMethod GetMethod(Type type, string name)
        {
            MethodInfo method = AccessTools.Method(type, name);
            return method == null ? null : new HarmonyMethod(method);
        }

        private static int TryPatch(Harmony harmony, ManualLogSource logger,
            Type originalType, string originalName,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            MethodBase original = AccessTools.Method(originalType, originalName);
            if (original == null)
            {
                logger.LogWarning($"[Minimap] Skipping patch: '{originalType.Name}.{originalName}' " +
                                  "was not found in this game version. Related feature may be inactive.");
                return 0;
            }

            try
            {
                harmony.Patch(original, prefix, postfix);
                return 1;
            }
            catch (Exception e)
            {
                logger.LogError($"[Minimap] Failed to patch '{originalType.Name}.{originalName}': {e.Message}");
                return 0;
            }
        }
    }
}
