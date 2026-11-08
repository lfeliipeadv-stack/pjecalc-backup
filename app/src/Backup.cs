// Orquestra o backup e a restauração, uma versão do PJe-Calc de cada vez.
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
        public int Exportados, Importados, Falhas, Bancos;
        public string Reaberto;
    }

    public static class Backup
    {
        private static string NomeCurto(string pasta) { return pasta == null ? "outro programa" : pasta; }

        /// <summary>
        /// Libera a porta da versão: fecha com segurança qualquer PJe-Calc aberto nela.
        /// Devolve false se não foi possível (e o usuário escolheu pular).
        /// </summary>
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

        private static void Reabrir(List<string> fechadasPorNos, ResultadoBackup res, Action<TipoLog, string> log)
        {
            if (fechadasPorNos.Count == 0) return;
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

        // ================================================================== BACKUP

        public static ResultadoBackup Executar(List<Instalacao> instalacoes, OpcoesBackup op, IRetornoBackup ui)
        {
            var res = new ResultadoBackup();
            res.Pasta = CriarPasta(op.Destino, "");
            var rel = new List<string>();
            var log = Registrador(ui, rel);
            var fechadasPorNos = new List<string>();

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

                    string nomePasta = "PJe-Calc " + inst.Versao;
                    for (int k = 2; nomesUsados.Contains(nomePasta); k++) nomePasta = "PJe-Calc " + inst.Versao + " (" + k + ")";
                    nomesUsados.Add(nomePasta);
                    string pastaV = Path.Combine(res.Pasta, nomePasta);
                    Directory.CreateDirectory(pastaV);

                    log(TipoLog.Titulo, "PJe-Calc " + inst.Versao + "  —  " + inst.Pasta);
                    ui.Progresso(ini, "PJe-Calc " + inst.Versao);

                    // 1) fechar o que estiver aberto na porta (inclusive esta versão)
                    bool usarAberta = false;
                    if (!LiberarPorta(inst, ui, log, fechadasPorNos))
                    {
                        if (ui.Cancelado) break;
                        if (inst.EstaAberta()) usarAberta = true; // exporta usando a que está aberta, sem copiar o banco
                        else { log(TipoLog.Aviso, "Pulada: outro PJe-Calc continuou aberto na mesma porta."); continue; }
                    }

                    // 2) banco de dados (com a versão fechada a cópia fica consistente)
                    if (op.CopiarBanco)
                    {
                        if (usarAberta)
                            log(TipoLog.Aviso, "Banco de dados não copiado: esta versão continuou aberta.");
                        else
                        {
                            try
                            {
                                string z = inst.CopiarBanco(pastaV);
                                log(TipoLog.Ok, "Banco de dados copiado (" + (new FileInfo(z).Length / 1024) + " KB)");
                                res.Bancos++;
                            }
                            catch (Exception ex) { log(TipoLog.Erro, "Banco de dados: " + ex.Message); res.Falhas++; }
                        }
                    }

                    if (!op.ExportarPjc) continue;

                    // 3) ligar em segundo plano
                    if (!usarAberta)
                    {
                        try
                        {
                            ui.Progresso(ini + passo * 0.05, "Abrindo o PJe-Calc " + inst.Versao + " em segundo plano…");
                            log(TipoLog.Info, "Abrindo o PJe-Calc em segundo plano (sem janela)…");
                            inst.Ligar(s => log(TipoLog.Info, s), () => ui.Cancelado);
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex) { log(TipoLog.Erro, "Não foi possível abrir: " + ex.Message); res.Falhas++; inst.Desligar(null); continue; }
                    }

                    // 4) exportar
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
                        log(TipoLog.Info, calcs.Count + " cálculo(s) encontrados");
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
                                log(TipoLog.Ok, "Cálculo " + c.Numero + " — " + c.Reclamante + "  (" + (new FileInfo(arq).Length / 1024) + " KB)");
                                res.Exportados++;
                            }
                            catch (OperationCanceledException) { break; }
                            catch (Exception ex) { log(TipoLog.Erro, "Cálculo " + c.Numero + " — " + c.Reclamante + ": " + ex.Message); res.Falhas++; }
                        }
                        exp.FecharCalculoAberto();
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { log(TipoLog.Erro, "Exportação interrompida: " + ex.Message); res.Falhas++; }
                    finally
                    {
                        if (inst.LigadaPorNos)
                        {
                            ui.Progresso(ini + passo * 0.97, "Fechando o PJe-Calc " + inst.Versao + "…");
                            inst.Desligar(s => log(TipoLog.Aviso, s));
                            log(TipoLog.Info, "PJe-Calc fechado com segurança");
                        }
                    }
                }
            }
            finally
            {
                if (nav != null) nav.Dispose();
                foreach (var inst in ordem) if (inst.LigadaPorNos) inst.Desligar(null);
            }

            if (ui.Cancelado) log(TipoLog.Aviso, "Backup cancelado pelo usuário.");
            Reabrir(fechadasPorNos, res, log);

            string resumo = "Backup de " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + ": " + res.Exportados + " cálculo(s) exportado(s), " +
                            res.Bancos + " banco(s) copiado(s), " + res.Falhas + " falha(s).";
            rel.Insert(0, resumo);
            rel.Insert(1, "Gerado por PJe-Calc Backup (projeto independente, não oficial).");
            File.WriteAllLines(Path.Combine(res.Pasta, "relatorio.txt"), rel, new UTF8Encoding(true));
            ui.Progresso(1, resumo);
            return res;
        }

        // ============================================================= RESTAURAÇÃO

        /// <summary>
        /// Importa arquivos .PJC numa versão do PJe-Calc. Antes, guarda uma cópia do
        /// banco da versão de destino (para poder desfazer).
        /// </summary>
        public static ResultadoBackup Restaurar(Instalacao inst, List<string> arquivos, string destinoBackups, IRetornoBackup ui)
        {
            var res = new ResultadoBackup();
            res.Pasta = CriarPasta(destinoBackups, " (antes da restauração)");
            var rel = new List<string>();
            var log = Registrador(ui, rel);
            var fechadasPorNos = new List<string>();
            Navegador nav = null;

            try
            {
                log(TipoLog.Titulo, "Restaurar " + arquivos.Count + " arquivo(s) no PJe-Calc " + inst.Versao + "  —  " + inst.Pasta);
                ui.Progresso(0, "Preparando…");

                if (!LiberarPorta(inst, ui, log, fechadasPorNos))
                {
                    log(TipoLog.Erro, "Restauração cancelada: o PJe-Calc precisa estar fechado.");
                    res.Falhas++;
                    return Finalizar(res, rel, ui, fechadasPorNos, log, "restauração");
                }

                // cópia de segurança obrigatória antes de alterar o banco
                try
                {
                    string z = inst.CopiarBanco(res.Pasta);
                    log(TipoLog.Ok, "Cópia de segurança do banco feita antes de restaurar (" + (new FileInfo(z).Length / 1024) + " KB)");
                    res.Bancos++;
                }
                catch (Exception ex)
                {
                    log(TipoLog.Erro, "Não consegui fazer a cópia de segurança do banco (" + ex.Message + "). Por segurança, nada foi importado.");
                    res.Falhas++;
                    return Finalizar(res, rel, ui, fechadasPorNos, log, "restauração");
                }

                ui.Progresso(0.05, "Abrindo o PJe-Calc " + inst.Versao + " em segundo plano…");
                log(TipoLog.Info, "Abrindo o PJe-Calc em segundo plano (sem janela)…");
                inst.Ligar(s => log(TipoLog.Info, s), () => ui.Cancelado);

                ui.Progresso(0.1, "Preparando o navegador invisível…");
                nav = new Navegador();
                var exp = new Exportador(nav, inst.UrlBase, () => ui.Cancelado);

                int j = 0;
                foreach (string arq in arquivos)
                {
                    if (ui.Cancelado) break;
                    j++;
                    string nome = Path.GetFileName(arq);
                    ui.Progresso(0.1 + 0.85 * (j - 1) / Math.Max(1, arquivos.Count), "Importando " + j + " de " + arquivos.Count + ": " + nome);
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
            catch (OperationCanceledException) { }
            catch (Exception ex) { log(TipoLog.Erro, "Restauração interrompida: " + ex.Message); res.Falhas++; }
            finally
            {
                if (nav != null) nav.Dispose();
                if (inst.LigadaPorNos)
                {
                    ui.Progresso(0.97, "Fechando o PJe-Calc " + inst.Versao + "…");
                    inst.Desligar(s => log(TipoLog.Aviso, s));
                    log(TipoLog.Info, "PJe-Calc fechado com segurança");
                }
            }
            return Finalizar(res, rel, ui, fechadasPorNos, log, "restauração");
        }

        private static ResultadoBackup Finalizar(ResultadoBackup res, List<string> rel, IRetornoBackup ui, List<string> fechadasPorNos, Action<TipoLog, string> log, string oque)
        {
            if (ui.Cancelado) log(TipoLog.Aviso, "Cancelado pelo usuário.");
            Reabrir(fechadasPorNos, res, log);
            string resumo = "Restauração de " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + ": " + res.Importados + " cálculo(s) importado(s), " + res.Falhas + " falha(s).";
            rel.Insert(0, resumo);
            rel.Insert(1, "Esta pasta guarda a cópia do banco ANTES da restauração. Para desfazer, feche o PJe-Calc e coloque o pjecalc.h2.db do zip na pasta .dados da instalação.");
            File.WriteAllLines(Path.Combine(res.Pasta, "relatorio.txt"), rel, new UTF8Encoding(true));
            ui.Progresso(1, resumo);
            return res;
        }
    }
}
