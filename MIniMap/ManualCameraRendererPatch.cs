using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.UI;

namespace MIniMap
{
    /// <summary>
    /// Postfix on ManualCameraRenderer.Update for the ship radar renderer.
    /// Keeps the map camera alive, applies zoom/auto-rotate and fixes icon rotation.
    /// </summary>
    internal static class ManualCameraRendererPatch
    {
        private static readonly Vector3 defaultEulerAngles = new Vector3(90f, 0f, 0f);

        // FindObjectsOfType каждый кадр - лишний мусор для GC; обновляем список раз в секунду.
        private static TerminalAccessibleObject[] cachedMapObjects = new TerminalAccessibleObject[0];
        private static float nextMapObjectsRefresh;

        // Самодиагностика для F6-дампа: по этим счётчикам сразу видно,
        // выполняется ли патч вообще и доходит ли он до кода корабельного радара.
        internal static long PostfixRunCount;
        internal static long ShipRadarRunCount;
        internal static float LastShipRadarRunTime;

        // Микроскоп над условиями прохождения стражей для корабельного радара:
        // тикает ли обновление mapScreen вообще и какая именно проверка его отсекает.
        internal static long MapScreenTickCount;
        internal static float LastMapScreenTickTime;
        internal static string LastMapScreenRejectReason = "never seen";

        // Детект "фантомных" переключений конфиг-хранилища BepInEx: если значение
        // Enabled в файле поменялось без нашего F2 (hot-reload, гонка записи), логируем.
        private static bool? lastSeenConfigValue;
        private static bool shipRadarEngagedLogged;

