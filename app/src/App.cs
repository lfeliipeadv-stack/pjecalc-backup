// PJe-Calc Backup - janela principal (WPF).
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Win32;

[assembly: AssemblyTitle("PJe-Calc Backup")]
[assembly: AssemblyProduct("PJe-Calc Backup")]
[assembly: AssemblyDescription("Backup, restauração e migração dos cálculos do PJe-Calc Cidadão (projeto independente, não oficial)")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

namespace PjeCalcBackup
{
    public class Programa : IRetornoBackup
    {
        private Window janela;
        private Config config;
        private JanelaVM vm;
        private volatile bool cancelado, executando;
        private CancellationTokenSource buscaCts;
        private Atualizacao.Novidade novidade;

        private Assistente asBk, asRs, asMg;
        private readonly Dictionary<string, List<CalculoInfo>> cacheCalculos = new Dictionary<string, List<CalculoInfo>>(StringComparer.OrdinalIgnoreCase);
        private string selecaoBkCarregada = "";

        // destino atual das mensagens de progresso
        private ObservableCollection<LinhaLog> logAtual;
        private ScrollViewer rolagemAtual;
        private ProgressBar barraAtual;
        private TextBlock etapaAtual;
        private Assistente assistenteAtual;

        [STAThread]
        public static void Main()
        {
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += (s, e) =>
            {
                Dialogo.Mostrar(null, TipoDialogo.Erro, "Ocorreu um erro inesperado", e.Exception.Message, "Fechar", null);
                e.Handled = true;
            };
            var p = new Programa();
            p.Criar(app);
            app.Run(p.janela);
        }

        private static Stream Recurso(string nome)
        {
            var asm = Assembly.GetExecutingAssembly();
            string n = asm.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith(nome, StringComparison.OrdinalIgnoreCase));
            return n == null ? null : asm.GetManifestResourceStream(n);
        }

        private T A<T>(string nome) where T : class { return janela.FindName(nome) as T; }

        private bool Perguntar(TipoDialogo tipo, string titulo, string msg, string sim, string nao) { return Dialogo.Mostrar(janela, tipo, titulo, msg, sim, nao); }
        private void Avisar(TipoDialogo tipo, string titulo, string msg) { Dialogo.Mostrar(janela, tipo, titulo, msg, "OK", null); }
        private void NaTela(Action a) { janela.Dispatcher.BeginInvoke(a); }

        // ============================================================ criação

        private void Criar(Application app)
        {
            config = Config.Carregar();
            Tema.Aplicar(app.Resources, config.Tema, config.Paleta);
            vm = new JanelaVM(config);

            using (var s = Recurso("JanelaPrincipal.xaml"))
                janela = (Window)XamlReader.Load(s);
            try
            {
                var ico = Recurso("app.ico");
                if (ico != null) janela.Icon = BitmapFrame.Create(ico, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
            catch { }
            janela.DataContext = vm;
            var area = SystemParameters.WorkArea;
            if (janela.Height > area.Height - 20) janela.Height = Math.Max(janela.MinHeight, area.Height - 20);
            if (janela.Width > area.Width - 20) janela.Width = Math.Max(janela.MinWidth, area.Width - 20);
            if (janela.MinHeight > area.Height - 20) janela.MinHeight = Math.Max(480, area.Height - 20);
            janela.SourceInitialized += (s, e) => Tema.BarraDeTitulo(janela, Tema.EscuroAtivo(config.Tema));

            ConfigurarAbas();
            ConfigurarBackup();
            ConfigurarRestaurar();
            ConfigurarMigrar();
            ConfigurarHistorico();
            ConfigurarConfiguracoes();

            A<Button>("lnkGithub").Click += (s, e) => Abrir(Atualizacao.UrlProjeto);
            A<Button>("btnFecharLembrete").Click += (s, e) => A<Border>("avisoLembrete").Visibility = Visibility.Collapsed;
            A<Button>("btnLembreteBackup").Click += (s, e) => { A<Border>("avisoLembrete").Visibility = Visibility.Collapsed; A<RadioButton>("abaBackup").IsChecked = true; };
            A<Button>("btnFecharAtualizacao").Click += (s, e) => A<Border>("avisoAtualizacao").Visibility = Visibility.Collapsed;
            A<Button>("btnBaixarAtualizacao").Click += (s, e) => Abrir(novidade != null ? novidade.Url : Atualizacao.UrlProjeto + "/releases/latest");

            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (config.Tema == "Sistema") NaTela(() => AplicarTema());
            };

            janela.Closing += (s, e) =>
            {
                if (executando)
                {
                    e.Cancel = true;
                    if (Perguntar(TipoDialogo.Pergunta, "Operação em andamento",
                        "Deseja cancelar?\n\nO programa termina o item atual, fecha com segurança o PJe-Calc que abriu em segundo plano e depois pode ser fechado.",
                        "Cancelar a operação", "Continuar"))
                        PedirCancelamento();
                    return;
                }
                SalvarConfig();
            };

            janela.Loaded += (s, e) =>
            {
                ProcurarInstalacoes();
                VerificarLembrete();
                VerificarAtualizacao(false);
            };
        }

        private static void Abrir(string alvo)
        {
            try { Process.Start(alvo); } catch { }
        }

        private static void AbrirPasta(string pasta)
        {
            try { Directory.CreateDirectory(pasta); Process.Start("explorer.exe", "\"" + pasta + "\""); } catch { }
        }

        private void SalvarConfig()
        {
            config.Desmarcadas = vm.Instalacoes.Where(i => !i.Selecionada).Select(i => i.Pasta).ToList();
            config.Salvar();
        }

        private void AplicarTema()
        {
            Tema.Aplicar(Application.Current.Resources, config.Tema, config.Paleta);
            foreach (var i in vm.Instalacoes) i.AtualizarCores();
            var h = vm.Historico.ToList(); vm.Historico.Clear(); foreach (var r in h) vm.Historico.Add(r);
            foreach (var log in new[] { vm.LogBackup, vm.LogRestaurar, vm.LogMigrar })
            {
                var linhas = log.ToList(); log.Clear();
                foreach (var l in linhas) { l.Cor = Tema.Recurso(l.Chave ?? "TextoSuave"); log.Add(l); }
            }
        }

        // ============================================================ abas

        private void ConfigurarAbas()
        {
            var abas = new[] { "abaBackup", "abaRestaurar", "abaMigrar", "abaHistorico", "abaConfig" };
            var paineis = new[] { "painelBackup", "painelRestaurar", "painelMigrar", "painelHistorico", "painelConfig" };
            for (int i = 0; i < abas.Length; i++)
            {
                int k = i;
                A<RadioButton>(abas[i]).Checked += (s, e) =>
                {
                    for (int j = 0; j < paineis.Length; j++)
                        A<FrameworkElement>(paineis[j]).Visibility = j == k ? Visibility.Visible : Visibility.Collapsed;
                    if (abas[k] == "abaHistorico") CarregarHistorico();
                };
            }
        }

        private void TravarAbas(bool travar, string excetoPainel)
        {
            foreach (var a in new[] { "abaBackup", "abaRestaurar", "abaMigrar", "abaHistorico", "abaConfig" })
            {
                var r = A<RadioButton>(a);
                r.IsEnabled = !travar || r.IsChecked == true;
            }
        }

        // ============================================================ busca de instalações

        private void ProcurarInstalacoes()
        {
            if (executando) return;
            if (buscaCts != null) buscaCts.Cancel();
            var cts = new CancellationTokenSource();
            buscaCts = cts;
            vm.Instalacoes.Clear();
            vm.InstalacoesComBanco.Clear();
            cacheCalculos.Clear(); selecaoBkCarregada = "";
            var txt = A<TextBlock>("txtProcurando");
            txt.Visibility = Visibility.Visible;
            txt.Text = "Procurando instalações do PJe-Calc no computador…";
            A<Button>("btnProcurar").IsEnabled = false;
            var extras = config.PastasExtras.ToList();
            var desmarcadas = new HashSet<string>(config.Desmarcadas, StringComparer.OrdinalIgnoreCase);

            Task.Factory.StartNew(() =>
            {
                var pastas = new List<string>();
                foreach (var x in extras) if (Instalacao.PareceInstalacao(x)) pastas.Add(x);
                foreach (var p in Instalacao.Procurar(null, () => cts.IsCancellationRequested))
                    if (!pastas.Any(a => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(p).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                        pastas.Add(p);

                var itens = new List<InstalacaoVM>();
                foreach (var p in pastas)
                {
                    try
                    {
                        var vmI = new InstalacaoVM(Instalacao.Carregar(p));
                        vmI.Estado = vmI.Inst.Estado();
                        vmI.Selecionada = vmI.Selecionavel && !desmarcadas.Contains(vmI.Pasta);
                        vmI.TextoCalculos = vmI.Selecionavel ? "contando…" : "";
                        itens.Add(vmI);
                    }
                    catch { }
                }
                itens = itens.OrderByDescending(i => VersaoOrdenavel(i.Inst.Versao)).ToList();
                if (cts.IsCancellationRequested) return;

                janela.Dispatcher.Invoke(new Action(() =>
                {
                    foreach (var i in itens) vm.Instalacoes.Add(i);
                    foreach (var i in itens.Where(x => x.Selecionavel)) vm.InstalacoesComBanco.Add(i);
                    if (vm.InstalacoesComBanco.Count > 0)
                    {
                        A<ComboBox>("cmbDestinoRs").SelectedIndex = 0;
                        A<ComboBox>("cmbDestinoMg").SelectedIndex = 0;
                        A<ComboBox>("cmbOrigemMg").SelectedIndex = vm.InstalacoesComBanco.Count - 1;
                    }
                    txt.Visibility = itens.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    txt.Text = "Nenhuma instalação do PJe-Calc encontrada. Use \"Adicionar pasta…\" para indicar a pasta da versão.";
                    A<Button>("btnProcurar").IsEnabled = true;
                }));

                foreach (var i in itens.Where(x => x.Selecionavel))
                {
                    if (cts.IsCancellationRequested) return;
                    List<CalculoInfo> l = null;
                    try { l = BancoPjeCalc.Listar(i.Inst); } catch { }
                    var alvo = i;
                    janela.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (l != null) cacheCalculos[alvo.Pasta] = l;
                        alvo.TextoCalculos = l == null ? "" : (l.Count == 1 ? "1 cálculo" : l.Count + " cálculos");
                    }));
                }
            }, TaskCreationOptions.LongRunning);
        }

        private static string VersaoOrdenavel(string v)
        {
            return string.Join(".", (v ?? "").Split('.').Select(p => p.PadLeft(5, '0')));
        }

        private void AtualizarEstados()
        {
            cacheCalculos.Clear(); selecaoBkCarregada = "";
            var lista = vm.Instalacoes.ToList();
            Task.Factory.StartNew(() =>
            {
                foreach (var i in lista)
                {
                    EstadoInstalacao e;
                    try { e = i.Inst.Estado(); } catch { continue; }
                    List<CalculoInfo> l = null;
                    if (e != EstadoInstalacao.SemBanco) { try { l = BancoPjeCalc.Listar(i.Inst); } catch { } }
                    var alvo = i;
                    NaTela(() =>
                    {
                        alvo.Estado = e;
                        if (l != null) cacheCalculos[alvo.Pasta] = l;
                        alvo.TextoCalculos = l == null ? "" : (l.Count == 1 ? "1 cálculo" : l.Count + " cálculos");
                    });
                }
            });
        }

        /// <summary>Cálculos de uma instalação (lidos do banco, sem abrir o PJe-Calc). Usa cache.</summary>
        private List<CalculoInfo> CalculosDe(Instalacao inst)
        {
            List<CalculoInfo> l;
            lock (cacheCalculos) { if (cacheCalculos.TryGetValue(inst.Pasta, out l)) return l; }
            l = BancoPjeCalc.Listar(inst);
            lock (cacheCalculos) { cacheCalculos[inst.Pasta] = l; }
            return l;
        }

        private void AdicionarPasta()
        {
            string p = SeletorPasta.Escolher(janela, "Escolha a pasta da versão do PJe-Calc (onde fica o iniciarPjeCalc.bat)", null);
            if (p == null) return;
            var achadas = Instalacao.PareceInstalacao(p) ? new List<string> { p } : Instalacao.Procurar(new[] { p }, null)
                .Where(a => a.StartsWith(p, StringComparison.OrdinalIgnoreCase)).ToList();
            if (achadas.Count == 0)
            {
                Avisar(TipoDialogo.Info, "PJe-Calc não encontrado nessa pasta", "Escolha a pasta que contém o arquivo iniciarPjeCalc.bat e as pastas bin e tomcat.");
                return;
            }
            foreach (var a in achadas)
                if (!config.PastasExtras.Contains(a, StringComparer.OrdinalIgnoreCase)) config.PastasExtras.Add(a);
            config.Salvar();
            ProcurarInstalacoes();
        }

        /// <summary>Texto sobre versões abertas agora (verificado na hora), para os avisos iniciais.</summary>
        private string TextoVersoesAbertas(IEnumerable<Instalacao> envolvidas)
        {
            var abertas = new List<string>();
            foreach (int porta in envolvidas.Select(i => i.PortaHttp).Distinct())
            {
                string dono = Instalacao.InstalacaoNaPorta(porta);
                if (dono == null) continue;
                var vmI = vm.Instalacoes.FirstOrDefault(i => string.Equals(i.Pasta, dono, StringComparison.OrdinalIgnoreCase));
                string nome = vmI != null ? vmI.Titulo : "PJe-Calc (" + dono + ")";
                if (!abertas.Contains(nome)) abertas.Add(nome);
            }
            if (abertas.Count == 0) return "";
            return "\n\nO " + string.Join(" e o ", abertas) + (abertas.Count == 1 ? " está aberto e será fechado" : " estão abertos e serão fechados") +
                   " automaticamente, com segurança (como o \"Sair\" do ícone na bandeja)." + (config.ReabrirPjeCalc ? " No final, o programa o reabre." : "");
        }

        private bool ConfirmarInicio(string titulo, string detalhe, string botao, IEnumerable<Instalacao> envolvidas)
        {
            return Perguntar(TipoDialogo.Aviso, titulo,
                "Salve e feche os cálculos que estiver editando no PJe-Calc, para não perder nenhuma alteração." + TextoVersoesAbertas(envolvidas) +
                (string.IsNullOrEmpty(detalhe) ? "" : "\n\n" + detalhe) + "\n\nDurante a operação, não abra o PJe-Calc.",
                botao, "Cancelar");
        }

        private bool DestinoValido()
        {
            if (string.IsNullOrWhiteSpace(config.Destino)) { Avisar(TipoDialogo.Info, "Escolha a pasta de backups", "Informe onde salvar os backups."); return false; }
            try { Directory.CreateDirectory(config.Destino); return true; }
            catch (Exception ex) { Avisar(TipoDialogo.Aviso, "Não consegui usar a pasta de backups", ex.Message); return false; }
        }

        private bool NavegadorOk()
        {
            if (Navegador.Localizar() != null) return true;
            Avisar(TipoDialogo.Aviso, "Navegador não encontrado", "É preciso ter o Microsoft Edge ou o Google Chrome instalado.");
            return false;
        }

        /// <summary>Lista com filtro de texto (por nome, número, processo).</summary>
        private void LigarFiltro(string caixa, ObservableCollection<CalcItemVM> lista)
        {
            var tb = A<TextBox>(caixa);
            var v = CollectionViewSource.GetDefaultView(lista);
            v.Filter = o => ((CalcItemVM)o).Corresponde(tb.Text);
            tb.TextChanged += (s, e) => v.Refresh();
        }

        private static void Marcar(IEnumerable<CalcItemVM> lista, bool valor)
        {
            foreach (var g in lista.Where(x => x.EhGrupo)) g.Selecionado = valor;
            foreach (var i in lista.Where(x => !x.EhGrupo && x.Grupo == null)) i.Selecionado = valor;
        }

        private static CalcItemVM ItemDe(CalculoInfo c, string pasta, CalcItemVM grupo, string dup)
        {
            return new CalcItemVM
            {
                Titulo = "Nº " + c.Numero + "  —  " + (string.IsNullOrEmpty(c.Reclamante) ? "(sem reclamante)" : c.Reclamante),
                Detalhe = c.Resumo,
                Numero = c.Numero, PastaInstalacao = pasta, Info = c, Grupo = grupo, Arquivo = c.Arquivo,
                TextoDuplicado = dup
            };
        }

        // ============================================================ BACKUP

        private void ConfigurarBackup()
        {
            var bk = new FrameworkElement[] { A<Border>("bk1"), A<Border>("bk2"), A<Border>("bk3"), A<Border>("bk4") };
            asBk = new Assistente(A<StackPanel>("passosBackup"), new[] { "Versões", "Cálculos", "Destino", "Backup" }, i => bk[i], bk,
                A<Button>("bkVoltar"), A<Button>("bkAvancar"), A<Button>("bkExtra"), "Concluir ✓", "Novo backup");
            LigarFiltro("txtFiltroBk", vm.CalculosBackup);
            A<Button>("btnBkTodos").Click += (s, e) => Marcar(vm.CalculosBackup, true);
            A<Button>("btnBkNenhum").Click += (s, e) => Marcar(vm.CalculosBackup, false);
            A<Button>("btnProcurar").Click += (s, e) => ProcurarInstalacoes();
            A<Button>("btnAdicionar").Click += (s, e) => AdicionarPasta();
            A<Button>("btnEscolher").Click += (s, e) => { var p = SeletorPasta.Escolher(janela, "Escolha onde salvar os backups", vm.Destino); if (p != null) vm.Destino = p; };
            A<Button>("btnAbrirPasta").Click += (s, e) => AbrirPasta(vm.Destino);

            asBk.AoAvancar = i =>
            {
                if (i == 0)
                {
                    if (!vm.Instalacoes.Any(x => x.Selecionada && x.Selecionavel)) { Avisar(TipoDialogo.Info, "Nenhuma versão marcada", "Marque pelo menos uma versão do PJe-Calc."); return false; }
                }
                if (i == 1 && vm.CalculosBackup.Count > 0 && !vm.CalculosBackup.Any(x => !x.EhGrupo && x.Selecionado) && vm.ExportarPjc)
                {
                    if (!Perguntar(TipoDialogo.Pergunta, "Nenhum cálculo marcado", "Nenhum cálculo será exportado em .PJC. Continuar só com a cópia do banco de dados?", "Continuar", "Voltar")) return false;
                }
                if (i == 2)
                {
                    if (!vm.ExportarPjc && !vm.CopiarBanco) { Avisar(TipoDialogo.Info, "Nada para fazer", "Escolha exportar os cálculos, copiar o banco, ou os dois."); return false; }
                    if (!DestinoValido()) return false;
                    if (vm.ExportarPjc && !NavegadorOk()) return false;
                    return ConfirmarInicio("Antes de começar", null, "Já salvei, fazer backup", vm.Instalacoes.Where(x => x.Selecionada && x.Selecionavel).Select(x => x.Inst));
                }
                return true;
            };
            asBk.AoEntrar = i =>
            {
                if (i == 1) CarregarCalculosBackup();
                if (i == 2)
                {
                    int n = vm.CalculosBackup.Count(x => !x.EhGrupo && x.Selecionado);
                    int v = vm.Instalacoes.Count(x => x.Selecionada && x.Selecionavel);
                    A<TextBlock>("txtResumoBk").Text = "Resumo: " + v + " versão(ões) do PJe-Calc, " + n + " cálculo(s) marcados para exportar em .PJC.";
                }
            };
            asBk.Executar = IniciarBackup;
            asBk.Cancelar = PedirCancelamento;
            asBk.AoAbrirPasta = () => { if (ultimaPasta != null) AbrirPasta(ultimaPasta); };
            asBk.Ir(0);
        }

        private string ultimaPasta;

        private void CarregarCalculosBackup()
        {
            var sel = vm.Instalacoes.Where(x => x.Selecionada && x.Selecionavel).ToList();
            string chave = string.Join("|", sel.Select(x => x.Pasta));
            if (chave == selecaoBkCarregada && vm.CalculosBackup.Count > 0) return;
            selecaoBkCarregada = chave;
            vm.CalculosBackup.Clear();
            var st = A<TextBlock>("txtStatusBk");
            st.Text = "Lendo os cálculos do banco de dados…";
            Task.Factory.StartNew(() =>
            {
                var res = new List<KeyValuePair<InstalacaoVM, List<CalculoInfo>>>();
                var erros = new List<string>();
                foreach (var i in sel)
                {
                    try { res.Add(new KeyValuePair<InstalacaoVM, List<CalculoInfo>>(i, CalculosDe(i.Inst))); }
                    catch (Exception ex) { erros.Add(i.Titulo + ": " + ex.Message); }
                }
                NaTela(() =>
                {
                    int total = 0;
                    foreach (var kv in res)
                    {
                        var g = new CalcItemVM { EhGrupo = true, Titulo = kv.Key.Titulo + "  —  " + kv.Value.Count + " cálculo(s)", PastaInstalacao = kv.Key.Pasta };
                        vm.CalculosBackup.Add(g);
                        foreach (var c in kv.Value)
                        {
                            var it = ItemDe(c, kv.Key.Pasta, g, null);
                            g.Filhos.Add(it); vm.CalculosBackup.Add(it); total++;
                        }
                    }
                    st.Text = total + " cálculo(s) em " + res.Count + " versão(ões)." + (erros.Count > 0 ? "  Não consegui ler: " + string.Join("; ", erros) : "");
                });
            });
        }

        private void IniciarBackup()
        {
            var selecionadas = vm.Instalacoes.Where(i => i.Selecionada && i.Selecionavel).Select(i => i.Inst).ToList();
            var op = new OpcoesBackup { Destino = config.Destino, ExportarPjc = config.ExportarPjc, CopiarBanco = config.CopiarBanco, ReabrirNoFinal = config.ReabrirPjeCalc };
            foreach (var g in vm.CalculosBackup.Where(x => x.EhGrupo))
                if (!g.Filhos.All(f => f.Selecionado))
                    op.Selecao[g.PastaInstalacao] = new HashSet<string>(g.Filhos.Where(f => f.Selecionado).Select(f => f.Numero));
            SalvarConfig();

            PrepararExecucao(asBk, vm.LogBackup, "rolagemBk", "barraBk", "txtEtapaBk", "txtTituloBk", "Fazendo backup…");
            Rodar(() => Backup.Executar(selecionadas, op, this), res =>
            {
                bool ok = res.Falhas == 0;
                A<TextBlock>("txtTituloBk").Text = ok ? "Backup concluído" : "Backup concluído com falhas";
                string conf = res.Esperados > 0 ? res.Conferidos + " de " + res.Esperados + " conferidos" : "";
                Finalizar(ok, ok ? "Backup concluído" : "Backup concluído com falhas",
                    res.Exportados + " cálculo(s) exportado(s)" + (conf != "" ? " (" + conf + ")" : "") + ", " + res.Bancos + " banco(s) copiado(s)" +
                    (res.Falhas > 0 ? ", " + res.Falhas + " falha(s)" : "") + ".", res);
            });
        }

        // ============================================================ RESTAURAR

        private string zipEscolhido;
        private readonly Dictionary<string, CalculoInfo> cachePjc = new Dictionary<string, CalculoInfo>(StringComparer.OrdinalIgnoreCase);

        private bool RestaurarBancoEscolhido { get { return A<RadioButton>("rsTipoBanco").IsChecked == true; } }

        private void ConfigurarRestaurar()
        {
            var todos = new FrameworkElement[] { A<Border>("rs1"), A<Border>("rs2pjc"), A<Border>("rs2banco"), A<Border>("rs3"), A<Border>("rs4") };
            asRs = new Assistente(A<StackPanel>("passosRestaurar"), new[] { "O que restaurar", "Arquivos", "Destino", "Restauração" },
                i => i == 0 ? todos[0] : i == 1 ? (RestaurarBancoEscolhido ? todos[2] : todos[1]) : i == 2 ? todos[3] : todos[4], todos,
                A<Button>("rsVoltar"), A<Button>("rsAvancar"), A<Button>("rsExtra"), "Concluir ✓", "Nova restauração");

            A<Button>("btnArquivosPjc").Click += (s, e) => EscolherArquivosPjc();
            A<Button>("btnPastaPjc").Click += (s, e) => EscolherPastaPjc();
            A<Button>("btnLimparPjc").Click += (s, e) => { vm.ArquivosPjc.Clear(); AtualizarResumoPjc(); };
            A<Button>("btnZipBanco").Click += (s, e) => EscolherZip();
            A<ComboBox>("cmbDestinoRs").SelectionChanged += (s, e) => { if (asRs.Atual == 2) PrepararRevisaoRestauracao(); };

            asRs.AoAvancar = i =>
            {
                if (i == 1)
                {
                    if (RestaurarBancoEscolhido && string.IsNullOrEmpty(zipEscolhido)) { Avisar(TipoDialogo.Info, "Escolha o arquivo", "Escolha o arquivo .zip do backup do banco."); return false; }
                    if (!RestaurarBancoEscolhido && !vm.ArquivosPjc.Any(a => a.Selecionado)) { Avisar(TipoDialogo.Info, "Nenhum arquivo", "Escolha arquivos .PJC (ou uma pasta de backup) e deixe marcados os que quer restaurar."); return false; }
                }
                if (i == 2)
                {
                    var dest = A<ComboBox>("cmbDestinoRs").SelectedItem as InstalacaoVM;
                    if (dest == null) { Avisar(TipoDialogo.Info, "Escolha a versão", "Escolha em qual versão do PJe-Calc restaurar."); return false; }
                    if (!DestinoValido()) return false;
                    if (RestaurarBancoEscolhido)
                    {
                        string v = BancoPjeCalc.VersaoDoZip(zipEscolhido);
                        if (v != null && v != dest.Inst.Versao)
                        {
                            Avisar(TipoDialogo.Aviso, "Versões diferentes",
                                "Este backup é do PJe-Calc " + v + " e você escolheu restaurar no " + dest.Titulo + ".\n\nO banco de uma versão pode não funcionar em outra. " +
                                "Para levar cálculos entre versões, use a aba \"Migrar entre versões\" ou restaure os arquivos .PJC.");
                            return false;
                        }
                        return ConfirmarInicio("Antes de restaurar o banco",
                            "Todos os cálculos atuais do " + dest.Titulo + " serão substituídos pelos do backup. Antes, uma cópia do banco atual será guardada em:\n" + config.Destino,
                            "Já salvei, restaurar", new[] { dest.Inst });
                    }
                    if (!NavegadorOk()) return false;
                    int n = vm.CalculosRestaurar.Count(x => x.Selecionado);
                    if (n == 0) { Avisar(TipoDialogo.Info, "Nenhum cálculo marcado", "Marque pelo menos um arquivo para restaurar."); return false; }
                    return ConfirmarInicio("Antes de restaurar",
                        n + " arquivo(s) serão importados no " + dest.Titulo + ". Cada um vira um cálculo NOVO. Antes, uma cópia do banco atual será guardada em:\n" + config.Destino,
                        "Já salvei, restaurar", new[] { dest.Inst });
                }
                return true;
            };
            asRs.AoEntrar = i => { if (i == 2) PrepararRevisaoRestauracao(); };
            asRs.Executar = IniciarRestauracao;
            asRs.Cancelar = PedirCancelamento;
            asRs.AoAbrirPasta = () => { if (ultimaPasta != null) AbrirPasta(ultimaPasta); };
            asRs.Ir(0);
            AtualizarResumoPjc();
        }

        private void AdicionarArquivosPjc(IEnumerable<string> arquivos)
        {
            var existentes = new HashSet<string>(vm.ArquivosPjc.Select(a => a.Caminho), StringComparer.OrdinalIgnoreCase);
            foreach (var a in arquivos.OrderBy(x => x))
            {
                if (existentes.Contains(a)) continue;
                long t = 0; try { t = new FileInfo(a).Length; } catch { }
                vm.ArquivosPjc.Add(new ArquivoPjcVM { Caminho = a, Tamanho = Math.Max(1, t / 1024) + " KB" });
            }
            AtualizarResumoPjc();
        }

        private void AtualizarResumoPjc()
        {
            int n = vm.ArquivosPjc.Count;
            A<TextBlock>("txtResumoPjc").Text = n == 0 ? "Escolha arquivos .PJC ou uma pasta de backup (as subpastas entram também)." : n + " arquivo(s). Desmarque os que não quiser restaurar.";
        }

        private void EscolherArquivosPjc()
        {
            var d = new Microsoft.Win32.OpenFileDialog { Title = "Escolha os arquivos .PJC para restaurar", Filter = "Cálculos do PJe-Calc (*.PJC)|*.PJC;*.pjc", Multiselect = true };
            if (!string.IsNullOrEmpty(config.UltimaPastaPjc) && Directory.Exists(config.UltimaPastaPjc)) d.InitialDirectory = config.UltimaPastaPjc;
            else if (Directory.Exists(config.Destino)) d.InitialDirectory = config.Destino;
            if (d.ShowDialog(janela) == true) { config.UltimaPastaPjc = Path.GetDirectoryName(d.FileNames[0]); AdicionarArquivosPjc(d.FileNames); }
        }

        private void EscolherPastaPjc()
        {
            string p = SeletorPasta.Escolher(janela, "Escolha a pasta com os arquivos .PJC (inclui subpastas)",
                Directory.Exists(config.UltimaPastaPjc ?? "") ? config.UltimaPastaPjc : config.Destino);
            if (p == null) return;
            config.UltimaPastaPjc = p;
            CarregarPastaPjc(p);
        }

        private void CarregarPastaPjc(string p)
        {
            string[] arqs;
            try { arqs = Directory.GetFiles(p, "*.PJC", SearchOption.AllDirectories); }
            catch (Exception ex) { Avisar(TipoDialogo.Erro, "Não consegui ler a pasta", ex.Message); return; }
            if (arqs.Length == 0) { Avisar(TipoDialogo.Info, "Nenhum arquivo .PJC", "Não encontrei arquivos .PJC nessa pasta nem nas subpastas."); return; }
            AdicionarArquivosPjc(arqs);
        }

        private void EscolherZip()
        {
            var d = new Microsoft.Win32.OpenFileDialog { Title = "Escolha o backup do banco (.zip)", Filter = "Backup do banco do PJe-Calc (*.zip)|*.zip" };
            if (Directory.Exists(config.Destino)) d.InitialDirectory = config.Destino;
            if (d.ShowDialog(janela) == true) DefinirZip(d.FileName);
        }

        private void DefinirZip(string zip)
        {
            zipEscolhido = zip;
            A<TextBox>("txtZipBanco").Text = zip;
            var info = A<TextBlock>("txtInfoZip");
            info.Text = "Conferindo o arquivo…";
            var java = vm.InstalacoesComBanco.Select(x => x.Inst).FirstOrDefault();
            Task.Factory.StartNew(() =>
            {
                string t;
                try
                {
                    string v = BancoPjeCalc.VersaoDoZip(zip);
                    int n = java != null ? BancoPjeCalc.ListarDoZip(java, zip).Count : -1;
                    t = "Backup do PJe-Calc " + (v ?? "?") + (n >= 0 ? " com " + n + " cálculo(s)" : "") + ", de " + File.GetLastWriteTime(zip).ToString("dd/MM/yyyy HH:mm") + ".";
                }
                catch (Exception ex) { t = "Não consegui ler este arquivo: " + ex.Message; }
                NaTela(() => info.Text = t);
            });
        }

        private void PrepararRevisaoRestauracao()
        {
            var dest = A<ComboBox>("cmbDestinoRs").SelectedItem as InstalacaoVM;
            var aviso = A<Border>("rsAvisoBanco");
            var st = A<TextBlock>("txtStatusRs");
            vm.CalculosRestaurar.Clear();
            if (RestaurarBancoEscolhido)
            {
                A<ScrollViewer>("rsListaRevisao").Visibility = Visibility.Collapsed;
                aviso.Visibility = Visibility.Visible;
                st.Text = "";
                string v = BancoPjeCalc.VersaoDoZip(zipEscolhido);
                A<TextBlock>("txtAvisoBanco").Text = dest == null ? "" :
                    "O banco do " + dest.Titulo + " será substituído pelo do backup (" + Path.GetFileName(zipEscolhido) + ")." +
                    (v != null && v != dest.Inst.Versao ? "\n\nATENÇÃO: o backup é do PJe-Calc " + v + "; escolha a mesma versão." : "") +
                    "\n\nOs cálculos atuais dessa versão serão trocados pelos do backup. O banco atual é guardado antes, para você poder desfazer.";
                return;
            }
            A<ScrollViewer>("rsListaRevisao").Visibility = Visibility.Visible;
            aviso.Visibility = Visibility.Collapsed;
            if (dest == null) return;
            st.Text = "Lendo os arquivos e comparando com os cálculos do " + dest.Titulo + "…";
            var arquivos = vm.ArquivosPjc.Where(a => a.Selecionado).Select(a => a.Caminho).ToList();
            Task.Factory.StartNew(() =>
            {
                var infos = new List<CalculoInfo>();
                var ruins = new List<string>();
                foreach (var a in arquivos)
                {
                    CalculoInfo ci;
                    lock (cachePjc) { cachePjc.TryGetValue(a, out ci); }
                    if (ci == null)
                    {
                        try { ci = BancoPjeCalc.LerPjc(a); lock (cachePjc) { cachePjc[a] = ci; } }
                        catch { ruins.Add(Path.GetFileName(a)); continue; }
                    }
                    infos.Add(ci);
                }
                Dictionary<string, string> dup = null;
                try { dup = BancoPjeCalc.MapaDuplicados(CalculosDe(dest.Inst)); } catch { }
                NaTela(() =>
                {
                    int ndup = 0;
                    foreach (var ci in infos)
                    {
                        string existe = null;
                        if (dup != null && dup.TryGetValue(ci.Chave, out existe)) ndup++;
                        var it = ItemDe(ci, null, null, existe != null ? "Parece já existir (nº " + existe + ")" : null);
                        it.Detalhe = (it.Detalhe.Length > 0 ? it.Detalhe + "   ·   " : "") + Path.GetFileName(ci.Arquivo);
                        it.Selecionado = existe == null;
                        vm.CalculosRestaurar.Add(it);
                    }
                    st.Text = infos.Count + " arquivo(s)" + (ndup > 0 ? " · " + ndup + " parece(m) já existir no " + dest.Titulo + " e vieram desmarcados" : " · nenhum parece já existir no destino") +
                              (ruins.Count > 0 ? " · ilegíveis: " + string.Join(", ", ruins) : "") + (dup == null ? " · não consegui ler o banco de destino para comparar" : "");
                });
            });
        }

        private void IniciarRestauracao()
        {
            var dest = (A<ComboBox>("cmbDestinoRs").SelectedItem as InstalacaoVM).Inst;
            SalvarConfig();
            PrepararExecucao(asRs, vm.LogRestaurar, "rolagemRs", "barraRs", "txtEtapaRs", "txtTituloRs", "Restaurando…");
            if (RestaurarBancoEscolhido)
            {
                string zip = zipEscolhido;
                Rodar(() => Backup.RestaurarBanco(dest, zip, config.Destino, config.ReabrirPjeCalc, this), res =>
                {
                    bool ok = res.Falhas == 0;
                    A<TextBlock>("txtTituloRs").Text = ok ? "Banco restaurado" : "Restauração do banco com falhas";
                    Finalizar(ok, ok ? "Banco restaurado" : "Restauração com falhas",
                        "PJe-Calc " + dest.Versao + ": " + res.Importados + " cálculo(s) no banco restaurado.", res);
                });
                return;
            }
            var arquivos = vm.CalculosRestaurar.Where(x => x.Selecionado).Select(x => x.Arquivo).ToList();
            Rodar(() => Backup.Restaurar(dest, arquivos, config.Destino, config.ReabrirPjeCalc, this), res =>
            {
                bool ok = res.Falhas == 0 && res.Importados > 0;
                A<TextBlock>("txtTituloRs").Text = ok ? "Restauração concluída" : "Restauração com falhas";
                Finalizar(ok, ok ? "Restauração concluída" : "Restauração com falhas",
                    res.Importados + " cálculo(s) importado(s) no PJe-Calc " + dest.Versao + (res.Falhas > 0 ? ", " + res.Falhas + " falha(s)" : "") + ".", res);
            });
        }

        // ============================================================ MIGRAR

        private void ConfigurarMigrar()
        {
            var mg = new FrameworkElement[] { A<Border>("mg1"), A<Border>("mg2"), A<Border>("mg3") };
            asMg = new Assistente(A<StackPanel>("passosMigrar"), new[] { "Versões", "Cálculos", "Migração" }, i => mg[i], mg,
                A<Button>("mgVoltar"), A<Button>("mgAvancar"), A<Button>("mgExtra"), "Concluir ✓", "Nova migração");
            LigarFiltro("txtFiltroMg", vm.CalculosMigrar);
            A<Button>("btnMgTodos").Click += (s, e) => Marcar(vm.CalculosMigrar, true);
            A<Button>("btnMgNenhum").Click += (s, e) => Marcar(vm.CalculosMigrar, false);
            SelectionChangedEventHandler info = (s, e) =>
            {
                var o = A<ComboBox>("cmbOrigemMg").SelectedItem as InstalacaoVM;
                var d = A<ComboBox>("cmbDestinoMg").SelectedItem as InstalacaoVM;
                A<TextBlock>("txtInfoMg").Text = o != null && d != null && o == d ? "Escolha versões diferentes." :
                    o != null && d != null ? "Os cálculos escolhidos do " + o.Titulo + " serão levados para o " + d.Titulo + "." : "";
            };
            A<ComboBox>("cmbOrigemMg").SelectionChanged += info;
            A<ComboBox>("cmbDestinoMg").SelectionChanged += info;

            asMg.AoAvancar = i =>
            {
                var o = A<ComboBox>("cmbOrigemMg").SelectedItem as InstalacaoVM;
                var d = A<ComboBox>("cmbDestinoMg").SelectedItem as InstalacaoVM;
                if (i == 0)
                {
                    if (o == null || d == null) { Avisar(TipoDialogo.Info, "Escolha as versões", "Escolha a versão de origem e a de destino."); return false; }
                    if (o == d) { Avisar(TipoDialogo.Info, "Versões iguais", "A origem e o destino precisam ser versões diferentes."); return false; }
                }
                if (i == 1)
                {
                    int n = vm.CalculosMigrar.Count(x => !x.EhGrupo && x.Selecionado);
                    if (n == 0) { Avisar(TipoDialogo.Info, "Nenhum cálculo marcado", "Marque pelo menos um cálculo para levar."); return false; }
                    if (!DestinoValido() || !NavegadorOk()) return false;
                    return ConfirmarInicio("Antes de migrar",
                        n + " cálculo(s) serão exportados do " + o.Titulo + " e importados no " + d.Titulo + " (cada um vira um cálculo novo lá). " +
                        "Antes de importar, uma cópia do banco do " + d.Titulo + " será guardada em:\n" + config.Destino, "Já salvei, migrar", new[] { o.Inst, d.Inst });
                }
                return true;
            };
            asMg.AoEntrar = i => { if (i == 1) CarregarCalculosMigracao(); };
            asMg.Executar = IniciarMigracao;
            asMg.Cancelar = PedirCancelamento;
            asMg.AoAbrirPasta = () => { if (ultimaPasta != null) AbrirPasta(ultimaPasta); };
            asMg.Ir(0);
        }

        private void CarregarCalculosMigracao()
        {
            var o = A<ComboBox>("cmbOrigemMg").SelectedItem as InstalacaoVM;
            var d = A<ComboBox>("cmbDestinoMg").SelectedItem as InstalacaoVM;
            vm.CalculosMigrar.Clear();
            var st = A<TextBlock>("txtStatusMg");
            A<TextBlock>("txtTituloMg2").Text = "Quais cálculos levar do " + o.Titulo + " para o " + d.Titulo + "?";
            st.Text = "Lendo os cálculos das duas versões (sem abrir o PJe-Calc)…";
            Task.Factory.StartNew(() =>
            {
                List<CalculoInfo> lo = null; Dictionary<string, string> dup = null; string erro = null;
                try { lo = CalculosDe(o.Inst); dup = BancoPjeCalc.MapaDuplicados(CalculosDe(d.Inst)); }
                catch (Exception ex) { erro = ex.Message; }
                NaTela(() =>
                {
                    if (lo == null) { st.Text = "Não consegui ler os cálculos: " + erro; return; }
                    int ndup = 0;
                    foreach (var c in lo)
                    {
                        string existe = null;
                        if (dup != null && dup.TryGetValue(c.Chave, out existe)) ndup++;
                        var it = ItemDe(c, o.Pasta, null, existe != null ? "Já existe no destino (nº " + existe + ")" : null);
                        it.Selecionado = existe == null;
                        vm.CalculosMigrar.Add(it);
                    }
                    st.Text = lo.Count + " cálculo(s) no " + o.Titulo + (ndup > 0 ? " · " + ndup + " parece(m) já existir no " + d.Titulo + " e vieram desmarcados" : " · nenhum parece já existir no destino");
                });
            });
        }

        private void IniciarMigracao()
        {
            var o = (A<ComboBox>("cmbOrigemMg").SelectedItem as InstalacaoVM).Inst;
            var d = (A<ComboBox>("cmbDestinoMg").SelectedItem as InstalacaoVM).Inst;
            var numeros = new HashSet<string>(vm.CalculosMigrar.Where(x => x.Selecionado).Select(x => x.Numero));
            SalvarConfig();
            PrepararExecucao(asMg, vm.LogMigrar, "rolagemMg", "barraMg", "txtEtapaMg", "txtTituloMg", "Migrando…");
            Rodar(() => Backup.Migrar(o, d, numeros, config.Destino, config.ReabrirPjeCalc, this), res =>
            {
                bool ok = res.Falhas == 0 && res.Importados > 0;
                A<TextBlock>("txtTituloMg").Text = ok ? "Migração concluída" : "Migração com falhas";
                Finalizar(ok, ok ? "Migração concluída" : "Migração com falhas",
                    res.Importados + " cálculo(s) levados do PJe-Calc " + o.Versao + " para o " + d.Versao + (res.Falhas > 0 ? ", " + res.Falhas + " falha(s)" : "") + ".", res);
            });
        }

        // ============================================================ HISTÓRICO

        private void ConfigurarHistorico()
        {
            A<Button>("btnAtualizarHist").Click += (s, e) => CarregarHistorico();
            A<ItemsControl>("lstHistorico").AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                var b = e.OriginalSource as Button;
                var r = b == null ? null : b.Tag as RegistroVM;
                if (r == null) return;
                string acao = Convert.ToString(b.Content);
                if (acao == "Abrir pasta") AbrirPasta(r.R.PastaOperacao);
                else if (acao == "Restaurar .PJC")
                {
                    vm.ArquivosPjc.Clear();
                    A<RadioButton>("rsTipoPjc").IsChecked = true;
                    CarregarPastaPjc(r.R.PastaOperacao);
                    A<RadioButton>("abaRestaurar").IsChecked = true;
                    asRs.Terminou = false; asRs.Ir(1);
                }
                else if (acao == "Restaurar banco")
                {
                    var zips = Directory.GetFiles(r.R.PastaOperacao, "*.zip", SearchOption.AllDirectories);
                    if (zips.Length == 0) return;
                    A<RadioButton>("rsTipoBanco").IsChecked = true;
                    string zip = zips[0];
                    if (zips.Length > 1)
                    {
                        var d = new Microsoft.Win32.OpenFileDialog { Title = "Este backup tem mais de um banco. Escolha qual restaurar", Filter = "Backup do banco (*.zip)|*.zip", InitialDirectory = r.R.PastaOperacao };
                        if (d.ShowDialog(janela) != true) return;
                        zip = d.FileName;
                    }
                    DefinirZip(zip);
                    // pré-seleciona a versão correspondente
                    string v = BancoPjeCalc.VersaoDoZip(zip);
                    var alvo = vm.InstalacoesComBanco.FirstOrDefault(x => x.Inst.Versao == v);
                    if (alvo != null) A<ComboBox>("cmbDestinoRs").SelectedItem = alvo;
                    A<RadioButton>("abaRestaurar").IsChecked = true;
                    asRs.Terminou = false; asRs.Ir(1);
                }
            }));
        }

        private void CarregarHistorico()
        {
            A<TextBlock>("txtPastaHist").Text = "Operações registradas em " + config.Destino;
            string destino = config.Destino;
            Task.Factory.StartNew(() =>
            {
                List<RegistroHistorico> l;
                try { l = Historico.Listar(destino); } catch { l = new List<RegistroHistorico>(); }
                NaTela(() =>
                {
                    vm.Historico.Clear();
                    foreach (var r in l) vm.Historico.Add(new RegistroVM(r));
                    A<TextBlock>("txtSemHist").Visibility = l.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                });
            });
        }

        private void VerificarLembrete()
        {
            if (!config.Lembrete) return;
            string destino = config.Destino; int dias = config.LembreteDias <= 0 ? 7 : config.LembreteDias;
            Task.Factory.StartNew(() =>
            {
                DateTime? ult = null;
                try { ult = Historico.UltimoBackup(destino); } catch { }
                NaTela(() =>
                {
                    string t = null;
                    if (ult == null) t = "Nenhum backup encontrado na pasta de backups. Que tal fazer o primeiro agora?";
                    else
                    {
                        int d = (int)(DateTime.Now - ult.Value).TotalDays;
                        if (d >= dias) t = "Seu último backup foi há " + d + " dias (" + ult.Value.ToString("dd/MM/yyyy") + ").";
                    }
                    if (t != null) { A<TextBlock>("txtLembrete").Text = t; A<Border>("avisoLembrete").Visibility = Visibility.Visible; }
                });
            });
        }

        private void VerificarAtualizacao(bool manual)
        {
            if (!manual && !config.VerificarAtualizacoes) return;
            var txt = A<TextBlock>("txtVersao");
            if (manual) txt.Text = "Verificando…";
            Task.Factory.StartNew(() =>
            {
                Atualizacao.Novidade n = null; string erro = null;
                try { n = Atualizacao.Verificar(); } catch (Exception ex) { erro = ex.Message; }
                NaTela(() =>
                {
                    novidade = n;
                    string atual = "Versão instalada: " + Atualizacao.Atual;
                    if (n != null)
                    {
                        A<TextBlock>("txtAtualizacao").Text = "Nova versão do PJe-Calc Backup disponível: " + n.Versao + " (você tem a " + Atualizacao.Atual + ").";
                        A<Border>("avisoAtualizacao").Visibility = Visibility.Visible;
                        txt.Text = atual + " · nova versão " + n.Versao + " disponível";
                    }
                    else txt.Text = atual + (manual ? (erro != null ? " · não consegui verificar (" + erro + ")" : " · é a mais recente") : "");
                });
            });
        }

        // ============================================================ CONFIGURAÇÕES

        private void ConfigurarConfiguracoes()
        {
            foreach (var nome in new[] { "temaSistema", "temaClaro", "temaEscuro" })
            {
                var r = A<RadioButton>(nome);
                if (Convert.ToString(r.Tag) == (config.Tema ?? "Sistema")) r.IsChecked = true;
                r.Checked += (s, e) => { config.Tema = Convert.ToString(((RadioButton)s).Tag); config.Salvar(); AplicarTema(); };
            }
            foreach (RadioButton r in A<StackPanel>("paletas").Children)
            {
                if (Convert.ToInt32(r.Tag) == config.Paleta) r.IsChecked = true;
                r.Checked += (s, e) => { config.Paleta = Convert.ToInt32(((RadioButton)s).Tag); config.Salvar(); AplicarTema(); };
            }
            var dias = A<ComboBox>("cmbDiasLembrete");
            foreach (ComboBoxItem it in dias.Items) if (Convert.ToInt32(it.Tag) == config.LembreteDias) dias.SelectedItem = it;
            dias.SelectionChanged += (s, e) => { var it = dias.SelectedItem as ComboBoxItem; if (it != null) { config.LembreteDias = Convert.ToInt32(it.Tag); config.Salvar(); } };
            A<Button>("btnEscolherCfg").Click += (s, e) => { var p = SeletorPasta.Escolher(janela, "Escolha a pasta padrão dos backups", vm.Destino); if (p != null) vm.Destino = p; };
            A<Button>("btnVerificarAgora").Click += (s, e) => VerificarAtualizacao(true);
            A<TextBlock>("txtVersao").Text = "Versão instalada: " + Atualizacao.Atual;
            vm.PropertyChanged += (s, e) => config.Salvar();
        }

        // ============================================================ execução (comum)

        private void PrepararExecucao(Assistente a, ObservableCollection<LinhaLog> log, string rolagem, string barra, string etapa, string titulo, string textoTitulo)
        {
            assistenteAtual = a;
            logAtual = log; rolagemAtual = A<ScrollViewer>(rolagem); barraAtual = A<ProgressBar>(barra); etapaAtual = A<TextBlock>(etapa);
            A<TextBlock>(titulo).Text = textoTitulo;
            log.Clear();
            cancelado = false; executando = true;
            a.Executando = true; a.Terminou = false; a.AtualizarBotoes(); a.DesenharIndicador();
            barraAtual.Value = 0;
            etapaAtual.Text = "Iniciando…";
            TravarAbas(true, null);
            var tb = janela.TaskbarItemInfo;
            if (tb != null) { tb.ProgressState = TaskbarItemProgressState.Normal; tb.ProgressValue = 0; }
        }

        private void Rodar(Func<ResultadoBackup> trabalho, Action<ResultadoBackup> fim)
        {
            Task.Factory.StartNew(() =>
            {
                ResultadoBackup res = null; Exception erro = null;
                try { res = trabalho(); } catch (Exception ex) { erro = ex; }
                NaTela(() =>
                {
                    executando = false;
                    assistenteAtual.Executando = false; assistenteAtual.Terminou = true;
                    assistenteAtual.AtualizarBotoes(); assistenteAtual.DesenharIndicador();
                    TravarAbas(false, null);
                    if (erro != null)
                    {
                        Log(TipoLog.Erro, erro.Message);
                        res = new ResultadoBackup { Falhas = 1 };
                        Finalizar(false, "Operação interrompida", erro.Message, res);
                    }
                    else fim(res);
                    ultimaPasta = res.Pasta;
                    AtualizarEstados();
                });
            }, TaskCreationOptions.LongRunning);
        }

        [DllImport("user32.dll")] private static extern bool FlashWindow(IntPtr h, bool invert);

        /// <summary>Aviso final: som, notificação do Windows, barra de tarefas e (opcional) abrir a pasta.</summary>
        private void Finalizar(bool ok, string titulo, string texto, ResultadoBackup res)
        {
            etapaAtual.Text = texto + (res.Reaberto != null ? " O " + res.Reaberto + " foi reaberto." : "");
            var tb = janela.TaskbarItemInfo;
            if (tb != null) { tb.ProgressValue = 1; tb.ProgressState = ok ? TaskbarItemProgressState.None : TaskbarItemProgressState.Error; }
            if (config.Sons) Avisos.Som(ok);
            if (config.Notificacoes) Avisos.Notificar(titulo, texto, ok);
            if (!janela.IsActive) { try { FlashWindow(new WindowInteropHelper(janela).Handle, true); } catch { } }
            if (ok && config.AbrirPastaAoTerminar && res.Pasta != null) AbrirPasta(res.Pasta);
            if (ok) A<Border>("avisoLembrete").Visibility = Visibility.Collapsed;
        }

        private void PedirCancelamento()
        {
            if (!executando) return;
            cancelado = true;
            assistenteAtual.Avancar.IsEnabled = false;
            assistenteAtual.Avancar.Content = "Cancelando…";
            etapaAtual.Text = "Cancelando: terminando o item atual e fechando o PJe-Calc com segurança…";
        }

        // ============================================================ IRetornoBackup

        public bool Cancelado { get { return cancelado; } }

        public void Log(TipoLog tipo, string texto)
        {
            string icone, cor; FontWeight peso = FontWeights.Normal; Thickness margem = new Thickness(0, 1, 0, 1);
            switch (tipo)
            {
                case TipoLog.Ok: icone = "✓"; cor = "Ok"; break;
                case TipoLog.Erro: icone = "✗"; cor = "Erro"; break;
                case TipoLog.Aviso: icone = "!"; cor = "Aviso"; break;
                case TipoLog.Titulo: icone = "▸"; cor = "Primaria"; peso = FontWeights.SemiBold; margem = new Thickness(0, 8, 0, 2); break;
                default: icone = "•"; cor = "TextoSuave"; break;
            }
            NaTela(() =>
            {
                if (logAtual == null) return;
                logAtual.Add(new LinhaLog { Chave = cor, Icone = icone, Texto = texto, Cor = Tema.Recurso(cor), Peso = peso, Margem = margem });
                rolagemAtual.ScrollToEnd();
            });
        }

        public void Progresso(double fracao, string etapa)
        {
            NaTela(() =>
            {
                if (barraAtual == null) return;
                if (fracao >= 0)
                {
                    barraAtual.Value = Math.Min(1, fracao);
                    var tb = janela.TaskbarItemInfo;
                    if (tb != null && executando) tb.ProgressValue = Math.Min(1, fracao);
                }
                if (!cancelado) etapaAtual.Text = etapa;
            });
        }

        public bool PedirParaFechar(string descricao)
        {
            bool tentar = false;
            janela.Dispatcher.Invoke(new Action(() =>
            {
                tentar = Perguntar(TipoDialogo.Aviso, "Feche o PJe-Calc aberto",
                    descricao + "\n\nFeche-o pelo ícone na bandeja do Windows (perto do relógio) → Sair, e clique em \"Já fechei\".", "Já fechei", "Pular");
            }));
            if (tentar) Thread.Sleep(2000);
            return tentar;
        }
    }
}
