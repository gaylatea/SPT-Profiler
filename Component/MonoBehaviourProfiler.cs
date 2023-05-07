using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Linq;

using HarmonyLib;

using UnityEngine;

using static Config.Profiles;

namespace Gaylatea.Profiler
{
    /// <summary>
    /// Sets up timing collectors for all the Unity scripts in the game,
    /// which helps for establishing baseline timing information.
    /// </summary>
    public class MonoBehaviourProfiler
    {
        private static Harmony _harmony;

        /// <summary>
        /// All the Unity script lifecycle methods that should be timed.
        /// </summary>
        private static List<string> _methodsToPatch = new List<string>{
            "Update",
            "LateUpdate",
            "FixedUpdate",
        };

        public static void Init()
        {
            _harmony = new Harmony("Gaylatea.Profiler.MonoBehaviour");

            var methodPrefix = new HarmonyMethod(typeof(MonoBehaviourProfiler).GetMethod("Prefix", BindingFlags.Static | BindingFlags.Public));
            var methodPostfix = new HarmonyMethod(typeof(MonoBehaviourProfiler).GetMethod("Postfix", BindingFlags.Static | BindingFlags.Public));

            Plugin.logger.LogInfo("[Profiler] Instrumenting built-in MonoBehaviours.");

            var mbType = typeof(MonoBehaviour);
            var methodsPatched = 0;

            var hits = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(x => x.GetTypes())
                .Where(x => mbType.IsAssignableFrom(x))
                .Select(t =>
                {
                    if (t.ContainsGenericParameters)
                    {
                        try
                        {
                            var genericType = t.MakeGenericType(t.GetGenericArguments().Select(x => x.BaseType).ToArray());
                            return genericType;
                        }
                        catch (Exception e)
                        {
                            Plugin.logger.LogError($"[Profiler] Failed to hook in class {t.FullName} - {e.Message}");
                            return null;
                        }
                    }

                    return t;
                })
                .SelectMany(x => x?.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? new MethodBase[0])
                .Where(x =>
                {
                    return _methodsToPatch.Contains(x.Name);
                });

                foreach(var method in hits)
                {
                    try
                    {
                        _harmony.Patch(original: method, prefix: methodPrefix, postfix: methodPostfix);
                        methodsPatched++;
                        Plugin.logger.LogDebug($"[Profiler] Instrumenting {method.DeclaringType.Name}::{method.Name}!");
                    }
                    catch (Exception e)
                    {
                        Plugin.logger.LogError($"[Profiler] Failed to hook {method.FullDescription()} - {e.Message}");
                    }
                }

                Plugin.logger.LogInfo($"[Profiler] Instrumented {methodsPatched} methods in total.");
        }

        public static bool Prefix(ref Stopwatch __state)
        {
            if (!Enabled.Value) { return true; }

            if (__state == null)
            {
                __state = new Stopwatch();
            }
            __state.Start();
            return true;
        }

        public static void Postfix(ref Stopwatch __state, MethodBase __originalMethod, object[] __args)
        {
            if (!Enabled.Value) { return; }

            __state.Stop();

            Controller.AddSample(__originalMethod.DeclaringType.Name, __originalMethod.Name, __state.Elapsed.TotalMilliseconds);
            __state.Reset();
        }
    }
}