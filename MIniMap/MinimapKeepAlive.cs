using UnityEngine;

namespace MIniMap
{
    /// <summary>
    /// Attach to a tiny scene GameObject we own. Its LateUpdate runs AFTER
    /// every Update method in the frame, so forcing the radar camera on here
    /// always beats any vanilla code that disables it in
    /// ManualCameraRenderer.Update (v80+ disables the map camera whenever the
    /// player can't directly see the physical monitor / is outside the ship).
    ///
    /// This intentionally does NOT go through the ManualCameraRenderer patch:
    /// it relies only on StartOfRound.Instance.mapScreen and works even if
    /// that behaviour stopped ticking entirely.
    /// </summary>
    internal sealed class MinimapKeepAlive : MonoBehaviour
    {
        // Диагностика для F6-дампа: доказывает, что сторож тикает и принуждает камеру.
        internal static long Ticks;
        internal static long EnforceCount;
        internal static float LastEnforceTime;

        private void LateUpdate()
        {
            if (MinimalMinimap.Instance == null ||
                MinimalMinimap.Data == null ||
                !MinimalMinimap.Data.RuntimeEnabled)
                return;

            StartOfRound sor = StartOfRound.Instance;
            ManualCameraRenderer map = sor != null ? sor.mapScreen : null;
            if (map == null)
                return;

            Ticks++;

            bool enforced = false;
            if (map.cam != null && !map.cam.enabled)
            {
                map.cam.enabled = true;
                enforced = true;
            }
            if (map.mapCamera != null && !map.mapCamera.enabled)
            {
                map.mapCamera.enabled = true;
                enforced = true;
            }

            if (enforced)
            {
                EnforceCount++;
                LastEnforceTime = Time.time;
            }
        }
    }
}
