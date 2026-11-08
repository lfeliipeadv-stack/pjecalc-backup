// Ferramentas da versão 1.3: limpeza de backups antigos, backup agendado (Agendador de Tarefas do Windows),
// planilha Excel dos cálculos e busca de cálculos dentro dos backups.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml;

namespace PjeCalcBackup
{
    // ================================================================== limpeza de backups antigos

    public static class Limpeza
    {
        public static readonly string[] Codigos = { "", "q5", "q10", "q20", "q30", "m3", "m6", "m12" };

        public static string Descrever(string codigo)
        {
            if (string.IsNullOrEmpty(codigo)) return "Desligada";
            int n = int.Parse(codigo.Substring(1));
            return codigo[0] == 'q' ? "Manter só os últimos " + n + " backups" : "Apagar backups com mais de " + n + (n == 1 ? " mês" : " meses");
        }

        /// <summary>Só mexe em pastas criadas pelo programa: dentro da pasta de backups, com nome de data e com relatório.</summary>
        private static bool PastaDoPrograma(string destino, string pasta)
        {
            try
            {
                string pai = Path.GetFullPath(Path.GetDirectoryName(pasta.TrimEnd('\\'))).TrimEnd('\\');
                if (!string.Equals(pai, Path.GetFullPath(destino).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
                if (!Regex.IsMatch(Path.GetFileName(pasta), "^\\d{4}-\\d{2}-\\d{2}_\\d{4}")) return false;
                return File.Exists(Path.Combine(pasta, "relatorio.txt")) || File.Exists(Path.Combine(pasta, Historico.Arquivo));
            }
            catch { return false; }
        }

        /// <summary>Backups que a regra manda apagar. Nunca inclui o backup mais recente sem falhas.
        /// Cópias de segurança de restauração/migração não entram.</summary>
        public static List<RegistroHistorico> Candidatos(string destino, string codigo)
        {
            var r = new List<RegistroHistorico>();
            if (string.IsNullOrEmpty(codigo)) return r;
            var backups = Historico.Listar(destino)
                .Where(x => x.Tipo == "backup" && x.PastaOperacao != null && PastaDoPrograma(destino, x.PastaOperacao))
                .OrderByDescending(x => x.Data).ToList();
            if (backups.Count == 0) return r;
            var protegido = backups.FirstOrDefault(x => x.Falhas == 0) ?? backups[0];
            int n = int.Parse(codigo.Substring(1));
            IEnumerable<RegistroHistorico> sai = codigo[0] == 'q'
                ? backups.Skip(n)
                : backups.Where(x => x.Data < DateTime.Now.AddMonths(-n));
            return sai.Where(x => x != protegido).ToList();
        }

        public static long Tamanho(string pasta)
        {
            try { return Directory.EnumerateFiles(pasta, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
            catch { return 0; }
        }

        public static string TextoTamanho(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0") + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0") + " MB";
            return Math.Max(1, bytes / 1024) + " KB";
        }

        /// <summary>A Lixeira só existe em discos internos. Em pendrive/rede o Windows apagaria de vez.</summary>
        public static bool TemLixeira(string destino)
        {
            try
            {
                if (destino.StartsWith("\\\\")) return false;
                return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destino))).DriveType == DriveType.Fixed;
            }
            catch { return false; }
        }

