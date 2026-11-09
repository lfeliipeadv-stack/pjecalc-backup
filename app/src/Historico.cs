// Registro de cada operação (backup.json dentro da pasta da operação) e leitura do histórico.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace PjeCalcBackup
{
    public class RegistroVersao
    {
        public string Versao { get; set; }
        public string Pasta { get; set; }
        public int CalculosNoBanco { get; set; }
        public int Exportados { get; set; }
        public int Conferidos { get; set; }
        public bool BancoCopiado { get; set; }
        public int Copiados { get; set; }                         // .PJC reaproveitados do backup anterior (sem alteração)
        public Dictionary<string, string> Assinaturas { get; set; } // nº do cálculo → impressão digital
        public Dictionary<string, string> Arquivos { get; set; }    // nº do cálculo → .PJC (relativo à pasta da operação)
    }

    public class RegistroHistorico
    {
        public string Tipo { get; set; }        // backup | restauracao | migracao | restauracao-banco | numeracao
        public DateTime Data { get; set; }
        public string Resumo { get; set; }
        public int Calculos { get; set; }
        public int Falhas { get; set; }
        public List<RegistroVersao> Versoes { get; set; }
        public string PastaOperacao { get; set; } // preenchido na leitura

        public RegistroHistorico() { Versoes = new List<RegistroVersao>(); Data = DateTime.Now; }

        public string TipoTexto
        {
            get
            {
                switch (Tipo)
                {
                    case "restauracao": return "Restauração de .PJC";
                    case "migracao": return "Migração entre versões";
                    case "restauracao-banco": return "Restauração do banco";
                    case "numeracao": return "Mudança na numeração";
                    case "arquivo": return "Arquivo salvo";
                    default: return "Backup";
                }
            }
        }

        public bool TemPjc
        {
            get { return PastaOperacao != null && Directory.Exists(PastaOperacao) && Directory.EnumerateFiles(PastaOperacao, "*.PJC", SearchOption.AllDirectories).Any(); }
        }

        public bool TemBanco
        {
            get { return PastaOperacao != null && Directory.Exists(PastaOperacao) && Directory.EnumerateFiles(PastaOperacao, "*.zip", SearchOption.AllDirectories).Any(); }
        }
    }

    public static class Historico
    {
        public const string Arquivo = "backup.json";

        public static void Salvar(string pasta, RegistroHistorico r)
        {
            try { File.WriteAllText(Path.Combine(pasta, Arquivo), new JavaScriptSerializer().Serialize(r)); } catch { }
        }

        /// <summary>Operações registradas na pasta de backups (mais recentes primeiro).</summary>
        public static List<RegistroHistorico> Listar(string destino)
        {
            var lista = new List<RegistroHistorico>();
            if (string.IsNullOrEmpty(destino) || !Directory.Exists(destino)) return lista;
            var json = new JavaScriptSerializer();
            foreach (var d in Directory.GetDirectories(destino))
            {
                try
                {
                    RegistroHistorico r = null;
                    string arq = Path.Combine(d, Arquivo);
                    if (File.Exists(arq)) r = json.Deserialize<RegistroHistorico>(File.ReadAllText(arq));
                    else r = LerAntigo(d);
                    if (r == null) continue;
                    if (r.Data.Kind == DateTimeKind.Utc) r.Data = r.Data.ToLocalTime();
                    r.PastaOperacao = d;
                    lista.Add(r);
                }
                catch { }
            }
            return lista.OrderByDescending(r => r.Data).ToList();
        }

        /// <summary>Backups feitos pela versão 1.0/1.1 (sem backup.json): lê relatorio.txt e as subpastas.</summary>
        private static RegistroHistorico LerAntigo(string d)
        {
            var m = Regex.Match(Path.GetFileName(d), "^(\\d{4}-\\d{2}-\\d{2}_\\d{4})");
            string rel = Path.Combine(d, "relatorio.txt");
            if (!m.Success || !File.Exists(rel)) return null;
            DateTime data;
            if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out data)) return null;
            string primeira = File.ReadLines(rel).FirstOrDefault() ?? "";
            var r = new RegistroHistorico
            {
                Data = data,
                Tipo = primeira.StartsWith("Restaura") ? "restauracao" : "backup",
                Resumo = primeira
            };
            var mc = Regex.Match(primeira, "(\\d+) cálculo");
            if (mc.Success) r.Calculos = int.Parse(mc.Groups[1].Value);
            var mf = Regex.Match(primeira, "(\\d+) falha");
            if (mf.Success) r.Falhas = int.Parse(mf.Groups[1].Value);
            foreach (var sub in Directory.GetDirectories(d, "PJe-Calc *"))
                r.Versoes.Add(new RegistroVersao { Versao = Path.GetFileName(sub).Replace("PJe-Calc ", "") });
            return r;
        }

        /// <summary>Data do último backup bem-sucedido (ou null).</summary>
        public static DateTime? UltimoBackup(string destino)
        {
            var b = Listar(destino).FirstOrDefault(r => r.Tipo == "backup" && r.Calculos > 0);
            return b == null ? (DateTime?)null : b.Data;
        }
    }
}
