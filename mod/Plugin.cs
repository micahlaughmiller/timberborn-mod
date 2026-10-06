using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Timberborn.ModManagerScene;
using UnityEngine;
using UnityEngine.LowLevel;

namespace TimberbornAI
{
    /// <summary>
    /// Entry point the game's own mod loader calls. Timberborn.ModManagerScene
    /// finds every IModStarter implementation in enabled mods' assemblies,
    /// constructs it (parameterless ctor required) and calls StartMod once at
    /// startup. Unity's RuntimeInitializeOnLoadMethod is NOT honored for mod
    /// assemblies, which is why the earlier bootstrap never ran.
    /// </summary>
    /// <summary>
    /// Bisect switches that need no rebuild: an empty file with one of these
    /// names next to TimberbornAI.dll turns that piece off.
    ///   no-plugin.flag : StartMod only logs, creates nothing
    ///   no-hook.flag   : skip the PlayerLoop hook
    ///   no-http.flag   : skip the HTTP listener and type-index warm-up
    /// </summary>
    internal static class Flags
    {
        private static readonly string Dir = ResolveDir();

        private static string ResolveDir()
        {
            try { return Path.GetDirectoryName(typeof(Flags).Assembly.Location) ?? ""; }
            catch { return ""; }
        }

        public static bool Has(string name)
        {
            try { return Dir.Length > 0 && File.Exists(Path.Combine(Dir, name)); }
            catch { return false; }
        }

        public static string Where() => Dir.Length > 0 ? Dir : "(unknown dir)";
    }

    public class ModStarter : IModStarter
    {
        private static bool _started;

        public void StartMod(IModEnvironment modEnvironment)
        {
            if (_started) return;
            _started = true;

            Debug.Log("[TimberbornAI] StartMod called; flag dir = " + Flags.Where());

            if (Flags.Has("no-plugin.flag"))
            {
                Debug.Log("[TimberbornAI] no-plugin.flag present: doing nothing");
                return;
            }

            var go = new GameObject("TimberbornAI");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Plugin>();

            if (Flags.Has("no-hook.flag")) Debug.Log("[TimberbornAI] no-hook.flag present: PlayerLoop hook skipped");
            else Plugin.InstallPlayerLoopHook();
        }
    }

    /// <summary>
    /// Hosts a small local HTTP API inside the game process so an external
    /// agent can read world state and enqueue commands.
    ///
    /// Threading rule: HttpListener callbacks run on worker threads, but all
    /// Unity/game access MUST happen on the main thread. Requests therefore
    /// enqueue work and block on a handle until Update() drains the queue.
    /// </summary>
    public class Plugin : MonoBehaviour
    {
        public const int Port = 8787;

        private static readonly ConcurrentQueue<PendingJob> Jobs = new ConcurrentQueue<PendingJob>();
        private HttpListener _listener;
        private Thread _listenerThread;

        private sealed class PendingJob
        {
            public Func<string> Work;
            public string Result;
            public string Error;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private void Awake()
        {
            if (Flags.Has("no-http.flag"))
            {
                Debug.Log("[TimberbornAI] no-http.flag present: listener not started");
                return;
            }

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();

            _listenerThread = new Thread(ListenLoop) { IsBackground = true };
            _listenerThread.Start();

            // Index game types off the main thread so the first /state isn't slow.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { GameAccess.Warm(); Debug.Log("[TimberbornAI] type index ready"); }
                catch (Exception e) { Debug.Log("[TimberbornAI] type index failed: " + e.Message); }
            });

            Debug.Log($"[TimberbornAI] listening on http://127.0.0.1:{Port}/");
        }

        private void OnDestroy()
        {
            try { _listener?.Stop(); } catch { /* shutting down */ }
        }

