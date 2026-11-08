// Sons do Windows, notificações do Windows e verificação de nova versão no GitHub.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
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

        /// <summary>Para o modo console (sem janela): mostra a notificação e espera alguns segundos antes de seguir.</summary>
        public static void NotificarEsperando(string titulo, string texto, bool sucesso)
        {
            try
            {
                using (var n = new NotifyIcon())
                {
                    try { n.Icon = Icon.ExtractAssociatedIcon(Assembly.GetEntryAssembly().Location); } catch { n.Icon = SystemIcons.Information; }
                    n.Text = "PJe-Calc Backup";
                    n.Visible = true;
                    n.ShowBalloonTip(10000, titulo, texto, sucesso ? ToolTipIcon.Info : ToolTipIcon.Warning);
                    var fim = DateTime.Now.AddSeconds(6);
                    while (DateTime.Now < fim) { Application.DoEvents(); System.Threading.Thread.Sleep(100); }
                    n.Visible = false;
                }
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

        public class Novidade
        {
            public string Versao; public string Url;
            /// <summary>Arquivos publicados (nome → endereço de download).</summary>
            public Dictionary<string, string> Arquivos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static string PastaDoPrograma { get { return Path.GetDirectoryName(Assembly.GetEntryAssembly().Location); } }

        /// <summary>Apaga as cópias ".old" deixadas pela última atualização.</summary>
        public static void LimparAntigos()
        {
            try { foreach (var f in Directory.GetFiles(PastaDoPrograma, "*.old")) { try { File.Delete(f); } catch { } } } catch { }
        }

        private static WebClient Cliente()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var w = new WebClient();
            w.Headers[HttpRequestHeader.UserAgent] = "PJeCalcBackup/" + Atual;
            return w;
        }

        private static string Hash(string arquivo)
        {
            using (var s = File.OpenRead(arquivo))
            using (var h = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "");
        }

        /// <summary>Baixa a versão nova do GitHub, confere o SHA-256 de cada arquivo e troca os arquivos
        /// desta pasta (os atuais ficam como ".old" até a próxima abertura). Devolve o caminho do programa novo.</summary>
        public static string Instalar(Novidade n, Action<string> etapa)
        {
            string dir = PastaDoPrograma;
            string teste = Path.Combine(dir, ".teste-escrita");
            try { File.WriteAllText(teste, ""); File.Delete(teste); }
            catch { throw new Exception("Sem permissão para gravar em " + dir + ". Baixe a versão nova pela página do projeto."); }

            string tmp = Path.Combine(Path.GetTempPath(), "PJeCalcBackup-atualizacao-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            var trocados = new List<Tuple<string, string>>(); // (destino, cópia .old)
            try
            {
                string urlSha;
                if (!n.Arquivos.TryGetValue("SHA256.txt", out urlSha)) throw new Exception("A versão publicada não tem a lista de SHA-256; atualize pela página do projeto.");
                var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using (var w = Cliente())
                {
                    etapa("Baixando a lista de verificação…");
                    foreach (var l in w.DownloadString(urlSha).Split('\n'))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(l, "^\\s*(\\S+)\\s+SHA-256:\\s*([0-9A-Fa-f]{64})");
                        if (m.Success) hashes[m.Groups[1].Value] = m.Groups[2].Value.ToUpperInvariant();
                    }

                    // pacote .zip (programa + versão para o CMD + .bat) ou, na falta dele, só o programa
                    string nomeZip = n.Arquivos.Keys.FirstOrDefault(k => k.StartsWith("PJeCalcBackup", StringComparison.OrdinalIgnoreCase) && k.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                    string pacote = Path.Combine(tmp, "pacote");
                    Directory.CreateDirectory(pacote);
                    var baixar = nomeZip != null ? new[] { nomeZip } : new[] { "PJeCalcBackup.exe", "PJeCalcBackup-Console.exe" }.Where(n.Arquivos.ContainsKey).ToArray();
                    foreach (var nome in baixar)
                    {
                        etapa("Baixando " + nome + "…");
                        string local = Path.Combine(tmp, nome);
                        w.DownloadFile(n.Arquivos[nome], local);
                        string esperado;
                        if (!hashes.TryGetValue(nome, out esperado)) throw new Exception("Sem SHA-256 publicado para " + nome + ".");
                        if (Hash(local) != esperado) throw new Exception("O arquivo baixado (" + nome + ") não confere com o SHA-256 publicado. Nada foi alterado.");
                        if (nome.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) System.IO.Compression.ZipFile.ExtractToDirectory(local, pacote);
                        else File.Copy(local, Path.Combine(pacote, nome));
                    }
                    foreach (var f in Directory.GetFiles(pacote))
                    {
                        string esperado;
                        if (hashes.TryGetValue(Path.GetFileName(f), out esperado) && Hash(f) != esperado)
                            throw new Exception(Path.GetFileName(f) + " não confere com o SHA-256 publicado. Nada foi alterado.");
                    }
                    string novoGui = Path.Combine(pacote, "PJeCalcBackup.exe");
                    if (!File.Exists(novoGui)) throw new Exception("O pacote baixado não tem o PJeCalcBackup.exe.");

                    // troca: o arquivo atual vira ".old" (o Windows permite renomear um programa aberto)
                    etapa("Instalando a versão " + n.Versao + "…");
                    string meuNome = Path.GetFileName(Assembly.GetEntryAssembly().Location);
                    foreach (var f in Directory.GetFiles(pacote))
                    {
                        string nome = Path.GetFileName(f);
                        bool ehGui = nome.Equals("PJeCalcBackup.exe", StringComparison.OrdinalIgnoreCase);
                        // o programa de janela é trocado sempre; os demais só se já estavam nesta pasta ou se é o console
                        string destino = Path.Combine(dir, ehGui && !meuNome.Equals("PJeCalcBackup-Console.exe", StringComparison.OrdinalIgnoreCase) ? meuNome : nome);
                        if (!ehGui && !nome.Equals("PJeCalcBackup-Console.exe", StringComparison.OrdinalIgnoreCase) && !File.Exists(destino)) continue;
                        string antigo = null;
                        if (File.Exists(destino))
                        {
                            antigo = destino + ".old";
                            if (File.Exists(antigo)) { try { File.Delete(antigo); } catch { antigo = destino + "." + DateTime.Now.Ticks + ".old"; } }
                            File.Move(destino, antigo);
                        }
                        trocados.Add(Tuple.Create(destino, antigo));
                        File.Copy(f, destino);
                    }
                    return Path.Combine(dir, meuNome.Equals("PJeCalcBackup-Console.exe", StringComparison.OrdinalIgnoreCase) ? "PJeCalcBackup.exe" : meuNome);
                }
            }
            catch
            {
                // desfaz o que já foi trocado
                foreach (var t in trocados)
                {
                    try { if (File.Exists(t.Item1)) File.Delete(t.Item1); if (t.Item2 != null) File.Move(t.Item2, t.Item1); } catch { }
                }
                throw;
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

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
                    if (n > Atual)
                    {
                        var nov = new Novidade { Versao = n.ToString(), Url = Convert.ToString(d["html_url"]) };
                        object assets;
                        if (d.TryGetValue("assets", out assets) && assets is System.Collections.ArrayList)
                            foreach (var a in (System.Collections.ArrayList)assets)
                            {
                                var da = a as Dictionary<string, object>;
                                if (da != null && da.ContainsKey("name") && da.ContainsKey("browser_download_url"))
                                    nov.Arquivos[Convert.ToString(da["name"])] = Convert.ToString(da["browser_download_url"]);
                            }
                        return nov;
                    }
                }
            }
            return null;
        }
    }
}
