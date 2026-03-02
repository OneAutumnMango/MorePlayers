using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using MageQuitModFramework.Modding;
using MageQuitModFramework.UI;

namespace MorePlayers
{
    [BepInPlugin("com.magequit.moreplayers", "More Players", "1.0.0")]
    [BepInDependency("com.magequit.modframework", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log;

        private static ConfigEntry<int> _maxPlayersConfig;
        public static int MaxPlayers => _maxPlayersConfig.Value;

        private ModuleManager _moduleManager;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Log.LogInfo("More Players loading...");

            _maxPlayersConfig = Config.Bind(
                "General",
                "MaxPlayers",
                16,
                new ConfigDescription(
                    "Maximum number of players (2-16). Restart required after changing.",
                    new AcceptableValueRange<int>(2, 16)
                )
            );

            _moduleManager = ModManager.RegisterMod("More Players", "com.magequit.moreplayers");
            _moduleManager.RegisterModule(new MP.MPModule());

            ModUIRegistry.RegisterMod(
                "More Players",
                $"Expands the player cap to {MaxPlayers}. Edit BepInEx config and restart to change.",
                BuildModUI,
                priority: 20
            );

            Log.LogInfo($"More Players loaded! MaxPlayers = {MaxPlayers}");
        }

        private void BuildModUI()
        {
            UnityEngine.GUILayout.Label($"Current MaxPlayers: {MaxPlayers}");
            UnityEngine.GUILayout.Label("Edit BepInEx/config/com.magequit.moreplayers.cfg and restart to change.");
        }
    }
}
