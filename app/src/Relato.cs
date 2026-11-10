// "Relatar um problema": monta um texto com as informações técnicas (sem dados das partes) e abre o e-mail
// ou o GitHub com a mensagem pronta. Nada é enviado sozinho: quem envia é a pessoa, no aplicativo dela.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PjeCalcBackup
{
    public static class Relato
    {
        public const string Email = "pjecalcbkp@gmail.com";

        public class VersaoInstalada { public string Versao, Pasta, Estado; public int? Calculos; }

        // ------------------------------------------------------------ sem dados das partes

        /// <summary>Tira nomes das partes, números de processo, CPF/CNPJ e o nome do usuário do Windows.
        /// Usa os nomes conhecidos (lidos dos bancos) e também os padrões do relatório e dos nomes de arquivo .PJC.</summary>
        public static string Anonimizar(string texto, IEnumerable<CalculoInfo> calculos)
        {
            if (string.IsNullOrEmpty(texto)) return texto ?? "";
            string s = texto;

            // nomes e processos conhecidos (os mais longos primeiro, para não sobrar pedaço)
            var nomes = new List<string>();
            var processos = new List<string>();
            if (calculos != null)
                foreach (var c in calculos)
                {
                    foreach (var n in new[] { c.Reclamante, c.Reclamado }) if (!string.IsNullOrWhiteSpace(n) && n.Trim().Length >= 3) nomes.Add(n.Trim());
                    if (!string.IsNullOrWhiteSpace(c.Processo) && c.Processo.Trim().Length >= 5) processos.Add(c.Processo.Trim());
                }
            // número completo do processo (CNJ) primeiro, para não sobrar o final dele
            s = Regex.Replace(s, @"\d{7}-\d{2}\.\d{4}(\.\d\.\d{2}\.\d{4})?", "[processo]");
            foreach (var p in processos.Distinct().OrderByDescending(x => x.Length))
                s = Regex.Replace(s, Regex.Escape(p) + @"(\.\d\.\d{2}\.\d{4})?", "[processo]", RegexOptions.IgnoreCase);
            foreach (var n in nomes.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Length)) s = Regex.Replace(s, Regex.Escape(n), "[nome]", RegexOptions.IgnoreCase);

            // padrões que sobram mesmo sem a lista de nomes
            s = Regex.Replace(s, @"(?i)(c[áa]lculo\s+(?:n[º°o]\s*)?\d+)\s+[—–-]\s+[^\r\n(]+", "$1 — [nome] ");
            s = Regex.Replace(s, @"(?i)(CALCULO_\d+_)[^\\/\r\n]*?(_DATA_)", "$1[nome]$2");
            s = Regex.Replace(s, @"(?i)PROCESSO_\d+", "PROCESSO_[nº]");
            s = Regex.Replace(s, @"\d{7}-\d{2}\.\d{4}(\.\d\.\d{2}\.\d{4})?", "[processo]");
            s = Regex.Replace(s, @"\b\d{20}\b", "[processo]");
            s = Regex.Replace(s, @"\b\d{3}\.?\d{3}\.?\d{3}-?\d{2}\b", "[CPF]");
            s = Regex.Replace(s, @"\b\d{2}\.?\d{3}\.?\d{3}/?\d{4}-?\d{2}\b", "[CNPJ]");

            // pasta e nome do usuário do Windows
            string perfil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(perfil)) s = Regex.Replace(s, Regex.Escape(perfil), "%USERPROFILE%", RegexOptions.IgnoreCase);
            try
            {
                string curto = Path.GetFullPath(Path.GetTempPath());
                var m = Regex.Match(curto, @"(?i)^[A-Z]:\\Users\\[^\\]+");
                if (m.Success) s = Regex.Replace(s, Regex.Escape(m.Value), "%USERPROFILE%", RegexOptions.IgnoreCase);
            }
            catch { }
            string usuario = Environment.UserName;
            if (!string.IsNullOrEmpty(usuario) && usuario.Length >= 3) s = Regex.Replace(s, @"\b" + Regex.Escape(usuario) + @"\b", "[usuário]", RegexOptions.IgnoreCase);
            return s;
        }

        // ------------------------------------------------------------ informações técnicas

        private static string Windows()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string nome = Convert.ToString(k.GetValue("ProductName"));
                    string ver = Convert.ToString(k.GetValue("DisplayVersion") ?? k.GetValue("ReleaseId"));
                    string build = Convert.ToString(k.GetValue("CurrentBuild"));
                    int b;
                    // o Windows 11 ainda se chama "Windows 10" nesse registro
                    if (int.TryParse(build, out b) && b >= 22000) nome = nome.Replace("Windows 10", "Windows 11");
                    return nome + (string.IsNullOrEmpty(ver) ? "" : " " + ver) + " (build " + build + ")" + (Environment.Is64BitOperatingSystem ? ", 64 bits" : ", 32 bits");
                }
            }
            catch { return Environment.OSVersion.VersionString; }
        }

        private static string DotNet()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    int r = Convert.ToInt32(k.GetValue("Release"));
                    string v = r >= 533320 ? "4.8.1" : r >= 528040 ? "4.8" : r >= 461808 ? "4.7.2" : "anterior ao 4.7.2";
                    return ".NET Framework " + v + " (release " + r + ")";
                }
            }
            catch { return ".NET Framework (não consegui ler a versão)"; }
        }

        private static string NomeNavegador()
        {
            try
            {
                string n = Navegador.Localizar();
                if (string.IsNullOrEmpty(n)) return "nenhum encontrado (Edge ou Chrome)";
                string f = Path.GetFileName(n).ToLowerInvariant();
                return f.Contains("msedge") ? "Microsoft Edge" : f.Contains("chrome") ? "Google Chrome" : Path.GetFileName(n);
            }
            catch { return "não consegui verificar"; }
        }

        /// <summary>Texto técnico (já sem dados das partes) para colar no relato.</summary>
        public static string InformacoesTecnicas(Config config, List<VersaoInstalada> versoes, IEnumerable<CalculoInfo> calculosConhecidos)
        {
            var t = new StringBuilder();
            t.AppendLine("PJe-Calc Backup " + Atualizacao.Atual + (Instalador.RodandoInstalado ? " (instalado)" : " (sem instalar)"));
            t.AppendLine(Windows());
            t.AppendLine(DotNet());
            t.AppendLine("Navegador para os .PJC: " + NomeNavegador());

            t.AppendLine("Versões do PJe-Calc:");
            if (versoes == null || versoes.Count == 0) t.AppendLine("  nenhuma encontrada");
            else foreach (var v in versoes)
                    t.AppendLine("  - " + v.Versao + " · " + v.Estado + (v.Calculos.HasValue ? " · " + v.Calculos.Value + " cálculo(s)" : "") + " · " + v.Pasta);

            string agenda = "não consegui verificar";
            try { agenda = Agendador.Consultar().Existe ? "ligado" : "desligado"; } catch { }
            t.AppendLine("Backup rápido: " + (config.BackupRapido ? "ligado" : "desligado") + " · Backup automático: " + agenda);
            t.AppendLine("Pasta de backups: " + config.Destino);
            try
            {
                AvisoDaAgenda(config, t);
            }
            catch { }

            // últimas operações e, da mais recente, as linhas de erro e aviso do relatório
            try
            {
                var h = Historico.Listar(config.Destino).OrderByDescending(r => r.Data).Take(3).ToList();
                if (h.Count > 0)
                {
                    t.AppendLine("Últimas operações:");
                    foreach (var r in h)
                        t.AppendLine("  - " + r.Data.ToString("dd/MM/yyyy HH:mm") + " · " + r.TipoTexto + " · " + r.Calculos + " cálculo(s), " + r.Falhas + " falha(s)");
                    var alvo = h.FirstOrDefault(r => r.Falhas > 0) ?? h[0];
                    string rel = string.IsNullOrEmpty(alvo.PastaOperacao) ? null : Path.Combine(alvo.PastaOperacao, "relatorio.txt");
                    if (rel != null && File.Exists(rel))
                    {
                        var linhas = File.ReadAllLines(rel, Encoding.UTF8);
                        var problemas = linhas.Where(l => l.StartsWith("ERRO") || l.StartsWith("AVISO")).Take(20).ToList();
                        t.AppendLine("Relatório da operação de " + alvo.Data.ToString("dd/MM/yyyy HH:mm") + ": " + (linhas.Length > 0 ? linhas[0].Trim('﻿') : ""));
                        if (problemas.Count == 0) t.AppendLine("  (sem linhas de erro ou aviso)");
                        foreach (var l in problemas) t.AppendLine("  " + l.Trim());
                    }
                }
            }
            catch { }
            return Anonimizar(t.ToString().TrimEnd(), calculosConhecidos);
        }

        private static void AvisoDaAgenda(Config config, StringBuilder t)
        {
            Agendador.Resultado ult;
            string msg = Agendador.Problema(config, out ult);
            if (!string.IsNullOrEmpty(msg)) t.AppendLine("Aviso do backup automático: " + msg);
        }

        // ------------------------------------------------------------ montar a mensagem e os links

        public static string Mensagem(string descricao, string tecnico)
        {
            return "Relato de problema no PJe-Calc Backup\r\n\r\nO que aconteceu:\r\n" + (descricao ?? "").Trim() +
                   "\r\n\r\n--- Informações técnicas (preenchidas pelo programa, sem dados das partes) ---\r\n" + (tecnico ?? "").Trim();
        }

        private const string AvisoCorte = "\r\n[…texto cortado: o completo foi copiado e pode ser colado aqui]";

        /// <summary>Monta o link com o texto codificado, cortando o texto até o link inteiro caber no limite
        /// (acentos e quebras de linha ocupam vários caracteres depois de codificados).</summary>
        private static string LinkCortado(string inicio, string texto, int limite, out bool cortou)
        {
            cortou = false;
            string t = texto;
            string link = inicio + Uri.EscapeDataString(t);
            while (link.Length > limite && t.Length > 200)
            {
                cortou = true;
                int novo = Math.Max(200, (int)(t.Length * 0.85) - AvisoCorte.Length);
                t = texto.Substring(0, Math.Min(novo, texto.Length)) + AvisoCorte;
                link = inicio + Uri.EscapeDataString(t);
            }
            return link;
        }

        public static string LinkEmail(string texto, out bool cortou)
        {
            // os programas de e-mail aceitam links de até uns 2.000 caracteres; o resto vai pela área de transferência
            return LinkCortado("mailto:" + Email + "?subject=" + Uri.EscapeDataString("Problema no PJe-Calc Backup " + Atualizacao.Atual) + "&body=", texto, 1900, out cortou);
        }

        public static string LinkGitHub(string descricao, string texto)
        {
            string titulo = (descricao ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
            if (titulo.Length > 80) titulo = titulo.Substring(0, 80) + "…";
            if (titulo.Length == 0) titulo = "Problema no PJe-Calc Backup " + Atualizacao.Atual;
            bool c;
            return LinkCortado(Atualizacao.UrlProjeto + "/issues/new?title=" + Uri.EscapeDataString(titulo) + "&body=", texto, 7000, out c);
        }
    }
}
