// PJe-Calc Backup — versão interativa para o CMD (mesmo motor da janela).
//   PJeCalcBackup-Console.exe                 -> menus interativos
//   PJeCalcBackup-Console.exe --backup [...]  -> backup automático (ver --ajuda)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace PjeCalcBackup
{
    public class ModoConsole : IRetornoBackup
    {
        private Config config;
        private List<Instalacao> instalacoes = new List<Instalacao>();
        private volatile bool cancelado, emOperacao;
        private bool semPerguntas; // modo automático/agendado: nunca espera por uma tecla
        private string ultimaEtapa = "";
        private double ultimaFracao;
        private Atualizacao.Novidade novidade;

        // ================================================================ entrada

        [STAThread]
        public static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; Console.InputEncoding = Encoding.UTF8; } catch { }
            try { Console.Title = "PJe-Calc Backup"; } catch { }
            var m = new ModoConsole();
            m.config = Config.Carregar();
            Atualizacao.LimparAntigos();
            Console.CancelKeyPress += (s, e) =>
            {
                if (!m.emOperacao) return; // no menu, Ctrl+C fecha o programa normalmente
                e.Cancel = true; m.cancelado = true; m.Aviso("Cancelando com segurança… aguarde o item atual terminar.");
            };
            try
            {
                // arquivos arrastados sobre o .bat/.exe: .PJC ou pastas → restaurar; .zip → restaurar o banco
                if (args.Length > 0 && args.All(a => !a.StartsWith("-") && !a.StartsWith("/") && (File.Exists(a) || Directory.Exists(a))))
                {
                    m.AjustarJanela();
                    if (args.Length == 1 && args[0].EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) m.FluxoRestaurarBanco(args[0]);
                    else
                    {
                        var arqs = new List<string>();
                        foreach (var a in args)
                        {
                            if (Directory.Exists(a)) arqs.AddRange(Directory.GetFiles(a, "*.PJC", SearchOption.AllDirectories));
                            else if (a.EndsWith(".pjc", StringComparison.OrdinalIgnoreCase)) arqs.Add(a);
                        }
                        m.FluxoRestaurar(arqs.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList());
                    }
                    return 0;
                }
                if (args.Length > 0) return m.Automatico(args);
                m.AjustarJanela();
                m.VerificarAtualizacaoEmSegundoPlano();
                m.Menu();
                return 0;
            }
            catch (Exception ex)
            {
                m.Cor(ConsoleColor.Red, "\n  Erro: " + ex.Message + "\n");
                return 2;
            }
        }

        // ================================================================ visual

        private void Cor(ConsoleColor c, string t) { var a = Console.ForegroundColor; Console.ForegroundColor = c; Console.Write(t); Console.ForegroundColor = a; }
        private void Linha(ConsoleColor c, string t) { Cor(c, t + Environment.NewLine); }
        private void Aviso(string t) { Linha(ConsoleColor.Yellow, "  ! " + t); }

        private void Cabecalho(string subtitulo)
        {
            try { Console.Clear(); } catch { }
            string titulo = "  PJe-Calc Backup  ·  versão " + Atualizacao.Atual + "  ·  modo console";
            int w = Math.Max(60, titulo.Length + 4);
            Linha(ConsoleColor.Cyan, "  ╔" + new string('═', w) + "╗");
            Cor(ConsoleColor.Cyan, "  ║"); Cor(ConsoleColor.White, titulo.PadRight(w)); Linha(ConsoleColor.Cyan, "║");
            Linha(ConsoleColor.Cyan, "  ╚" + new string('═', w) + "╝");
            Linha(ConsoleColor.DarkGray, "  Projeto independente e não oficial. Tudo roda só neste computador.");
            if (!string.IsNullOrEmpty(subtitulo)) { Console.WriteLine(); Linha(ConsoleColor.White, "  " + subtitulo); Linha(ConsoleColor.DarkGray, "  " + new string('─', subtitulo.Length)); }
            Console.WriteLine();
        }

        /// <summary>Menu com setas ↑↓ e Enter (ou o número). Devolve o índice, ou -1 para voltar (Esc).</summary>
        private int Escolher(string titulo, IList<string> opcoes, int inicial = 0)
        {
            if (!string.IsNullOrEmpty(titulo)) Linha(ConsoleColor.White, "  " + titulo);
            Linha(ConsoleColor.DarkGray, "  (↑ ↓ para mover, Enter para escolher, Esc para voltar)");
            int topo = Console.CursorTop, sel = Math.Max(0, Math.Min(inicial, opcoes.Count - 1));
            while (true)
            {
                Console.SetCursorPosition(0, topo);
                for (int i = 0; i < opcoes.Count; i++)
                {
                    string t = "   " + (i == sel ? "► " : "  ") + opcoes[i];
                    if (i == sel) Linha(ConsoleColor.Cyan, t.PadRight(Math.Max(t.Length, Console.WindowWidth - 1)));
                    else Linha(ConsoleColor.Gray, t.PadRight(Math.Max(t.Length, Console.WindowWidth - 1)));
                }
                var k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.UpArrow) sel = (sel + opcoes.Count - 1) % opcoes.Count;
                else if (k.Key == ConsoleKey.DownArrow) sel = (sel + 1) % opcoes.Count;
                else if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); return sel; }
                else if (k.Key == ConsoleKey.Escape) { Console.WriteLine(); return -1; }
                else if (char.IsDigit(k.KeyChar))
                {
                    int n = k.KeyChar - '0';
                    for (int i = 0; i < opcoes.Count; i++)
                        if (opcoes[i].TrimStart().StartsWith(n + " ")) { Console.SetCursorPosition(0, topo + opcoes.Count); Console.WriteLine(); return i; }
                }
            }
        }

        /// <summary>Lista para marcar: ↑↓ move, Espaço marca, A marca/desmarca todos, Enter confirma, Esc volta.</summary>
        private bool[] Marcar(string titulo, IList<string> itens, bool[] marcados, IList<string> etiquetas = null)
        {
            Linha(ConsoleColor.White, "  " + titulo);
            Linha(ConsoleColor.DarkGray, "  (↑ ↓ mover · Espaço marcar · A marcar/desmarcar todos · Enter confirmar · Esc voltar)");
            int visiveis = Math.Max(5, Math.Min(itens.Count, Console.WindowHeight - Console.CursorTop - 4));
            int topo = Console.CursorTop, sel = 0, ini = 0;
            var m = (bool[])marcados.Clone();
            while (true)
            {
                if (sel < ini) ini = sel;
                if (sel >= ini + visiveis) ini = sel - visiveis + 1;
                Console.SetCursorPosition(0, topo);
                for (int j = 0; j < visiveis; j++)
                {
                    int i = ini + j;
                    string t = i < itens.Count ? "   " + (i == sel ? "► " : "  ") + (m[i] ? "[√] " : "[ ] ") + itens[i] : "";
                    int largura = Console.WindowWidth - 1;
                    string et = (i < itens.Count && etiquetas != null && !string.IsNullOrEmpty(etiquetas[i])) ? "  " + etiquetas[i] : "";
                    if (t.Length + et.Length > largura) t = t.Substring(0, Math.Max(0, largura - et.Length - 1)) + "…";
                    Cor(i == sel ? ConsoleColor.Cyan : (i < itens.Count && m[i] ? ConsoleColor.White : ConsoleColor.Gray), t);
                    Cor(ConsoleColor.Yellow, et);
                    Console.WriteLine(new string(' ', Math.Max(0, largura - t.Length - et.Length)));
                }
                Cor(ConsoleColor.DarkGray, ("   " + m.Count(x => x) + " de " + itens.Count + " marcado(s)" + (itens.Count > visiveis ? "  ·  " + (ini + 1) + "–" + Math.Min(itens.Count, ini + visiveis) + " de " + itens.Count : "")).PadRight(Console.WindowWidth - 1) + "\n");
                var k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.UpArrow && itens.Count > 0) sel = (sel + itens.Count - 1) % itens.Count;
                else if (k.Key == ConsoleKey.DownArrow && itens.Count > 0) sel = (sel + 1) % itens.Count;
                else if (k.Key == ConsoleKey.PageDown) sel = Math.Min(itens.Count - 1, sel + visiveis);
                else if (k.Key == ConsoleKey.PageUp) sel = Math.Max(0, sel - visiveis);
                else if (k.Key == ConsoleKey.Spacebar && itens.Count > 0) m[sel] = !m[sel];
                else if (k.Key == ConsoleKey.A) { bool todos = !m.All(x => x); for (int i = 0; i < m.Length; i++) m[i] = todos; }
                else if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); return m; }
                else if (k.Key == ConsoleKey.Escape) { Console.WriteLine(); return null; }
            }
        }

        private string Perguntar(string texto, string padrao)
        {
            Cor(ConsoleColor.White, "  " + texto);
            if (!string.IsNullOrEmpty(padrao)) Cor(ConsoleColor.DarkGray, " [" + padrao + "]");
            Cor(ConsoleColor.White, ": ");
            string r = Console.ReadLine();
            return string.IsNullOrWhiteSpace(r) ? padrao : r.Trim().Trim('"');
        }

        private bool SimNao(string texto, bool padrao)
        {
            Cor(ConsoleColor.White, "  " + texto + (padrao ? " (S/n): " : " (s/N): "));
            while (true)
            {
                var k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.Enter) { Console.WriteLine(padrao ? "S" : "N"); return padrao; }
                if (k.Key == ConsoleKey.S || k.Key == ConsoleKey.Y) { Console.WriteLine("S"); return true; }
                if (k.Key == ConsoleKey.N || k.Key == ConsoleKey.Escape) { Console.WriteLine("N"); return false; }
            }
        }

        private void Pausa()
        {
            Linha(ConsoleColor.DarkGray, "\n  Pressione qualquer tecla para voltar ao menu…");
            Console.ReadKey(true);
        }

        // ================================================================ menu

        private void Menu()
        {
            while (true)
            {
                Cabecalho(null);
                try
                {
                    var ult = Historico.UltimoBackup(config.Destino);
                    if (ult == null) Aviso("Nenhum backup encontrado em " + config.Destino);
                    else
                    {
                        int d = (int)(DateTime.Now - ult.Value).TotalDays;
                        if (config.Lembrete && d >= Math.Max(1, config.LembreteDias)) Aviso("Seu último backup foi há " + d + " dias (" + ult.Value.ToString("dd/MM/yyyy") + ").");
                        else Linha(ConsoleColor.DarkGray, "  Último backup: " + ult.Value.ToString("dd/MM/yyyy HH:mm"));
                    }
                    Console.WriteLine();
                }
                catch { }
                lock (this)
                {
                    if (novidade != null) { Linha(ConsoleColor.Cyan, "  * Nova versão disponível: " + novidade.Versao + " (você tem a " + Atualizacao.Atual + "). Veja em Configurações → Atualizar."); Console.WriteLine(); }
                }
                int op = Escolher("O que você quer fazer?", new[]
                {
                    "1  Fazer backup",
                    "2  Restaurar cálculos (.PJC)",
                    "3  Migrar cálculos entre versões",
                    "4  Restaurar o banco completo (.zip)",
                    "5  Histórico de backups",
                    "6  Buscar um cálculo nos backups",
                    "7  Planilha dos cálculos (Excel)",
                    "8  Limpar backups antigos",
                    "9  Configurações (backup automático, atualização…)",
                    "0  Sair"
                });
                switch (op)
                {
                    case 0: FluxoBackup(); break;
                    case 1: FluxoRestaurar(null); break;
                    case 2: FluxoMigrar(); break;
                    case 3: FluxoRestaurarBanco(null); break;
                    case 4: FluxoHistorico(); break;
                    case 5: FluxoBuscar(); break;
                    case 6: FluxoPlanilha(); break;
                    case 7: FluxoLimpeza(); break;
                    case 8: FluxoConfiguracoes(); break;
                    default: return;
                }
            }
        }

        private void CarregarInstalacoes()
        {
            Linha(ConsoleColor.DarkGray, "  Procurando instalações do PJe-Calc no computador…");
            var pastas = config.PastasExtras.Where(Instalacao.PareceInstalacao).ToList();
            foreach (var p in Instalacao.Procurar(null, null))
                if (!pastas.Any(a => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(p).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) pastas.Add(p);
            instalacoes = new List<Instalacao>();
            foreach (var p in pastas) { try { instalacoes.Add(Instalacao.Carregar(p)); } catch { } }
            instalacoes = instalacoes.Where(i => File.Exists(i.ArquivoBanco))
                .OrderByDescending(i => string.Join(".", i.Versao.Split('.').Select(x => x.PadLeft(5, '0')))).ToList();
        }

        private string Rotulo(Instalacao i)
        {
            string est = i.EstaAberta() ? "aberta agora" : "fechada";
            int? n = i.ContarCalculos();
            return ("PJe-Calc " + i.Versao).PadRight(18) + (n.HasValue ? (n + " cálculo(s)").PadRight(16) : "".PadRight(16)) + est.PadRight(14) + i.Pasta;
        }

        private Instalacao EscolherVersao(string titulo, Instalacao excluir = null)
        {
            var lista = instalacoes.Where(i => i != excluir).ToList();
            if (lista.Count == 0) { Aviso("Nenhuma versão disponível."); return null; }
            int s = Escolher(titulo, lista.Select(Rotulo).ToList());
            return s < 0 ? null : lista[s];
        }

        private string TextoAbertas(IEnumerable<Instalacao> envolvidas)
        {
            var abertas = new List<string>();
            foreach (int porta in envolvidas.Select(i => i.PortaHttp).Distinct())
            {
                string dono = Instalacao.InstalacaoNaPorta(porta);
                if (dono == null) continue;
                var i = instalacoes.FirstOrDefault(x => string.Equals(x.Pasta, dono, StringComparison.OrdinalIgnoreCase));
                abertas.Add(i != null ? "PJe-Calc " + i.Versao : dono);
            }
            return abertas.Count == 0 ? null : string.Join(" e ", abertas);
        }

        private bool AvisoInicial(string detalhe, IEnumerable<Instalacao> envolvidas)
        {
            Console.WriteLine();
            Linha(ConsoleColor.Yellow, "  ┌─ Antes de começar ─────────────────────────────────────────");
            Linha(ConsoleColor.Yellow, "  │ Salve e feche os cálculos que estiver editando no PJe-Calc.");
            string ab = TextoAbertas(envolvidas);
            if (ab != null) Linha(ConsoleColor.Yellow, "  │ O " + ab + " está aberto e será fechado com segurança" + (config.ReabrirPjeCalc ? " (e reaberto no final)." : "."));
            if (!string.IsNullOrEmpty(detalhe)) foreach (var l in detalhe.Split('\n')) Linha(ConsoleColor.Yellow, "  │ " + l);
            Linha(ConsoleColor.Yellow, "  │ Durante a operação, não abra o PJe-Calc.");
            Linha(ConsoleColor.Yellow, "  └────────────────────────────────────────────────────────────");
            return SimNao("Já salvei. Continuar?", true);
        }

        private bool Destino()
        {
            Linha(ConsoleColor.White, "  Os backups ficam em: " + config.Destino);
            if (!SimNao("Usar essa pasta?", true))
            {
                string p = Perguntar("Nova pasta", config.Destino);
                config.Destino = p; config.Salvar();
            }
            try { Directory.CreateDirectory(config.Destino); return true; }
            catch (Exception ex) { Aviso("Não consegui usar a pasta: " + ex.Message); return false; }
        }

        private void Resultado(ResultadoBackup r, string texto)
        {
            Console.WriteLine();
            bool ok = r.Falhas == 0;
            Linha(ok ? ConsoleColor.Green : ConsoleColor.Yellow, "  " + (ok ? "√ " : "! ") + texto);
            if (r.Reaberto != null) Linha(ConsoleColor.Gray, "    O " + r.Reaberto + " foi reaberto.");
            Linha(ConsoleColor.Gray, "    Pasta: " + r.Pasta);
            if (config.Sons) Avisos.Som(ok);
            if (r.Pasta != null && SimNao("Abrir a pasta?", false)) { try { Process.Start("explorer.exe", "\"" + r.Pasta + "\""); } catch { } }
        }

        // ================================================================ fluxos

        private void FluxoBackup()
        {
            Cabecalho("Fazer backup  ·  etapa 1 de 4: versões");
            CarregarInstalacoes();
            if (instalacoes.Count == 0) { Aviso("Nenhuma instalação do PJe-Calc com cálculos foi encontrada."); Pausa(); return; }
            var marc = Marcar("Quais versões entram no backup?", instalacoes.Select(Rotulo).ToList(), instalacoes.Select(i => !config.Desmarcadas.Contains(i.Pasta, StringComparer.OrdinalIgnoreCase)).ToArray());
            if (marc == null) return;
            var escolhidas = instalacoes.Where((i, k) => marc[k]).ToList();
            if (escolhidas.Count == 0) { Aviso("Nenhuma versão marcada."); Pausa(); return; }

            var op = new OpcoesBackup { Destino = config.Destino, ReabrirNoFinal = config.ReabrirPjeCalc };
            Cabecalho("Fazer backup  ·  etapa 2 de 4: cálculos");
            int modo = Escolher("Quais cálculos exportar em .PJC?", new[] { "1  Todos", "2  Escolher um a um", "3  Nenhum (só copiar o banco de dados)" });
            if (modo < 0) return;
            if (modo == 1)
            {
                foreach (var inst in escolhidas)
                {
                    Cabecalho("Fazer backup  ·  etapa 2 de 4: cálculos do PJe-Calc " + inst.Versao);
                    Linha(ConsoleColor.DarkGray, "  Lendo os cálculos do banco (sem abrir o PJe-Calc)…");
                    var calcs = BancoPjeCalc.Listar(inst);
                    var m = Marcar("Cálculos do PJe-Calc " + inst.Versao + ":", calcs.Select(c => ("nº " + c.Numero).PadRight(8) + (c.Reclamante ?? "") + "  ·  " + c.Resumo).ToList(), calcs.Select(c => true).ToArray());
                    if (m == null) return;
                    if (!m.All(x => x)) op.Selecao[inst.Pasta] = new HashSet<string>(calcs.Where((c, k) => m[k]).Select(c => c.Numero));
                }
            }
            op.ExportarPjc = modo != 2;

            Cabecalho("Fazer backup  ·  etapa 3 de 4: destino");
            if (!Destino()) { Pausa(); return; }
            op.Destino = config.Destino;
            op.CopiarBanco = SimNao("Copiar também o banco de dados completo de cada versão?", true);
            if (op.ExportarPjc) { op.SoAlterados = SimNao("Backup rápido (exportar só os cálculos novos ou alterados; os outros são copiados do backup anterior)?", config.BackupRapido); config.BackupRapido = op.SoAlterados; config.Salvar(); }
            if (!op.ExportarPjc && !op.CopiarBanco) { Aviso("Nada para fazer."); Pausa(); return; }
            if (op.ExportarPjc && Navegador.Localizar() == null) { Aviso("É preciso ter o Microsoft Edge ou o Google Chrome."); Pausa(); return; }
            if (!AvisoInicial(null, escolhidas)) return;

            Cabecalho("Fazer backup  ·  etapa 4 de 4: backup");
            cancelado = false;
            emOperacao = true; ResultadoBackup r; try { r = Backup.Executar(escolhidas, op, this); } finally { emOperacao = false; }
            FimProgresso();
            Resultado(r, "Backup concluído: " + r.Exportados + " cálculo(s) exportado(s)" + (r.Copiados > 0 ? " + " + r.Copiados + " sem alteração (copiados)" : "") + (r.Esperados > 0 ? " (" + r.Conferidos + " de " + r.Esperados + " conferidos)" : "") + ", " + r.Bancos + " banco(s), " + r.Falhas + " falha(s).");
            Pausa();
        }

        private List<string> PedirArquivosPjc()
        {
            string p = Perguntar("Arquivo .PJC ou pasta com arquivos .PJC (pode arrastar para cá)", config.UltimaPastaPjc ?? config.Destino);
            if (string.IsNullOrEmpty(p)) return null;
            if (File.Exists(p)) return new List<string> { p };
            if (Directory.Exists(p)) { config.UltimaPastaPjc = p; config.Salvar(); return Directory.GetFiles(p, "*.PJC", SearchOption.AllDirectories).OrderBy(x => x).ToList(); }
            Aviso("Não encontrei: " + p);
            return null;
        }

        private void FluxoRestaurar(List<string> jaEscolhidos)
        {
            Cabecalho("Restaurar cálculos (.PJC)  ·  etapa 1 de 3: arquivos");
            var arqs = jaEscolhidos;
            if (arqs != null) Linha(ConsoleColor.Gray, "  " + arqs.Count + " arquivo(s) .PJC escolhido(s).");
            else arqs = PedirArquivosPjc();
            if (arqs == null || arqs.Count == 0) { Aviso("Nenhum arquivo .PJC."); Pausa(); return; }
            CarregarInstalacoes();
            Cabecalho("Restaurar cálculos (.PJC)  ·  etapa 2 de 3: destino");
            var dest = EscolherVersao("Em qual versão restaurar?");
            if (dest == null) return;

            Linha(ConsoleColor.DarkGray, "  Lendo os arquivos e comparando com os cálculos do PJe-Calc " + dest.Versao + "…");
            var infos = new List<CalculoInfo>();
            foreach (var a in arqs) { try { infos.Add(BancoPjeCalc.LerPjc(a)); } catch { Aviso("Ilegível: " + Path.GetFileName(a)); } }
            var dup = BancoPjeCalc.MapaDuplicados(BancoPjeCalc.Listar(dest));
            var etiquetas = infos.Select(i => { string n; return dup.TryGetValue(i.Chave, out n) ? "parece já existir (nº " + n + ")" : ""; }).ToList();
            Cabecalho("Restaurar cálculos (.PJC)  ·  etapa 3 de 3: revisão");
            var m = Marcar("Quais restaurar? (os que parecem já existir vêm desmarcados)",
                infos.Select(i => ("nº " + i.Numero).PadRight(8) + (i.Reclamante ?? "") + "  ·  " + Path.GetFileName(i.Arquivo)).ToList(),
                etiquetas.Select(e => e == "").ToArray(), etiquetas);
            if (m == null) return;
            var escolhidos = infos.Where((i, k) => m[k]).Select(i => i.Arquivo).ToList();
            if (escolhidos.Count == 0) { Aviso("Nenhum marcado."); Pausa(); return; }
            if (!Destino()) { Pausa(); return; }
            if (Navegador.Localizar() == null) { Aviso("É preciso ter o Microsoft Edge ou o Google Chrome."); Pausa(); return; }
            if (!AvisoInicial(escolhidos.Count + " arquivo(s) serão importados no PJe-Calc " + dest.Versao + " (cada um vira um cálculo NOVO).\nAntes, uma cópia do banco atual é guardada em " + config.Destino, new[] { dest })) return;

            Console.WriteLine();
            cancelado = false;
            emOperacao = true; ResultadoBackup r; try { r = Backup.Restaurar(dest, escolhidos, config.Destino, config.ReabrirPjeCalc, this); } finally { emOperacao = false; }
            FimProgresso();
            Resultado(r, "Restauração concluída: " + r.Importados + " cálculo(s) importado(s), " + r.Falhas + " falha(s).");
            Pausa();
        }

        private void FluxoMigrar()
        {
            Cabecalho("Migrar entre versões  ·  etapa 1 de 3: versões");
            CarregarInstalacoes();
            if (instalacoes.Count < 2) { Aviso("É preciso ter pelo menos duas versões do PJe-Calc com cálculos."); Pausa(); return; }
            var o = EscolherVersao("Levar os cálculos DE qual versão?");
            if (o == null) return;
            var d = EscolherVersao("PARA qual versão?", o);
            if (d == null) return;

            Cabecalho("Migrar do PJe-Calc " + o.Versao + " para o " + d.Versao + "  ·  etapa 2 de 3: cálculos");
            Linha(ConsoleColor.DarkGray, "  Lendo os cálculos das duas versões (sem abrir o PJe-Calc)…");
            var calcs = BancoPjeCalc.Listar(o);
            var dup = BancoPjeCalc.MapaDuplicados(BancoPjeCalc.Listar(d));
            var etiquetas = calcs.Select(c => { string n; return dup.TryGetValue(c.Chave, out n) ? "já existe no destino (nº " + n + ")" : ""; }).ToList();
            var m = Marcar("Quais levar? (os que já existem no destino vêm desmarcados)",
                calcs.Select(c => ("nº " + c.Numero).PadRight(8) + (c.Reclamante ?? "") + "  ·  " + c.Resumo).ToList(), etiquetas.Select(e => e == "").ToArray(), etiquetas);
            if (m == null) return;
            var nums = new HashSet<string>(calcs.Where((c, k) => m[k]).Select(c => c.Numero));
            if (nums.Count == 0) { Aviso("Nenhum marcado."); Pausa(); return; }
            if (!Destino()) { Pausa(); return; }
            if (Navegador.Localizar() == null) { Aviso("É preciso ter o Microsoft Edge ou o Google Chrome."); Pausa(); return; }
            if (!AvisoInicial(nums.Count + " cálculo(s) do PJe-Calc " + o.Versao + " serão levados para o " + d.Versao + ".\nAntes, uma cópia do banco do " + d.Versao + " é guardada em " + config.Destino, new[] { o, d })) return;

            Cabecalho("Migrar do PJe-Calc " + o.Versao + " para o " + d.Versao + "  ·  etapa 3 de 3: migração");
            cancelado = false;
            emOperacao = true; ResultadoBackup r; try { r = Backup.Migrar(o, d, nums, config.Destino, config.ReabrirPjeCalc, this); } finally { emOperacao = false; }
            FimProgresso();
            Resultado(r, "Migração concluída: " + r.Exportados + " exportado(s), " + r.Importados + " importado(s), " + r.Falhas + " falha(s).");
            Pausa();
        }

        private void FluxoRestaurarBanco(string zipEscolhido)
        {
            Cabecalho("Restaurar o banco completo");
            string zip = zipEscolhido ?? Perguntar("Arquivo .zip do backup do banco (pode arrastar para cá)", null);
            if (string.IsNullOrEmpty(zip) || !File.Exists(zip)) { Aviso("Arquivo não encontrado."); Pausa(); return; }
            CarregarInstalacoes();
            string v = BancoPjeCalc.VersaoDoZip(zip);
            Linha(ConsoleColor.Gray, "  Backup do PJe-Calc " + (v ?? "?"));
            var dest = EscolherVersao("Em qual versão restaurar?");
            if (dest == null) return;
            if (v != null && v != dest.Versao) { Aviso("Este backup é do PJe-Calc " + v + "; o banco de uma versão pode não funcionar em outra. Use a migração ou os .PJC."); Pausa(); return; }
            if (!Destino()) { Pausa(); return; }
            if (!AvisoInicial("TODOS os cálculos atuais do PJe-Calc " + dest.Versao + " serão substituídos pelos do backup.\nAntes, uma cópia do banco atual é guardada em " + config.Destino, new[] { dest })) return;
            Console.WriteLine();
            cancelado = false;
            emOperacao = true; ResultadoBackup r; try { r = Backup.RestaurarBanco(dest, zip, config.Destino, config.ReabrirPjeCalc, this); } finally { emOperacao = false; }
            FimProgresso();
            Resultado(r, "Restauração do banco: " + r.Importados + " cálculo(s) no banco restaurado, " + r.Falhas + " falha(s).");
            Pausa();
        }

        private void FluxoHistorico()
        {
            Cabecalho("Histórico  ·  " + config.Destino);
            var l = Historico.Listar(config.Destino).Take(30).ToList();
            if (l.Count == 0) { Aviso("Nenhum backup encontrado nesta pasta."); Pausa(); return; }
            var opcoes = l.Select(r => r.Data.ToString("dd/MM/yyyy HH:mm") + "  " + r.TipoTexto.PadRight(24) + " " +
                ("PJe-Calc " + string.Join(", ", r.Versoes.Select(x => x.Versao).Distinct())).PadRight(28) + r.Calculos + " cálculo(s)" + (r.Falhas > 0 ? ", " + r.Falhas + " falha(s)" : "")).ToList();
            int s = Escolher("Escolha um item para abrir a pasta:", opcoes);
            if (s >= 0) { try { Process.Start("explorer.exe", "\"" + l[s].PastaOperacao + "\""); } catch { } }
        }

        private void FluxoConfiguracoes()
        {
            while (true)
            {
                Cabecalho("Configurações");
                int s = Escolher(null, new[]
                {
                    "1  Pasta dos backups: " + config.Destino,
                    "2  Sons ao terminar: " + (config.Sons ? "ligados" : "desligados"),
                    "3  Reabrir o PJe-Calc no final: " + (config.ReabrirPjeCalc ? "sim" : "não"),
                    "4  Lembrete de backup: " + (config.Lembrete ? "a cada " + config.LembreteDias + " dias" : "desligado"),
                    "5  Limpeza de backups antigos: " + Limpeza.Descrever(config.Limpeza),
                    "6  Backup automático: " + TextoAgenda(),
                    "7  Atualizar o programa (versão instalada: " + Atualizacao.Atual + ")",
                    "8  Sobre e apoie este projeto ♥",
                    "0  Voltar"
                });
                if (s == 0) { string p = Perguntar("Nova pasta dos backups", config.Destino); config.Destino = p; }
                else if (s == 1) config.Sons = !config.Sons;
                else if (s == 2) config.ReabrirPjeCalc = !config.ReabrirPjeCalc;
                else if (s == 3)
                {
                    int d = Escolher("Lembrete:", new[] { "Desligado", "7 dias", "15 dias", "30 dias" });
                    if (d == 0) config.Lembrete = false; else if (d > 0) { config.Lembrete = true; config.LembreteDias = new[] { 0, 7, 15, 30 }[d]; }
                }
                else if (s == 4)
                {
                    int d = Escolher("Limpeza de backups antigos (ao terminar um backup, o programa oferece mover os antigos para a Lixeira):",
                        Limpeza.Codigos.Select(Limpeza.Descrever).ToList(), Array.IndexOf(Limpeza.Codigos, config.Limpeza ?? ""));
                    if (d >= 0) config.Limpeza = Limpeza.Codigos[d];
                }
                else if (s == 5) FluxoAgenda();
                else if (s == 6) { if (FluxoAtualizar()) return; }
                else if (s == 7) FluxoSobre();
                else { config.Salvar(); return; }
                config.Salvar();
            }
        }

        // ================================================================ 1.3: agenda, atualização, busca, planilha, limpeza

        /// <summary>A janela padrão do CMD é pequena; aumenta, mantendo o histórico de rolagem.</summary>
        private void AjustarJanela()
        {
            try
            {
                int w = Math.Min(130, Console.LargestWindowWidth), h = Math.Min(42, Console.LargestWindowHeight);
                if (Console.BufferWidth < w) Console.BufferWidth = w;
                if (Console.BufferHeight < 3000) Console.BufferHeight = 3000;
                if (Console.WindowWidth < w) Console.WindowWidth = w;
                if (Console.WindowHeight < h) Console.WindowHeight = h;
            }
            catch { }
        }

        private void VerificarAtualizacaoEmSegundoPlano()
        {
            if (!config.VerificarAtualizacoes) return;
            ThreadPool.QueueUserWorkItem(_ => { try { var n = Atualizacao.Verificar(); lock (this) novidade = n; } catch { } });
        }

        private string TextoAgenda()
        {
            var e = Agendador.Consultar();
            if (!e.Existe) return "desligado";
            return "ligado, " + Agendador.Descrever(config.AgendaFrequencia, config.AgendaDias, config.AgendaHora) + (e.ExeExiste ? "" : " (programa não encontrado — salve de novo)");
        }

        private void FluxoAgenda()
        {
            Cabecalho("Backup automático");
            var e = Agendador.Consultar();
            if (e.Existe)
            {
                Linha(ConsoleColor.Green, "  Ligado: " + Agendador.Descrever(config.AgendaFrequencia, config.AgendaDias, config.AgendaHora));
                if (!string.IsNullOrEmpty(e.Proxima)) Linha(ConsoleColor.Gray, "  Próximo: " + e.Proxima + (string.IsNullOrEmpty(e.Resultado) ? "" : "   ·   Último: " + e.Ultima + " (" + e.Resultado + ")"));
                Console.WriteLine();
            }
            Linha(ConsoleColor.DarkGray, "  Cria uma tarefa no Agendador de Tarefas do Windows. Roda com você conectado; se o computador");
            Linha(ConsoleColor.DarkGray, "  estiver desligado na hora marcada, roda assim que for ligado. Usa a pasta de backups e as");
            Linha(ConsoleColor.DarkGray, "  versões marcadas no programa. Se o PJe-Calc estiver aberto, avisa e espera 2 minutos.");
            Console.WriteLine();
            int op = Escolher(null, e.Existe ? new[] { "1  Alterar dias e horário", "2  Desligar o backup automático", "0  Voltar" } : new[] { "1  Ligar o backup automático", "0  Voltar" });
            if (op < 0 || (e.Existe ? op == 2 : op == 1)) return;
            if (e.Existe && op == 1) { try { Agendador.Remover(); Linha(ConsoleColor.Green, "  √ Backup automático desligado."); } catch (Exception ex) { Aviso(ex.Message); } Pausa(); return; }

            int f = Escolher("Com que frequência?", new[] { "1  Todos os dias", "2  Em dias da semana" }, config.AgendaFrequencia == "diario" ? 0 : 1);
            if (f < 0) return;
            string freq = f == 0 ? "diario" : "semanal";
            var dias = config.AgendaDias;
            if (freq == "semanal")
            {
                var m = Marcar("Quais dias?", Agendador.DiasPt.ToList(), Agendador.Dias.Select(d => config.AgendaDias.Contains(d)).ToArray());
                if (m == null) return;
                dias = Agendador.Dias.Where((d, i) => m[i]).ToList();
                if (dias.Count == 0) { Aviso("Nenhum dia marcado."); Pausa(); return; }
            }
            string hora = Perguntar("Horário (ex.: 18:00)", config.AgendaHora);
            if (hora.Length == 4 && hora[1] == ':') hora = "0" + hora;
            bool limpar = false;
            if (!string.IsNullOrEmpty(config.Limpeza) && Limpeza.TemLixeira(config.Destino))
                limpar = SimNao("Depois do backup, aplicar a limpeza (" + Limpeza.Descrever(config.Limpeza) + ") sem perguntar?", config.AgendaLimpar);
            try
            {
                Agendador.Criar(freq, dias, hora, limpar); config.AgendaCriada = DateTime.Now;
                config.AgendaFrequencia = freq; config.AgendaDias = dias; config.AgendaHora = hora; config.AgendaLimpar = limpar; config.Salvar();
                Linha(ConsoleColor.Green, "  √ Backup automático ligado: " + Agendador.Descrever(freq, dias, hora) + ".");
            }
            catch (Exception ex) { Aviso(ex.Message); }
            Pausa();
        }

        private void FluxoSobre()
        {
            Cabecalho("Sobre e apoie este projeto");
            Linha(ConsoleColor.White, "  Este programa nasceu de uma dificuldade real no dia a dia da advocacia: ao instalar uma");
            Linha(ConsoleColor.White, "  versão nova do PJe-Calc, os cálculos não vão junto, e a única saída era exportar e importar");
            Linha(ConsoleColor.White, "  um por um, manualmente, o que levava horas. O PJe-Calc Backup faz isso de forma automática");
            Linha(ConsoleColor.White, "  e segura, com conferência no final.");
            Console.WriteLine();
            Linha(ConsoleColor.Gray, "  Projeto independente, gratuito e de código aberto, feito no Brasil.");
            Linha(ConsoleColor.DarkGray, "  Não oficial: sem vínculo com nenhum TRT, com o CSJT ou com a equipe do PJe-Calc.");
            Linha(ConsoleColor.DarkGray, "  Licença MIT: pode usar, copiar, modificar e compartilhar, mantendo o aviso de autoria; sem garantia.");
            Linha(ConsoleColor.DarkGray, "  " + Atualizacao.UrlProjeto);
            Console.WriteLine();
            Cor(ConsoleColor.Red, "  ♥ "); Linha(ConsoleColor.White, "Apoie este projeto: se ele economizou o seu tempo, um PIX de qualquer valor ajuda. Obrigado!");
            Cor(ConsoleColor.Gray, "    Chave PIX (aleatória): "); Linha(ConsoleColor.Cyan, Pix.Chave);
            Console.WriteLine();
            DesenharQr(Pix.CopiaECola());
            Console.WriteLine();
            int op = Escolher(null, new[] { "1  Copiar a chave PIX", "2  Copiar o código \"Pix Copia e Cola\"", "0  Voltar" });
            string texto = op == 0 ? Pix.Chave : op == 1 ? Pix.CopiaECola() : null;
            if (texto == null) return;
            try { System.Windows.Forms.Clipboard.SetText(texto); Linha(ConsoleColor.Green, "  √ Copiado. Cole no app do banco."); }
            catch { Aviso("Não consegui copiar. Selecione o texto na tela e copie."); }
            Pausa();
        }

        /// <summary>QR Code no terminal com meios-blocos (▀ ▄ █), preto no branco para os apps de banco lerem.</summary>
        private void DesenharQr(string texto)
        {
            bool[,] m;
            try { m = QrCode.Gerar(texto); } catch { return; }
            int n = m.GetLength(0), q = 2;
            Func<int, int, bool> escuro = (x, y) => x >= 0 && y >= 0 && x < n && y < n && m[y, x];
            var fundo = Console.BackgroundColor; var frente = Console.ForegroundColor;
            for (int y = -q; y < n + q; y += 2)
            {
                Console.Write("    ");
                Console.BackgroundColor = ConsoleColor.White; Console.ForegroundColor = ConsoleColor.Black;
                var sb = new StringBuilder();
                for (int x = -q; x < n + q; x++)
                {
                    bool a = escuro(x, y), b = escuro(x, y + 1);
                    sb.Append(a && b ? '█' : a ? '▀' : b ? '▄' : ' ');
                }
                Console.Write(sb.ToString());
                Console.BackgroundColor = fundo; Console.ForegroundColor = frente;
                Console.WriteLine();
            }
        }

        /// <summary>true = atualizou (o programa deve fechar).</summary>
        private bool FluxoAtualizar()
        {
            Cabecalho("Atualizar o programa");
            Linha(ConsoleColor.DarkGray, "  Verificando no GitHub…");
            Atualizacao.Novidade n;
            try { n = Atualizacao.Verificar(); }
            catch (Exception ex) { Aviso("Não consegui verificar: " + ex.Message); Pausa(); return false; }
            if (n == null) { Linha(ConsoleColor.Green, "  √ Você já tem a versão mais recente (" + Atualizacao.Atual + ")."); Pausa(); return false; }
            Linha(ConsoleColor.Cyan, "  Nova versão: " + n.Versao + " (você tem a " + Atualizacao.Atual + ").  O que mudou: " + n.Url);
            if (!SimNao("Baixar e instalar agora? (o SHA-256 de cada arquivo é conferido)", true)) return false;
            try
            {
                Atualizacao.Instalar(n, t => Linha(ConsoleColor.Gray, "  • " + t));
                Linha(ConsoleColor.Green, "  √ Atualizado para a versão " + n.Versao + ". Abra o programa de novo para usar a versão nova.");
                Pausa();
                return true;
            }
            catch (Exception ex) { Aviso("Não consegui atualizar: " + ex.Message); Pausa(); return false; }
        }

        private void FluxoBuscar()
        {
            Cabecalho("Buscar um cálculo nos backups  ·  " + config.Destino);
            string termo = Perguntar("Nome do reclamante ou reclamado, ou nº do processo", null);
            if (string.IsNullOrWhiteSpace(termo)) return;
            Linha(ConsoleColor.DarkGray, "  Lendo os arquivos .PJC dos backups…");
            var todos = BuscaBackups.Indexar(config.Destino, null, null);
            var achados = BuscaBackups.Filtrar(todos, termo);
            if (achados.Count == 0) { Aviso("Nada encontrado em " + todos.Count + " arquivo(s) .PJC."); Pausa(); return; }
            var lista = achados.Take(60).ToList();
            int s = Escolher(achados.Count + " arquivo(s) encontrado(s), mais recentes primeiro" + (achados.Count > 60 ? " (mostrando 60)" : "") + ". Escolha um:",
                lista.Select(a => (a.Data.ToString("dd/MM/yyyy") + "  " + ("PJe-Calc " + a.Versao).PadRight(16) + ("nº " + a.Info.Numero).PadRight(8) + (a.Info.Reclamante ?? "") +
                    (string.IsNullOrEmpty(a.Info.Reclamado) ? "" : " × " + a.Info.Reclamado) + (string.IsNullOrEmpty(a.Info.Processo) ? "" : "  ·  " + a.Info.Processo))).ToList());
            if (s < 0) return;
            var esc = lista[s];
            Console.WriteLine();
            Linha(ConsoleColor.Gray, "  " + esc.Arquivo);
            int acao = Escolher(null, new[] { "1  Restaurar este cálculo", "2  Mostrar o arquivo na pasta", "0  Voltar" });
            if (acao == 0) FluxoRestaurar(new List<string> { esc.Arquivo });
            else if (acao == 1) { try { Process.Start("explorer.exe", "/select,\"" + esc.Arquivo + "\""); } catch { } }
        }

        private void FluxoPlanilha()
        {
            Cabecalho("Planilha dos cálculos (Excel)");
            CarregarInstalacoes();
            if (instalacoes.Count == 0) { Aviso("Nenhuma instalação do PJe-Calc com cálculos foi encontrada."); Pausa(); return; }
            var marc = Marcar("Quais versões entram na planilha?", instalacoes.Select(Rotulo).ToList(), instalacoes.Select(i => true).ToArray());
            if (marc == null) return;
            var linhas = new List<Planilha.Linha>();
            foreach (var inst in instalacoes.Where((i, k) => marc[k]))
            {
                try { linhas.AddRange(BancoPjeCalc.Listar(inst).Select(c => new Planilha.Linha { Versao = inst.Versao, C = c })); }
                catch (Exception ex) { Aviso("PJe-Calc " + inst.Versao + ": " + ex.Message); }
            }
            string padrao = Path.Combine(Directory.Exists(config.Destino) ? config.Destino : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Cálculos PJe-Calc " + DateTime.Now.ToString("yyyy-MM-dd") + ".xlsx");
            string arq = Perguntar("Salvar em", padrao);
            if (!arq.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) arq += ".xlsx";
            try
            {
                Planilha.Salvar(arq, linhas);
                Linha(ConsoleColor.Green, "  √ " + linhas.Count + " cálculo(s) salvos em " + arq);
                Linha(ConsoleColor.DarkGray, "  A planilha tem nomes das partes: guarde-a em local seguro.");
                if (SimNao("Abrir a planilha?", true)) { try { Process.Start(arq); } catch { } }
            }
            catch (Exception ex) { Aviso("Não consegui salvar: " + ex.Message); }
            Pausa();
        }

        /// <summary>Mostra os backups que a regra manda apagar e move para a Lixeira (perguntar = false: sem confirmação, só no modo agendado).</summary>
        private void FluxoLimpeza(bool perguntar = true)
        {
            if (perguntar) Cabecalho("Limpar backups antigos");
            if (string.IsNullOrEmpty(config.Limpeza)) { Aviso("A limpeza está desligada. Escolha uma regra em Configurações → Limpeza de backups antigos."); if (perguntar) Pausa(); return; }
            var cand = Limpeza.Candidatos(config.Destino, config.Limpeza);
            if (cand.Count == 0) { Linha(ConsoleColor.Green, "  √ Nenhum backup se encaixa na regra \"" + Limpeza.Descrever(config.Limpeza) + "\"."); if (perguntar) Pausa(); return; }
            bool lixeira = Limpeza.TemLixeira(config.Destino);
            long total = cand.Sum(c => Limpeza.Tamanho(c.PastaOperacao));
            Linha(ConsoleColor.White, "  Regra: " + Limpeza.Descrever(config.Limpeza) + ". " + cand.Count + " backup(s) antigo(s), " + Limpeza.TextoTamanho(total) + ":");
            foreach (var c in cand.OrderBy(c => c.Data).Take(15)) Linha(ConsoleColor.Gray, "    • " + c.Data.ToString("dd/MM/yyyy HH:mm") + "  (" + c.Calculos + " cálculo(s))  " + c.PastaOperacao);
            if (cand.Count > 15) Linha(ConsoleColor.Gray, "    • … e mais " + (cand.Count - 15));
            if (!lixeira)
            {
                Aviso("A pasta de backups fica num pendrive, HD externo ou rede, sem Lixeira: os backups seriam apagados DEFINITIVAMENTE.");
                if (!perguntar) { Aviso("Limpeza automática não feita por esse motivo."); return; }
            }
            if (perguntar && !SimNao(lixeira ? "Mover para a Lixeira?" : "Apagar definitivamente?", false)) return;
            int n = Limpeza.MoverParaLixeira(cand, er => Aviso(er));
            Linha(ConsoleColor.Green, "  √ " + n + " backup(s) antigo(s) " + (lixeira ? "movido(s) para a Lixeira" : "apagado(s)") + ".");
            if (perguntar) Pausa();
        }

        /// <summary>Modo agendado: se alguma versão estiver aberta, avisa e espera antes de fechar (Enter = agora, A = adiar 30 min, Esc = pular hoje).</summary>
        private bool EsperarPjeCalcLivre(List<Instalacao> envolvidas)
        {
            for (int adiamentos = 0; ; adiamentos++)
            {
                string ab = TextoAbertas(envolvidas);
                if (ab == null) return true;
                Avisos.NotificarEsperando("Backup automático do PJe-Calc", "O " + ab + " está aberto e será fechado com segurança em 2 minutos. Salve o que estiver editando.", true);
                Console.WriteLine();
                Linha(ConsoleColor.Yellow, "  ┌─ Backup automático ─────────────────────────────────────────");
                Linha(ConsoleColor.Yellow, "  │ O " + ab + " está aberto. Salve o que estiver editando.");
                Linha(ConsoleColor.Yellow, "  │ Ele será fechado com segurança" + (config.ReabrirPjeCalc ? " e reaberto no final." : "."));
                Linha(ConsoleColor.Yellow, "  │ Enter = fazer agora   ·   A = adiar 30 minutos   ·   Esc = pular o backup de hoje");
                Linha(ConsoleColor.Yellow, "  └─────────────────────────────────────────────────────────────");
                var fim = DateTime.Now.AddMinutes(2);
                char r = ' ';
                while (DateTime.Now < fim && r == ' ')
                {
                    try { Console.Write("\r  Fechando em " + (int)(fim - DateTime.Now).TotalSeconds + " s…   "); } catch { }
                    try
                    {
                        if (Console.KeyAvailable)
                        {
                            var k = Console.ReadKey(true);
                            if (k.Key == ConsoleKey.Enter) r = 'E'; else if (k.Key == ConsoleKey.A) r = 'A'; else if (k.Key == ConsoleKey.Escape) r = 'X';
                        }
                    }
                    catch { }
                    Thread.Sleep(250);
                }
                Console.WriteLine();
                if (r == 'X') return false;
                if (r != 'A' || adiamentos >= 3) return true;
                Linha(ConsoleColor.Gray, "  Adiado: tento de novo às " + DateTime.Now.AddMinutes(30).ToString("HH:mm") + ".");
                Thread.Sleep(TimeSpan.FromMinutes(30));
            }
        }

        // ================================================================ modo automático

        private int Automatico(string[] args)
        {
            var a = args.Select(x => x.ToLowerInvariant()).ToList();
            if (a.Count == 1 && a[0] == "--atualizar")
            {
                Console.WriteLine("PJe-Calc Backup " + Atualizacao.Atual + " — verificando atualização no GitHub…");
                try
                {
                    var n = Atualizacao.Verificar();
                    if (n == null) { Console.WriteLine("Já é a versão mais recente."); return 0; }
                    Console.WriteLine("Nova versão: " + n.Versao);
                    Atualizacao.Instalar(n, t => Console.WriteLine("  " + t));
                    Console.WriteLine("Atualizado para a versão " + n.Versao + ".");
                    return 0;
                }
                catch (Exception ex) { Linha(ConsoleColor.Red, "Não consegui atualizar: " + ex.Message); return 1; }
            }
            if (a.Contains("--ajuda") || a.Contains("-h") || a.Contains("/?") || !a.Contains("--backup"))
            {
                Console.WriteLine("PJe-Calc Backup " + Atualizacao.Atual + " — modo console\n");
                Console.WriteLine("Sem parâmetros: abre os menus interativos.\n");
                Console.WriteLine("Backup automático (sem perguntas):");
                Console.WriteLine("  PJeCalcBackup-Console.exe --backup [--destino \"pasta\"] [--versoes 2.16.0,2.14.0] [--pasta \"C:\\pjecalc-...\"]");
                Console.WriteLine("                            [--sem-pjc] [--sem-banco] [--nao-reabrir]\n");
                Console.WriteLine("                            [--agendado] [--limpar] [--rapido | --completo]\n");
                Console.WriteLine("  --pasta     (pode repetir) usa só estas instalações, sem procurar no computador");
                Console.WriteLine("  --agendado  usado pelo backup automático: respeita as versões desmarcadas no programa, avisa e espera");
                Console.WriteLine("              2 minutos se o PJe-Calc estiver aberto, e mostra uma notificação do Windows no final");
                Console.WriteLine("  --limpar    depois do backup, move para a Lixeira os backups antigos (regra das Configurações)");
                Console.WriteLine("  --rapido    exporta só os cálculos novos ou alterados; os outros são copiados do backup anterior");
                Console.WriteLine("  --completo  exporta todos de novo (sem nenhum dos dois, vale a opção das Configurações)\n");
                Console.WriteLine("Atualizar o programa (baixa do GitHub e confere o SHA-256):  PJeCalcBackup-Console.exe --atualizar\n");
                Console.WriteLine("Arrastar arquivos .PJC (ou uma pasta) sobre o programa abre a restauração deles;");
                Console.WriteLine("arrastar um .zip do banco abre a restauração do banco.\n");
                Console.WriteLine("Atenção: fecha com segurança o PJe-Calc que estiver aberto. Código de saída: 0 = ok, 1 = houve falhas.\n");
                Console.WriteLine("Licença MIT. Projeto independente e não oficial. Apoie o projeto: chave PIX " + Pix.Chave);
                return 0;
            }
            bool agendado = a.Contains("--agendado");
            semPerguntas = true;
            if (agendado) { try { Console.Title = "PJe-Calc Backup — backup automático"; } catch { } }
            Func<string, string> valor = nome => { int i = a.IndexOf(nome); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; };
            var op = new OpcoesBackup
            {
                Destino = valor("--destino") ?? config.Destino,
                ExportarPjc = !a.Contains("--sem-pjc"),
                CopiarBanco = !a.Contains("--sem-banco"),
                ReabrirNoFinal = !a.Contains("--nao-reabrir") && config.ReabrirPjeCalc,
                SoAlterados = a.Contains("--rapido") || (!a.Contains("--completo") && config.BackupRapido)
            };
            Directory.CreateDirectory(op.Destino);
            var pastas = new List<string>();
            for (int i = 0; i < a.Count - 1; i++) if (a[i] == "--pasta") pastas.Add(args[i + 1]);
            if (pastas.Count > 0)
            {
                instalacoes = pastas.Where(Instalacao.PareceInstalacao).Select(Instalacao.Carregar).Where(i => File.Exists(i.ArquivoBanco)).ToList();
                if (instalacoes.Count < pastas.Count) Linha(ConsoleColor.Yellow, "Algumas pastas indicadas não são instalações do PJe-Calc com cálculos.");
            }
            else CarregarInstalacoes();
            string vs = valor("--versoes");
            var escolhidas = string.IsNullOrEmpty(vs) || vs.ToLowerInvariant() == "todas" ? instalacoes :
                instalacoes.Where(i => vs.Split(',').Select(x => x.Trim()).Contains(i.Versao)).ToList();
            if (agendado && string.IsNullOrEmpty(vs) && pastas.Count == 0)
                escolhidas = escolhidas.Where(i => !config.Desmarcadas.Contains(i.Pasta, StringComparer.OrdinalIgnoreCase)).ToList();
            if (escolhidas.Count == 0)
            {
                Linha(ConsoleColor.Red, "Nenhuma versão do PJe-Calc encontrada.");
                if (agendado) { Agendador.SalvarResultado(false, "nenhuma versão do PJe-Calc encontrada", null); Avisos.NotificarEsperando("Backup automático do PJe-Calc", "Nenhuma versão do PJe-Calc foi encontrada.", false); }
                return 1;
            }
            Linha(ConsoleColor.White, "Backup de: " + string.Join(", ", escolhidas.Select(i => "PJe-Calc " + i.Versao)) + "  →  " + op.Destino);
            if (agendado && !EsperarPjeCalcLivre(escolhidas))
            {
                Linha(ConsoleColor.Yellow, "Backup de hoje pulado a pedido do usuário.");
                if (agendado) Agendador.SalvarResultado(false, "pulado a pedido do usuário", null);
                return 1;
            }
            if (op.ExportarPjc && Navegador.Localizar() == null) { Linha(ConsoleColor.Yellow, "Sem Edge/Chrome: só o banco de dados será copiado."); op.ExportarPjc = false; }
            emOperacao = true; ResultadoBackup r; try { r = Backup.Executar(escolhidas, op, this); } finally { emOperacao = false; }
            FimProgresso();
            bool ok = r.Falhas == 0;
            string resumo = r.Exportados + " cálculo(s) exportado(s)" + (r.Copiados > 0 ? " + " + r.Copiados + " sem alteração (copiados)" : "") + (r.Esperados > 0 ? " (" + r.Conferidos + " de " + r.Esperados + " conferidos)" : "") + ", " + r.Bancos + " banco(s), " + r.Falhas + " falha(s).";
            Linha(ok ? ConsoleColor.Green : ConsoleColor.Yellow, (ok ? "OK: " : "COM FALHAS: ") + resumo + " Pasta: " + r.Pasta);
            if (ok && a.Contains("--limpar")) { Console.WriteLine(); FluxoLimpeza(false); }
            if (agendado)
            {
                Agendador.SalvarResultado(ok, resumo, r.Pasta);
                if (config.Sons) Avisos.Som(ok);
                if (config.Notificacoes) Avisos.NotificarEsperando(ok ? "Backup automático concluído" : "Backup automático com falhas", resumo, ok);
            }
            return ok ? 0 : 1;
        }

        // ================================================================ IRetornoBackup

        public bool Cancelado { get { return cancelado; } }

        private void LimparStatus()
        {
            try { Console.Write("\r" + new string(' ', Math.Max(1, Console.WindowWidth - 1)) + "\r"); } catch { }
        }

        private void DesenharStatus()
        {
            try
            {
                int largura = Math.Max(40, Console.WindowWidth - 1);
                int barra = 24, cheio = (int)Math.Round(barra * Math.Max(0, Math.Min(1, ultimaFracao)));
                string pct = ((int)(ultimaFracao * 100)).ToString().PadLeft(3) + "% ";
                string et = ultimaEtapa ?? "";
                int resto = largura - barra - pct.Length - 6;
                if (et.Length > resto) et = et.Substring(0, Math.Max(0, resto - 1)) + "…";
                Console.Write("\r  ");
                Cor(ConsoleColor.Cyan, new string('█', cheio)); Cor(ConsoleColor.DarkGray, new string('░', barra - cheio));
                Cor(ConsoleColor.White, " " + pct); Cor(ConsoleColor.Gray, et.PadRight(Math.Max(0, resto)));
            }
            catch { }
        }

        private void FimProgresso() { LimparStatus(); Console.WriteLine(); }

        public void Log(TipoLog tipo, string texto)
        {
            lock (this)
            {
                LimparStatus();
                switch (tipo)
                {
                    case TipoLog.Ok: Linha(ConsoleColor.Green, "  √ " + texto); break;
                    case TipoLog.Erro: Linha(ConsoleColor.Red, "  × " + texto); break;
                    case TipoLog.Aviso: Linha(ConsoleColor.Yellow, "  ! " + texto); break;
                    case TipoLog.Titulo: Console.WriteLine(); Linha(ConsoleColor.Cyan, "  ► " + texto); break;
                    default: Linha(ConsoleColor.Gray, "  • " + texto); break;
                }
                DesenharStatus();
            }
        }

        public void Progresso(double fracao, string etapa)
        {
            lock (this)
            {
                if (fracao >= 0) ultimaFracao = fracao;
                ultimaEtapa = etapa;
                DesenharStatus();
            }
        }

        public bool PedirParaFechar(string descricao)
        {
            lock (this)
            {
                LimparStatus();
                Aviso(descricao);
                if (semPerguntas) { Aviso("Essa versão foi pulada (modo automático)."); return false; }
                Linha(ConsoleColor.Yellow, "  Feche-o pelo ícone na bandeja do Windows (perto do relógio) → Sair.");
                Cor(ConsoleColor.White, "  Pressione Enter depois de fechar, ou P para pular: ");
                while (true)
                {
                    var k = Console.ReadKey(true);
                    if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); Thread.Sleep(2000); return true; }
                    if (k.Key == ConsoleKey.P || k.Key == ConsoleKey.Escape) { Console.WriteLine("P"); return false; }
                }
            }
        }
    }
}
