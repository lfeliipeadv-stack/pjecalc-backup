// Aba Numeração: lê e muda a numeração dos cálculos direto no banco do PJe-Calc.
//
// Regras de segurança:
//  - o PJe-Calc precisa estar fechado;
//  - antes de tudo, o banco atual é guardado na pasta de backups (dá para desfazer pelo programa
//    ou pela aba Histórico do PJe-Calc Backup);
//  - as mudanças são feitas numa CÓPIA do banco e conferidas (mesmos registros de cada cálculo,
//    nenhum registro solto, contador certo). Só então a cópia substitui o banco original.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace PjeCalcBackup
{
    public enum NivelLog { Info, Ok, Aviso, Erro }

    public class CalculoNumerado
    {
        public long Numero;
        public bool Ativo;
        public string Reclamante, Reclamado, Processo, Criacao;
    }

    /// <summary>Uma coluna de outra tabela que guarda o número do cálculo.</summary>
    public class LigacaoCalculo
    {
        public string Tabela, Coluna;
    }

    public class EstadoNumeracao
    {
        public long Contador;                 // último número entregue (CURRENT_VALUE da sequência SQCALCULO)
        public int Reserva;                   // quantos números o banco reserva de cada vez (CACHE)
        public List<CalculoNumerado> Calculos = new List<CalculoNumerado>();
        public List<LigacaoCalculo> Ligacoes = new List<LigacaoCalculo>();
        /// <summary>Motivo para não permitir mudar o número dos cálculos nesta versão (null = permitido).</summary>
        public string BloqueioEdicao;
        /// <summary>Motivo para não permitir mudar a reserva (null = permitido).</summary>
        public string BloqueioReserva;

        public long Maior { get { return Calculos.Count == 0 ? 0 : Calculos.Max(c => c.Numero); } }
        /// <summary>Número que o próximo cálculo criado vai receber.</summary>
        public long Proximo { get { return Math.Max(Contador, Maior) + 1; } }
        /// <summary>Números que ficaram sem uso entre os cálculos existentes (os "pulos").</summary>
        public long Buracos
        {
            get
            {
                long n = 0, ant = 0;
                foreach (var c in Calculos.OrderBy(x => x.Numero)) { if (c.Numero > ant + 1) n += c.Numero - ant - 1; ant = c.Numero; }
                return n;
            }
        }
    }

    public class Mudanca
    {
        public long De, Para;
    }

    public class ResultadoNumeracao
    {
        public bool Ok;
        public string PastaSeguranca;
        public string Resumo;
        public long Proximo;
        public int Mudancas;     // quantos cálculos mudaram de número
    }

    /// <summary>O que fica gravado na pasta de segurança (numeracao.json), para desfazer.</summary>
    public class RegistroNumeracao
    {
        public string Versao { get; set; }
        public string PastaInstalacao { get; set; }
        public List<Mudanca> Mudancas { get; set; }
        public int ReservaAntes { get; set; }
        public int ReservaDepois { get; set; }
        public long ProximoAntes { get; set; }
        public long ProximoDepois { get; set; }
        public string Sha256Antes { get; set; }
        public string Sha256Depois { get; set; }
        public bool Desfeito { get; set; }
        public DateTime Data { get; set; }
    }

    public static class Numeracao
    {
        public const string ArquivoRegistro = "numeracao.json";

        /// <summary>
        /// Versões em que foi conferido no código do PJe-Calc que o "hash" de validação do cálculo
        /// (calcularHashCodeDaLiquidacao e calcularHashCodeExpandidoDoCalculo) não usa o número do cálculo.
        /// Numa versão nova, a edição fica bloqueada até ela ser conferida.
        /// </summary>
        public static readonly string[] VersoesConferidas = { "2.13.0", "2.14.0", "2.16.0" };

        public static bool VersaoConferida(string versao)
        {
            return VersoesConferidas.Contains((versao ?? "").Trim());
        }

        private static readonly Regex NomeSeguro = new Regex("^[A-Z][A-Z0-9_]*$");

        // ------------------------------------------------------------------ leitura

        /// <summary>Lê os cálculos, o contador e a reserva (numa cópia; funciona com o PJe-Calc aberto).</summary>
        public static EstadoNumeracao Ler(Instalacao inst)
        {
            if (!File.Exists(inst.ArquivoBanco)) throw new Exception("esta versão ainda não tem banco de dados (nunca foi aberta)");
            string tmp = PastaTemporaria("leitura");
            try
            {
                File.Copy(inst.ArquivoBanco, Path.Combine(tmp, "pjecalc.h2.db"));
                return LerCopia(inst, tmp);
            }
            finally { Apagar(tmp); }
        }

        private static EstadoNumeracao LerCopia(Instalacao inst, string tmp)
        {
            var e = new EstadoNumeracao();
            var r = ConsultarVarias(inst, tmp,
                "SELECT CURRENT_VALUE, CACHE, INCREMENT FROM INFORMATION_SCHEMA.SEQUENCES WHERE SEQUENCE_SCHEMA = 'PUBLIC' AND SEQUENCE_NAME = 'SQCALCULO'",
                "SELECT FKTABLE_NAME, FKCOLUMN_NAME, PKCOLUMN_NAME FROM INFORMATION_SCHEMA.CROSS_REFERENCES WHERE PKTABLE_SCHEMA = 'PUBLIC' AND PKTABLE_NAME = 'TBCALCULO'",
                "SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'PUBLIC' AND COLUMN_NAME = 'IIDCALCULO' AND TABLE_NAME <> 'TBCALCULO'",
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE COLUMN_DEFAULT LIKE '%SQCALCULO%'",
                "SELECT c.IIDCALCULO, c.SFLATIVO, c.DDTCRIACAOCALCULO, p.SNMRECLAMANTE, p.SNMRECLAMADO, p.INRPROCESSOUNICA, p.INRDIGITOUNICA, p.INRPROCESSOANOUNICA " +
                "FROM TBCALCULO c LEFT JOIN TBPROCESSOCALCULO p ON p.IIDPROCESSOCALCULO = c.IIDPROCESSOCALCULO ORDER BY c.IIDCALCULO");

            var seq = r[0];
            if (seq.Count == 0)
            {
                e.BloqueioReserva = e.BloqueioEdicao = "o banco não tem o contador de cálculos (SQCALCULO) no formato esperado";
            }
            else
            {
                e.Contador = long.Parse(seq[0][0], CultureInfo.InvariantCulture);
                e.Reserva = int.Parse(seq[0][1], CultureInfo.InvariantCulture);
                if (seq[0][2] != "1") e.BloqueioReserva = e.BloqueioEdicao = "o contador de cálculos deste banco não anda de 1 em 1";
            }
            if (r[3].Count > 0 && r[3][0][0] != "0")
                e.BloqueioReserva = e.BloqueioEdicao = "alguma coluna do banco depende diretamente do contador de cálculos";

            var vistos = new HashSet<string>();
            foreach (var l in r[1])
            {
                if (l[2] != "IIDCALCULO") { e.BloqueioEdicao = "o banco tem uma ligação com os cálculos num formato que o programa não conhece (" + l[0] + ")"; continue; }
                if (!NomeSeguro.IsMatch(l[0]) || !NomeSeguro.IsMatch(l[1])) { e.BloqueioEdicao = "nome de tabela inesperado no banco: " + l[0]; continue; }
                if (vistos.Add(l[0] + "." + l[1])) e.Ligacoes.Add(new LigacaoCalculo { Tabela = l[0], Coluna = l[1] });
            }
            foreach (var l in r[2])
            {
                if (!NomeSeguro.IsMatch(l[0])) { e.BloqueioEdicao = "nome de tabela inesperado no banco: " + l[0]; continue; }
                if (vistos.Add(l[0] + "." + l[1])) e.Ligacoes.Add(new LigacaoCalculo { Tabela = l[0], Coluna = l[1] });
            }
            if (e.Ligacoes.Count == 0 && e.BloqueioEdicao == null) e.BloqueioEdicao = "não encontrei as tabelas ligadas aos cálculos";

            foreach (var l in r[4])
            {
                string proc = "";
                if (!string.IsNullOrEmpty(l[5]))
                    proc = l[5].PadLeft(7, '0') + (string.IsNullOrEmpty(l[6]) ? "" : "-" + l[6].PadLeft(2, '0')) + (string.IsNullOrEmpty(l[7]) ? "" : "." + l[7]);
                e.Calculos.Add(new CalculoNumerado
                {
                    Numero = long.Parse(l[0], CultureInfo.InvariantCulture), Ativo = l[1] != "N", Criacao = l[2],
                    Reclamante = l[3], Reclamado = l[4], Processo = proc
                });
            }

            if (e.BloqueioEdicao == null && !VersaoConferida(inst.Versao))
                e.BloqueioEdicao = "a versão " + inst.Versao + " do PJe-Calc ainda não foi conferida por este programa. " +
                    "Mudar o número só é liberado nas versões em que foi verificado que o PJe-Calc não usa o número para validar o cálculo (" +
                    string.Join(", ", VersoesConferidas) + "). Procure uma versão mais nova do PJe-Calc Backup.";
            return e;
        }

        // ------------------------------------------------------------------ validação

        /// <summary>Confere a numeração pedida (número atual → novo). Devolve os problemas (lista vazia = ok).</summary>
        public static List<string> Validar(EstadoNumeracao e, IDictionary<long, long> novoPorAtual)
        {
            var erros = new List<string>();
            var finais = new Dictionary<long, List<long>>();
            foreach (var c in e.Calculos)
            {
                long n;
                if (!novoPorAtual.TryGetValue(c.Numero, out n)) n = c.Numero;
                if (n < 1) { erros.Add("O cálculo " + c.Numero + " ficaria com um número inválido (" + n + "). Use números a partir de 1."); continue; }
                if (n > 999999999) { erros.Add("O número " + n + " é grande demais."); continue; }
                List<long> l;
                if (!finais.TryGetValue(n, out l)) finais[n] = l = new List<long>();
                l.Add(c.Numero);
            }
            foreach (var kv in finais.Where(x => x.Value.Count > 1).OrderBy(x => x.Key))
                erros.Add("O número " + kv.Key + " ficaria repetido (cálculos atuais " + string.Join(" e ", kv.Value) + ").");
            return erros;
        }

        /// <summary>Numeração em sequência: os cálculos que aparecem no PJe-Calc ficam 1, 2, 3… na ordem atual,
        /// e os excluídos (que só existem no banco) vêm depois.</summary>
        public static Dictionary<long, long> EmSequencia(EstadoNumeracao e)
        {
            return EmSequencia(e, "numero");
        }

        /// <summary>Ordens possíveis de "Colocar em sequência" (código, texto).</summary>
        public static readonly string[,] Ordens =
        {
            { "numero", "pelo número atual" },
            { "criacao", "pela data de criação" },
            { "reclamante", "pelo reclamante" },
            { "processo", "pelo processo" },
        };

        /// <summary>
        /// Numeração em sequência na ordem pedida ("numero", "criacao", "reclamante" ou "processo").
        /// Empates seguem o número atual. Os excluídos (que só existem no banco) vêm sempre depois.
        /// </summary>
        public static Dictionary<long, long> EmSequencia(EstadoNumeracao e, string ordem)
        {
            Func<IEnumerable<CalculoNumerado>, IEnumerable<CalculoNumerado>> ordenar = l =>
            {
                switch (ordem)
                {
                    case "criacao": return l.OrderBy(c => string.IsNullOrEmpty(c.Criacao) ? "9999" : c.Criacao).ThenBy(c => c.Numero);
                    case "reclamante": return l.OrderBy(c => string.IsNullOrEmpty(c.Reclamante) ? 1 : 0).ThenBy(c => CalculoInfo.Normalizar(c.Reclamante), StringComparer.Ordinal)
                                               .ThenBy(c => CalculoInfo.Normalizar(c.Reclamado), StringComparer.Ordinal).ThenBy(c => c.Numero);
                    case "processo": return l.OrderBy(c => ChaveProcesso(c.Processo), StringComparer.Ordinal).ThenBy(c => c.Numero);
                    default: return l.OrderBy(c => c.Numero);
                }
            };
            var d = new Dictionary<long, long>();
            long n = 1;
            foreach (var c in ordenar(e.Calculos.Where(x => x.Ativo))) d[c.Numero] = n++;
            foreach (var c in ordenar(e.Calculos.Where(x => !x.Ativo))) d[c.Numero] = n++;
            return d;
        }

        /// <summary>"0001000-80.2024" → "2024|0001000" (ano primeiro, para ordenar); sem processo vai para o fim.</summary>
        private static string ChaveProcesso(string p)
        {
            var m = Regex.Match(p ?? "", "^(\\d+)(?:-\\d+)?(?:\\.(\\d{4}))?");
            if (!m.Success) return "~";
            return (m.Groups[2].Success ? m.Groups[2].Value : "0000") + "|" + m.Groups[1].Value.PadLeft(7, '0');
        }

        /// <summary>
        /// Fecha com segurança o PJe-Calc desta versão, se estiver aberto (como o "Sair" da bandeja), roda a operação
        /// e reabre no fim (se pedido). Usado pela aba Numeração e pela versão para o CMD.
        /// </summary>
        public static ResultadoNumeracao ComPjeCalcFechado(Instalacao inst, bool reabrir, Action<NivelLog, string> log, Action<double, string> progresso,
                                                          Func<ResultadoNumeracao> acao)
        {
            bool fechadaPorNos = false;
            ResultadoNumeracao res;
            try
            {
                if (inst.EstaAberta())
                {
                    progresso(-1, "Fechando o PJe-Calc " + inst.Versao + " com segurança…");
                    log(NivelLog.Info, "Fechando com segurança o PJe-Calc " + inst.Versao + ", que está aberto…");
                    if (!Instalacao.FecharPjeCalcNaPorta(inst.PortaHttp, inst.PortaDesligar, inst.ComandoDesligar, s => log(NivelLog.Aviso, s)))
                        throw new Exception("não consegui fechar o PJe-Calc automaticamente. Feche-o pelo \"Sair\" do ícone da bandeja e tente de novo.");
                    fechadaPorNos = true;
                    for (int i = 0; i < 20 && File.Exists(inst.ArquivoTrava); i++) System.Threading.Thread.Sleep(500);
                    log(NivelLog.Ok, "PJe-Calc fechado com segurança");
                }
                else if (File.Exists(inst.ArquivoTrava))
                    throw new Exception("o banco do PJe-Calc " + inst.Versao + " parece estar em uso (arquivo de trava pjecalc.lock.db), mas o PJe-Calc não está aberto. " +
                                        "Isso acontece quando ele foi fechado de forma anormal: abra o PJe-Calc, feche pelo \"Sair\" da bandeja e tente de novo.");
                res = acao();
            }
            catch (Exception ex)
            {
                log(NivelLog.Erro, "Operação interrompida: " + ex.Message);
                res = new ResultadoNumeracao { Ok = false, Resumo = "Nada foi alterado." };
            }
            if (fechadaPorNos && reabrir)
            {
                try
                {
                    if (!Instalacao.PortaOcupada(inst.PortaHttp)) { inst.AbrirPeloAtalho(); log(NivelLog.Info, "Reabrindo o PJe-Calc " + inst.Versao + ", que estava aberto antes"); }
                }
                catch (Exception ex) { log(NivelLog.Aviso, "Não consegui reabrir o PJe-Calc: " + ex.Message); }
            }
            return res;
        }

        // ------------------------------------------------------------------ aplicar

        /// <summary>
        /// Aplica as mudanças de número e/ou passa a reservar 1 número por vez. O PJe-Calc precisa estar fechado.
        /// O próximo cálculo criado recebe o maior número existente + 1.
        /// </summary>
        public static ResultadoNumeracao Aplicar(Instalacao inst, IList<Mudanca> mudancas, bool reservaDeUm, string destinoBackups,
                                                 Action<NivelLog, string> log, Action<double, string> progresso)
        {
            mudancas = (mudancas ?? new List<Mudanca>()).Where(m => m.De != m.Para).ToList();
            var res = new ResultadoNumeracao();
            var rel = new List<string>();
            Action<NivelLog, string> L = (n, s) =>
            {
                log(n, s);
                rel.Add((n == NivelLog.Ok ? "OK    " : n == NivelLog.Erro ? "ERRO  " : n == NivelLog.Aviso ? "AVISO " : "      ") + s);
            };
            var reg = new RegistroHistorico { Tipo = "numeracao" };
            reg.Versoes.Add(new RegistroVersao { Versao = inst.Versao, Pasta = inst.Pasta, BancoCopiado = true });
            var regNum = new RegistroNumeracao { Versao = inst.Versao, PastaInstalacao = inst.Pasta, Mudancas = mudancas.ToList(), Data = DateTime.Now };
            string tmp = PastaTemporaria("mudanca");
            bool trocado = false;
            string copiaOriginal = Path.Combine(tmp, "original"), trabalho = Path.Combine(tmp, "trabalho");

            try
            {
                if (inst.EstaAberta() || File.Exists(inst.ArquivoTrava)) throw new Exception("o PJe-Calc " + inst.Versao + " está aberto; feche-o antes");

                // 1) estado atual (de uma cópia) e conferência do pedido
                progresso(0.05, "Lendo o banco do PJe-Calc " + inst.Versao + "…");
                Directory.CreateDirectory(copiaOriginal); Directory.CreateDirectory(trabalho);
                File.Copy(inst.ArquivoBanco, Path.Combine(copiaOriginal, "pjecalc.h2.db"));
                regNum.Sha256Antes = Sha256(Path.Combine(copiaOriginal, "pjecalc.h2.db"));
                if (Sha256(inst.ArquivoBanco) != regNum.Sha256Antes) throw new Exception("o banco mudou enquanto era lido; tente de novo");
                File.Copy(Path.Combine(copiaOriginal, "pjecalc.h2.db"), Path.Combine(trabalho, "pjecalc.h2.db"));
                var e = LerCopia(inst, trabalho);
                if (mudancas.Count > 0 && e.BloqueioEdicao != null) throw new Exception("mudar o número não é permitido aqui: " + e.BloqueioEdicao);
                if (reservaDeUm && e.BloqueioReserva != null) throw new Exception("mudar a reserva não é permitido aqui: " + e.BloqueioReserva);
                var existentes = new HashSet<long>(e.Calculos.Select(c => c.Numero));
                foreach (var m in mudancas)
                    if (!existentes.Contains(m.De)) throw new Exception("o cálculo " + m.De + " não existe mais no banco; atualize a tela e confira de novo");
                var mapa = mudancas.ToDictionary(m => m.De, m => m.Para);
                var erros = Validar(e, mapa);
                if (erros.Count > 0) throw new Exception(erros[0]);
                var finais = e.Calculos.Select(c => mapa.ContainsKey(c.Numero) ? mapa[c.Numero] : c.Numero).ToList();
                long proximo = (finais.Count == 0 ? 0 : finais.Max()) + 1;
                int reserva = reservaDeUm ? 1 : e.Reserva;
                regNum.ReservaAntes = e.Reserva; regNum.ReservaDepois = reserva;
                regNum.ProximoAntes = e.Proximo; regNum.ProximoDepois = proximo;
                if (mudancas.Count == 0 && reserva == e.Reserva && proximo == e.Proximo) throw new Exception("não há nada para mudar");

                L(NivelLog.Info, "PJe-Calc " + inst.Versao + " (" + inst.Pasta + "): " + e.Calculos.Count + " cálculo(s), " +
                    e.Ligacoes.Count + " tabela(s) ligadas aos cálculos, reserva de " + e.Reserva + " número(s), próximo nº " + e.Proximo);

                // 2) cópia de segurança do banco atual, na pasta de backups
                progresso(0.15, "Guardando uma cópia de segurança do banco…");
                res.PastaSeguranca = CriarPasta(destinoBackups, " (antes de mudar a numeração)");
                string zip = inst.CopiarBanco(res.PastaSeguranca);
                string conf = Path.Combine(tmp, "conferencia");
                Directory.CreateDirectory(conf);
                BancoPjeCalc.ExtrairBancoDoZip(zip, conf);
                if (Sha256(Path.Combine(conf, "pjecalc.h2.db")) != regNum.Sha256Antes) throw new Exception("a cópia de segurança não ficou igual ao banco; nada foi alterado");
                reg.Calculos = e.Calculos.Count(c => c.Ativo);
                L(NivelLog.Ok, "Cópia de segurança do banco atual guardada e conferida: " + zip);

                // 3) mudanças na cópia de trabalho
                progresso(0.35, "Mudando a numeração numa cópia do banco…");
                var antes = Contagens(inst, trabalho, e.Ligacoes);
                var script = new StringBuilder();
                if (mudancas.Count > 0)
                {
                    var alvos = new List<LigacaoCalculo>(e.Ligacoes) { new LigacaoCalculo { Tabela = "TBCALCULO", Coluna = "IIDCALCULO" } };
                    string deLista = string.Join(", ", mudancas.Select(m => m.De.ToString(CultureInfo.InvariantCulture)));
                    string negLista = string.Join(", ", mudancas.Select(m => (-m.De).ToString(CultureInfo.InvariantCulture)));
                    string casos = string.Join(" ", mudancas.Select(m => "WHEN " + (-m.De).ToString(CultureInfo.InvariantCulture) + " THEN " + m.Para.ToString(CultureInfo.InvariantCulture)));
                    script.AppendLine("SET REFERENTIAL_INTEGRITY FALSE;");
                    // primeiro todos vão para números temporários (negativos), assim trocas como 5 ↔ 7 nunca colidem
                    foreach (var a in alvos) script.AppendLine("UPDATE " + a.Tabela + " SET " + a.Coluna + " = -" + a.Coluna + " WHERE " + a.Coluna + " IN (" + deLista + ");");
                    foreach (var a in alvos) script.AppendLine("UPDATE " + a.Tabela + " SET " + a.Coluna + " = CASE " + a.Coluna + " " + casos + " END WHERE " + a.Coluna + " IN (" + negLista + ");");
                    script.AppendLine("SET REFERENTIAL_INTEGRITY TRUE;");
                }
                // o contador é recriado (esta versão do banco não deixa mudar a reserva de uma sequência existente)
                script.AppendLine("DROP SEQUENCE SQCALCULO;");
                script.AppendLine("CREATE SEQUENCE SQCALCULO START WITH " + proximo.ToString(CultureInfo.InvariantCulture) + " INCREMENT BY 1 CACHE " + reserva.ToString(CultureInfo.InvariantCulture) + ";");
                File.WriteAllText(Path.Combine(res.PastaSeguranca, "comandos-executados.sql"),
                    "-- PJe-Calc Backup " + Atualizacao.Atual + ", aba Numeração" + " — " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + " — PJe-Calc " + inst.Versao + "\r\n" + script, new UTF8Encoding(false));
                RodarScript(inst, trabalho, script.ToString());

                // 4) conferência da cópia
                progresso(0.6, "Conferindo a cópia alterada…");
                var depois = LerCopia(inst, trabalho);
                var esperado = new HashSet<long>(finais);
                if (depois.Calculos.Count != e.Calculos.Count || !depois.Calculos.All(c => esperado.Contains(c.Numero)))
                    throw new Exception("a conferência dos números dos cálculos falhou; o banco original não foi alterado");
                if (depois.Reserva != reserva || depois.Proximo != proximo)
                    throw new Exception("a conferência do contador falhou (reserva " + depois.Reserva + ", próximo " + depois.Proximo + "); o banco original não foi alterado");
                var agora = Contagens(inst, trabalho, e.Ligacoes);
                var antesMapeado = new Dictionary<string, long>();
                foreach (var kv in antes)
                {
                    var p = kv.Key.Split('|');
                    long n = long.Parse(p[2], CultureInfo.InvariantCulture);
                    string k = p[0] + "|" + p[1] + "|" + (mapa.ContainsKey(n) ? mapa[n] : n).ToString(CultureInfo.InvariantCulture);
                    antesMapeado[k] = kv.Value;
                }
                if (antesMapeado.Count != agora.Count || antesMapeado.Any(kv => !agora.ContainsKey(kv.Key) || agora[kv.Key] != kv.Value))
                    throw new Exception("a conferência dos registros ligados aos cálculos falhou; o banco original não foi alterado");
                long soltos = Soltos(inst, trabalho, e.Ligacoes);
                if (soltos != 0) throw new Exception(soltos + " registro(s) ficariam sem cálculo; o banco original não foi alterado");
                if (Directory.GetFiles(trabalho, "*.lock.db").Length > 0) throw new Exception("a cópia alterada não foi fechada direito; o banco original não foi alterado");
                L(NivelLog.Ok, "Cópia conferida: " + antes.Count + " grupo(s) de registros iguais antes e depois, nenhum registro solto");

                // 5) troca do banco
                progresso(0.8, "Trocando o banco do PJe-Calc " + inst.Versao + "…");
                if (inst.EstaAberta() || File.Exists(inst.ArquivoTrava)) throw new Exception("o PJe-Calc foi aberto durante a operação; nada foi alterado");
                if (Sha256(inst.ArquivoBanco) != regNum.Sha256Antes) throw new Exception("o banco foi alterado durante a operação; nada foi alterado");
                inst.SubstituirBanco(Path.Combine(trabalho, "pjecalc.h2.db"));
                trocado = true;
                var final = Ler(inst);
                if (final.Calculos.Count != e.Calculos.Count || !final.Calculos.All(c => esperado.Contains(c.Numero)) || final.Reserva != reserva || final.Proximo != proximo)
                    throw new Exception("a conferência do banco trocado falhou");
                regNum.Sha256Depois = Sha256(inst.ArquivoBanco);

                foreach (var m in mudancas.OrderBy(x => x.Para))
                {
                    var c = e.Calculos.First(x => x.Numero == m.De);
                    L(NivelLog.Ok, "Cálculo " + m.De + " → " + m.Para + (string.IsNullOrEmpty(c.Reclamante) ? "" : "  (" + c.Reclamante + ")"));
                }
                if (reserva != e.Reserva) L(NivelLog.Ok, "Reserva do contador: de " + e.Reserva + " para " + reserva + " número(s) por vez");
                L(NivelLog.Ok, "Próximo cálculo: nº " + proximo + (proximo != e.Proximo ? " (antes seria o " + e.Proximo + ")" : ""));
                res.Ok = true; res.Proximo = proximo; res.Mudancas = mudancas.Count;
            }
            catch (Exception ex)
            {
                L(NivelLog.Erro, "Operação interrompida: " + ex.Message);
                if (trocado)
                {
                    // algo deu errado depois da troca: volta o banco original na hora
                    try
                    {
                        inst.SubstituirBanco(Path.Combine(copiaOriginal, "pjecalc.h2.db"));
                        L(NivelLog.Aviso, Sha256(inst.ArquivoBanco) == regNum.Sha256Antes ? "O banco original foi colocado de volta." : "Tentei colocar o banco original de volta, mas a conferência falhou. Restaure-o pela cópia de segurança.");
                    }
                    catch (Exception ex2) { L(NivelLog.Erro, "Não consegui colocar o banco original de volta (" + ex2.Message + "). Restaure-o pela cópia de segurança em " + res.PastaSeguranca); }
                }
                reg.Falhas = 1;
            }
            finally { Apagar(tmp); }

            var partesResumo = new List<string>();
            if (mudancas.Count > 0) partesResumo.Add(mudancas.Count + " cálculo(s) com número novo");
            if (regNum.ReservaDepois != regNum.ReservaAntes) partesResumo.Add("reserva de " + regNum.ReservaDepois + " número por vez");
            partesResumo.Add("próximo nº " + res.Proximo);
            res.Resumo = res.Ok
                ? "Mudança na numeração de " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + ": " + string.Join(", ", partesResumo) + "."
                : "Mudança na numeração de " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + " interrompida; o banco do PJe-Calc ficou como estava.";
            if (res.PastaSeguranca != null)
            {
                rel.Insert(0, res.Resumo);
                rel.Insert(1, "Esta pasta guarda o banco que existia ANTES da mudança (para desfazer). Gerado pela aba Numeração do PJe-Calc Backup (projeto independente, não oficial).");
                try { File.WriteAllLines(Path.Combine(res.PastaSeguranca, "relatorio.txt"), rel, new UTF8Encoding(true)); } catch { }
                reg.Resumo = res.Resumo;
                if (res.Ok)
                {
                    Historico.Salvar(res.PastaSeguranca, reg);
                    try { File.WriteAllText(Path.Combine(res.PastaSeguranca, ArquivoRegistro), new JavaScriptSerializer().Serialize(regNum)); } catch { }
                }
                else Historico.Salvar(res.PastaSeguranca, reg);
            }
            progresso(1, res.Resumo);
            return res;
        }

        // ------------------------------------------------------------------ desfazer

        public class MudancaFeita
        {
            public RegistroHistorico Historico;
            public RegistroNumeracao Registro;
            public string Pasta { get { return Historico.PastaOperacao; } }
        }

        /// <summary>Mudanças feitas nesta instalação (mais recentes primeiro).</summary>
        public static List<MudancaFeita> Feitas(string destinoBackups, Instalacao inst)
        {
            var l = new List<MudancaFeita>();
            var json = new JavaScriptSerializer();
            foreach (var h in Historico.Listar(destinoBackups).Where(x => x.Tipo == "numeracao" && x.Falhas == 0))
            {
                try
                {
                    string arq = Path.Combine(h.PastaOperacao, ArquivoRegistro);
                    if (!File.Exists(arq)) continue;
                    var r = json.Deserialize<RegistroNumeracao>(File.ReadAllText(arq));
                    if (!string.Equals((r.PastaInstalacao ?? "").TrimEnd('\\'), inst.Pasta, StringComparison.OrdinalIgnoreCase)) continue;
                    l.Add(new MudancaFeita { Historico = h, Registro = r });
                }
                catch { }
            }
            return l;
        }

        /// <summary>O banco continua exatamente como ficou depois desta mudança? (se não, desfazer perde o que foi feito depois)</summary>
        public static bool BancoIntactoDesde(Instalacao inst, MudancaFeita m)
        {
            try { return File.Exists(inst.ArquivoBanco) && Sha256(inst.ArquivoBanco) == m.Registro.Sha256Depois; }
            catch { return false; }
        }

        /// <summary>Coloca de volta o banco guardado antes da mudança. Antes, guarda o banco atual (também dá para desfazer).</summary>
        public static ResultadoNumeracao Desfazer(Instalacao inst, MudancaFeita m, string destinoBackups, Action<NivelLog, string> log, Action<double, string> progresso)
        {
            var res = new ResultadoNumeracao();
            var rel = new List<string>();
            Action<NivelLog, string> L = (n, s) => { log(n, s); rel.Add((n == NivelLog.Ok ? "OK    " : n == NivelLog.Erro ? "ERRO  " : n == NivelLog.Aviso ? "AVISO " : "      ") + s); };
            var reg = new RegistroHistorico { Tipo = "restauracao-banco" };
            reg.Versoes.Add(new RegistroVersao { Versao = inst.Versao, Pasta = inst.Pasta, BancoCopiado = true });
            string tmp = PastaTemporaria("desfazer");
            try
            {
                if (inst.EstaAberta() || File.Exists(inst.ArquivoTrava)) throw new Exception("o PJe-Calc " + inst.Versao + " está aberto; feche-o antes");
                string zip = Directory.GetFiles(m.Pasta, "*.zip").FirstOrDefault();
                if (zip == null) throw new Exception("a cópia de segurança não está mais em " + m.Pasta);
                progresso(0.2, "Conferindo a cópia de segurança…");
                string db = BancoPjeCalc.ExtrairBancoDoZip(zip, tmp);
                if (m.Registro.Sha256Antes != null && Sha256(db) != m.Registro.Sha256Antes) throw new Exception("a cópia de segurança não confere com o banco que existia antes da mudança");
                int noBackup = BancoPjeCalc.ListarDoArquivo(inst, db).Count(c => c.Ativo);

                progresso(0.45, "Guardando o banco atual…");
                res.PastaSeguranca = CriarPasta(destinoBackups, " (antes de desfazer a numeração)");
                string seg = inst.CopiarBanco(res.PastaSeguranca);
                L(NivelLog.Ok, "Cópia de segurança do banco atual feita: " + seg);

                progresso(0.7, "Colocando o banco anterior de volta…");
                inst.SubstituirBanco(db);
                if (Sha256(inst.ArquivoBanco) != Sha256(db)) throw new Exception("a conferência do banco restaurado falhou");
                L(NivelLog.Ok, "Banco de " + m.Historico.Data.ToString("dd/MM/yyyy HH:mm") + " restaurado: " + noBackup + " cálculo(s)");
                res.Ok = true;
                reg.Calculos = noBackup;
                try
                {
                    m.Registro.Desfeito = true;
                    File.WriteAllText(Path.Combine(m.Pasta, ArquivoRegistro), new JavaScriptSerializer().Serialize(m.Registro));
                }
                catch { }
            }
            catch (Exception ex) { L(NivelLog.Erro, "Não desfiz: " + ex.Message); reg.Falhas = 1; }
            finally { Apagar(tmp); }

            res.Resumo = res.Ok ? "Mudança na numeração de " + m.Historico.Data.ToString("dd/MM/yyyy HH:mm") + " desfeita." : "A mudança não foi desfeita; o banco ficou como estava.";
            if (res.PastaSeguranca != null)
            {
                rel.Insert(0, res.Resumo);
                rel.Insert(1, "Esta pasta guarda o banco que existia ANTES de desfazer a mudança na numeração. Gerado pela aba Numeração do PJe-Calc Backup (projeto independente, não oficial).");
                try { File.WriteAllLines(Path.Combine(res.PastaSeguranca, "relatorio.txt"), rel, new UTF8Encoding(true)); } catch { }
                reg.Resumo = res.Resumo;
                Historico.Salvar(res.PastaSeguranca, reg);
            }
            progresso(1, res.Resumo);
            return res;
        }

        // ------------------------------------------------------------------ conferências

        /// <summary>Quantidade de registros por (tabela, coluna, cálculo), incluindo a própria TBCALCULO.</summary>
        private static Dictionary<string, long> Contagens(Instalacao inst, string pasta, List<LigacaoCalculo> ligacoes)
        {
            var partes = ligacoes.Select(a => "SELECT '" + a.Tabela + "' T, '" + a.Coluna + "' C, " + a.Coluna + " N, COUNT(*) Q FROM " + a.Tabela +
                                              " WHERE " + a.Coluna + " IS NOT NULL GROUP BY " + a.Coluna).ToList();
            partes.Add("SELECT 'TBCALCULO' T, 'IIDCALCULO' C, IIDCALCULO N, COUNT(*) Q FROM TBCALCULO GROUP BY IIDCALCULO");
            var d = new Dictionary<string, long>();
            foreach (var l in BancoPjeCalc.Consultar(inst, pasta, string.Join(" UNION ALL ", partes)))
                d[l[0] + "|" + l[1] + "|" + l[2]] = long.Parse(l[3], CultureInfo.InvariantCulture);
            return d;
        }

        /// <summary>Registros que apontam para um cálculo que não existe.</summary>
        private static long Soltos(Instalacao inst, string pasta, List<LigacaoCalculo> ligacoes)
        {
            if (ligacoes.Count == 0) return 0;
            string q = "SELECT SUM(N) FROM (" + string.Join(" UNION ALL ", ligacoes.Select(a =>
                "SELECT COUNT(*) N FROM " + a.Tabela + " WHERE " + a.Coluna + " IS NOT NULL AND " + a.Coluna + " NOT IN (SELECT IIDCALCULO FROM TBCALCULO)")) + ")";
            var l = BancoPjeCalc.Consultar(inst, pasta, q);
            return l.Count == 0 || string.IsNullOrEmpty(l[0][0]) ? 0 : long.Parse(l[0][0], CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ H2 (o banco do PJe-Calc)

        private static string H2Jar(Instalacao inst)
        {
            string h2 = Directory.GetFiles(Path.Combine(inst.Pasta, @"bin\lib"), "h2-*.jar").FirstOrDefault();
            if (h2 == null || !File.Exists(inst.JavaExe)) throw new Exception("não encontrei o Java/H2 da instalação");
            return h2;
        }

        private static string Url(string pasta) { return "jdbc:h2:" + Path.Combine(pasta, "pjecalc"); }

        private static string Rodar(Instalacao inst, string argumentos, int timeoutMs, out int codigo)
        {
            var psi = new ProcessStartInfo(inst.JavaExe, "-cp \"" + H2Jar(inst) + "\" " + argumentos);
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (var p = Process.Start(psi))
            {
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } throw new Exception("o banco demorou demais para responder"); }
                codigo = p.ExitCode;
                return (e.Result + "\n" + o.Result).Trim();
            }
        }

        /// <summary>Várias consultas numa só abertura do banco (cada uma vira um CSV).</summary>
        private static List<List<List<string>>> ConsultarVarias(Instalacao inst, string pasta, params string[] selects)
        {
            var arquivos = selects.Select(s => Path.Combine(pasta, "q" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".csv")).ToList();
            var sql = new StringBuilder();
            for (int i = 0; i < selects.Length; i++)
                sql.Append("CALL CSVWRITE('" + arquivos[i].Replace("'", "''") + "', '" + selects[i].Replace("'", "''") + "', 'UTF-8', ';'); ");
            int codigo;
            string saida = Rodar(inst, "org.h2.tools.Shell -url \"" + Url(pasta) + "\" -user \"" + inst.UsuarioBanco + "\" -password \"" + inst.SenhaBanco +
                                       "\" -sql \"" + sql.ToString().Replace("\"", "\\\"") + "\"", 90000, out codigo);
            var r = new List<List<List<string>>>();
            foreach (var a in arquivos)
            {
                if (!File.Exists(a)) throw new Exception("não consegui ler o banco: " + saida.Split('\n').FirstOrDefault());
                r.Add(File.ReadAllLines(a, Encoding.UTF8).Skip(1).Select(BancoPjeCalc.DividirCsv).ToList());
                try { File.Delete(a); } catch { }
            }
            return r;
        }

        private static void RodarScript(Instalacao inst, string pasta, string script)
        {
            string arq = Path.Combine(Path.GetDirectoryName(pasta), "script-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".sql");
            File.WriteAllText(arq, script, new UTF8Encoding(false));
            try
            {
                int codigo;
                string saida = Rodar(inst, "org.h2.tools.RunScript -url \"" + Url(pasta) + "\" -user \"" + inst.UsuarioBanco + "\" -password \"" + inst.SenhaBanco +
                                           "\" -script \"" + arq + "\"", 300000, out codigo);
                if (codigo != 0 || saida.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new Exception("o banco recusou a mudança: " + saida.Split('\n').FirstOrDefault());
            }
            finally { try { File.Delete(arq); } catch { } }
        }

        // ------------------------------------------------------------------ utilitários

        public static string Sha256(string arquivo)
        {
            using (var s = File.OpenRead(arquivo))
            using (var h = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "");
        }

        private static string PastaTemporaria(string nome)
        {
            string p = Path.Combine(Path.GetTempPath(), "pjecalc-ferramentas-" + nome + "-" + Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(p);
            return p;
        }

        private static void Apagar(string pasta)
        {
            for (int i = 0; i < 5; i++)
            {
                try { if (Directory.Exists(pasta)) Directory.Delete(pasta, true); return; }
                catch { System.Threading.Thread.Sleep(300); }
            }
        }

        private static string CriarPasta(string destino, string sufixo)
        {
            string p = Path.Combine(destino, DateTime.Now.ToString("yyyy-MM-dd_HHmm") + sufixo);
            int n = 2; string b = p;
            while (Directory.Exists(p)) p = b + "_" + (n++);
            Directory.CreateDirectory(p);
            return p;
        }
    }
}
