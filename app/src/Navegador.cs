// Navegador invisível (Edge/Chrome em modo headless) controlado pelo
// Chrome DevTools Protocol (CDP) através de um WebSocket.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace PjeCalcBackup
{
    public class ErroNaPagina : Exception
    {
        public ErroNaPagina(string msg) : base(msg) { }
    }

    /// <summary>Cliente mínimo do Chrome DevTools Protocol.</summary>
    public class Cdp : IDisposable
    {
        private readonly ClientWebSocket ws = new ClientWebSocket();
        private int ultimoId;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };

        public static Cdp Conectar(string url)
        {
            var c = new Cdp();
            if (!c.ws.ConnectAsync(new Uri(url), CancellationToken.None).Wait(20000))
                throw new TimeoutException("Não foi possível conectar ao navegador invisível.");
            return c;
        }

        public Dictionary<string, object> Enviar(string metodo, Dictionary<string, object> parametros, int timeoutMs)
        {
            int id = Interlocked.Increment(ref ultimoId);
            var msg = new Dictionary<string, object>();
            msg["id"] = id;
            msg["method"] = metodo;
            msg["params"] = parametros ?? new Dictionary<string, object>();
            byte[] bytes = Encoding.UTF8.GetBytes(Json.Serialize(msg));
            ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait();

            DateTime limite = DateTime.Now.AddMilliseconds(timeoutMs);
            while (true)
            {
                var o = Json.Deserialize<Dictionary<string, object>>(Receber(limite));
                object oid;
                if (o.TryGetValue("id", out oid) && Convert.ToInt32(oid) == id)
                {
                    object erro;
                    if (o.TryGetValue("error", out erro))
                    {
                        var d = erro as Dictionary<string, object>;
                        throw new Exception("Navegador (" + metodo + "): " + (d != null && d.ContainsKey("message") ? d["message"] : erro));
                    }
                    object res;
                    o.TryGetValue("result", out res);
                    return (res as Dictionary<string, object>) ?? new Dictionary<string, object>();
                }
            }
        }

        public Dictionary<string, object> Enviar(string metodo, Dictionary<string, object> parametros)
        {
            return Enviar(metodo, parametros, 120000);
        }

        private string Receber(DateTime limite)
        {
            var buf = new byte[65536];
            var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                TimeSpan resta = limite - DateTime.Now;
                if (resta <= TimeSpan.Zero) throw new TimeoutException("O navegador invisível parou de responder.");
                var t = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                if (!t.Wait(resta)) throw new TimeoutException("O navegador invisível parou de responder.");
                r = t.Result;
                if (r.MessageType == WebSocketMessageType.Close) throw new Exception("O navegador invisível foi encerrado.");
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        /// <summary>Executa JavaScript na página e devolve o valor (já convertido de JSON).</summary>
        public object Js(string expressao, bool aguardarPromise, int timeoutMs)
        {
            var p = new Dictionary<string, object>();
            p["expression"] = expressao;
            p["awaitPromise"] = aguardarPromise;
            p["returnByValue"] = true;
            var r = Enviar("Runtime.evaluate", p, timeoutMs);
            object exc;
            if (r.TryGetValue("exceptionDetails", out exc))
            {
                var d = exc as Dictionary<string, object>;
                string desc = "erro desconhecido";
                if (d != null)
                {
                    object e2;
                    if (d.TryGetValue("exception", out e2) && e2 is Dictionary<string, object> && ((Dictionary<string, object>)e2).ContainsKey("description"))
                        desc = Convert.ToString(((Dictionary<string, object>)e2)["description"]);
                    else if (d.ContainsKey("text"))
                        desc = Convert.ToString(d["text"]);
                }
                throw new ErroNaPagina(desc);
            }
            var res = r.ContainsKey("result") ? r["result"] as Dictionary<string, object> : null;
            object v = null;
            if (res != null) res.TryGetValue("value", out v);
            return v;
        }

        public object Js(string expressao)
        {
            return Js(expressao, false, 60000);
        }

        public void Dispose()
        {
            try { ws.Dispose(); } catch { }
        }
    }

    /// <summary>Abre o Edge (ou Chrome) invisível com um perfil temporário.</summary>
    public class Navegador : IDisposable
    {
        private Process processo;
        private readonly string perfil;
        private readonly int porta;
        public Cdp Pagina { get; private set; }

        public static string Localizar()
        {
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string pf = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidatos =
            {
                Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
            };
            foreach (var c in candidatos) if (File.Exists(c)) return c;
            return null;
        }

        private static int PortaLivre()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        public Navegador()
        {
            string exe = Localizar();
            if (exe == null) throw new Exception("Não encontrei o Microsoft Edge nem o Google Chrome neste computador.");
            porta = PortaLivre();
            perfil = Path.Combine(Path.GetTempPath(), "pjecalc-backup-" + Guid.NewGuid().ToString("N"));

            var psi = new ProcessStartInfo(exe,
                "--headless=new --disable-gpu --no-first-run --no-default-browser-check --disable-extensions " +
                "--disable-background-networking --disable-sync --remote-debugging-port=" + porta +
                " --user-data-dir=\"" + perfil + "\" about:blank");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            processo = Process.Start(psi);

            string wsUrl = null;
            var json = new JavaScriptSerializer();
            for (int i = 0; i < 60 && wsUrl == null; i++)
            {
                Thread.Sleep(500);
                try
                {
                    using (var wc = new WebClient())
                    {
                        var lista = json.Deserialize<ArrayList>(wc.DownloadString("http://127.0.0.1:" + porta + "/json/list"));
                        foreach (Dictionary<string, object> alvo in lista)
                            if (Convert.ToString(alvo["type"]) == "page") { wsUrl = Convert.ToString(alvo["webSocketDebuggerUrl"]); break; }
                    }
                }
                catch { }
            }
            if (wsUrl == null) { Dispose(); throw new Exception("O navegador invisível não iniciou."); }
            Pagina = Cdp.Conectar(wsUrl);
        }

        /// <summary>Coloca um arquivo do disco num campo &lt;input type="file"&gt; (como se o usuário o tivesse escolhido).</summary>
        public void DefinirArquivo(string expressaoElemento, string caminho)
        {
            var p = new Dictionary<string, object>();
            p["expression"] = expressaoElemento;
            p["returnByValue"] = false;
            var r = Pagina.Enviar("Runtime.evaluate", p);
            var res = r.ContainsKey("result") ? r["result"] as Dictionary<string, object> : null;
            if (res == null || !res.ContainsKey("objectId")) throw new Exception("campo de arquivo não encontrado na tela");
            var q = new Dictionary<string, object>();
            q["objectId"] = res["objectId"];
            q["files"] = new[] { caminho };
            Pagina.Enviar("DOM.setFileInputFiles", q);
        }

        public void Ir(string url)
        {
            var p = new Dictionary<string, object>();
            p["url"] = url;
            Pagina.Enviar("Page.navigate", p);
        }

        public void Dispose()
        {
            try
            {
                using (var wc = new WebClient())
                {
                    var v = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(wc.DownloadString("http://127.0.0.1:" + porta + "/json/version"));
                    using (var b = Cdp.Conectar(Convert.ToString(v["webSocketDebuggerUrl"])))
                        b.Enviar("Browser.close", null, 5000);
                }
            }
            catch { }
            if (Pagina != null) Pagina.Dispose();
            try
            {
                if (processo != null && !processo.WaitForExit(8000))
                {
                    var k = new ProcessStartInfo("taskkill", "/PID " + processo.Id + " /T /F");
                    k.CreateNoWindow = true; k.UseShellExecute = false;
                    var pk = Process.Start(k); pk.WaitForExit(5000);
                }
            }
            catch { }
            for (int i = 0; i < 10; i++)
            {
                try { if (Directory.Exists(perfil)) Directory.Delete(perfil, true); break; }
                catch { Thread.Sleep(500); }
            }
        }
    }
}
