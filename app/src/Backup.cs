// Orquestra backup, restauração, migração e restauração do banco — uma versão do PJe-Calc de cada vez.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PjeCalcBackup
{
    public enum TipoLog { Info, Ok, Erro, Aviso, Titulo }

    public class OpcoesBackup
    {
        public string Destino;
        public bool ExportarPjc = true;
        public bool CopiarBanco = true;
        public bool ReabrirNoFinal = true;
        /// <summary>Cálculos escolhidos por instalação (chave = pasta). Sem entrada = todos.</summary>
        public Dictionary<string, HashSet<string>> Selecao = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    }

    public interface IRetornoBackup
    {
        void Log(TipoLog tipo, string texto);
        void Progresso(double fracao, string etapa);
        /// <summary>Pede ao usuário para fechar um PJe-Calc que não pôde ser fechado automaticamente. true = tentar de novo.</summary>
        bool PedirParaFechar(string descricao);
        bool Cancelado { get; }
    }

    public class ResultadoBackup
    {
        public string Pasta;
        public int Exportados, Importados, Falhas, Bancos, Conferidos, Esperados, Pulados;
        public string Reaberto;
    }

    public static class Backup
    {
        // ================================================================ utilitários

        /// <summary>Fecha com segurança qualquer PJe-Calc aberto na porta da versão. false = não foi possível.</summary>
        private static bool LiberarPorta(Instalacao inst, IRetornoBackup ui, Action<TipoLog, string> log, List<string> fechadasPorNos)
        {
            while (Instalacao.PortaOcupada(inst.PortaHttp))
            {
                if (ui.Cancelado) return false;
                string dono = Instalacao.InstalacaoNaPorta(inst.PortaHttp);
                Instalacao donoInst = null;
                try { if (dono != null) donoInst = Instalacao.Carregar(dono); } catch { }
                string rotulo = donoInst != null ? "PJe-Calc " + donoInst.Versao : "o programa";

                ui.Progresso(-1, "Fechando " + rotulo + " que está aberto…");
                log(TipoLog.Info, "Fechando com segurança " + rotulo + " que estava aberto" + (dono != null ? " (" + dono + ")" : "") + "…");
                bool ok = Instalacao.FecharPjeCalcNaPorta(inst.PortaHttp,
                    donoInst != null ? donoInst.PortaDesligar : inst.PortaDesligar,
                    donoInst != null ? donoInst.ComandoDesligar : inst.ComandoDesligar,
                    s => log(TipoLog.Aviso, s));
                if (ok)
                {
                    log(TipoLog.Ok, rotulo.Substring(0, 1).ToUpper() + rotulo.Substring(1) + " foi fechado com segurança");
                    if (dono != null && !fechadasPorNos.Contains(dono, StringComparer.OrdinalIgnoreCase)) fechadasPorNos.Add(dono);
                    continue;
                }
                string desc = "Não consegui fechar " + rotulo + " automaticamente" + (dono != null ? " (" + dono + ")" : "") + ".";
                if (!ui.PedirParaFechar(desc)) return false;
            }
            return true;
        }

        private static string CriarPasta(string destino, string sufixo)
        {
            string p = Path.Combine(destino, DateTime.Now.ToString("yyyy-MM-dd_HHmm") + sufixo);
            int n = 2; string b = p;
            while (Directory.Exists(p)) p = b + "_" + (n++);
            Directory.CreateDirectory(p);
            return p;
        }

        private static Action<TipoLog, string> Registrador(IRetornoBackup ui, List<string> rel)
        {
            return (t, s) =>
            {
                ui.Log(t, s);
                string pre = t == TipoLog.Ok ? "OK    " : t == TipoLog.Erro ? "ERRO  " : t == TipoLog.Aviso ? "AVISO " : t == TipoLog.Titulo ? "\r\n== " : "      ";
                rel.Add(pre + s);
            };
        }

        private static void Reabrir(bool reabrir, List<string> fechadasPorNos, ResultadoBackup res, Action<TipoLog, string> log)
        {
            if (!reabrir || fechadasPorNos.Count == 0) return;
            try
            {
                var inst = Instalacao.Carregar(fechadasPorNos[0]);
                if (Instalacao.PortaOcupada(inst.PortaHttp)) return;
                inst.AbrirPeloAtalho();
                res.Reaberto = "PJe-Calc " + inst.Versao;
                log(TipoLog.Info, "Reabrindo o PJe-Calc " + inst.Versao + ", que estava aberto antes do início");
            }
            catch (Exception ex) { log(TipoLog.Aviso, "Não consegui reabrir o PJe-Calc: " + ex.Message); }
        }

        private static void Gravar(string pasta, List<string> rel, string resumo, string aviso, RegistroHistorico reg)
        {
            rel.Insert(0, resumo);
            rel.Insert(1, aviso ?? "Gerado por PJe-Calc Backup (projeto independente, não oficial).");
            File.WriteAllLines(Path.Combine(pasta, "relatorio.txt"), rel, new UTF8Encoding(true));
            reg.Resumo = resumo;
            Historico.Salvar(pasta, reg);
        }

        /// <summary>Liga a versão (se preciso) e exporta os cálculos escolhidos (null = todos). Devolve os arquivos.</summary>
        private static List<string> ExportarVersao(Instalacao inst, string pastaV, HashSet<string> escolhidos, bool usarAberta,
            IRetornoBackup ui, Action<TipoLog, string> log, ref Navegador nav, ResultadoBackup res, double ini, double passo)
        {
            var arquivos = new List<string>();
            if (!usarAberta)
            {
                ui.Progresso(ini + passo * 0.05, "Abrindo o PJe-Calc " + inst.Versao + " em segundo plano…");
                log(TipoLog.Info, "Abrindo o PJe-Calc em segundo plano (sem janela)…");
                inst.Ligar(s => log(TipoLog.Info, s), () => ui.Cancelado);
            }
            try
            {
                if (nav == null)
                {
                    ui.Progresso(ini + passo * 0.1, "Preparando o navegador invisível…");
                    nav = new Navegador();
                }
                var exp = new Exportador(nav, inst.UrlBase, () => ui.Cancelado);
                ui.Progresso(ini + passo * 0.15, "Listando os cálculos do PJe-Calc " + inst.Versao + "…");
                var calcs = exp.ListarTodos();
                if (escolhidos != null) calcs = calcs.Where(c => escolhidos.Contains(c.Numero)).ToList();
                log(TipoLog.Info, calcs.Count + " cálculo(s) para exportar");
                int j = 0;
                foreach (var c in calcs)
                {
                    if (ui.Cancelado) break;
                    j++;
                    ui.Progresso(ini + passo * (0.15 + 0.8 * (j - 1) / Math.Max(1, calcs.Count)),
                        "PJe-Calc " + inst.Versao + " — cálculo " + j + " de " + calcs.Count + ": " + c.Reclamante);
                    try
                    {
                        string arq = exp.Exportar(c, pastaV);
                        arquivos.Add(arq);
                        log(TipoLog.Ok, "Cálculo " + c.Numero + " — " + c.Reclamante + "  (" + (new FileInfo(arq).Length / 1024) + " KB)");
                        res.Exportados++;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { log(TipoLog.Erro, "Cálculo " + c.Numero + " — " + c.Reclamante + ": " + ex.Message); res.Falhas++; }
                }
                exp.FecharCalculoAberto();
            }
            finally
            {
                if (inst.LigadaPorNos)
                {
                    ui.Progresso(ini + passo * 0.97, "Fechando o PJe-Calc " + inst.Versao + "…");
                    inst.Desligar(s => log(TipoLog.Aviso, s));
                    log(TipoLog.Info, "PJe-Calc fechado com segurança");
                }
            }
            return arquivos;
        }

        /// <summary>Confere: cada cálculo do banco (escolhido) tem um .PJC legível com o mesmo número.</summary>
        private static int Conferir(List<CalculoInfo> doBanco, List<string> arquivos, Action<TipoLog, string> log, out int esperados)
        {
            var lidos = new HashSet<string>();
            foreach (var a in arquivos)
            {
                try { var i = BancoPjeCalc.LerPjc(a); if (i.Numero != null) lidos.Add(i.Numero); }
                catch (Exception ex) { log(TipoLog.Erro, "Arquivo ilegível: " + Path.GetFileName(a) + " (" + ex.Message + ")"); }
            }
            esperados = doBanco.Count;
            var faltando = doBanco.Where(c => !lidos.Contains(c.Numero)).ToList();
            int ok = esperados - faltando.Count;
            if (faltando.Count == 0)
                log(TipoLog.Ok, "Conferência: " + ok + " de " + esperados + " cálculo(s) conferidos (banco ↔ arquivos .PJC)");
            else
            {
                log(TipoLog.Erro, "Conferência: " + ok + " de " + esperados + " — faltam: " +
                    string.Join(", ", faltando.Select(c => "nº " + c.Numero + " (" + c.Reclamante + ")")));
            }
            return ok;
        }

        // ===================================================================== BACKUP

        public static ResultadoBackup Executar(List<Instalacao> instalacoes, OpcoesBackup op, IRetornoBackup ui)
        {
            var res = new ResultadoBackup();
            res.Pasta = CriarPasta(op.Destino, "");
            var rel = new List<string>();
            var log = Registrador(ui, rel);
            var fechadasPorNos = new List<string>();
            var reg = new RegistroHistorico { Tipo = "backup" };

            var ordem = instalacoes.OrderByDescending(i => i.EstaAberta()).ThenBy(i => i.Versao).ToList();
            var nomesUsados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Navegador nav = null;
            int idx = 0;

            try
            {
                foreach (var inst in ordem)
                {
                    if (ui.Cancelado) break;
                    double ini = (double)idx / ordem.Count, passo = 1.0 / ordem.Count;
                    idx++;
                    var rv = new RegistroVersao { Versao = inst.Versao, Pasta = inst.Pasta };
                    reg.Versoes.Add(rv);

                    string nomePasta = "PJe-Calc " + inst.Versao;
                    for (int k = 2; nomesUsados.Contains(nomePasta); k++) nomePasta = "PJe-Calc " + inst.Versao + " (" + k + ")";
                    nomesUsados.Add(nomePasta);
                    string pastaV = Path.Combine(res.Pasta, nomePasta);
                    Directory.CreateDirectory(pastaV);

                    HashSet<string> escolhidos;
                    op.Selecao.TryGetValue(inst.Pasta, out escolhidos);

                    log(TipoLog.Titulo, "PJe-Calc " + inst.Versao + "  —  " + inst.Pasta);
                    ui.Progresso(ini, "PJe-Calc " + inst.Versao);

                    // 1) fechar o que estiver aberto na porta
                    bool usarAberta = false;
                    if (!LiberarPorta(inst, ui, log, fechadasPorNos))
                    {
                        if (ui.Cancelado) break;
                        if (inst.EstaAberta()) usarAberta = true;
                        else { log(TipoLog.Aviso, "Pulada: outro PJe-Calc continuou aberto na mesma porta."); continue; }
                    }

                    // 2) banco de dados + lista de referência para a conferência
                    List<CalculoInfo> doBanco = null;
                    if (op.CopiarBanco && !usarAberta)
                    {
                        try
                        {
                            string z = inst.CopiarBanco(pastaV);
                            doBanco = BancoPjeCalc.ListarDoZip(inst, z);  // abrir a cópia já prova que ela está íntegra
                            log(TipoLog.Ok, "Banco de dados copiado e aberto com sucesso: " + doBanco.Count + " cálculo(s) (" + (new FileInfo(z).Length / 1024) + " KB)");
                            res.Bancos++;
                            rv.BancoCopiado = true;
                        }
                        catch (Exception ex) { log(TipoLog.Erro, "Banco de dados: " + ex.Message); res.Falhas++; }
                    }
                    else if (op.CopiarBanco)
                        log(TipoLog.Aviso, "Banco de dados não copiado: esta versão continuou aberta.");
                    if (doBanco == null) { try { doBanco = BancoPjeCalc.Listar(inst); } catch { doBanco = new List<CalculoInfo>(); } }
                    rv.CalculosNoBanco = doBanco.Count;
                    if (escolhidos != null)
                    {
                        doBanco = doBanco.Where(c => escolhidos.Contains(c.Numero)).ToList();
                        log(TipoLog.Info, "Cálculos escolhidos: " + doBanco.Count + " de " + rv.CalculosNoBanco);
                    }

                    if (!op.ExportarPjc) continue;
                    if (escolhidos != null && escolhidos.Count == 0) { log(TipoLog.Info, "Nenhum cálculo escolhido para exportar nesta versão."); continue; }

                    // 3) exportar e conferir
                    try
                    {
                        int antes = res.Exportados;
                        var arqs = ExportarVersao(inst, pastaV, escolhidos, usarAberta, ui, log, ref nav, res, ini, passo);
                        rv.Exportados = res.Exportados - antes;
                        if (!ui.Cancelado)
                        {
                            int esp;
                            rv.Conferidos = Conferir(doBanco, arqs, log, out esp);
                            res.Conferidos += rv.Conferidos; res.Esperados += esp;
                            if (rv.Conferidos < esp) res.Falhas++;
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { log(TipoLog.Erro, "Exportação interrompida: " + ex.Message); res.Falhas++; }
                }
            }
            finally
            {
                if (nav != null) nav.Dispose();
                foreach (var inst in ordem) if (inst.LigadaPorNos) inst.Desligar(null);
            }

            if (ui.Cancelado) log(TipoLog.Aviso, "Backup cancelado pelo usuário.");
            Reabrir(op.ReabrirNoFinal, fechadasPorNos, res, log);

            string resumo = "Backup de " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + ": " + res.Exportados + " cálculo(s) exportado(s)" +
                            (res.Esperados > 0 ? " (" + res.Conferidos + " de " + res.Esperados + " conferidos)" : "") + ", " +
                            res.Bancos + " banco(s) copiado(s), " + res.Falhas + " falha(s).";
            reg.Calculos = res.Exportados; reg.Falhas = res.Falhas;
            Gravar(res.Pasta, rel, resumo, null, reg);
            ui.Progresso(1, resumo);
            return res;
        }

        // =========================================================== RESTAURAR .PJC

        public static ResultadoBackup Restaurar(Instalacao inst, List<string> arquivos, string destinoBackups, bool reabrir, IRetornoBackup ui)
        {
            var res = new ResultadoBackup();
            res.Pasta = CriarPasta(destinoBackups, " (antes da restauração)");
            var rel = new List<string>();
            var log = Registrador(ui, rel);
            var fechadasPorNos = new List<string>();
            var reg = new RegistroHistorico { Tipo = "restauracao" };
            reg.Versoes.Add(new RegistroVersao { Versao = inst.Versao, Pasta = inst.Pasta });

            log(TipoLog.Titulo, "Restaurar " + arquivos.Count + " arquivo(s) no PJe-Calc " + inst.Versao + "  —  " + inst.Pasta);
            try
            {
                ImportarArquivos(inst, arquivos, res.Pasta, ui, log, fechadasPorNos, res, 0, 1);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log(TipoLog.Erro, "Restauração interrompida: " + ex.Message); res.Falhas++; }

            if (ui.Cancelado) log(TipoLog.Aviso, "Cancelado pelo usuário.");
            Reabrir(reabrir, fechadasPorNos, res, log);
            string resumo = "Restauração de " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + ": " + res.Importados + " cálculo(s) importado(s), " + res.Falhas + " falha(s).";
            reg.Calculos = res.Importados; reg.Falhas = res.Falhas;
            Gravar(res.Pasta, rel, resumo, "Esta pasta guarda a cópia do banco ANTES da restauração (para desfazer).", reg);
            ui.Progresso(1, resumo);
            return res;
        }

        /// <summary>Cópia de segurança do banco de destino + importação dos arquivos (usada por Restaurar e Migrar).</summary>
        private static void ImportarArquivos(Instalacao inst, List<string> arquivos, string pastaSeguranca, IRetornoBackup ui,
            Action<TipoLog, string> log, List<string> fechadasPorNos, ResultadoBackup res, double ini, double passo)
        {
            if (!LiberarPorta(inst, ui, log, fechadasPorNos))
            {
                log(TipoLog.Erro, "Cancelado: o PJe-Calc " + inst.Versao + " precisa estar fechado.");
                res.Falhas++;
                return;
            }
            // cópia de segurança obrigatória antes de alterar o banco
            try
            {
                string z = inst.CopiarBanco(pastaSeguranca);
                log(TipoLog.Ok, "Cópia de segurança do banco do PJe-Calc " + inst.Versao + " feita antes de importar (" + (new FileInfo(z).Length / 1024) + " KB)");
                res.Bancos++;
            }
            catch (Exception ex)
            {
                log(TipoLog.Erro, "Não consegui fazer a cópia de segurança do banco (" + ex.Message + "). Por segurança, nada foi importado.");
                res.Falhas++;
                return;
            }

            Navegador nav = null;
            try
            {
                ui.Progresso(ini + passo * 0.05, "Abrindo o PJe-Calc " + inst.Versao + " em segundo plano…");
                log(TipoLog.Info, "Abrindo o PJe-Calc " + inst.Versao + " em segundo plano (sem janela)…");
                inst.Ligar(s => log(TipoLog.Info, s), () => ui.Cancelado);
                ui.Progresso(ini + passo * 0.1, "Preparando o navegador invisível…");
                nav = new Navegador();
                var exp = new Exportador(nav, inst.UrlBase, () => ui.Cancelado);
                int j = 0;
                foreach (string arq in arquivos)
                {
                    if (ui.Cancelado) break;
                    j++;
                    string nome = Path.GetFileName(arq);
                    ui.Progresso(ini + passo * (0.1 + 0.85 * (j - 1) / Math.Max(1, arquivos.Count)), "Importando " + j + " de " + arquivos.Count + ": " + nome);
                    try
                    {
                        string numero = exp.Importar(arq);
                        log(TipoLog.Ok, nome + "  →  cálculo nº " + numero);
                        res.Importados++;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { log(TipoLog.Erro, nome + ": " + ex.Message); res.Falhas++; }
                }
            }
            finally
            {
                if (nav != null) nav.Dispose();
                if (inst.LigadaPorNos)
                {
                    ui.Progresso(ini + passo * 0.97, "Fechando o PJe-Calc " + inst.Versao + "…");
                    inst.Desligar(s => log(TipoLog.Aviso, s));
                    log(TipoLog.Info, "PJe-Calc " + inst.Versao + " fechado com segurança");
                }
            }
        }

        // ================================================================== MIGRAR

        /// <summary>Exporta os cálculos escolhidos da versão de origem e importa na de destino.</summary>
        public static ResultadoBackup Migrar(Instalacao origem, Instalacao destino, HashSet<string> numeros, string destinoBackups, bool reabrir, IRetornoBackup ui)
        {
            var res = new ResultadoBackup();
            res.Pasta = CriarPasta(destinoBackups, " (migração " + origem.Versao + " para " + destino.Versao + ")");
            var rel = new List<string>();
            var log = Registrador(ui, rel);
            var fechadasPorNos = new List<string>();
            var reg = new RegistroHistorico { Tipo = "migracao" };
            reg.Versoes.Add(new RegistroVersao { Versao = origem.Versao, Pasta = origem.Pasta });
            reg.Versoes.Add(new RegistroVersao { Versao = destino.Versao, Pasta = destino.Pasta });
            Navegador nav = null;

            try
            {
                // 1) exportar da origem
                log(TipoLog.Titulo, "1/2  Exportar " + numeros.Count + " cálculo(s) do PJe-Calc " + origem.Versao);
                string pastaPjc = Path.Combine(res.Pasta, "Exportados " + origem.Versao);
                Directory.CreateDirectory(pastaPjc);
                List<string> arqs = new List<string>();
                if (LiberarPorta(origem, ui, log, fechadasPorNos))
                {
                    try { arqs = ExportarVersao(origem, pastaPjc, numeros, false, ui, log, ref nav, res, 0, 0.5); }
                    finally { if (nav != null) { nav.Dispose(); nav = null; } }
                }
                else { log(TipoLog.Erro, "O PJe-Calc " + origem.Versao + " precisa estar fechado."); res.Falhas++; }

                // 2) importar no destino
                if (!ui.Cancelado && arqs.Count > 0)
                {
                    log(TipoLog.Titulo, "2/2  Importar " + arqs.Count + " cálculo(s) no PJe-Calc " + destino.Versao);
                    string pastaSeg = Path.Combine(res.Pasta, "Banco " + destino.Versao + " antes");
                    Directory.CreateDirectory(pastaSeg);
                    ImportarArquivos(destino, arqs, pastaSeg, ui, log, fechadasPorNos, res, 0.5, 0.5);
                }
                else if (!ui.Cancelado) log(TipoLog.Aviso, "Nenhum cálculo foi exportado; nada a importar.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log(TipoLog.Erro, "Migração interrompida: " + ex.Message); res.Falhas++; }
            finally { if (nav != null) nav.Dispose(); }

            if (ui.Cancelado) log(TipoLog.Aviso, "Cancelado pelo usuário.");
            Reabrir(reabrir, fechadasPorNos, res, log);
            string resumo = "Migração de " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + " (PJe-Calc " + origem.Versao + " → " + destino.Versao + "): " +
                            res.Exportados + " exportado(s), " + res.Importados + " importado(s), " + res.Falhas + " falha(s).";
            reg.Calculos = res.Importados; reg.Falhas = res.Falhas;
            Gravar(res.Pasta, rel, resumo, "Os arquivos .PJC exportados e a cópia do banco de destino (antes da migração) ficam nesta pasta.", reg);
            ui.Progresso(1, resumo);
            return res;
        }

        // ===================================================== RESTAURAR O BANCO (.zip)

        public static ResultadoBackup RestaurarBanco(Instalacao inst, string zip, string destinoBackups, bool reabrir, IRetornoBackup ui)
        {
            var res = new ResultadoBackup();
            res.Pasta = CriarPasta(destinoBackups, " (antes de restaurar o banco)");
            var rel = new List<string>();
            var log = Registrador(ui, rel);
            var fechadasPorNos = new List<string>();
            var reg = new RegistroHistorico { Tipo = "restauracao-banco" };
            reg.Versoes.Add(new RegistroVersao { Versao = inst.Versao, Pasta = inst.Pasta });
            string tmp = Path.Combine(Path.GetTempPath(), "pjecalc-restaurar-" + Guid.NewGuid().ToString("N"));

            try
            {
                log(TipoLog.Titulo, "Restaurar o banco do PJe-Calc " + inst.Versao + " a partir de " + Path.GetFileName(zip));
                ui.Progresso(0.05, "Conferindo o arquivo de backup…");
                Directory.CreateDirectory(tmp);
                string novo = BancoPjeCalc.ExtrairBancoDoZip(zip, tmp);
                int noBackup = BancoPjeCalc.ListarDoArquivo(inst, novo).Count(c => c.Ativo);
                log(TipoLog.Ok, "Backup conferido: " + noBackup + " cálculo(s)");

                ui.Progresso(0.2, "Fechando o PJe-Calc…");
                if (!LiberarPorta(inst, ui, log, fechadasPorNos))
                {
                    log(TipoLog.Erro, "Cancelado: o PJe-Calc precisa estar fechado.");
                    res.Falhas++;
                }
                else
                {
                    ui.Progresso(0.4, "Guardando uma cópia do banco atual…");
                    string seg = inst.CopiarBanco(res.Pasta);
                    log(TipoLog.Ok, "Cópia de segurança do banco atual feita (" + (new FileInfo(seg).Length / 1024) + " KB)");
                    res.Bancos++;

                    ui.Progresso(0.7, "Restaurando o banco…");
                    inst.SubstituirBanco(novo);
                    int agora = BancoPjeCalc.Listar(inst).Count;
                    if (agora == noBackup) { log(TipoLog.Ok, "Banco restaurado e conferido: " + agora + " cálculo(s)"); res.Importados = agora; }
                    else { log(TipoLog.Erro, "Banco restaurado, mas a conferência encontrou " + agora + " de " + noBackup + " cálculo(s)."); res.Falhas++; }
                }
            }
            catch (Exception ex) { log(TipoLog.Erro, "Restauração do banco interrompida: " + ex.Message); res.Falhas++; }
            finally { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { } }

            Reabrir(reabrir, fechadasPorNos, res, log);
            string resumo = "Restauração do banco de " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + ": " + res.Importados + " cálculo(s) no banco restaurado, " + res.Falhas + " falha(s).";
            reg.Calculos = res.Importados; reg.Falhas = res.Falhas;
            Gravar(res.Pasta, rel, resumo, "Esta pasta guarda o banco que existia ANTES da restauração (para desfazer).", reg);
            ui.Progresso(1, resumo);
            return res;
        }
    }
}
