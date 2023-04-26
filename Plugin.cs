using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

using BepInEx;
using BepInEx.Logging;

using UnityEngine;

using Config;

namespace Gaylatea
{
    namespace Profiler
    {
        [BepInPlugin("com.gaylatea.profiler", "SPT-Profiler", "1.0.0")]
        public class Plugin : BaseUnityPlugin
        {
            private GameObject Hook;
            internal static ManualLogSource logger;

            public Plugin()
            {
                Profiles.Init(Config);

                logger = Logger;

                Hook = new GameObject("Gaylatea.Profiler");
                Hook.AddComponent<Controller>();
                DontDestroyOnLoad(Hook);

                Logger.LogInfo($"Profiler Loaded");
            }
        }
    }
}