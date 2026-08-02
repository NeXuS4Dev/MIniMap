using BepInEx;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.UI;

namespace MIniMap
{
    /// <summary>
    /// HUD overlay creation, hotkeys and radar target switching.
    /// Everything here is local-only: the mod never sends RPCs and never
    /// registers network prefabs, so it is fully client-side and safe to
    /// use while connected to unmodded (or vanilla) servers.
    /// </summary>
    internal static class MinimapPatch
    {
        private static GameObject minimapObject;
        private static RawImage minimapImage;

        // Уже выбирали цель за текущую жизнь (авто-центрирование на себе или F3)?
        // Сбрасывается при смерти, чтобы после возрождения карта снова встала на игрока.
        private static bool selfCenteredThisLife;

        // Postfix on PlayerControllerB.ConnectClientToPlayerObject
        internal static void CreateMinimap()
        {
            if (minimapObject != null)
                return;

            if (HUDManager.Instance == null || HUDManager.Instance.playerScreenTexture == null)
            {
                MinimalMinimap.PluginLogger?.LogWarning(
                    "[Minimap] HUD not ready yet; overlay will be created on the next frame instead.");
                return;
            }

            minimapObject = new GameObject("MIniMap_UI");
            minimapImage = minimapObject.AddComponent<RawImage>();

            RectTransform rt = minimapImage.rectTransform;
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(MinimalMinimap.Data.Size, MinimalMinimap.Data.Size);
            rt.anchoredPosition = new Vector2(MinimalMinimap.Data.XOffset, MinimalMinimap.Data.YOffset);

            ApplyRadarTexture();

            minimapObject.transform.SetParent(HUDManager.Instance.playerScreenTexture.transform, false);

            bool isEnabled = MinimalMinimap.Instance.ConfigEnabled.Value;
            minimapObject.SetActive(isEnabled);

            MinimalMinimap.PluginLogger?.LogInfo(
                $"[Minimap] Overlay created, visible={isEnabled}. Press {MinimalMinimap.Data.ToggleKey} to toggle.");
        }

        // The minimap simply renders the ship radar camera's RenderTexture.
        // Keep it bound to whatever targetTexture the camera has RIGHT NOW:
        // the game can swap/replace render targets at round transitions
        // (orbit info screen / landing / late join), which otherwise leaves
        // the minimap showing a stale frozen frame forever.
        private static void ApplyRadarTexture()
        {
            if (minimapImage == null || StartOfRound.Instance == null ||
                StartOfRound.Instance.mapScreen == null || StartOfRound.Instance.mapScreen.cam == null)
                return;

            Texture current = StartOfRound.Instance.mapScreen.cam.targetTexture;
            if (current != null && minimapImage.texture != current)
                minimapImage.texture = current;
        }

        // Postfix on PlayerControllerB.Update
        internal static void HandleHotkeys(PlayerControllerB __instance)
        {
            if (GameNetworkManager.Instance == null || !__instance.IsOwner ||
                __instance != GameNetworkManager.Instance.localPlayerController)
                return;

            // Если HUD успел пересоздаться без нас (или мы его пропустили) - создаём с задержкой.
            if (minimapObject == null)
            {
                CreateMinimap();
                if (minimapObject == null) return;
            }

            // Cheap: only re-assigns when the camera's target texture changed.
            ApplyRadarTexture();

            // F6 - отладочный дамп состояния радара в лог (для расследования багов)
            if (UnityInput.Current.GetKeyDown(MinimalMinimap.Data.DebugKey))
            {
                DumpRadarState();
            }

            if (!__instance.isPlayerControlled && !__instance.isPlayerDead) return;

            // F2 - вкл/выкл миникарту
            if (UnityInput.Current.GetKeyDown(MinimalMinimap.Data.ToggleKey))
            {
                bool newState = !MinimalMinimap.Instance.ConfigEnabled.Value;
                MinimalMinimap.Instance.ConfigEnabled.Value = newState;
                if (minimapObject != null) minimapObject.SetActive(newState);
                MinimalMinimap.PluginLogger?.LogInfo($"[Minimap] Minimap {(newState ? "ON" : "OFF")} (F2).");
            }

            if (!MinimalMinimap.Instance.ConfigEnabled.Value) return;

            // F3 - ручное переключение цели радара
            if (UnityInput.Current.GetKeyDown(MinimalMinimap.Data.SwitchKey))
            {
                SwitchTarget();
            }

            // Логика поведения при смерти и возрождении
            if (__instance.isPlayerDead)
            {
                selfCenteredThisLife = false;

                // Если умерли — выключаем заморозку, чтобы следить за живыми
                if (MinimalMinimap.Data.FreezeTarget)
                    MinimalMinimap.Data.FreezeTarget = false;

                if (__instance.spectatedPlayerScript != null)
                    SetMapTargetToPlayer(__instance.spectatedPlayerScript);
            }
            else
            {
                // Если возродились, а заморозка еще выключена — включаем обратно
                if (!MinimalMinimap.Data.FreezeTarget)
                    MinimalMinimap.Data.FreezeTarget = true;

                // Пока фиксация цели включена, центрируем радар на себе - но только
                // один раз за жизнь: ручное переключение по F3 потом не перебиваем.
                if (MinimalMinimap.Data.FreezeTarget && !selfCenteredThisLife)
                    selfCenteredThisLife = SetMapTargetToPlayer(__instance);
            }
        }

