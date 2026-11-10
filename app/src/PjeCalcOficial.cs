// Versão nova do PJe-Calc no site do TRT da 8ª Região: consulta a página oficial de instalação ao abrir o programa,
// avisa numa faixa no topo e baixa o instalador oficial, conferindo o MD5 publicado na própria página.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PjeCalcBackup
{
    public static class PjeCalcOficial
    {
        public const string Pagina = "https://www.trt8.jus.br/pjecalc-cidadao/instalando-o-pje-calc-cidadao";
        private const string HostOficial = "www.trt8.jus.br";

        public class Publicada
        {
            public string Versao;     // ex.: 2.16.0
            public string Url;        // instalador para este Windows (64 ou 32 bits)
            public string Md5;        // checksum publicado na página
            public string Arquivo;    // nome do arquivo (ex.: pjecalc-2.16.0-instalador-x64.exe)
            public long Tamanho;      // bytes (0 = desconhecido)
        }

        /// <summary>Lê a página oficial de instalação. Devolve null se a página mudou e não deu para entender.</summary>
        public static Publicada Consultar()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string html;
            var req = (HttpWebRequest)WebRequest.Create(Pagina);
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PJeCalcBackup/" + Atualizacao.Atual;
            req.Timeout = 10000;
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), System.Text.Encoding.UTF8))
                html = r.ReadToEnd();
            return Interpretar(html, Environment.Is64BitOperatingSystem);
        }

        /// <summary>Extrai versão, link do instalador e MD5 do HTML da página (separado para dar para testar).</summary>
        public static Publicada Interpretar(string html, bool x64)
        {
            string bits = x64 ? "x64" : "x32";
            var m = Regex.Match(html, "href=\"(?<url>[^\"]*?pjecalc-(?<v>\\d+(?:\\.\\d+)+)-instalador-" + bits + "\\.exe)\"[\\s\\S]{0,400}?MD5\\)?\\s*:\\s*(?<md5>[0-9A-Fa-f]{32})", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            Uri url;
            if (!Uri.TryCreate(new Uri(Pagina), WebUtility.HtmlDecode(m.Groups["url"].Value), out url)) return null;
            // só aceita o instalador do próprio site do TRT8, por https
            if (url.Scheme != Uri.UriSchemeHttps || !string.Equals(url.Host, HostOficial, StringComparison.OrdinalIgnoreCase)) return null;
            string versao = m.Groups["v"].Value;
            var mv = Regex.Match(html, "Vers[ãa]o:\\s*(?:<[^>]+>\\s*)*(\\d+(?:\\.\\d+)+)", RegexOptions.IgnoreCase);
            if (mv.Success && mv.Groups[1].Value != versao) return null; // versão do texto e do arquivo não batem: melhor não arriscar
            return new Publicada { Versao = versao, Url = url.AbsoluteUri, Md5 = m.Groups["md5"].Value.ToUpperInvariant(), Arquivo = Path.GetFileName(url.LocalPath) };
        }

        public static int Comparar(string a, string b)
        {
            Version va, vb;
            if (!Version.TryParse(a ?? "", out va)) va = new Version(0, 0);
            if (!Version.TryParse(b ?? "", out vb)) vb = new Version(0, 0);
            return va.CompareTo(vb);
        }

        public static string Md5(string arquivo)
        {
            using (var s = File.OpenRead(arquivo))
            using (var h = System.Security.Cryptography.MD5.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "");
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr caminho);

        /// <summary>Pasta Downloads do usuário (mesmo se ela foi movida para outro disco).</summary>
        public static string PastaDownloads()
        {
            try
            {
                IntPtr p;
                if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out p) == 0)
                {
                    string s = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p);
                    if (!string.IsNullOrEmpty(s) && Directory.Exists(s)) return s;
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    /// <summary>Faixa "Saiu o PJe-Calc X" e o download do instalador oficial.</summary>
    public class AvisoPjeCalcOficial
    {
        private readonly Window janela;
        private readonly Config config;
        private readonly Func<bool> ocupado;
        private bool verificado;
        private PjeCalcOficial.Publicada publicada;
        private WebClient baixando;

        public AvisoPjeCalcOficial(Window janela, Config config, Func<bool> ocupado)
        {
            this.janela = janela; this.config = config; this.ocupado = ocupado;
            A<Button>("btnSitePjeCalc").Click += (s, e) => Abrir(PjeCalcOficial.Pagina);
            A<Button>("btnFecharPjeCalc").Click += (s, e) =>
            {
                if (baixando != null) { baixando.CancelAsync(); return; }
                A<Border>("avisoPjeCalc").Visibility = Visibility.Collapsed;
                if (publicada != null) { config.PjeCalcAvisoIgnorado = publicada.Versao; config.Salvar(); }
            };
            A<Button>("btnBaixarPjeCalc").Click += (s, e) => { if (baixando != null) baixando.CancelAsync(); else Baixar(); };
        }

        private T A<T>(string nome) where T : class { return janela.FindName(nome) as T; }
        private void NaTela(Action a) { janela.Dispatcher.BeginInvoke(a); }
        private static void Abrir(string alvo) { try { Process.Start(new ProcessStartInfo(alvo) { UseShellExecute = true }); } catch { } }

        /// <summary>Consulta o site do TRT8 uma vez por abertura do programa, depois que as instalações foram encontradas.</summary>
        public void Verificar(List<string> versoesInstaladas)
        {
            if (verificado || !config.VerificarPjeCalc) return;
            verificado = true;
            string maior = versoesInstaladas.OrderByDescending(v => v, Comparer<string>.Create(PjeCalcOficial.Comparar)).FirstOrDefault();
            Task.Factory.StartNew(() =>
            {
                PjeCalcOficial.Publicada p = null;
                try { p = PjeCalcOficial.Consultar(); } catch { }
                if (p == null) return; // sem internet ou página diferente: não incomoda
                if (maior != null && PjeCalcOficial.Comparar(p.Versao, maior) <= 0) return;
                if (p.Versao == config.PjeCalcAvisoIgnorado) return;
                NaTela(() =>
                {
                    publicada = p;
                    A<TextBlock>("txtAvisoPjeCalc").Text = maior == null
                        ? "O PJe-Calc " + p.Versao + " está disponível no site do TRT da 8ª Região."
                        : "Saiu o PJe-Calc " + p.Versao + " no site do TRT da 8ª Região. Neste computador, a versão mais nova é a " + maior + ".";
                    A<Border>("avisoPjeCalc").Visibility = Visibility.Visible;
                });
            });
        }

        private bool Perguntar(TipoDialogo tipo, string titulo, string msg, string sim, string nao) { return Dialogo.Mostrar(janela, tipo, titulo, msg, sim, nao); }
        private void Avisar(TipoDialogo tipo, string titulo, string msg) { Dialogo.Mostrar(janela, tipo, titulo, msg, "OK", null); }

        private void Baixar()
        {
            var p = publicada;
            if (p == null) return;
            string pasta = PjeCalcOficial.PastaDownloads();
            string destino = Path.Combine(pasta, p.Arquivo);

            // já baixado antes e conferido: só abre
            if (File.Exists(destino))
            {
                bool igual = false;
                try { igual = PjeCalcOficial.Md5(destino) == p.Md5; } catch { }
                if (igual) { OferecerInstalar(destino, p); return; }
            }

            if (!Perguntar(TipoDialogo.Pergunta, "Baixar o PJe-Calc " + p.Versao + "?",
                "O instalador oficial vem direto do site do TRT da 8ª Região (www.trt8.jus.br) para a sua pasta Downloads. É um arquivo grande, de algumas centenas de MB.\n\n" +
                "Depois de baixar, o programa confere o código MD5 publicado na página do TRT8 e só então oferece abrir o instalador.\n\n" +
                "A versão atual do PJe-Calc não é alterada.", "Baixar", "Agora não")) return;

            long? livre = Backup.EspacoLivre(pasta);
            if (livre.HasValue && livre.Value < 600L * 1024 * 1024)
            {
                Avisar(TipoDialogo.Aviso, "Pouco espaço em disco", "Há só " + Backup.Tamanho(livre.Value) + " livres em " + pasta + ". Libere espaço e tente de novo.");
                return;
            }

            string parcial = destino + ".baixando";
            var txt = A<TextBlock>("txtAvisoPjeCalc");
            var barra = A<ProgressBar>("barraPjeCalc");
            var btn = A<Button>("btnBaixarPjeCalc");
            string textoAntes = txt.Text;
            barra.Value = 0; barra.IsIndeterminate = true; barra.Visibility = Visibility.Visible;
            btn.Content = "Cancelar";
            A<Button>("btnFecharPjeCalc").Visibility = Visibility.Collapsed;
            txt.Text = "Baixando o PJe-Calc " + p.Versao + " do site do TRT8…";

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var w = new WebClient();
            w.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PJeCalcBackup/" + Atualizacao.Atual;
            baixando = w;
            w.DownloadProgressChanged += (s, e) =>
            {
                if (e.TotalBytesToReceive > 0)
                {
                    barra.IsIndeterminate = false;
                    barra.Value = (double)e.BytesReceived / e.TotalBytesToReceive;
                    txt.Text = "Baixando o PJe-Calc " + p.Versao + " do site do TRT8… " + (int)(barra.Value * 100) + "% (" +
                               Backup.Tamanho(e.BytesReceived) + " de " + Backup.Tamanho(e.TotalBytesToReceive) + ")";
                }
                else txt.Text = "Baixando o PJe-Calc " + p.Versao + " do site do TRT8… " + Backup.Tamanho(e.BytesReceived);
            };
            w.DownloadFileCompleted += (s, e) =>
            {
                baixando = null; w.Dispose();
                Action restaurar = () =>
                {
                    barra.Visibility = Visibility.Collapsed;
                    btn.Content = "Baixar e instalar"; btn.IsEnabled = true;
                    A<Button>("btnFecharPjeCalc").Visibility = Visibility.Visible;
                    txt.Text = textoAntes;
                };
                if (e.Cancelled || e.Error != null)
                {
                    try { if (File.Exists(parcial)) File.Delete(parcial); } catch { }
                    restaurar();
                    if (e.Error != null && Perguntar(TipoDialogo.Erro, "Não consegui baixar",
                        "O download do PJe-Calc " + p.Versao + " falhou: " + (e.Error.InnerException ?? e.Error).Message + "\n\nVocê pode baixar pelo site do TRT8.", "Abrir o site", "Fechar"))
                        Abrir(PjeCalcOficial.Pagina);
                    return;
                }
                // confere o MD5 publicado pelo TRT8 antes de liberar o arquivo
                txt.Text = "Conferindo o arquivo baixado (MD5)…";
                barra.IsIndeterminate = true; btn.IsEnabled = false;
                Task.Factory.StartNew(() =>
                {
                    string erro = null;
                    try
                    {
                        string md5 = PjeCalcOficial.Md5(parcial);
                        if (md5 != p.Md5) erro = "O arquivo baixado não confere com o código MD5 publicado pelo TRT8 (esperado " + p.Md5 + ", veio " + md5 + "). Ele foi apagado.";
                        else
                        {
                            if (File.Exists(destino)) File.Delete(destino);
                            File.Move(parcial, destino);
                        }
                    }
                    catch (Exception ex) { erro = ex.Message; }
                    if (erro != null) { try { if (File.Exists(parcial)) File.Delete(parcial); } catch { } }
                    NaTela(() =>
                    {
                        restaurar();
                        if (erro != null)
                        {
                            if (Perguntar(TipoDialogo.Erro, "Download não conferido", erro + "\n\nVocê pode baixar pelo site do TRT8.", "Abrir o site", "Fechar"))
                                Abrir(PjeCalcOficial.Pagina);
                            return;
                        }
                        OferecerInstalar(destino, p);
                    });
                });
            };
            try { w.DownloadFileAsync(new Uri(p.Url), parcial); }
            catch (Exception ex)
            {
                baixando = null; w.Dispose();
                barra.Visibility = Visibility.Collapsed; btn.Content = "Baixar e instalar";
                A<Button>("btnFecharPjeCalc").Visibility = Visibility.Visible; txt.Text = textoAntes;
                Avisar(TipoDialogo.Erro, "Não consegui baixar", ex.Message);
            }
        }

        private void OferecerInstalar(string arquivo, PjeCalcOficial.Publicada p)
        {
            A<TextBlock>("txtAvisoPjeCalc").Text = "O instalador do PJe-Calc " + p.Versao + " foi baixado e conferido: " + arquivo;
            if (ocupado())
            {
                Avisar(TipoDialogo.Info, "Instalador baixado", "O instalador do PJe-Calc " + p.Versao + " está em:\n" + arquivo + "\n\nTermine a operação em andamento e depois abra o instalador.");
                return;
            }
            bool abrir = Perguntar(TipoDialogo.Sucesso, "PJe-Calc " + p.Versao + " baixado",
                "O instalador oficial foi baixado e conferido: o código MD5 é igual ao publicado pelo TRT da 8ª Região.\n\n" +
                "Arquivo: " + arquivo + "\n\n" +
                "Antes de instalar, salve e feche o PJe-Calc. Siga o instalador normalmente; a versão atual continua instalada, com os cálculos dela.\n\n" +
                "Depois, abra a versão nova uma vez pelo atalho oficial e feche. Ao abrir o PJe-Calc Backup de novo, ele oferece levar os seus cálculos para a versão nova.",
                "Abrir o instalador", "Mostrar na pasta");
            if (abrir)
            {
                try { Process.Start(new ProcessStartInfo(arquivo) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(arquivo) }); }
                catch (Win32Exception) { } // o usuário recusou a permissão do Windows
                catch (Exception ex) { Avisar(TipoDialogo.Erro, "Não consegui abrir o instalador", ex.Message); }
            }
            else
            {
                try { Process.Start("explorer.exe", "/select,\"" + arquivo + "\""); } catch { }
            }
        }
    }
}
