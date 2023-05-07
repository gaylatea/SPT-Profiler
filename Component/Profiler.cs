using System;
using System.Data.SQLite;
using System.Reflection;

using System.Threading;
using System.Threading.Channels;

using HarmonyLib;

using UnityEngine;

using Aki.Reflection.Utils;

namespace Gaylatea.Profiler
{
    /// <summary>
    /// Coordinates background writes to the backend database. This allows
    /// the profiler to run without undue performance impact.
    /// </summary>
    public class Controller : MonoBehaviour
    {
        private static string currentLocalGame;
        private static string currentProfileSession;

        private static Harmony _harmony;
        private static Channel<SQLiteCommand> commandChannel;

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
            _harmony = new Harmony("Gaylatea.Profiler");
            commandChannel = Channel.CreateUnbounded<SQLiteCommand>();

            // Start the batch write control thread and spin it off in the background.
            (new Thread(new ThreadStart(ThreadMain))).Start();

            // Record when new raids start.
            var prefixActivate = new HarmonyMethod(typeof(Controller).GetMethod("RecordGameDetails", BindingFlags.Static | BindingFlags.NonPublic));
            _harmony.Patch(PatchConstants.LocalGameType.GetMethod("vmethod_0", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public), prefix: prefixActivate);

            MonoBehaviourProfiler.Init();
            JobSchedulerProfiler.Init();
        }

        private async void ThreadMain()
        {
            db.Open();

            // Ensure that the DB and associated tables exist.
            var create_cmd = db.CreateCommand();
            create_cmd.CommandText = queryCreateFlamegraphTable;
            create_cmd.ExecuteNonQuery();

            // TODO: more monotonic time?
            var lastWritten = DateTime.Now;
            var lastAmount = 0;
            var currentTransaction = db.BeginTransaction();
            Plugin.logger.LogInfo("[Profiler] Starting the batch write controller.");
            while (await commandChannel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                if (commandChannel.Reader.TryRead(out SQLiteCommand item))
                {
                    await item.ExecuteNonQueryAsync();
                    lastAmount++;
                }

                // Every second (or more depending on channel activity), 
                // write out the current batch of samples and start a new
                // transaction.
                if (DateTime.Now - lastWritten >= TimeSpan.FromSeconds(1.0f))
                {
                    currentTransaction.Commit();

                    Plugin.logger.LogDebug($"[Profiler] Wrote out {lastAmount} samples.");
                    lastAmount = 0;
                    lastWritten = DateTime.Now;
                    currentTransaction = db.BeginTransaction();
                }
            }
        }

        /// <summary>
        /// Adds a marker for the current game to any
        /// profiling calls that are made, so that multiple runs can be
        /// analyzed easily.
        /// </summary>
        private static bool RecordGameDetails(object __instance)
        {
            currentLocalGame = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            return true;
        }

        /// <summary>
        /// Pushes a new sample into the background for processing.
        /// </summary>
        public static void AddSample(string typeName, string methodName, double totalMs)
        {
            var cmd = db.CreateCommand();
            cmd.CommandText = queryAddFlamegraphCall;
            cmd.Parameters.AddWithValue("$frame", Time.frameCount);
            cmd.Parameters.AddWithValue("$session", currentProfileSession);
            cmd.Parameters.AddWithValue("$game", currentLocalGame);
            cmd.Parameters.AddWithValue("$component", typeName);
            cmd.Parameters.AddWithValue("$phase", methodName);
            cmd.Parameters.AddWithValue("$time_ms", totalMs);

            // TODO(gaylatea): Error handling.
            commandChannel.Writer.TryWrite(cmd);
        }
    }
}