        // Prefix on ManualCameraRenderer.SwitchRadarTargetForward:
        // полностью заменяет игровое переключение цели нашим (без RPC - чисто локально).
        internal static bool BlockOriginalSwitch()
        {
            SwitchTarget();
            return false;
        }

        // Prefix on ManualCameraRenderer.SwitchRadarTargetAndSync and
        // ManualCameraRenderer.SwitchRadarTargetClientRpc:
        // пока цель заморожена, не даём игре/другим игрокам поменять наш радар-таргет.
        // Напрямую корутину updateMapTarget больше не блокируем (см. MinimapPatches) -
        // все разрешённые переключения проходят штатно через ванильную логику.
        internal static bool BlockSyncedTargetSwitch()
        {
            return !MinimalMinimap.Data.FreezeTarget;
        }

        // Вспомогательный метод для поиска игрока (используется при смерти/возрождении).
        // Возвращает true, если цель фактически установлена (уже стояла или только что переключили).
        private static bool SetMapTargetToPlayer(PlayerControllerB target)
        {
            var map = StartOfRound.Instance != null ? StartOfRound.Instance.mapScreen : null;
            if (map == null || target == null || map.radarTargets == null) return false;
            if (map.targetedPlayer == target) return true;

            for (int i = 0; i < map.radarTargets.Count; i++)
            {
                var t = map.radarTargets[i];
                if (t != null && t.transform != null)
                {
                    if (t.transform.GetComponent<PlayerControllerB>() == target)
                    {
                        map.targetTransformIndex = i;
                        map.targetedPlayer = target;
                        SyncRadarTargetName(map);
                        return true;
                    }
                }
            }

            return false;
        }

        private static void SwitchTarget()
        {
            var map = StartOfRound.Instance != null ? StartOfRound.Instance.mapScreen : null;
            if (map == null || map.radarTargets == null || map.radarTargets.Count == 0)
                return;

            int count = map.radarTargets.Count;
            int next = map.targetTransformIndex;
            if (next < 0 || next >= count) next = 0;

            for (int i = 0; i < count; i++)
            {
                next = (next + 1) % count;

                var t = map.radarTargets[next];
                if (t == null || t.transform == null) continue;

                PlayerControllerB player = t.transform.GetComponent<PlayerControllerB>();

                // Пропускаем радар-бустеры и прочие не-игровые цели
                if (player == null) continue;

                // 👇 ВАЖНО: фильтр живых/валидных
                if (!player.isPlayerControlled && !player.isPlayerDead)
                    continue;

                map.targetTransformIndex = next;
                map.targetedPlayer = player;
                SyncRadarTargetName(map);

                // Осознанный выбор цели - не перебивать его авто-центрированием на себе.
                selfCenteredThisLife = true;
                MinimalMinimap.PluginLogger?.LogInfo($"[Minimap] Radar target -> {map.radarTargets[next].name}");

                return;
            }
        }

        // v80/v81: имя цели на мониторе обновляется только внутри корутины
        // updateMapTarget, а мы переключаем цель напрямую - обновляем подпись сами.
        private static void SyncRadarTargetName(ManualCameraRenderer map)
        {
            if (StartOfRound.Instance == null || StartOfRound.Instance.mapScreenPlayerName == null)
                return;
            if (map.radarTargets == null || map.radarTargets.Count == 0)
                return;
            if (map.targetTransformIndex < 0 || map.targetTransformIndex >= map.radarTargets.Count)
                return;

            StartOfRound.Instance.mapScreenPlayerName.text =
                map.radarTargets[map.targetTransformIndex].name ?? "";
        }

