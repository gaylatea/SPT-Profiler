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
    /// Sets up timing collectors for the weird-ass job scheduler that BSG
    /// included in the game. It is used for a variety of purposes, including
    /// making HTTP/WebSocket calls, and loading bundles from the filesystem.
    /// </summary>
    public class JobSchedulerProfiler
    {
        private static Harmony _harmony;

        public static void Init()
        {
            // TODO: AsyncWorker
            _harmony = new Harmony("Gaylatea.Profiler.JobScheduler");
            var taskType = AccessTools.FirstInner(AccessTools.TypeByName("AsyncWorker"), (x) => {
                Plugin.logger.LogInfo($"Checking {x.Name} for inner type...");
                return x.Name.Contains("Class2906");
            });
            taskType = taskType.MakeGenericType(taskType.GetGenericArguments().Select(x => x.BaseType).ToArray());

            Plugin.logger.LogInfo("[Profiler] Instrumenting BSG's background worker system.");

            var methodPrefix = new HarmonyMethod(typeof(JobSchedulerProfiler).GetMethod("Prefix", BindingFlags.Static | BindingFlags.Public));
            var schedulerPostfix = new HarmonyMethod(typeof(JobSchedulerProfiler).GetMethod("SchedulerPostfix", BindingFlags.Static | BindingFlags.Public));
            _harmony.Patch(AccessTools.TypeByName("Struct846").GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public), prefix: methodPrefix, postfix: schedulerPostfix);

            var asyncWorkerPostfix = new HarmonyMethod(typeof(JobSchedulerProfiler).GetMethod("AsyncWorkerPostfix", BindingFlags.Static | BindingFlags.Public));
            _harmony.Patch(taskType.GetMethod("method_0", BindingFlags.NonPublic | BindingFlags.Instance), prefix: methodPrefix, postfix: asyncWorkerPostfix);
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

        public static void SchedulerPostfix(ref Stopwatch __state, string ___jobStageName)
        {
            if (!Enabled.Value) { return; }

            __state.Stop();

            Controller.AddSample("JobScheduler.Execute", ___jobStageName, __state.Elapsed.TotalMilliseconds);
            __state.Reset();
        }

        public static void AsyncWorkerPostfix(ref Stopwatch __state, Func<Action> ___function)
        {
            if (!Enabled.Value) { return; }

            __state.Stop();

            var job = ___function.GetMethodInfo();
            var jobName = $"{job.DeclaringType.Name}.{job.Name}";

            Controller.AddSample("AsyncWorker.Execute", jobName, __state.Elapsed.TotalMilliseconds);
            __state.Reset();
        }
    }
}