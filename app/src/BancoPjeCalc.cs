// Leitura dos cálculos SEM abrir o PJe-Calc:
//  - do banco de dados (cópia temporária, consultada com o H2 da própria instalação)
//  - de arquivos .PJC (zip com um XML)
// Também gera a "assinatura" usada para detectar cálculos duplicados.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace PjeCalcBackup
{
    public class CalculoInfo
    {
        public string Numero;
        public string Reclamante;
        public string Reclamado;
        public string Processo;          // NNNNNNN-DD.AAAA ou vazio
        public string Criacao, Admissao, Demissao, Ajuizamento, Inicio, Termino; // yyyy-MM-dd
        public bool Ativo = true;
        public string Arquivo;           // quando veio de um .PJC

        /// <summary>Assinatura para detectar duplicados (não usa o número nem a data de criação,
        /// que mudam ao importar).</summary>
        public string Chave
        {
            get
            {
                return string.Join("|", new[] { Normalizar(Reclamante), Normalizar(Reclamado), Processo ?? "",
                    Admissao ?? "", Demissao ?? "", Ajuizamento ?? "", Inicio ?? "", Termino ?? "" });
            }
        }

        public string Resumo
        {
            get
            {
                var p = new List<string>();
                if (!string.IsNullOrEmpty(Reclamado)) p.Add("× " + Reclamado);
                if (!string.IsNullOrEmpty(Processo)) p.Add("Proc. " + Processo);
                if (!string.IsNullOrEmpty(Ajuizamento)) p.Add("Ajuiz. " + DataBr(Ajuizamento));
                if (!string.IsNullOrEmpty(Criacao)) p.Add("Criado em " + DataBr(Criacao));
                return string.Join("   ·   ", p);
            }
        }

        public static string DataBr(string iso)
        {
            DateTime d;
            return DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d.ToString("dd/MM/yyyy") : iso;
        }

        public static string Normalizar(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string d = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char c in d)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return Regex.Replace(sb.ToString().ToUpperInvariant(), "\\s+", " ").Trim();
        }
    }

    public static class BancoPjeCalc
    {
        /// <summary>
        /// Lista os cálculos de um arquivo de banco (pjecalc.h2.db). Trabalha numa cópia
        /// temporária, então funciona mesmo com o PJe-Calc aberto e nunca altera o original.
        /// </summary>
        public static List<CalculoInfo> ListarDoArquivo(Instalacao inst, string arquivoBanco)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "pjecalc-leitura-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                File.Copy(arquivoBanco, Path.Combine(tmp, "pjecalc.h2.db"));
                string csv = Path.Combine(tmp, "lista.csv");
                string sql = "CALL CSVWRITE('" + csv.Replace("'", "''") + "', 'SELECT c.IIDCALCULO, c.DDTCRIACAOCALCULO, c.DDTADMISSAO, c.DDTDEMISSAO, " +
                    "c.DDTAJUIZAMENTO, c.DDTINICIOCALCULO, c.DDTTERMINOCALCULO, c.SFLATIVO, p.SNMRECLAMANTE, p.SNMRECLAMADO, " +
                    "p.INRPROCESSOUNICA, p.INRDIGITOUNICA, p.INRPROCESSOANOUNICA FROM TBCALCULO c LEFT JOIN TBPROCESSOCALCULO p " +
                    "ON p.IIDPROCESSOCALCULO = c.IIDPROCESSOCALCULO ORDER BY c.IIDCALCULO', 'UTF-8', ';')";
                string h2 = Directory.GetFiles(Path.Combine(inst.Pasta, @"bin\lib"), "h2-*.jar").FirstOrDefault();
                if (h2 == null || !File.Exists(inst.JavaExe)) throw new Exception("não encontrei o Java/H2 da instalação");

                var psi = new ProcessStartInfo(inst.JavaExe,
                    "-cp \"" + h2 + "\" org.h2.tools.Shell -url \"jdbc:h2:" + Path.Combine(tmp, "pjecalc") + "\" -user \"" + inst.UsuarioBanco +
                    "\" -password \"" + inst.SenhaBanco + "\" -sql \"" + sql.Replace("\"", "\\\"") + "\"");
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                using (var p = Process.Start(psi))
                {
                    var o = p.StandardOutput.ReadToEndAsync();
                    var e = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } throw new Exception("a leitura do banco demorou demais"); }
                    if (!File.Exists(csv)) throw new Exception("não consegui ler o banco: " + (e.Result + o.Result).Trim().Split('\n').FirstOrDefault());
                }
                return LerCsv(csv);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        /// <summary>Lista os cálculos ativos da instalação (sem abrir o PJe-Calc).</summary>
        public static List<CalculoInfo> Listar(Instalacao inst)
        {
            if (!File.Exists(inst.ArquivoBanco)) return new List<CalculoInfo>();
            return ListarDoArquivo(inst, inst.ArquivoBanco).Where(c => c.Ativo).ToList();
        }

        /// <summary>Lista os cálculos de um backup do banco (.zip criado pelo programa).</summary>
        public static List<CalculoInfo> ListarDoZip(Instalacao instParaJava, string zip)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "pjecalc-zip-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                string db = ExtrairBancoDoZip(zip, tmp);
                return ListarDoArquivo(instParaJava, db).Where(c => c.Ativo).ToList();
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        public static string ExtrairBancoDoZip(string zip, string pasta)
        {
            using (var z = ZipFile.OpenRead(zip))
            {
                var e = z.Entries.FirstOrDefault(x => x.Name.EndsWith(".h2.db", StringComparison.OrdinalIgnoreCase));
                if (e == null) throw new Exception("o arquivo não contém um banco do PJe-Calc (pjecalc.h2.db)");
                string destino = Path.Combine(pasta, "pjecalc.h2.db");
                e.ExtractToFile(destino, true);
                return destino;
            }
        }

        /// <summary>Versão do PJe-Calc registrada no LEIA-ME do zip (ou no nome do arquivo).</summary>
        public static string VersaoDoZip(string zip)
        {
            try
            {
                using (var z = ZipFile.OpenRead(zip))
                {
                    var e = z.Entries.FirstOrDefault(x => x.Name.Equals("LEIA-ME.txt", StringComparison.OrdinalIgnoreCase));
                    if (e != null)
                        using (var r = new StreamReader(e.Open()))
                        {
                            var m = Regex.Match(r.ReadToEnd(), "PJe-Calc\\s+([0-9.]+)");
                            if (m.Success) return m.Groups[1].Value.TrimEnd('.');
                        }
                }
            }
            catch { }
            var n = Regex.Match(Path.GetFileName(zip), "(\\d+\\.\\d+\\.\\d+)");
            return n.Success ? n.Groups[1].Value : null;
        }

        private static List<CalculoInfo> LerCsv(string arquivo)
        {
            var lista = new List<CalculoInfo>();
            var linhas = File.ReadAllLines(arquivo, Encoding.UTF8);
            for (int i = 1; i < linhas.Length; i++)
            {
                var c = DividirCsv(linhas[i]);
                if (c.Count < 13) continue;
                string proc = "";
                if (!string.IsNullOrEmpty(c[10]))
                    proc = c[10].PadLeft(7, '0') + (string.IsNullOrEmpty(c[11]) ? "" : "-" + c[11].PadLeft(2, '0')) + (string.IsNullOrEmpty(c[12]) ? "" : "." + c[12]);
                lista.Add(new CalculoInfo
                {
                    Numero = c[0], Criacao = c[1], Admissao = c[2], Demissao = c[3], Ajuizamento = c[4], Inicio = c[5], Termino = c[6],
                    Ativo = c[7] != "N", Reclamante = c[8], Reclamado = c[9], Processo = proc
                });
            }
            return lista;
        }

        private static List<string> DividirCsv(string linha)
        {
            var r = new List<string>();
            var sb = new StringBuilder();
            bool aspas = false;
            for (int i = 0; i < linha.Length; i++)
            {
                char ch = linha[i];
                if (aspas)
                {
                    if (ch == '"' && i + 1 < linha.Length && linha[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (ch == '"') aspas = false;
                    else sb.Append(ch);
                }
                else if (ch == '"') aspas = true;
                else if (ch == ';') { r.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
            }
            r.Add(sb.ToString());
            return r;
        }

        // ------------------------------------------------------------- arquivos .PJC

        /// <summary>Lê as informações de identificação de um arquivo .PJC (sem abrir o PJe-Calc).</summary>
        public static CalculoInfo LerPjc(string arquivo)
        {
            using (var z = ZipFile.OpenRead(arquivo))
            {
                var e = z.Entries.FirstOrDefault();
                if (e == null) throw new Exception("arquivo .PJC vazio");
                var cfg = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                var info = new CalculoInfo { Arquivo = arquivo };
                using (var r = XmlReader.Create(e.Open(), cfg))
                {
                    // percorre só o começo do XML (cabeçalho do cálculo e do processo)
                    var caminho = new List<string>();
                    string num = null, dig = null, ano = null;
                    while (r.Read())
                    {
                        if (r.NodeType == XmlNodeType.Element)
                        {
                            if (r.IsEmptyElement) continue;
                            caminho.Add(r.Name);
                            if (r.Name == "verbas" || r.Name == "historicosSalariais") break; // daqui em diante não interessa
                        }
                        else if (r.NodeType == XmlNodeType.EndElement)
                        {
                            if (caminho.Count > 0) caminho.RemoveAt(caminho.Count - 1);
                        }
                        else if (r.NodeType == XmlNodeType.Text)
                        {
                            string v = r.Value == "null" ? null : r.Value;
                            string atual = string.Join("/", caminho);
                            switch (atual)
                            {
                                case "Calculo/id": info.Numero = v; break;
                                case "Calculo/ativo": info.Ativo = v != "false"; break;
                                case "Calculo/dataCriacao": info.Criacao = Data(v); break;
                                case "Calculo/dataAdmissao": info.Admissao = Data(v); break;
                                case "Calculo/dataDemissao": info.Demissao = Data(v); break;
                                case "Calculo/dataAjuizamento": info.Ajuizamento = Data(v); break;
                                case "Calculo/dataInicioCalculo": info.Inicio = Data(v); break;
                                case "Calculo/dataTerminoCalculo": info.Termino = Data(v); break;
                                case "Calculo/processo/Processo/reclamante/Reclamante/nome": info.Reclamante = v; break;
                                case "Calculo/processo/Processo/reclamado/Reclamado/nome": info.Reclamado = v; break;
                                case "Calculo/processo/Processo/identificador/IdentificadorDoProcesso/numero": num = v; break;
                                case "Calculo/processo/Processo/identificador/IdentificadorDoProcesso/digito": dig = v; break;
                                case "Calculo/processo/Processo/identificador/IdentificadorDoProcesso/ano": ano = v; break;
                            }
                        }
                    }
                    info.Processo = string.IsNullOrEmpty(num) ? "" :
                        num.PadLeft(7, '0') + (string.IsNullOrEmpty(dig) ? "" : "-" + dig.PadLeft(2, '0')) + (string.IsNullOrEmpty(ano) ? "" : "." + ano);
                }
                return info;
            }
        }

        /// <summary>Datas do .PJC vêm em milissegundos; o PJe-Calc roda no fuso GMT-3.</summary>
        private static string Data(string ms)
        {
            long v;
            if (string.IsNullOrEmpty(ms) || !long.TryParse(ms, out v)) return "";
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(v).AddHours(-3).ToString("yyyy-MM-dd");
        }

        /// <summary>Mapa assinatura -> número do cálculo já existente (para avisar duplicados).</summary>
        public static Dictionary<string, string> MapaDuplicados(IEnumerable<CalculoInfo> existentes)
        {
            var d = new Dictionary<string, string>();
            foreach (var c in existentes)
                if (!d.ContainsKey(c.Chave)) d[c.Chave] = c.Numero;
            return d;
        }
    }
}