        /// <summary>Move as pastas para a Lixeira do Windows (dá para recuperar). Devolve quantas foram movidas.</summary>
        public static int MoverParaLixeira(IEnumerable<RegistroHistorico> lista, Action<string> erro)
        {
            int n = 0;
            foreach (var r in lista)
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(r.PastaOperacao,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    n++;
                }
                catch (Exception ex) { if (erro != null) erro(Path.GetFileName(r.PastaOperacao) + ": " + ex.Message); }
            }
            return n;
        }
    }

    // ================================================================== backup agendado

    public static class Agendador
    {
        public const string Nome = "PJe-Calc Backup automático";
        public static readonly string[] Dias = { "MON", "TUE", "WED", "THU", "FRI", "SAT", "SUN" };
        public static readonly string[] DiasPt = { "seg", "ter", "qua", "qui", "sex", "sáb", "dom" };
        private static readonly string[] DiasXml = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

        public class Estado
        {
            public bool Existe;
            public string Proxima, Ultima, Resultado, Comando;
            public bool ExeExiste { get { return !string.IsNullOrEmpty(Comando) && File.Exists(Comando); } }
        }

        public static string ExeConsole()
        {
            string dir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            return Path.Combine(dir, "PJeCalcBackup-Console.exe");
        }

        private static int Rodar(string args, out string saida)
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
                StandardErrorEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
            };
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                saida = (o + e).Trim();
                return p.ExitCode;
            }
        }

        public static string Descrever(string frequencia, List<string> dias, string hora)
        {
            if (frequencia == "diario") return "todos os dias às " + hora;
            var nomes = Dias.Select((d, i) => dias.Contains(d) ? DiasPt[i] : null).Where(x => x != null).ToList();
            return "toda " + string.Join(", ", nomes) + " às " + hora;
        }

        /// <summary>Cria (ou substitui) a tarefa no Agendador do Windows. Roda só com o usuário conectado,
        /// e roda assim que possível se o computador estava desligado na hora marcada.</summary>
        public static void Criar(string frequencia, List<string> dias, string hora, bool limpar)
        {
            string exe = ExeConsole();
            if (!File.Exists(exe)) throw new Exception("Não encontrei o PJeCalcBackup-Console.exe ao lado deste programa (" + exe + "). Baixe o pacote completo (.zip) do GitHub.");
            TimeSpan h;
            if (!TimeSpan.TryParseExact(hora, "hh\\:mm", CultureInfo.InvariantCulture, out h)) throw new Exception("Horário inválido: " + hora);
            if (frequencia != "diario" && (dias == null || dias.Count == 0)) throw new Exception("Escolha pelo menos um dia da semana.");
            string xml = MontarXml(frequencia, dias, h, limpar, exe);
            string tmp = Path.Combine(Path.GetTempPath(), "pjecalc-backup-tarefa.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            try
            {
                string saida;
                if (Rodar("/Create /TN \"" + Nome + "\" /XML \"" + tmp + "\" /F", out saida) != 0)
                    throw new Exception("O Agendador de Tarefas recusou: " + saida);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        /// <summary>Definição da tarefa no formato do Agendador de Tarefas (mesma ordem dos elementos que o Windows exporta).</summary>
        public static string MontarXml(string frequencia, List<string> dias, TimeSpan h, bool limpar, string exe)
        {
            string inicio = DateTime.Today.Add(h).ToString("yyyy-MM-dd'T'HH:mm:ss");
            string gatilho = frequencia == "diario"
                ? "<ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>"
                : "<ScheduleByWeek><DaysOfWeek>" + string.Concat(Dias.Select((d, i) => dias.Contains(d) ? "<" + DiasXml[i] + "/>" : "")) +
                  "</DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek>";
            string usuario = Environment.UserDomainName + "\\" + Environment.UserName;
            string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Description>Backup dos cálculos do PJe-Calc, criado pelo PJe-Calc Backup.</Description></RegistrationInfo>\r\n" +
"  <Triggers><CalendarTrigger><StartBoundary>" + inicio + "</StartBoundary><Enabled>true</Enabled>" + gatilho + "</CalendarTrigger></Triggers>\r\n" +
"  <Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(usuario) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\r\n" +
"  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
"<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable>" +
"<IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings><Enabled>true</Enabled><ExecutionTimeLimit>PT6H</ExecutionTimeLimit></Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(exe) + "</Command><Arguments>--backup --agendado" + (limpar ? " --limpar" : "") +
"</Arguments><WorkingDirectory>" + SecurityElement.Escape(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions>\r\n" +
"</Task>\r\n";
            return xml;
        }

        public static void Remover()
        {
            string saida;
            if (Consultar().Existe && Rodar("/Delete /TN \"" + Nome + "\" /F", out saida) != 0)
                throw new Exception("O Agendador de Tarefas recusou: " + saida);
        }

        public static Estado Consultar()
        {
            var e = new Estado();
            try
            {
                string saida;
                if (Rodar("/Query /TN \"" + Nome + "\" /FO CSV /V /NH", out saida) != 0) return e;
                var linha = saida.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("\""));
                if (linha == null) return e;
                var c = Csv(linha);
                e.Existe = true;
                if (c.Count > 6)
                {
                    e.Proxima = c[2]; e.Ultima = c[5];
                    int cod;
                    e.Resultado = int.TryParse(c[6], out cod)
                        ? (cod == 0 ? "concluído" : cod == 267011 ? "ainda não rodou" : cod == 267009 ? "rodando agora" : cod == 1 ? "terminou com falhas" : "código " + cod)
                        : c[6];
                }
                if (Rodar("/Query /TN \"" + Nome + "\" /XML", out saida) == 0)
                {
                    var m = Regex.Match(saida, "<Command>(.*?)</Command>", RegexOptions.Singleline);
                    if (m.Success) e.Comando = SecurityElementDecode(m.Groups[1].Value.Trim());
                }
            }
            catch { }
            return e;
        }

        private static string SecurityElementDecode(string s)
        {
            return s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");
        }

        private static List<string> Csv(string linha)
        {
            var r = new List<string>(); var sb = new StringBuilder(); bool aspas = false;
            for (int i = 0; i < linha.Length; i++)
            {
                char ch = linha[i];
                if (ch == '"') { if (aspas && i + 1 < linha.Length && linha[i + 1] == '"') { sb.Append('"'); i++; } else aspas = !aspas; }
                else if (ch == ',' && !aspas) { r.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
            }
            r.Add(sb.ToString());
            return r;
        }
    }

    // ================================================================== planilha Excel (.xlsx)

    public static class Planilha
    {
        public class Linha { public string Versao; public CalculoInfo C; }

        private static readonly string[] Colunas = { "Versão", "Nº", "Reclamante", "Reclamado", "Processo", "Admissão", "Demissão", "Ajuizamento", "Início do cálculo", "Término do cálculo", "Criado em" };
        private static readonly double[] Larguras = { 10, 7, 38, 38, 26, 12, 12, 13, 16, 17, 12 };

        /// <summary>Gera um .xlsx simples (uma aba, cabeçalho fixo, filtro), sem precisar do Excel instalado.</summary>
        public static void Salvar(string caminho, List<Linha> linhas)
        {
            string tmp = caminho + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var z = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                Parte(z, "[Content_Types].xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                    "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                    "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
                Parte(z, "_rels/.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                Parte(z, "xl/workbook.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                    "<sheets><sheet name=\"Cálculos\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
                    "<definedNames><definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"0\" hidden=\"1\">'Cálculos'!$A$1:$K$" + (linhas.Count + 1) + "</definedName></definedNames></workbook>");
                Parte(z, "xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                    "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
                Parte(z, "xl/styles.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                    "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"dd/mm/yyyy\"/></numFmts>" +
                    "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font></fonts>" +
                    "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
                    "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF2F5D8F\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
                    "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                    "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                    "<cellXfs count=\"3\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                    "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
                    "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs>" +
                    "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");

                var e = z.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
                using (var s = e.Open())
                using (var w = XmlWriter.Create(s, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
                {
                    const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                    w.WriteStartDocument(true);
                    w.WriteStartElement("worksheet", ns);
                    w.WriteStartElement("sheetViews", ns); w.WriteStartElement("sheetView", ns); w.WriteAttributeString("workbookViewId", "0");
                    w.WriteStartElement("pane", ns); w.WriteAttributeString("ySplit", "1"); w.WriteAttributeString("topLeftCell", "A2");
                    w.WriteAttributeString("activePane", "bottomLeft"); w.WriteAttributeString("state", "frozen"); w.WriteEndElement();
                    w.WriteEndElement(); w.WriteEndElement();
                    w.WriteStartElement("cols", ns);
                    for (int i = 0; i < Larguras.Length; i++)
                    {
                        w.WriteStartElement("col", ns);
                        w.WriteAttributeString("min", (i + 1).ToString()); w.WriteAttributeString("max", (i + 1).ToString());
                        w.WriteAttributeString("width", Larguras[i].ToString(CultureInfo.InvariantCulture)); w.WriteAttributeString("customWidth", "1");
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                    w.WriteStartElement("sheetData", ns);
                    Linhaxml(w, ns, 1, Colunas.Select(c => (object)c).ToArray(), true);
                    int r = 2;
                    foreach (var l in linhas)
                    {
                        var c = l.C;
                        int num;
                        Linhaxml(w, ns, r++, new object[]
                        {
                            l.Versao, int.TryParse(c.Numero, out num) ? (object)num : c.Numero, c.Reclamante, c.Reclamado, c.Processo,
                            Data(c.Admissao), Data(c.Demissao), Data(c.Ajuizamento), Data(c.Inicio), Data(c.Termino), Data(c.Criacao)
                        }, false);
                    }
                    w.WriteEndElement();
                    w.WriteStartElement("autoFilter", ns); w.WriteAttributeString("ref", "A1:K" + (linhas.Count + 1)); w.WriteEndElement();
                    w.WriteEndElement();
                    w.WriteEndDocument();
                }
            }
            if (File.Exists(caminho)) File.Delete(caminho);
            File.Move(tmp, caminho);
        }

        private static object Data(string iso)
        {
            DateTime d;
            if (!string.IsNullOrEmpty(iso) && DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
            return null;
        }

        private static void Parte(ZipArchive z, string nome, string conteudo)
        {
            var e = z.CreateEntry(nome, CompressionLevel.Optimal);
            using (var s = new StreamWriter(e.Open(), new UTF8Encoding(false))) s.Write(conteudo);
        }

        private static string Limpo(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s) if (XmlConvert.IsXmlChar(ch)) sb.Append(ch);
            return sb.ToString();
        }

        private static void Linhaxml(XmlWriter w, string ns, int r, object[] valores, bool cabecalho)
        {
            w.WriteStartElement("row", ns); w.WriteAttributeString("r", r.ToString());
            for (int i = 0; i < valores.Length; i++)
            {
                object v = valores[i];
                if (v == null || (v is string && ((string)v).Length == 0)) continue;
                string refc = ((char)('A' + i)).ToString() + r;
                w.WriteStartElement("c", ns); w.WriteAttributeString("r", refc);
                if (cabecalho) w.WriteAttributeString("s", "1");
                if (v is DateTime)
                {
                    w.WriteAttributeString("s", "2");
                    w.WriteElementString("v", ns, ((DateTime)v).ToOADate().ToString(CultureInfo.InvariantCulture));
                }
                else if (v is int) w.WriteElementString("v", ns, ((int)v).ToString(CultureInfo.InvariantCulture));
                else
                {
                    w.WriteAttributeString("t", "inlineStr");
                    w.WriteStartElement("is", ns); w.WriteElementString("t", ns, Limpo(Convert.ToString(v))); w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
    }

    // ================================================================== busca de cálculos nos backups

    public class AchadoBusca
    {
        public CalculoInfo Info;
        public string Arquivo, Versao, PastaOperacao, TipoOperacao;
        public DateTime Data;
    }

    public static class BuscaBackups
    {
        public class ItemIndice
        {
            public long Tamanho { get; set; }
            public long Modificado { get; set; }
            public string Numero { get; set; }
            public string Reclamante { get; set; }
            public string Reclamado { get; set; }
            public string Processo { get; set; }
            public string Admissao { get; set; }
            public string Demissao { get; set; }
            public string Ajuizamento { get; set; }
            public string Inicio { get; set; }
            public string Termino { get; set; }
            public string Criacao { get; set; }
        }

        private static string ArquivoIndice
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"PJeCalcBackup\indice-busca.json"); }
        }

        private static Dictionary<string, ItemIndice> CarregarIndice()
        {
            try
            {
                if (File.Exists(ArquivoIndice))
                {
                    var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    var d = js.Deserialize<Dictionary<string, ItemIndice>>(File.ReadAllText(ArquivoIndice));
                    if (d != null) return new Dictionary<string, ItemIndice>(d, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { }
            return new Dictionary<string, ItemIndice>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Versão pelo nome das pastas ("PJe-Calc 2.16.0", "Exportados 2.14.0", "… (PJe-Calc 2.16.0)").</summary>
        private static string VersaoDoCaminho(string destino, string arquivo)
        {
            string rel = Path.GetDirectoryName(arquivo).Substring(Math.Min(Path.GetDirectoryName(arquivo).Length, destino.TrimEnd('\\').Length));
            var ms = Regex.Matches(rel, "(?:PJe-Calc|Exportados|Banco)\\s+(\\d+\\.\\d+(?:\\.\\d+)?)", RegexOptions.IgnoreCase);
            return ms.Count > 0 ? ms[ms.Count - 1].Groups[1].Value : "";
        }

        /// <summary>Lê todos os .PJC da pasta de backups (usa um índice para não reler arquivos que não mudaram).</summary>
        public static List<AchadoBusca> Indexar(string destino, Action<int, int> progresso, Func<bool> cancelado)
        {
            var achados = new List<AchadoBusca>();
            if (string.IsNullOrEmpty(destino) || !Directory.Exists(destino)) return achados;
            var indice = CarregarIndice();
            var novo = new Dictionary<string, ItemIndice>(StringComparer.OrdinalIgnoreCase);
            // todos os .PJC da pasta de backups; os que estão dentro de uma operação registrada levam a data dela
            var operacoes = Historico.Listar(destino);
            var arquivos = new List<Tuple<string, RegistroHistorico>>();
            var pilha = new Stack<string>(); pilha.Push(destino);
            while (pilha.Count > 0)
            {
                string d = pilha.Pop();
                try
                {
                    foreach (var sub in Directory.GetDirectories(d)) pilha.Push(sub);
                    foreach (var f in Directory.GetFiles(d, "*.PJC"))
                    {
                        var op = operacoes.FirstOrDefault(o => f.StartsWith(o.PastaOperacao.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
                        if (op == null)
                        {
                            DateTime quando; try { quando = File.GetLastWriteTime(f); } catch { quando = DateTime.MinValue; }
                            op = new RegistroHistorico { Tipo = "arquivo", Data = quando, PastaOperacao = Path.GetDirectoryName(f) };
                        }
                        arquivos.Add(Tuple.Create(f, op));
                    }
                }
                catch { }
            }
            int i = 0;
            foreach (var t in arquivos)
            {
                if (cancelado != null && cancelado()) break;
                i++;
                if (progresso != null && (i % 10 == 0 || i == arquivos.Count)) progresso(i, arquivos.Count);
                try
                {
                    var fi = new FileInfo(t.Item1);
                    ItemIndice it;
                    if (!indice.TryGetValue(fi.FullName, out it) || it.Tamanho != fi.Length || it.Modificado != fi.LastWriteTimeUtc.Ticks)
                    {
                        var c = BancoPjeCalc.LerPjc(fi.FullName);
                        it = new ItemIndice
                        {
                            Tamanho = fi.Length, Modificado = fi.LastWriteTimeUtc.Ticks, Numero = c.Numero, Reclamante = c.Reclamante, Reclamado = c.Reclamado,
                            Processo = c.Processo, Admissao = c.Admissao, Demissao = c.Demissao, Ajuizamento = c.Ajuizamento, Inicio = c.Inicio, Termino = c.Termino, Criacao = c.Criacao
                        };
                    }
                    novo[fi.FullName] = it;
                    achados.Add(new AchadoBusca
                    {
                        Arquivo = fi.FullName, Versao = VersaoDoCaminho(destino, fi.FullName),
                        PastaOperacao = t.Item2.PastaOperacao, TipoOperacao = t.Item2.TipoTexto, Data = t.Item2.Data,
                        Info = new CalculoInfo
                        {
                            Numero = it.Numero, Reclamante = it.Reclamante, Reclamado = it.Reclamado, Processo = it.Processo, Admissao = it.Admissao,
                            Demissao = it.Demissao, Ajuizamento = it.Ajuizamento, Inicio = it.Inicio, Termino = it.Termino, Criacao = it.Criacao, Arquivo = fi.FullName
                        }
                    });
                }
                catch { }
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ArquivoIndice));
                File.WriteAllText(ArquivoIndice, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(novo));
            }
            catch { }
            return achados.OrderByDescending(a => a.Data).ToList();
        }

        private static string SoDigitos(string s) { return new string((s ?? "").Where(char.IsDigit).ToArray()); }

        /// <summary>Procura por nome do reclamante/reclamado, número do processo ou número do cálculo.</summary>
        public static List<AchadoBusca> Filtrar(List<AchadoBusca> todos, string termo)
        {
            if (string.IsNullOrWhiteSpace(termo)) return new List<AchadoBusca>();
            string t = CalculoInfo.Normalizar(termo);
            string dig = SoDigitos(termo);
            var palavras = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return todos.Where(a =>
            {
                string texto = CalculoInfo.Normalizar((a.Info.Reclamante ?? "") + " " + (a.Info.Reclamado ?? "") + " " + (a.Info.Processo ?? ""));
                if (palavras.All(p => texto.Contains(p))) return true;
                if (dig.Length >= 4 && SoDigitos(a.Info.Processo).Contains(dig)) return true;
                return dig.Length > 0 && dig == termo.Trim() && a.Info.Numero == dig;
            }).ToList();
        }
    }
}
