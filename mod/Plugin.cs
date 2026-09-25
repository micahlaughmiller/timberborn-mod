using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace TimberbornAI
{
    /// <summary>
    /// Hosts a small local HTTP API inside the game process so an external
    /// agent can read world state and enqueue commands.
    ///
    /// Threading rule: HttpListener callbacks run on worker threads, but all
    /// Unity/game access MUST happen on the main thread. Requests therefore
    /// enqueue work and block on a handle until Update() drains the queue.
    /// </summary>
    [BepInPlugin("solutions.eo.timberborn.ai", "Timberborn AI Director", "0.1.0")]
    public class Plugin : BaseUnityPlugin
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
            new Harmony("solutions.eo.timberborn.ai").PatchAll();

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();

            _listenerThread = new Thread(ListenLoop) { IsBackground = true };
            _listenerThread.Start();

            Logger.LogInfo($"Timberborn AI Director listening on http://127.0.0.1:{Port}/");
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

            Func<string> work;
            switch (path)
            {
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

            if (!job.Done.Wait(TimeSpan.FromSeconds(10)))
            {
                Respond(ctx, 504, "{\"error\":\"game thread did not respond in 10s\"}");
                return;
            }

            if (job.Error != null)
                Respond(ctx, 500, $"{{\"error\":{Json.Str(job.Error)}}}");
            else
                Respond(ctx, 200, job.Result);
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

        /// <summary>Drains queued work on the Unity main thread.</summary>
        private void Update()
        {
            while (Jobs.TryDequeue(out var job))
            {
                try { job.Result = job.Work(); }
                catch (Exception e) { job.Error = e.ToString(); }
                finally { job.Done.Set(); }
            }
        }

        private void OnGUI() => OverlayPanel.Draw();
    }
}
