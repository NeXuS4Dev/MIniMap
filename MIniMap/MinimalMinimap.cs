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
        // ВНИМАНИЕ: Instance - это Unity-компонент (MonoBehaviour). Если игра
        // уничтожит его GameObject, Unity делает объект "почти null": управляемый
        // код, поля и методы продолжают работать, но проверка "Instance == null"
        // навсегда возвращает истину. Именно так раньше умирала миникарта: все
        // стражи опирались на эту проверку и молча отсекали логику при полностью
        // живом моде. Поэтому Instance НЕ используется ни в одной проверке -
        // только для диагностики (F6-дамп печатает pluginInstanceAlive).
        public static MinimalMinimap Instance;
        public static MinimapData Data;

        // BepInEx's BaseUnityPlugin.Logger is protected in 5.4.x, so keep an
        // internal copy for the static patch helpers to write warnings to.
        internal static ManualLogSource PluginLogger;

        // Статическое намеренно: ConfigEntry - обычный C#-объект, принадлежащий
        // ConfigFile, а не Unity-компонент, поэтому он не умирает вместе с
        // GameObject'ом плагина (см. замечание про Instance выше).
        public static ConfigEntry<bool> ConfigEnabled;

        private Harmony harmony;

        private void Awake()
        {
            Instance = this;
            Data = new MinimapData();
            PluginLogger = Logger;

            // "General" - секция, "Enabled" - ключ, true - миникарта включена по умолчанию.
            // (По умолчанию false она слишком часто "молча не работала" на свежих профилях.)
            ConfigEnabled = Config.Bind("General", "Enabled", true,
                "Enable or disable the minimap. Also toggled in-game with F2.");

            // Конфиг - только стартовое значение и хранилище между запусками.
            // Внутри сессии эталонным состоянием является RuntimeEnabled (см. MinimapData).
            Data.RuntimeEnabled = ConfigEnabled.Value;

            harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            MinimapPatches.Apply(harmony, Logger);

            Logger.LogInfo($"{MyPluginInfo.PLUGIN_NAME} v{MyPluginInfo.PLUGIN_VERSION} loaded. " +
                           "Client-side only, safe to use on vanilla servers. Built for Lethal Company v81+.");
        }

        // Единственный источник правды о том, включён ли мод. Намеренно НЕ
        // трогает Instance (Unity fake-null, см. выше). Data тоже живёт вне
        // Unity-жизненного цикла; если она вдруг не инициализирована
        // (экзотическая двойная загрузка сборки), безопаснее считать мод
        // включённым, чем молча гасить его, - поэтому проверка fail-open.
        internal static bool IsEnabled()
        {
            return Data == null || Data.RuntimeEnabled;
        }
    }

    public static class MyPluginInfo
    {
        public const string PLUGIN_GUID = "com.diman3012.minimap";
        public const string PLUGIN_NAME = "Minimal Minimap";
        public const string PLUGIN_VERSION = "1.2.6";
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

        // Вкл/выкл миникарты в ТЕКУЩЕЙ сессии. Инициализируется из конфига при
        // загрузке, дальше управляется только F2. Все проверки состояния должны
        // читать ЭТО поле, а не BepInEx-конфиг: hot-reload конфиг-файла и гонки
        // его записи (F2 сразу сохраняет файл, BepInEx может перечитывать его
        // асинхронно) не должны молча менять состояние мода посреди игры.
        public bool RuntimeEnabled = true;

        public KeyCode SwitchKey = KeyCode.F3;
        public KeyCode ToggleKey = KeyCode.F2;
        public KeyCode DebugKey = KeyCode.F6; // дамп состояния радара в лог
    }
}