        internal static void MapCameraLogic(
            ManualCameraRenderer __instance,
            ref Camera ___mapCamera,
            ref PlayerControllerB ___targetedPlayer,
            ref Image ___compassRose,
            ref List<TransformAndName> ___radarTargets,
            ref int ___targetTransformIndex)
        {
            PostfixRunCount++;

            // Одноразовая "карточка примет" при первом тике патча: доказывает,
            // какая именно копия сборки выполняет код (защита от двойной
            // загрузки старой dll из другого каталога) и жив ли Unity-объект
            // плагина (диагноз "fake-null", см. MinimalMinimap).
            if (PostfixRunCount == 1)
            {
                MinimalMinimap.PluginLogger?.LogInfo(
                    "[Minimap] Radar patch alive. Fingerprint: " +
                    $"pluginInstanceAlive={(MinimalMinimap.Instance != null)}, " +
                    $"dataInitialized={(MinimalMinimap.Data != null)}, " +
                    $"configEntry={(MinimalMinimap.ConfigEnabled != null ? MinimalMinimap.ConfigEnabled.Value.ToString() : "null")}, " +
                    $"pluginAsm=\"{typeof(MinimalMinimap).Assembly.Location}\", " +
                    $"patchAsm=\"{typeof(ManualCameraRendererPatch).Assembly.Location}\"");
            }

            bool isShipScreen = StartOfRound.Instance != null &&
                                ReferenceEquals(__instance, StartOfRound.Instance.mapScreen);
            if (isShipScreen)
            {
                MapScreenTickCount++;
                LastMapScreenTickTime = Time.time;
            }

            // Детект "фантомных" переключений конфиг-хранилища BepInEx. Читается
            // СТАТИЧЕСКИЙ ConfigEntry - он обычный C#-объект и не зависит от
            // судьбы Unity-объекта плагина (fake-null Instance).
            if (MinimalMinimap.ConfigEnabled != null)
            {
                bool configValue = MinimalMinimap.ConfigEnabled.Value;
                if (lastSeenConfigValue != configValue)
                {
                    MinimalMinimap.PluginLogger?.LogWarning(
                        $"[Minimap] Config file entry now reads Enabled={configValue} " +
                        $"(runtime state: {(MinimalMinimap.Data == null ? "UNINITIALIZED" : (MinimalMinimap.Data.RuntimeEnabled ? "ON" : "OFF"))}). " +
                        "Runtime state is authoritative for this session.");
                    lastSeenConfigValue = configValue;
                }
            }

            // НИКОГДА не проверять здесь MinimalMinimap.Instance: это Unity-объект,
            // и если игра уничтожит его, "Instance == null" навсегда становится
            // истиной (fake-null), молча убивая всю логику мода при живом патче -
            // именно так миникарта замирала у части игроков (дампы с причиной
            // "runtimeDisabled" при RuntimeEnabled=True). Только простые статики.
            if (!MinimalMinimap.IsEnabled())
            {
                if (isShipScreen) LastMapScreenRejectReason = "runtimeDisabled";
                return;
            }

            // IsEnabled() нарочно fail-open при Data == null (двойная загрузка
            // сборки), но дальше читаются поля Data - здесь без них никак.
            if (MinimalMinimap.Data == null)
            {
                if (isShipScreen) LastMapScreenRejectReason = "dataUninitialized";
                return;
            }

            if (___mapCamera == null ||
                (__instance.cam != ___mapCamera && !isShipScreen))
            {
                if (isShipScreen)
                {
                    LastMapScreenRejectReason = ___mapCamera == null
                        ? "mapCamera==null"
                        : $"cam!=mapCamera (cam#{( __instance.cam == null ? -1 : __instance.cam.GetInstanceID())}," +
                          $" mapCam#{(__instance.mapCamera == null ? -1 : __instance.mapCamera.GetInstanceID())})";
                }
                return;
            }

            if (isShipScreen)
                LastMapScreenRejectReason = __instance.cam == ___mapCamera
                    ? "passing"
                    : "passing via mapScreen-reference (cam!=mapCamera!)";

            ShipRadarRunCount++;
            LastShipRadarRunTime = Time.time;

            if (!shipRadarEngagedLogged)
            {
                shipRadarEngagedLogged = true;
                MinimalMinimap.PluginLogger?.LogInfo(
                    "[Minimap] Ship radar control engaged - the minimap now stays live everywhere.");
            }

            // Пока идёт раунд (корабль сел), экран карты обязан быть в режиме радара.
            // Если флаг завис в состоянии "инфо-экран орбиты", ванильный Update
            // пропускает ВЕСЬ код радара (камера не следует за целью) и картинка
            // навсегда замирает - снимаем флаг. На орбите (inShipPhase) инфо-экран
            // является легитимным состоянием и не трогается.
            if (__instance.overrideCameraForOtherUse &&
                StartOfRound.Instance != null && !StartOfRound.Instance.inShipPhase)
            {
                __instance.overrideCameraForOtherUse = false;
            }

            // Защита от рассинхрона, если цели удалили из списка (игрок вышел и т.п.)
            if (___radarTargets != null && ___radarTargets.Count > 0)
            {
                if (___targetTransformIndex < 0 || ___targetTransformIndex >= ___radarTargets.Count)
                    ___targetTransformIndex = 0;
            }

            // v80+: игра рендерит монитор с пониженным FPS и только пока он виден игроку.
            // Вне корабля MeetsCameraEnabledConditions=false -> игра гасит камеру каждый
            // кадр -> RenderTexture замирает последним кадром. Нам нужна живая миникарта
            // всегда - держим камеру включённой (оба поля - это один и тот же Camera,
            // но страхуемся от переименований в будущих версиях игры).
            if (!__instance.cam.enabled)
                __instance.cam.enabled = true;
            if (!___mapCamera.enabled)
                ___mapCamera.enabled = true;

            if (___mapCamera.orthographicSize != MinimalMinimap.Data.Zoom)
            {
                ___mapCamera.orthographicSize = MinimalMinimap.Data.Zoom;
            }

            // Логика автоповорота
            if (MinimalMinimap.Data.AutoRotate && ___targetedPlayer != null)
            {
                ___mapCamera.transform.eulerAngles = new Vector3(
                    defaultEulerAngles.x,
                    ___targetedPlayer.transform.eulerAngles.y,
                    defaultEulerAngles.z
                );
            }
            else
            {
                if (___mapCamera.transform.eulerAngles != defaultEulerAngles)
                {
                    ___mapCamera.transform.eulerAngles = defaultEulerAngles;
                }
            }

            // Исправление вращения иконок объектов (турели, коды дверей и т.п.)
            if (Time.time >= nextMapObjectsRefresh)
            {
                cachedMapObjects = Object.FindObjectsOfType<TerminalAccessibleObject>();
                nextMapObjectsRefresh = Time.time + 1f;
            }

            for (int i = 0; i < cachedMapObjects.Length; i++)
            {
                var mapObject = cachedMapObjects[i];
                if (mapObject == null || mapObject.mapRadarObject == null) continue;

                mapObject.mapRadarObject.transform.eulerAngles = new Vector3(
                    defaultEulerAngles.x,
                    ___mapCamera.transform.eulerAngles.y,
                    defaultEulerAngles.z
                );
            }

            // Поворот компаса
            if (___compassRose != null)
            {
                ___compassRose.rectTransform.localEulerAngles = new Vector3(
                    0f, 0f, ___mapCamera.transform.eulerAngles.y
                );
            }
        }
    }
}