        // F6 diagnostic: writes the full radar state to the BepInEx log so a
        // frozen/blank minimap can be diagnosed from the log file.
        // NOTE: private game fields must be read via reflection - Mono enforces
        // member accessibility at runtime (FieldAccessException) even though the
        // publicized reference assemblies make them look public at compile time.
        private static void DumpRadarState()
        {
            var log = MinimalMinimap.PluginLogger;
            if (log == null) return;

            try
            {
                var sor = StartOfRound.Instance;
                var map = sor != null ? sor.mapScreen : null;
                if (sor == null || map == null)
                {
                    log.LogWarning("[Minimap F6] StartOfRound/mapScreen is null.");
                    return;
                }

                Camera cam = map.cam;
                Camera mapCamera = map.mapCamera;

                // Первый блок - состояние САМОГО МОДА: с него начинать чтение дампа.
                // configEnabled=false или overlay=INACTIVE = миникарта выключена (F2).
                // patchRuns растёт каждый кадр, если патч камеры жив; shipRadarRuns
                // растёт только когда мод реально управляет корабельным радаром.
                bool configOn = MinimalMinimap.Instance != null && MinimalMinimap.Instance.ConfigEnabled.Value;
                string lastShipRun = ManualCameraRendererPatch.ShipRadarRunCount == 0
                    ? "never"
                    : (Time.time - ManualCameraRendererPatch.LastShipRadarRunTime).ToString("F1") + "s ago";

                log.LogWarning(
                    "[Minimap F6] Radar state dump: " +
                    $"configEnabled={configOn}, " +
                    $"overlay={(minimapObject == null ? "MISSING" : (minimapObject.activeSelf ? "active" : "INACTIVE"))}, " +
                    $"freezeTarget={MinimalMinimap.Data.FreezeTarget}, selfCentered={selfCenteredThisLife}, " +
                    $"patchRuns={ManualCameraRendererPatch.PostfixRunCount}, " +
                    $"shipRadarRuns={ManualCameraRendererPatch.ShipRadarRunCount}, lastShipRun={lastShipRun}, " +
                    $"inShipPhase={sor.inShipPhase}, " +
                    $"overrideCameraForOtherUse={map.overrideCameraForOtherUse}, " +
                    $"overrideRadarCameraOnAlways={map.overrideRadarCameraOnAlways}, " +
                    $"screenEnabledOnLocalClient={GetPrivateBool(map, "screenEnabledOnLocalClient")}, " +
                    $"mapCameraMaxFramerate={GetPrivateBool(map, "mapCameraMaxFramerate")}, " +
                    $"currentCameraDisabled={map.currentCameraDisabled}, " +
                    $"renderAtLowerFramerate={map.renderAtLowerFramerate}, fps={map.fps}, " +
                    $"cam={(cam != null ? $"enabled={cam.enabled}, pos={cam.transform.position}, targetTexture={(cam.targetTexture != null ? "ok" : "NULL")}" : "NULL")}, " +
                    $"mapCamera={(mapCamera != null ? $"enabled={mapCamera.enabled}, orthoSize={mapCamera.orthographicSize}" : "NULL")}, " +
                    $"cam==mapCamera? {(cam == mapCamera)}, " +
                    $"targetedPlayer={(map.targetedPlayer != null ? map.targetedPlayer.playerUsername : "null")}, " +
                    $"targetTransformIndex={map.targetTransformIndex}/{(map.radarTargets != null ? map.radarTargets.Count : -1)}, " +
                    $"minimapTexture={(minimapImage == null || minimapImage.texture == null
                        ? "NULL"
                        : (cam != null && minimapImage.texture == cam.targetTexture ? "bound" : "STALE"))})");
            }
            catch (System.Exception e)
            {
                log.LogError($"[Minimap F6] Dump failed: {e}");
            }
        }

        private static object GetPrivateBool(ManualCameraRenderer map, string fieldName)
        {
            var field = HarmonyLib.AccessTools.Field(typeof(ManualCameraRenderer), fieldName);
            return field != null ? field.GetValue(map) : (object)"<not found>";
        }
    }
}
