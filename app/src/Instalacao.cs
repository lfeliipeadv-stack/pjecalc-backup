// Uma instalação do PJe-Calc Cidadão: localizar, verificar estado,
// ligar/desligar o Tomcat com segurança e copiar o banco de dados.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PjeCalcBackup
{
    public enum EstadoInstalacao { Fechada, Aberta, SemBanco }

    public class Instalacao
    {
        public string Pasta { get; private set; }
        public string Versao { get; private set; }
        public int PortaHttp { get; private set; }
        public int PortaDesligar { get; private set; }
        public string ComandoDesligar { get; private set; }
        public string UsuarioBanco { get; private set; }
        public string SenhaBanco { get; private set; }

        public string UrlBase { get { return "http://127.0.0.1:" + PortaHttp + "/pjecalc"; } }
        public string ArquivoBanco { get { return Path.Combine(Pasta, @".dados\pjecalc.h2.db"); } }
        public string ArquivoTrava { get { return Path.Combine(Pasta, @".dados\pjecalc.lock.db"); } }
        public string JavaExe { get { return Path.Combine(Pasta, @"bin\jre\bin\java.exe"); } }

        private Process tomcat;
        private readonly StringBuilder logTomcat = new StringBuilder();

        // --------------------------------------------------------------- busca

        public static bool PareceInstalacao(string pasta)
        {
            try
            {
                return File.Exists(Path.Combine(pasta, @"bin\pjecalc.jar"))
                    && Directory.Exists(Path.Combine(pasta, @"tomcat\webapps\pjecalc"))
                    && File.Exists(Path.Combine(pasta, @"tomcat\conf\server.xml"));
            }
            catch { return false; }
        }

        private static readonly HashSet<string> Ignorar = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "$Recycle.Bin", "System Volume Information", "Recovery", "$WinREAgent", "AppData",
            "node_modules", ".git", "Config.Msi", "PerfLogs", "MSOCache", "$SysReset", "$Windows.~BT", "$Windows.~WS",
            "tomcat", "navegador", "jre", "WindowsApps", "Microsoft", "Packages"
        };

        /// <summary>Procura instalações nos discos fixos (até 5 níveis de pasta).</summary>
        public static List<string> Procurar(IEnumerable<string> raizesExtras, Func<bool> cancelado)
        {
            var achadas = new List<string>();
            var raizes = DriveInfo.GetDrives()
                .Where(d => { try { return d.DriveType == DriveType.Fixed && d.IsReady; } catch { return false; } })
                .Select(d => d.RootDirectory.FullName).ToList();
            if (raizesExtras != null) raizes.AddRange(raizesExtras.Where(Directory.Exists));

            foreach (string raiz in raizes)
            {
                var fila = new Queue<KeyValuePair<string, int>>();
                fila.Enqueue(new KeyValuePair<string, int>(raiz, 0));
                while (fila.Count > 0)
                {
                    if (cancelado != null && cancelado()) return achadas;
                    var item = fila.Dequeue();
                    if (PareceInstalacao(item.Key))
                    {
                        if (!achadas.Any(a => string.Equals(a, item.Key, StringComparison.OrdinalIgnoreCase)))
                            achadas.Add(item.Key);
                        continue;
                    }
                    if (item.Value >= 5) continue;
                    string[] subs;
                    try { subs = Directory.GetDirectories(item.Key); } catch { continue; }
                    foreach (string s in subs)
                    {
                        string nome = Path.GetFileName(s);
                        if (Ignorar.Contains(nome) || nome.StartsWith("$")) continue;
                        try { if ((new DirectoryInfo(s).Attributes & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                        fila.Enqueue(new KeyValuePair<string, int>(s, item.Value + 1));
                    }
                }
            }
            return achadas;
        }

        // ------------------------------------------------------------ leitura

        public static Instalacao Carregar(string pasta)
        {
            var i = new Instalacao();
            i.Pasta = Path.GetFullPath(pasta).TrimEnd('\\');
            i.Versao = LerVersao(i.Pasta);

            string server = Regex.Replace(File.ReadAllText(Path.Combine(i.Pasta, @"tomcat\conf\server.xml")), "<!--.*?-->", "", RegexOptions.Singleline);
            var ms = Regex.Match(server, "<Server\\s[^>]*port=\"(\\d+)\"[^>]*shutdown=\"([^\"]+)\"");
            if (!ms.Success) ms = Regex.Match(server, "<Server\\s[^>]*shutdown=\"([^\"]+)\"[^>]*port=\"(\\d+)\"");
            i.PortaDesligar = 9256; i.ComandoDesligar = "SHUTDOWN";
            if (ms.Success)
            {
                int a; if (int.TryParse(ms.Groups[1].Value, out a)) { i.PortaDesligar = a; i.ComandoDesligar = ms.Groups[2].Value; }
                else { int.TryParse(ms.Groups[2].Value, out a); i.PortaDesligar = a; i.ComandoDesligar = ms.Groups[1].Value; }
            }
            i.PortaHttp = 9257;
            foreach (Match c in Regex.Matches(server, "<Connector\\s[^>]*>"))
            {
                if (c.Value.Contains("AJP") || c.Value.Contains("SSLEnabled=\"true\"")) continue;
                var p = Regex.Match(c.Value, "port=\"(\\d+)\"");
                if (p.Success) { i.PortaHttp = int.Parse(p.Groups[1].Value); break; }
            }

            i.UsuarioBanco = "pjecalc"; i.SenhaBanco = "/pjecalc/";
            string ctx = Path.Combine(i.Pasta, @"tomcat\webapps\pjecalc\META-INF\context.xml");
            if (File.Exists(ctx))
            {
                string t = Regex.Replace(File.ReadAllText(ctx), "<!--.*?-->", "", RegexOptions.Singleline);
                var r = Regex.Match(t, "<Resource\\s[^>]*org\\.h2\\.Driver[^>]*>");
                if (r.Success)
                {
                    var u = Regex.Match(r.Value, "username=\"([^\"]*)\""); if (u.Success) i.UsuarioBanco = u.Groups[1].Value;
                    var s = Regex.Match(r.Value, "password=\"([^\"]*)\""); if (s.Success) i.SenhaBanco = s.Groups[1].Value;
                }
            }
            return i;
        }

        private static string LerVersao(string pasta)
        {
            try
            {
                string v = File.ReadAllText(Path.Combine(pasta, @"tomcat\webapps\pjecalc\versao.html"));
                var m = Regex.Match(v, "Vers[^:]*:\\s*([0-9][0-9.]*)");
                if (m.Success) return m.Groups[1].Value.TrimEnd('.');
            }
            catch { }
            var n = Regex.Match(Path.GetFileName(pasta), "(\\d+\\.\\d+\\.\\d+)");
            return n.Success ? n.Groups[1].Value : "?";
        }

        // -------------------------------------------------------------- estado

        public static bool PortaOcupada(int porta)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var t = c.ConnectAsync("127.0.0.1", porta);
                    return t.Wait(1500) && c.Connected;
                }
            }
            catch { return false; }
        }

        /// <summary>PID do programa que está escutando na porta (0 se nenhum).</summary>
        public static int ProcessoNaPorta(int porta)
        {
            try
            {
                var psi = new ProcessStartInfo("netstat", "-ano -p TCP");
                psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.CreateNoWindow = true;
                string saida;
                using (var p = Process.Start(psi)) { saida = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000); }
                foreach (string linha in saida.Split('\n'))
                {
                    var m = Regex.Match(linha, "^\\s*TCP\\s+\\S+:" + porta + "\\s+\\S+\\s+\\S+\\s+(\\d+)\\s*$");
                    if (!m.Success || !linha.Contains("LISTEN") && !linha.Contains("ESCUTA") && !linha.Contains("OUVINDO")) continue;
                    return int.Parse(m.Groups[1].Value);
                }
            }
            catch { }
            return 0;
        }

        /// <summary>Caminho do programa que está escutando na porta (ou null).</summary>
        public static string ProgramaNaPorta(int porta)
        {
            int pid = ProcessoNaPorta(porta);
            return pid == 0 ? null : CaminhoDoProcesso(pid);
        }

        private static string CaminhoDoProcesso(int pid)
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT ExecutablePath FROM Win32_Process WHERE ProcessId=" + pid))
                    foreach (ManagementObject o in s.Get())
                        return Convert.ToString(o["ExecutablePath"]);
            }
            catch { }
            return null;
        }

        /// <summary>Processos Java que rodam de dentro de uma instalação do PJe-Calc: (pid, pasta da instalação).</summary>
        public static List<KeyValuePair<int, string>> ProcessosPjeCalc()
        {
            var lista = new List<KeyValuePair<int, string>>();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE Name='java.exe' OR Name='javaw.exe'"))
                    foreach (ManagementObject o in s.Get())
                    {
                        string exe = Convert.ToString(o["ExecutablePath"]);
                        if (string.IsNullOrEmpty(exe)) continue;
                        // <instalação>\bin\jre\bin\javaw.exe
                        string d = Path.GetDirectoryName(exe);
                        for (int i = 0; i < 3 && d != null; i++) d = Path.GetDirectoryName(d);
                        if (d != null && PareceInstalacao(d))
                            lista.Add(new KeyValuePair<int, string>(Convert.ToInt32(o["ProcessId"]), d.TrimEnd('\\')));
                    }
            }
            catch { }
            return lista;
        }

        /// <summary>Pasta da instalação do PJe-Calc que está aberta usando esta porta (ou null).</summary>
        public static string InstalacaoNaPorta(int porta)
        {
            if (!PortaOcupada(porta)) return null;
            foreach (var p in ProcessosPjeCalc())
            {
                try { if (Carregar(p.Value).PortaHttp == porta) return p.Value; } catch { }
            }
            string prog = ProgramaNaPorta(porta);
            if (prog == null) return null;
            string d = Path.GetDirectoryName(prog);
            for (int i = 0; i < 3 && d != null; i++) d = Path.GetDirectoryName(d);
            return d != null && PareceInstalacao(d) ? d : null;
        }

        // ------------------------------------------------ fechar um PJe-Calc aberto

        private delegate bool EnumJanelas(IntPtr h, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumJanelas f, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr res);

        /// <summary>
        /// Fecha com segurança o PJe-Calc que está usando a porta, do mesmo jeito que o
        /// Windows faz ao desligar o computador: o Java desliga o Tomcat e fecha o banco
        /// de forma ordenada (equivale ao "Sair" do ícone da bandeja). Nunca encerra à força.
        /// </summary>
        public static bool FecharPjeCalcNaPorta(int portaHttp, int portaDesligar, string comandoDesligar, Action<string> log)
        {
            if (!PortaOcupada(portaHttp)) return true;

            // processos do PJe-Calc (de qualquer instalação) que usam esta porta
            var pids = new List<int>();
            foreach (var p in ProcessosPjeCalc())
            {
                try { if (Carregar(p.Value).PortaHttp == portaHttp) pids.Add(p.Key); } catch { }
            }
            if (pids.Count == 0)
            {
                int pidPorta = ProcessoNaPorta(portaHttp);
                string caminho = pidPorta == 0 ? null : CaminhoDoProcesso(pidPorta);
                if (pidPorta != 0 && caminho != null && Regex.IsMatch(Path.GetFileName(caminho), "^javaw?\\.exe$", RegexOptions.IgnoreCase))
                    pids.Add(pidPorta);
                else
                {
                    if (log != null) log("A porta " + portaHttp + " está em uso por outro programa" + (caminho != null ? " (" + Path.GetFileName(caminho) + ")" : "") + ", não pelo PJe-Calc.");
                    return false;
                }
            }

            foreach (int pid in pids)
            {
                // 1) aberto pelo atalho oficial: mensagem de fim de sessão para a janela interna do Java
                var janelas = new List<IntPtr>();
                EnumWindows((h, l) =>
                {
                    uint p; GetWindowThreadProcessId(h, out p);
                    if (p == pid)
                    {
                        var c = new StringBuilder(128); GetClassName(h, c, 128);
                        if (c.ToString() == "SunAwtToolkit") janelas.Add(h);
                    }
                    return true;
                }, IntPtr.Zero);

                IntPtr r;
                if (janelas.Count > 0)
                {
                    foreach (var h in janelas)
                    {
                        SendMessageTimeout(h, 0x0011 /*WM_QUERYENDSESSION*/, IntPtr.Zero, IntPtr.Zero, 0x0002, 5000, out r);
                        SendMessageTimeout(h, 0x0016 /*WM_ENDSESSION*/, new IntPtr(1), IntPtr.Zero, 0x0002, 30000, out r);
                    }
                }
                else
                {
                    // 2) aberto em segundo plano (sem janela): comando oficial de desligamento do Tomcat
                    try
                    {
                        using (var c = new TcpClient("127.0.0.1", portaDesligar))
                        {
                            byte[] b = Encoding.ASCII.GetBytes(comandoDesligar);
                            c.GetStream().Write(b, 0, b.Length);
                        }
                    }
                    catch { }
                }

                try
                {
                    using (var proc = Process.GetProcessById(pid))
                        if (!proc.WaitForExit(90000)) return false;
                }
                catch (ArgumentException) { /* já saiu */ }
            }

            for (int i = 0; i < 10 && PortaOcupada(portaHttp); i++) Thread.Sleep(1000);
            return !PortaOcupada(portaHttp);
        }

        /// <summary>Abre o PJe-Calc normalmente, pelo iniciarPjeCalc.bat da instalação.</summary>
        public void AbrirPeloAtalho()
        {
            var psi = new ProcessStartInfo(Path.Combine(Pasta, "iniciarPjeCalc.bat"));
            psi.WorkingDirectory = Pasta;
            psi.UseShellExecute = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(psi);
        }

        public EstadoInstalacao Estado()
        {
            if (!File.Exists(ArquivoBanco)) return EstadoInstalacao.SemBanco;
            return EstaAberta() ? EstadoInstalacao.Aberta : EstadoInstalacao.Fechada;
        }

        public bool EstaAberta()
        {
            if (!PortaOcupada(PortaHttp)) return false;
            if (ProcessosPjeCalc().Any(p => string.Equals(p.Value, Pasta, StringComparison.OrdinalIgnoreCase))) return true;
            string prog = ProgramaNaPorta(PortaHttp);
            if (prog != null) return prog.StartsWith(Pasta + "\\", StringComparison.OrdinalIgnoreCase);
            return File.Exists(ArquivoTrava);
        }

        // ------------------------------------------------- contar cálculos (fechada)

        /// <summary>Conta os cálculos lendo o banco em modo somente leitura. Só com a versão fechada.</summary>
        public int? ContarCalculos()
        {
            try
            {
                if (!File.Exists(ArquivoBanco) || File.Exists(ArquivoTrava) || !File.Exists(JavaExe)) return null;
                string h2 = Directory.GetFiles(Path.Combine(Pasta, @"bin\lib"), "h2-*.jar").FirstOrDefault();
                if (h2 == null) return null;
                string url = "jdbc:h2:" + Path.Combine(Pasta, @".dados\pjecalc") + ";ACCESS_MODE_DATA=r";
                var psi = new ProcessStartInfo(JavaExe,
                    "-cp \"" + h2 + "\" org.h2.tools.Shell -url \"" + url + "\" -user \"" + UsuarioBanco + "\" -password \"" + SenhaBanco +
                    "\" -sql \"SELECT COUNT(*) FROM TBCALCULO\"");
                psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.CreateNoWindow = true;
                using (var p = Process.Start(psi))
                {
                    var t = p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return null; }
                    var m = Regex.Match(t.Result, "^\\s*(\\d+)\\s*$", RegexOptions.Multiline);
                    if (m.Success) return int.Parse(m.Groups[1].Value);
                }
            }
            catch { }
            return null;
        }

        // ----------------------------------------------------- copiar o banco

        /// <summary>Compacta o banco (só com a versão fechada, para a cópia ficar consistente).</summary>
        public string CopiarBanco(string pastaDestino)
        {
            if (File.Exists(ArquivoTrava) || EstaAberta())
                throw new Exception("a versão está aberta; feche o PJe-Calc para copiar o banco");
            Directory.CreateDirectory(pastaDestino);
            string zip = Path.Combine(pastaDestino, "banco-de-dados-pjecalc-" + Versao + ".zip");
            if (File.Exists(zip)) File.Delete(zip);
            using (var arq = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                arq.CreateEntryFromFile(ArquivoBanco, "pjecalc.h2.db", CompressionLevel.Optimal);
                string leia = "Banco de dados do PJe-Calc " + Versao + "\r\nOrigem: " + Pasta + "\r\nCopiado em: " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") +
                    "\r\n\r\nPara restaurar: feche o PJe-Calc, e coloque pjecalc.h2.db na pasta .dados da instalação (substituindo o atual).\r\n";
                var e = arq.CreateEntry("LEIA-ME.txt");
                using (var w = new StreamWriter(e.Open(), new UTF8Encoding(true))) w.Write(leia);
            }
            return zip;
        }

        // ------------------------------------------------------- ligar/desligar

        public bool PrecisaPrimeiraAbertura
        {
            get { return File.Exists(Path.Combine(Pasta, @"tomcat\webapps\pjecalc\WEB-INF\classes\META-INF\persistence.xml.tmp")); }
        }

        /// <summary>Lê as opções de Java do iniciarPjeCalc.bat (fuso, codificação, memória...).</summary>
        private List<string> OpcoesJava()
        {
            var op = new List<string>();
            try
            {
                string bat = File.ReadAllText(Path.Combine(Pasta, "iniciarPjeCalc.bat"));
                string linha = bat.Split('\n').FirstOrDefault(l => l.Contains("pjecalc.jar")) ?? "";
                foreach (Match m in Regex.Matches(linha, "(-D[\\w.\\-]+=(?:\"[^\"]*\"|\\S+)|-Xm[sx]\\d+[kmgKMG]?)"))
                    op.Add(m.Value);
            }
            catch { }
            if (!op.Any(o => o.StartsWith("-Duser.timezone"))) op.Add("-Duser.timezone=GMT-3");
            if (!op.Any(o => o.StartsWith("-Dfile.encoding"))) op.Add("-Dfile.encoding=ISO-8859-1");
            if (!op.Any(o => o.StartsWith("-Xmx"))) op.Add("-Xmx2048m");
            return op;
        }

        private static string Aspas(string s) { return s.Contains(" ") && !s.Contains("\"") ? "\"" + s + "\"" : s; }

        /// <summary>Liga o Tomcat desta versão em segundo plano (sem abrir o Firefox).</summary>
        public void Ligar(Action<string> log, Func<bool> cancelado)
        {
            if (PrecisaPrimeiraAbertura)
                throw new Exception("esta versão nunca foi aberta; abra-a uma vez pelo atalho do PJe-Calc e feche pela bandeja");
            if (PortaOcupada(PortaHttp))
                throw new Exception("a porta " + PortaHttp + " está em uso por outro PJe-Calc aberto");

            string tomcatDir = Path.Combine(Pasta, "tomcat");
            var args = new List<string>(OpcoesJava());
            args.Add("-Dcaminho.instalacao=" + Pasta);
            args.Add("-Dcatalina.home=" + tomcatDir);
            args.Add("-Dcatalina.base=" + tomcatDir);
            args.Add("-cp"); args.Add(Path.Combine(Pasta, @"bin\pjecalc.jar"));
            args.Add("org.apache.catalina.startup.Bootstrap"); args.Add("start");

            var psi = new ProcessStartInfo(JavaExe, string.Join(" ", args.Select(Aspas)));
            psi.WorkingDirectory = Pasta;
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            logTomcat.Clear();
            tomcat = Process.Start(psi);
            DataReceivedEventHandler h = (s, e) => { if (e.Data != null) lock (logTomcat) { if (logTomcat.Length < 200000) logTomcat.AppendLine(e.Data); } };
            tomcat.OutputDataReceived += h; tomcat.ErrorDataReceived += h;
            tomcat.BeginOutputReadLine(); tomcat.BeginErrorReadLine();

            DateTime limite = DateTime.Now.AddMinutes(5);
            while (true)
            {
                if (tomcat.HasExited) throw new Exception("o PJe-Calc não iniciou. " + UltimasLinhasLog(6));
                if (cancelado != null && cancelado()) throw new OperationCanceledException();
                if (DateTime.Now > limite) throw new Exception("o PJe-Calc demorou demais para iniciar");
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(UrlBase + "/logon.jsf");
                    req.Timeout = 5000;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                        if (resp.StatusCode == HttpStatusCode.OK) return;
                }
                catch { }
                Thread.Sleep(1500);
            }
        }

        public bool LigadaPorNos { get { return tomcat != null && !tomcat.HasExited; } }

        /// <summary>Desliga pelo comando oficial do Tomcat (fecha o banco corretamente).</summary>
        public void Desligar(Action<string> log)
        {
            if (tomcat == null) return;
            for (int tentativa = 0; tentativa < 5 && !tomcat.HasExited; tentativa++)
            {
                try
                {
                    using (var c = new TcpClient("127.0.0.1", PortaDesligar))
                    {
                        byte[] b = Encoding.ASCII.GetBytes(ComandoDesligar);
                        c.GetStream().Write(b, 0, b.Length);
                    }
                    break;
                }
                catch { Thread.Sleep(2000); }
            }
            if (!tomcat.WaitForExit(120000))
            {
                if (log != null) log("O PJe-Calc não fechou sozinho em 2 minutos; encerrando à força.");
                try { tomcat.Kill(); tomcat.WaitForExit(10000); } catch { }
            }
            tomcat = null;
        }

        public string UltimasLinhasLog(int n)
        {
            lock (logTomcat)
            {
                var linhas = logTomcat.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                return string.Join(" | ", linhas.Skip(Math.Max(0, linhas.Length - n)));
            }
        }
    }
}
