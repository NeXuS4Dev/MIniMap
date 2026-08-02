using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace MIniMap
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    [BepInProcess("Lethal Company.exe")]
    public class MinimalMinimap : BaseUnityPlugin
    {
        public static MinimalMinimap Instance;
        public static MinimapData Data;

        // BepInEx's BaseUnityPlugin.Logger is protected in 5.4.x, so keep an
        // internal copy for the static patch helpers to write warnings to.
        internal static ManualLogSource PluginLogger;

        public ConfigEntry<bool> ConfigEnabled;

        private Harmony harmony;

        private void Awake()
        {
            Instance = this;
            Data = new MinimapData();
            PluginLogger = Logger;

            // "General" - секция, "Enabled" - ключ, false - значение по умолчанию (выключено)
            ConfigEnabled = Config.Bind("General", "Enabled", false,
                "Enable or disable the minimap. Also toggled in-game with F2.");

            harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            MinimapPatches.Apply(harmony, Logger);

            Logger.LogInfo($"{MyPluginInfo.PLUGIN_NAME} v{MyPluginInfo.PLUGIN_VERSION} loaded. " +
                           "Client-side only, safe to use on vanilla servers. Built for Lethal Company v81+.");
        }
    }

    public static class MyPluginInfo
    {
        public const string PLUGIN_GUID = "com.diman3012.minimap";
        public const string PLUGIN_NAME = "Minimal Minimap";
        public const string PLUGIN_VERSION = "1.2.2";
    }

    public class MinimapData
    {
        // 🔧 НАСТРОЙКИ
        public int Size = 200;
        public float XOffset = -10f;
        public float YOffset = -10f;
        public float Zoom = 20f;
        public bool AutoRotate = true;

        // 🎮 УПРАВЛЕНИЕ
        public bool FreezeTarget = true;

        public KeyCode SwitchKey = KeyCode.F3;
        public KeyCode ToggleKey = KeyCode.F2;
        public KeyCode DebugKey = KeyCode.F6; // дамп состояния радара в лог
    }
}
