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

        internal static void MapCameraLogic(
            ManualCameraRenderer __instance,
            ref Camera ___mapCamera,
            ref PlayerControllerB ___targetedPlayer,
            ref Image ___compassRose,
            ref List<TransformAndName> ___radarTargets,
            ref int ___targetTransformIndex)
        {
            PostfixRunCount++;

            if (MinimalMinimap.Instance == null ||
                !MinimalMinimap.Instance.ConfigEnabled.Value ||
                ___mapCamera == null)
                return;

            // Работаем только с ship radar (на случай других ManualCameraRenderer, напр. камер наблюдения).
            if (__instance.cam != ___mapCamera)
                return;

            ShipRadarRunCount++;
            LastShipRadarRunTime = Time.time;

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
