using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace StationeersPlc
{
    /// <summary>
    /// Pop-out workstation: a web page on http://127.0.0.1:15882/ with the ST editor, RUN/STOP and the live I/O list.
    /// Put it on a second monitor. Loopback only. The HTTP thread serves cached strings built on the Unity main thread
    /// and never touches game objects.
    /// </summary>
    /// <remarks>
    /// Security model: the listener is bound only to the 127.0.0.1 prefix (loopback), every POST must carry the custom
    /// header X-Plc: 1 (browsers cannot add it cross-site without a preflight, which we never approve), and no
    /// CORS headers are sent. Request bodies above a size limit are rejected (the check runs after the body is read; see CODE_REVIEW.md). Anything shown in the page that originates in the game
    /// (device names) is escaped in index.html before it is put into the DOM.
    /// </remarks>
    internal static class Web
    {
        private static HttpListener _http;
        private static Thread _thread;
        private static volatile bool _running;
        private static volatile string _stateJson = "{}";
        private static volatile string _pointsJson = "[]";
        private static float _nextState, _nextPoints;
        public static int Port;
        public static string LadderPath;

        public static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append(' '); break;
                    default: if (c >= ' ') sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        public static void Start(int port)
        {
            Port = port;
            try { LadderPath = Path.ChangeExtension(StRuntime.ProgramPath, ".ladder.json"); } catch { }
            _http = new HttpListener();
            _http.Prefixes.Add("http://127.0.0.1:" + port + "/");
            _http.Start();
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "PLC-web" };
            _thread.Start();
        }

        public static void Stop()
        {
            _running = false;
            try { _http?.Stop(); } catch { }
        }

        /// <summary>Main thread, every frame.</summary>
        public static void Tick(float now)
        {
            if (!_running) return;
            if (now >= _nextState)
            {
                _nextState = now + 0.2f;
                try { _stateJson = StRuntime.StateJson(); } catch { }
            }
            if (now >= _nextPoints)
            {
                _nextPoints = now + 1.0f;
                try { _pointsJson = BuildPoints(); } catch { }
            }
        }

        private static string BuildPoints()
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var a in IoMap.Snapshot())
            {
                if (!first) sb.Append(',');
                first = false;
                string raw, eng;
                switch (a.Table)
                {
                    case Table.DiscreteInput: raw = eng = PointStore.Di[a.Address] ? "1" : "0"; break;
                    case Table.Coil: raw = eng = PointStore.Coil[a.Address] ? "1" : "0"; break;
                    case Table.InputRegister:
                        raw = PointStore.Ir[a.Address].ToString(inv);
                        eng = (PointStore.Ir[a.Address] / (a.Scale == 0 ? 1 : a.Scale)).ToString("0.###", inv); break;
                    default:
                        raw = PointStore.Hr[a.Address].ToString(inv);
                        eng = (PointStore.Hr[a.Address] / (a.Scale == 0 ? 1 : a.Scale)).ToString("0.###", inv); break;
                }
                sb.Append("{\"t\":\"").Append(IoMap.TableName(a.Table)).Append("\",\"a\":").Append(a.Address.ToString(inv))
                  .Append(",\"tag\":\"").Append(Esc(a.Tag)).Append("\",\"dev\":\"").Append(Esc(a.DeviceType))
                  .Append("\",\"sig\":\"").Append(Esc(a.Signal)).Append("\",\"raw\":").Append(raw)
                  .Append(",\"eng\":\"").Append(eng).Append("\",\"act\":").Append(a.Active ? "true" : "false").Append('}');
            }
            return sb.Append(']').ToString();
        }

        private static void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _http.GetContext(); }
                catch { break; }
                try { Handle(ctx); }
                catch { try { ctx.Response.Abort(); } catch { } }
            }
        }

        private static void Send(HttpListenerContext c, string body, string type, int status = 200)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            c.Response.StatusCode = status;
            c.Response.ContentType = type + "; charset=utf-8";
            c.Response.AddHeader("Cache-Control", "no-store");
            c.Response.ContentLength64 = bytes.Length;
            c.Response.OutputStream.Write(bytes, 0, bytes.Length);
            c.Response.OutputStream.Close();
        }

        private static void Handle(HttpListenerContext c)
        {
            string path = c.Request.Url.AbsolutePath;
            bool post = c.Request.HttpMethod == "POST";

            if (post && c.Request.Headers["X-Plc"] != "1") { Send(c, "missing X-Plc header", "text/plain", 403); return; }

            if (!post && path == "/") { Send(c, Page, "text/html"); return; }
            if (!post && path == "/api/state") { Send(c, _stateJson, "application/json"); return; }
            if (!post && path == "/api/points") { Send(c, _pointsJson, "application/json"); return; }
            if (!post && path == "/api/program") { Send(c, StRuntime.Source ?? "", "text/plain"); return; }

            if (post && path == "/api/program")
            {
                string body;
                using (var r = new StreamReader(c.Request.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
                if (body.Length > 200000) { Send(c, "program too large", "text/plain", 413); return; }
                StRuntime.Submit(body);
                Send(c, "queued", "text/plain");
                return;
            }
            if (!post && path == "/api/ladder")
            {
                string txt = "";
                try { if (LadderPath != null && File.Exists(LadderPath)) txt = File.ReadAllText(LadderPath); } catch { }
                Send(c, txt, "application/json");
                return;
            }
            if (post && path == "/api/ladder")
            {
                string txt;
                using (var r = new StreamReader(c.Request.InputStream, Encoding.UTF8)) txt = r.ReadToEnd();
                if (txt.Length > 500000) { Send(c, "too large", "text/plain", 413); return; }
                try { File.WriteAllText(LadderPath, txt, new UTF8Encoding(false)); Send(c, "saved", "text/plain"); }
                catch (Exception e) { Send(c, e.Message, "text/plain", 500); }
                return;
            }
            if (post && path == "/api/check")
            {
                string src;
                using (var r = new StreamReader(c.Request.InputStream, Encoding.UTF8)) src = r.ReadToEnd();
                Send(c, StRuntime.Check(src), "application/json");
                return;
            }
            if (post && path == "/api/force")
            {
                string name = c.Request.QueryString["name"] ?? "";
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$")) { Send(c, "bad name", "text/plain", 400); return; }
                if (c.Request.QueryString["release"] == "1") StRuntime.Release(name);
                else
                {
                    double val;
                    if (!double.TryParse(c.Request.QueryString["value"], NumberStyles.Float, CultureInfo.InvariantCulture, out val)) { Send(c, "bad value", "text/plain", 400); return; }
                    StRuntime.Force(name, val);
                }
                Send(c, "queued", "text/plain");
                return;
            }
            if (post && path == "/api/write")
            {
                // Manual on/off switch from the I/O list: lets a student see a device react in the game.
                // Coils only. Same rules as an outside Modbus write: refused while the program runs
                // (it owns the outputs) or when AllowWrites is false.
                int addr;
                if (!int.TryParse(c.Request.QueryString["addr"], out addr) || addr < 0 || addr >= PointStore.Size) { Send(c, "bad address", "text/plain", 400); return; }
                string v = c.Request.QueryString["value"];
                if (v != "0" && v != "1") { Send(c, "bad value", "text/plain", 400); return; }
                if (!Plugin.WritesAllowed) { Send(c, "Writes are turned off (AllowWrites=false).", "text/plain", 409); return; }
                if (StRuntime.Running && StRuntime.BlockExternalWrites) { Send(c, "The program is running and owns the outputs. Press Stop first.", "text/plain", 409); return; }
                if (!PointStore.CoilAssigned[addr]) { Send(c, "That address is not an assigned switch.", "text/plain", 409); return; }
                bool on = v == "1";
                PointStore.Coil[addr] = on;
                Plugin.PendingWrites.Enqueue(new WriteRequest { Table = Table.Coil, Address = addr, Value = on ? 1 : 0 });
                Send(c, "queued", "text/plain");
                return;
            }
            if (post && path == "/api/prune")
            {
                IoMap.RequestPrune();
                Send(c, "queued", "text/plain");
                return;
            }
            if (post && path == "/api/mode")
            {
                StRuntime.RequestMode(c.Request.QueryString["run"] == "1");
                Send(c, "queued", "text/plain");
                return;
            }
            Send(c, "not found", "text/plain", 404);
        }

        private static string _page;
        private static string Page
        {
            get
            {
                if (_page != null) return _page;
                using (var st = typeof(Web).Assembly.GetManifestResourceStream("index.html"))
                {
                    if (st == null) return "<h1>index.html is not embedded in the DLL</h1>";
                    using (var r = new StreamReader(st, Encoding.UTF8)) _page = r.ReadToEnd();
                }
                return _page;
            }
        }
    }
}
