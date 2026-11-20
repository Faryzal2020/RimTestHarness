using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Verse;

namespace RimTestHarness
{
    /// <summary>
    /// Loopback-only HTTP/JSON server. POST /{command} with a JSON object body (the arguments), or
    /// POST / with {"cmd":"...","args":{...}}. GET /{command}?a=b also works for simple calls.
    /// Every response is {"ok":true,"result":...} or {"ok":false,"error":"...","type":"..."}.
    /// </summary>
    public static class HarnessServer
    {
        static HttpListener listener;
        static Thread acceptThread;
        public static int DefaultTimeoutMs = 120000;
        public static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = false,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
            IncludeFields = true,
        };

        public static int Start(int preferredPort)
        {
            Exception last = null;
            for (int port = preferredPort; port < preferredPort + 10; port++)
            {
                try
                {
                    var l = new HttpListener();
                    l.Prefixes.Add($"http://127.0.0.1:{port}/");
                    l.Start();
                    listener = l;
                    acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "RimTestHarness.Http" };
                    acceptThread.Start();
                    return port;
                }
                catch (Exception ex) { last = ex; }
            }
            throw new InvalidOperationException("could not bind any port from " + preferredPort, last);
        }

        static void AcceptLoop()
        {
            while (listener != null && listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch (Exception) { if (listener == null || !listener.IsListening) return; continue; }
                Task.Run(() => Handle(ctx));
            }
        }

        static async Task Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            string cmd = req.Url.AbsolutePath.Trim('/');
            JsonObject args = new JsonObject();
            int timeoutMs = DefaultTimeoutMs;
            object payload;
            try
            {
                if (req.HasEntityBody)
                {
                    using var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    var body = await sr.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        var node = JsonNode.Parse(body) as JsonObject ?? new JsonObject();
                        if (string.IsNullOrEmpty(cmd) && node["cmd"] != null)
                        {
                            cmd = node["cmd"].GetValue<string>();
                            args = node["args"] as JsonObject ?? new JsonObject();
                            if (node["timeout_ms"] != null) timeoutMs = node["timeout_ms"].GetValue<int>();
                        }
                        else
                        {
                            args = node;
                        }
                    }
                }
                foreach (string key in req.QueryString.AllKeys)
                {
                    if (key == null) continue;
                    var v = req.QueryString[key];
                    if (key == "timeout_ms" && int.TryParse(v, out var t)) { timeoutMs = t; continue; }
                    if (long.TryParse(v, out var l)) args[key] = l;
                    else if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) args[key] = d;
                    else if (v == "true" || v == "false") args[key] = v == "true";
                    else args[key] = v;
                }
                if (args["timeout_ms"] != null) { timeoutMs = (int)args["timeout_ms"].GetValue<long>(); }

                if (string.IsNullOrEmpty(cmd) || cmd == "help")
                {
                    payload = new { ok = true, result = Commands.Describe() };
                }
                else
                {
                    var task = Commands.Execute(cmd, args);
                    var finished = await Task.WhenAny(task, Task.Delay(timeoutMs));
                    if (finished != task)
                    {
                        payload = new { ok = false, error = $"timeout after {timeoutMs} ms waiting for '{cmd}' (the job keeps running on the main thread)", type = "Timeout" };
                    }
                    else
                    {
                        var result = await task;
                        payload = new { ok = true, result };
                    }
                }
            }
            catch (CommandException ce)
            {
                payload = new { ok = false, error = ce.Message, type = "CommandError" };
            }
            catch (Exception ex)
            {
                var inner = ex is AggregateException ae ? ae.Flatten().InnerException ?? ex : ex;
                payload = new { ok = false, error = inner.Message, type = inner.GetType().Name, stack = inner.StackTrace };
            }

            try
            {
                byte[] bytes;
                try { bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts); }
                catch (Exception ser) { bytes = JsonSerializer.SerializeToUtf8Bytes(new { ok = false, error = "result not serializable: " + ser.Message, type = "SerializationError" }, JsonOpts); }
                res.StatusCode = 200;
                res.ContentType = "application/json";
                res.ContentLength64 = bytes.Length;
                await res.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            }
            catch (Exception) { }
            finally
            {
                try { res.Close(); } catch { }
            }
        }
    }

    public class CommandException : Exception
    {
        public CommandException(string msg) : base(msg) { }
    }
}
