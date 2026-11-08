// Sons do Windows, notificações do Windows e verificação de nova versão no GitHub.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Media;
using System.Net;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PjeCalcBackup
{
    public static class Avisos
    {
        /// <summary>Som do próprio Windows (respeita o volume e o "não perturbe").</summary>
        public static void Som(bool sucesso)
        {
            try { if (sucesso) SystemSounds.Asterisk.Play(); else SystemSounds.Hand.Play(); } catch { }
        }

        /// <summary>Notificação do Windows (aparece na central de notificações).</summary>
        public static void Notificar(string titulo, string texto, bool sucesso)
        {
            try
            {
                var n = new NotifyIcon();
                try { n.Icon = Icon.ExtractAssociatedIcon(Assembly.GetEntryAssembly().Location); } catch { n.Icon = SystemIcons.Information; }
                n.Text = "PJe-Calc Backup";
                n.Visible = true;
                n.ShowBalloonTip(10000, titulo, texto, sucesso ? ToolTipIcon.Info : ToolTipIcon.Warning);
                var t = new Timer { Interval = 15000 };
                t.Tick += (s, e) => { t.Stop(); t.Dispose(); n.Visible = false; n.Dispose(); };
                t.Start();
            }
            catch { }
        }
    }

    public static class Atualizacao
    {
        public const string Repositorio = "lfeliipeadv-stack/pjecalc-backup";
        public static string UrlProjeto { get { return "https://github.com/" + Repositorio; } }

        public static Version Atual
        {
            get { var v = Assembly.GetExecutingAssembly().GetName().Version; return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }
        }

        public class Novidade { public string Versao; public string Url; }

        /// <summary>Consulta a última versão publicada no GitHub. Devolve null se já está na mais recente.</summary>
        public static Novidade Verificar()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repositorio + "/releases/latest");
            req.UserAgent = "PJeCalcBackup/" + Atual;
            req.Accept = "application/vnd.github+json";
            req.Timeout = 8000;
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream()))
            {
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(r.ReadToEnd());
                string tag = Convert.ToString(d["tag_name"]).TrimStart('v', 'V');
                Version v;
                if (Version.TryParse(tag, out v))
                {
                    var n = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
                    if (n > Atual) return new Novidade { Versao = n.ToString(), Url = Convert.ToString(d["html_url"]) };
                }
            }
            return null;
        }
    }
}
