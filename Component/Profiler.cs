using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.Reflection;
using System.Linq;

using HarmonyLib;

using UnityEngine;

using Aki.Reflection.Utils;

using static Config.Profiles;

namespace Gaylatea
{
    namespace Profiler
    {
        public class ProfilerState
        {
            public Stopwatch timer;
        }

        public class Controller : MonoBehaviour
        {
            private static string currentLocalGame;
            private static string currentProfileSession;

            private static Harmony _harmony;
            private static HarmonyMethod methodPrefix;
            private static HarmonyMethod methodPostfix;
            private static Stack<SQLiteCommand> batchedCommands;

            private static string queryCreateFlamegraphTable = @"CREATE TABLE IF NOT EXISTS calls (
            frame number,
            session text,
            game text,
            component text,
            phase text,
            time_ms number
        );";

            private static string queryAddFlamegraphCall = @"INSERT INTO calls(
            frame, session, game, component, phase, time_ms
        ) VALUES (
            $frame, $session, $game, $component, $phase, $time_ms
        );";
            private static SQLiteConnection db = new SQLiteConnection("Data Source=profiler.sqlite3;Version=3;New=True;Compress=True;");

            public void Awake()
            {
                currentProfileSession = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                _harmony = new Harmony("Gaylatea-Profiling");
                // TODO(gaylatea): Figure out a better batch size.
                batchedCommands = new Stack<SQLiteCommand>(1000);

                db.Open();

                var create_cmd = db.CreateCommand();
                create_cmd.CommandText = queryCreateFlamegraphTable;
                create_cmd.ExecuteNonQuery();

                methodPrefix = new HarmonyMethod(typeof(Controller).GetMethod("Prefix", BindingFlags.Static | BindingFlags.Public));
                methodPostfix = new HarmonyMethod(typeof(Controller).GetMethod("Postfix", BindingFlags.Static | BindingFlags.Public));

                var prefixActivate = new HarmonyMethod(typeof(Controller).GetMethod("RecordGameDetails", BindingFlags.Static | BindingFlags.Public));
                _harmony.Patch(PatchConstants.LocalGameType.GetMethod("vmethod_0", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public), prefix: prefixActivate);

                var mbType = typeof(MonoBehaviour);

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
                        var name = x.Name;
                        if (name == "FixedUpdate") return true;
                        if (name == "LateUpdate") return true;
                        if (name == "Update") return true;
                        return false;
                    })
                    .ToList();

                foreach (var hit in hits)
                {
                    try
                    {
                        _harmony.Patch(original: hit, prefix: methodPrefix, postfix: methodPostfix);
                    }
                    catch (Exception e)
                    {
                        Plugin.logger.LogError($"[Profiler] Failed to hook {hit.FullDescription()} - {e.Message}");
                    }
                    Plugin.logger.LogDebug($"[Profiler] Instrumenting {hit.DeclaringType.Name}::{hit.Name}!");
                }

                StartCoroutine(BatchWrites());
            }

            public IEnumerator BatchWrites()
            {
                var frameWait = new WaitForEndOfFrame();
                var lastWritten = 0;
                Plugin.logger.LogInfo("[Profiler] Starting the batch write controller.");
                while (true)
                {
                    // Every 10 frames, we write out the current batch of frame
                    // timings to the database in a new transaction. This should
                    // be a lot faster than writing from each method on each 
                    // frame.
                    if (Time.frameCount % 10 == 0)
                    {
                        lastWritten = 0;
                        using (var transaction = db.BeginTransaction())
                        {
                            while(batchedCommands.Count > 0) {
                                var cmd = batchedCommands.Pop();
                                lastWritten += cmd.ExecuteNonQuery();
                            }

                            transaction.Commit();
                        }

                        Plugin.logger.LogDebug($"[Profiler] Wrote out {lastWritten} samples.");
                    }
                    yield return frameWait;
                }
            }

            // This patch will add a marker for the current game to any profiling
            // calls that are made, so that multiple runs can be analyzed easily.
            public static bool RecordGameDetails(object __instance)
            {
                currentLocalGame = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                return true;
            }

            public void EnableOn(Type typeName, string methodName)
            {
                var m = typeName.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (m != null)
                {
                    _harmony.Patch(m, prefix: methodPrefix, postfix: methodPostfix);
                }
            }

            public static bool Prefix(ref ProfilerState __state)
            {
                if (!Enabled.Value) { return true; }

                if (__state == null)
                {
                    __state = new ProfilerState();
                    __state.timer = new Stopwatch();
                }
                __state.timer.Start();
                return true;
            }

            public static void Postfix(ref ProfilerState __state, MethodBase __originalMethod, object[] __args)
            {
                if (!Enabled.Value) { return; }

                __state.timer.Stop();

                var cmd = db.CreateCommand();
                cmd.CommandText = queryAddFlamegraphCall;
                cmd.Parameters.AddWithValue("$frame", Time.frameCount);
                cmd.Parameters.AddWithValue("$session", currentProfileSession);
                cmd.Parameters.AddWithValue("$game", currentLocalGame);
                cmd.Parameters.AddWithValue("$component", __originalMethod.DeclaringType.Name);
                cmd.Parameters.AddWithValue("$phase", __originalMethod.Name);
                cmd.Parameters.AddWithValue("$time_ms", __state.timer.Elapsed.TotalMilliseconds);
                batchedCommands.Push(cmd);

                __state.timer.Reset();
            }
        }
    }
}