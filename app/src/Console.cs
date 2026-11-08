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
        private string ultimaEtapa = "";
        private double ultimaFracao;

        // ================================================================ entrada

        [STAThread]
        public static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; Console.InputEncoding = Encoding.UTF8; } catch { }
            try { Console.Title = "PJe-Calc Backup"; } catch { }
            var m = new ModoConsole();
            m.config = Config.Carregar();
            Console.CancelKeyPress += (s, e) =>
            {
                if (!m.emOperacao) return; // no menu, Ctrl+C fecha o programa normalmente
                e.Cancel = true; m.cancelado = true; m.Aviso("Cancelando com segurança… aguarde o item atual terminar.");
            };
            try
            {
                if (args.Length > 0) return m.Automatico(args);
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
                    string t = "   " + (i == sel ? "❯ " : "  ") + opcoes[i];
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
                    string t = i < itens.Count ? "   " + (i == sel ? "❯ " : "  ") + (m[i] ? "[✓] " : "[ ] ") + itens[i] : "";
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
                int op = Escolher("O que você quer fazer?", new[]
                {
                    "1  Fazer backup",
                    "2  Restaurar cálculos (.PJC)",
                    "3  Migrar cálculos entre versões",
                    "4  Restaurar o banco completo (.zip)",
                    "5  Histórico de backups",
                    "6  Configurações",
                    "0  Sair"
                });
                switch (op)
                {
                    case 0: FluxoBackup(); break;
                    case 1: FluxoRestaurar(); break;
                    case 2: FluxoMigrar(); break;
                    case 3: FluxoRestaurarBanco(); break;
                    case 4: FluxoHistorico(); break;
                    case 5: FluxoConfiguracoes(); break;
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
            Linha(ok ? ConsoleColor.Green : ConsoleColor.Yellow, "  " + (ok ? "✓ " : "! ") + texto);
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
            if (!op.ExportarPjc && !op.CopiarBanco) { Aviso("Nada para fazer."); Pausa(); return; }
            if (op.ExportarPjc && Navegador.Localizar() == null) { Aviso("É preciso ter o Microsoft Edge ou o Google Chrome."); Pausa(); return; }
            if (!AvisoInicial(null, escolhidas)) return;

            Cabecalho("Fazer backup  ·  etapa 4 de 4: backup");
            cancelado = false;
            emOperacao = true; ResultadoBackup r; try { r = Backup.Executar(escolhidas, op, this); } finally { emOperacao = false; }
            FimProgresso();
            Resultado(r, "Backup concluído: " + r.Exportados + " cálculo(s) exportado(s)" + (r.Esperados > 0 ? " (" + r.Conferidos + " de " + r.Esperados + " conferidos)" : "") + ", " + r.Bancos + " banco(s), " + r.Falhas + " falha(s).");
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

        private void FluxoRestaurar()
        {
            Cabecalho("Restaurar cálculos (.PJC)  ·  etapa 1 de 3: arquivos");
            var arqs = PedirArquivosPjc();
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

        private void FluxoRestaurarBanco()
        {
            Cabecalho("Restaurar o banco completo");
            string zip = Perguntar("Arquivo .zip do backup do banco (pode arrastar para cá)", null);
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
                else { config.Salvar(); return; }
                config.Salvar();
            }
        }

        // ================================================================ modo automático

        private int Automatico(string[] args)
        {
            var a = args.Select(x => x.ToLowerInvariant()).ToList();
            if (a.Contains("--ajuda") || a.Contains("-h") || a.Contains("/?") || !a.Contains("--backup"))
            {
                Console.WriteLine("PJe-Calc Backup " + Atualizacao.Atual + " — modo console\n");
                Console.WriteLine("Sem parâmetros: abre os menus interativos.\n");
                Console.WriteLine("Backup automático (sem perguntas):");
                Console.WriteLine("  PJeCalcBackup-Console.exe --backup [--destino \"pasta\"] [--versoes 2.16.0,2.14.0] [--pasta \"C:\\pjecalc-...\"]");
                Console.WriteLine("                            [--sem-pjc] [--sem-banco] [--nao-reabrir]\n");
                Console.WriteLine("  --pasta     (pode repetir) usa só estas instalações, sem procurar no computador");
                Console.WriteLine("Atenção: fecha com segurança o PJe-Calc que estiver aberto. Código de saída: 0 = ok, 1 = houve falhas.");
                return a.Contains("--backup") ? 0 : (args.Length == 0 ? 0 : 0);
            }
            Func<string, string> valor = nome => { int i = a.IndexOf(nome); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; };
            var op = new OpcoesBackup
            {
                Destino = valor("--destino") ?? config.Destino,
                ExportarPjc = !a.Contains("--sem-pjc"),
                CopiarBanco = !a.Contains("--sem-banco"),
                ReabrirNoFinal = !a.Contains("--nao-reabrir") && config.ReabrirPjeCalc
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
            if (escolhidas.Count == 0) { Linha(ConsoleColor.Red, "Nenhuma versão do PJe-Calc encontrada."); return 1; }
            Linha(ConsoleColor.White, "Backup de: " + string.Join(", ", escolhidas.Select(i => "PJe-Calc " + i.Versao)) + "  →  " + op.Destino);
            emOperacao = true; ResultadoBackup r; try { r = Backup.Executar(escolhidas, op, this); } finally { emOperacao = false; }
            FimProgresso();
            Linha(r.Falhas == 0 ? ConsoleColor.Green : ConsoleColor.Yellow, (r.Falhas == 0 ? "OK: " : "COM FALHAS: ") + r.Exportados + " exportado(s), " + r.Bancos + " banco(s), " + r.Falhas + " falha(s). Pasta: " + r.Pasta);
            return r.Falhas == 0 ? 0 : 1;
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
                    case TipoLog.Ok: Linha(ConsoleColor.Green, "  ✓ " + texto); break;
                    case TipoLog.Erro: Linha(ConsoleColor.Red, "  ✗ " + texto); break;
                    case TipoLog.Aviso: Linha(ConsoleColor.Yellow, "  ! " + texto); break;
                    case TipoLog.Titulo: Console.WriteLine(); Linha(ConsoleColor.Cyan, "  ▸ " + texto); break;
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