        private void ListenLoop()
        {
            while (_listener != null && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { return; } // listener stopped

                try { Handle(ctx); }
                catch (Exception e) { Respond(ctx, 500, $"{{\"error\":{Json.Str(e.Message)}}}"); }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
            string body = "";
            if (ctx.Request.HasEntityBody)
            {
                using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = r.ReadToEnd();
            }

            if (path == "/health")
            {
                Respond(ctx, 200, Health());
                return;
            }

            Func<string> work;
            switch (path)
            {
                case "/ping":    work = () => "{\"ok\":true}"; break;
                case "/state":   work = () => StateReader.Snapshot(); break;
                case "/command": work = () => CommandExecutor.Execute(body); break;
                case "/say":     work = () => OverlayPanel.SetNarration(body); break;
                case "/dump":    work = () => TypeDump.Dump(); break;
                default:
                    Respond(ctx, 404, "{\"error\":\"unknown endpoint\"}");
                    return;
            }

            var job = new PendingJob { Work = work };
            Jobs.Enqueue(job);

            if (!job.Done.Wait(TimeSpan.FromSeconds(30)))
            {
                Respond(ctx, 504, "{\"error\":\"game thread did not respond in 30s\",\"health\":" + Health() + "}");
                return;
            }

            if (job.Error != null)
                Respond(ctx, 500, $"{{\"error\":{Json.Str(job.Error)}}}");
            else
                Respond(ctx, 200, job.Result);
        }

        /// <summary>Runs on the HTTP thread. Never touches the game, so it answers even if the game thread is stuck.</summary>
        private static string Health()
        {
            var last = Interlocked.Read(ref _lastTickUtc);
            long sinceMs = last == 0 ? -1 : (long)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - last).TotalMilliseconds;
            return "{\"pump_calls\":" + Interlocked.Read(ref _pumpCalls)
                 + ",\"update_calls\":" + Interlocked.Read(ref _updateCalls)
                 + ",\"queued_jobs\":" + Jobs.Count
                 + ",\"ms_since_last_tick\":" + sinceMs + "}";
        }

        private static void Respond(HttpListenerContext ctx, int status, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private static long _updateCalls;
        private static long _lastTickUtc;

        /// <summary>Backup drain path; only runs while this GameObject is alive and active.</summary>
        private void Update()
        {
            var n = Interlocked.Increment(ref _updateCalls);
            if (n == 1) Debug.Log("[TimberbornAI] Update loop running");
            else if (n % 600 == 0) Debug.Log("[TimberbornAI] Update alive, calls=" + n);
            Interlocked.Exchange(ref _lastTickUtc, DateTime.UtcNow.Ticks);
            DrainJobs();
        }

        // ---- PlayerLoop hook -------------------------------------------------
        // The GameObject above can be disabled or destroyed by the game's own
        // scene handling after startup, which silently stops Update(). A system
        // injected into Unity's PlayerLoop has no such dependency.

        private struct TimberbornAIPump { }

        private static long _pumpCalls;

        internal static void InstallPlayerLoopHook()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type != typeof(UnityEngine.PlayerLoop.Update)) continue;

                var list = (loop.subSystemList[i].subSystemList ?? new PlayerLoopSystem[0]).ToList();
                if (list.Any(x => x.type == typeof(TimberbornAIPump))) return;

                list.Add(new PlayerLoopSystem { type = typeof(TimberbornAIPump), updateDelegate = Pump });
                loop.subSystemList[i].subSystemList = list.ToArray();
                PlayerLoop.SetPlayerLoop(loop);
                Debug.Log("[TimberbornAI] PlayerLoop hook installed");
                return;
            }

            Debug.Log("[TimberbornAI] PlayerLoop hook NOT installed: Update phase not found");
        }

        private static void Pump()
        {
            var n = Interlocked.Increment(ref _pumpCalls);
            if (n == 1) Debug.Log("[TimberbornAI] PlayerLoop pump running");
            else if (n % 600 == 0) Debug.Log("[TimberbornAI] pump alive, calls=" + n);
            Interlocked.Exchange(ref _lastTickUtc, DateTime.UtcNow.Ticks);
            DrainJobs();
        }

        private static void DrainJobs()
        {
            while (Jobs.TryDequeue(out var job))
            {
                var started = DateTime.UtcNow;
                try { job.Result = job.Work(); }
                catch (Exception e) { job.Error = e.ToString(); }
                finally { job.Done.Set(); }

                var ms = (DateTime.UtcNow - started).TotalMilliseconds;
                if (ms > 200) Debug.Log("[TimberbornAI] slow job: " + (int)ms + " ms");
            }
        }

        private void OnGUI() => OverlayPanel.Draw();
    }
}
