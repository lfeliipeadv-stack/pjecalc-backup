// Instalação com um clique (pasta do usuário, sem pedir administrador), atalhos, registro em
// "Aplicativos instalados" do Windows e desinstalação.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;

namespace PjeCalcBackup
{
    public static class Instalador
    {
        private const string NomeAtalho = "PJe-Calc Backup.lnk";

        // Só para testes do desenvolvimento: PJECALC_BACKUP_TESTE=<pasta> desvia a instalação, os atalhos e o registro
        // para dentro dessa pasta, sem tocar no programa instalado de verdade.
        private static string Teste { get { var t = Environment.GetEnvironmentVariable("PJECALC_BACKUP_TESTE"); return string.IsNullOrEmpty(t) ? null : t; } }

        private static string ChaveDesinstalar
        {
            get { return @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PJeCalcBackup" + (Teste != null ? "Teste" : ""); }
        }

        public static string Pasta
        {
            get
            {
                string baseLocal = Teste ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(baseLocal, @"Programs\PJe-Calc Backup");
            }
        }

        public static string Exe { get { return Path.Combine(Pasta, "PJeCalcBackup.exe"); } }
        private static string EsteExe { get { return Assembly.GetEntryAssembly().Location; } }

        private static bool Mesma(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>O programa aberto agora é o que está instalado?</summary>
        public static bool RodandoInstalado { get { return Mesma(Path.GetDirectoryName(EsteExe), Pasta); } }

        public static bool JaInstalado { get { return File.Exists(Exe); } }
        public static bool ModoTeste { get { return Teste != null; } }

        /// <summary>Versão do programa instalado (ou null).</summary>
        public static Version VersaoInstalada
        {
            get
            {
                try
                {
                    if (!File.Exists(Exe)) return null;
                    var v = FileVersionInfo.GetVersionInfo(Exe);
                    return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
                }
                catch { return null; }
            }
        }

        private static Stream Recurso(string nome)
        {
            var asm = Assembly.GetExecutingAssembly();
            string n = asm.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith(nome, StringComparison.OrdinalIgnoreCase));
            return n == null ? null : asm.GetManifestResourceStream(n);
        }

        /// <summary>Troca um arquivo, mesmo que o atual esteja aberto (o Windows deixa renomear um programa em uso).</summary>
        private static void Trocar(string destino, Action<string> gravar)
        {
            if (File.Exists(destino))
            {
                string antigo = destino + ".old";
                try { if (File.Exists(antigo)) File.Delete(antigo); } catch { antigo = destino + "." + DateTime.Now.Ticks + ".old"; }
                File.Move(destino, antigo);
            }
            gravar(destino);
        }

        private static void GravarRecurso(string nomeRecurso, string destino)
        {
            using (var r = Recurso(nomeRecurso))
            {
                if (r == null) return;
                Trocar(destino, d => { using (var f = File.Create(d)) r.CopyTo(f); });
            }
        }

        /// <summary>Copia o programa para a pasta do usuário, cria os atalhos e registra em "Aplicativos instalados".
        /// Devolve o caminho do programa instalado.</summary>
        public static string Instalar()
        {
            Directory.CreateDirectory(Pasta);
            string origem = Path.GetDirectoryName(EsteExe);
            if (!RodandoInstalado)
            {
                Trocar(Exe, d => File.Copy(EsteExe, d));
                // versão para o CMD, o .bat e o LEIA-ME: ao lado deste programa ou embutidos nele
                foreach (var nome in new[] { "PJeCalcBackup-Console.exe", "backup-calculos.bat", "LEIA-ME.txt" })
                {
                    string ao_lado = Path.Combine(origem, nome), destino = Path.Combine(Pasta, nome);
                    if (File.Exists(ao_lado)) Trocar(destino, d => File.Copy(ao_lado, d));
                    else GravarRecurso("." + nome, destino);
                }
            }
            Atalhos();
            Registrar();
            return Exe;
        }

        // ---- atalhos (WScript.Shell, que vem no Windows)

        private static object Shell()
        {
            return Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        }

        private static object Invocar(object o, string membro, BindingFlags tipo, params object[] args)
        {
            return o.GetType().InvokeMember(membro, tipo, null, o, args);
        }

        private static string AlvoDoAtalho(object shell, string lnk)
        {
            try { var a = Invocar(shell, "CreateShortcut", BindingFlags.InvokeMethod, lnk); return Convert.ToString(Invocar(a, "TargetPath", BindingFlags.GetProperty)); }
            catch { return null; }
        }

        private static void CriarAtalho(object shell, string lnk, string descricao)
        {
            var a = Invocar(shell, "CreateShortcut", BindingFlags.InvokeMethod, lnk);
            Invocar(a, "TargetPath", BindingFlags.SetProperty, Exe);
            Invocar(a, "WorkingDirectory", BindingFlags.SetProperty, Pasta);
            Invocar(a, "IconLocation", BindingFlags.SetProperty, Exe + ",0");
            Invocar(a, "Description", BindingFlags.SetProperty, descricao);
            Invocar(a, "Save", BindingFlags.InvokeMethod);
        }

        private static IEnumerable<string> PastasDeAtalhos()
        {
            if (Teste != null)
            {
                foreach (var p in new[] { "Desktop", "MenuIniciar" }) { string d = Path.Combine(Teste, p); Directory.CreateDirectory(d); yield return d; }
                yield break;
            }
            yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        }

        /// <summary>Atalho na Área de Trabalho e no menu Iniciar (sem duplicar um que já aponte para o programa instalado).</summary>
        public static void Atalhos()
        {
            try
            {
                var shell = Shell();
                foreach (var pasta in PastasDeAtalhos())
                {
                    if (!Directory.Exists(pasta)) continue;
                    bool existe = Directory.GetFiles(pasta, "*.lnk").Any(l => { var alvo = AlvoDoAtalho(shell, l); return alvo != null && Mesma(alvo, Exe); });
                    if (!existe) CriarAtalho(shell, Path.Combine(pasta, NomeAtalho), "Backup, restauração e migração dos cálculos do PJe-Calc");
                }
            }
            catch { }
        }

        /// <summary>Registro em "Configurações → Aplicativos instalados" (com o botão Desinstalar do Windows).</summary>
        public static void Registrar()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(ChaveDesinstalar))
                {
                    k.SetValue("DisplayName", "PJe-Calc Backup");
                    k.SetValue("DisplayVersion", Atualizacao.Atual.ToString());
                    k.SetValue("Publisher", "Projeto independente (código aberto)");
                    k.SetValue("DisplayIcon", Exe + ",0");
                    k.SetValue("InstallLocation", Pasta);
                    k.SetValue("UninstallString", "\"" + Exe + "\" --desinstalar");
                    k.SetValue("URLInfoAbout", Atualizacao.UrlProjeto);
                    k.SetValue("HelpLink", Atualizacao.UrlProjeto);
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    long kb = 0;
                    try { kb = Directory.GetFiles(Pasta).Sum(f => new FileInfo(f).Length) / 1024; } catch { }
                    k.SetValue("EstimatedSize", (int)kb, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        /// <summary>Remove o programa (atalhos, backup automático, registro e a pasta do programa).
        /// Os backups, as configurações e o PJe-Calc não são tocados.</summary>
        public static void Desinstalar()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var c = Config.Carregar();
            Tema.Aplicar(app.Resources, c.Tema, c.Paleta);
            if (!Dialogo.Mostrar(null, TipoDialogo.Pergunta, "Remover o PJe-Calc Backup?",
                "O programa, os atalhos e o backup automático serão removidos deste computador.\n\n" +
                "Seus backups continuam em " + c.Destino + ", e o PJe-Calc não é alterado.", "Remover", "Cancelar")) return;
            if (!ModoTeste) { try { Agendador.Remover(); } catch { } }
            try
            {
                var shell = Shell();
                foreach (var pasta in PastasDeAtalhos())
                    if (Directory.Exists(pasta))
                        foreach (var l in Directory.GetFiles(pasta, "*.lnk"))
                        {
                            var alvo = AlvoDoAtalho(shell, l);
                            if (alvo != null && Mesma(alvo, Exe)) { try { File.Delete(l); } catch { } }
                        }
            }
            catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(ChaveDesinstalar, false); } catch { }
            bool apagarPasta = Directory.Exists(Pasta) && Pasta.EndsWith(@"\Programs\PJe-Calc Backup", StringComparison.OrdinalIgnoreCase);
            if (apagarPasta && !RodandoInstalado) { try { Directory.Delete(Pasta, true); } catch { } }
            Dialogo.Mostrar(null, TipoDialogo.Sucesso, "PJe-Calc Backup removido",
                "Obrigado por usar o programa! Seus backups continuam em " + c.Destino + ".", "Fechar", null);
            // a pasta só é apagada se for mesmo a do programa (…\Programs\PJe-Calc Backup); como este programa está
            // dentro dela, quem apaga é o CMD, depois que ele fechar (tenta por até ~20 segundos)
            if (apagarPasta && RodandoInstalado)
                Process.Start(new ProcessStartInfo("cmd.exe",
                    "/c for /l %i in (1,1,10) do (ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + Pasta + "\" 2>nul & if not exist \"" + Pasta + "\" exit /b 0)")
                { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        }
    }
}
