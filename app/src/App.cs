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
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Win32;

[assembly: AssemblyTitle("PJe-Calc Backup")]
[assembly: AssemblyProduct("PJe-Calc Backup")]
[assembly: AssemblyDescription("Backup, restauração e migração dos cálculos do PJe-Calc Cidadão (projeto independente, não oficial)")]
[assembly: AssemblyVersion("1.3.2.0")]
[assembly: AssemblyFileVersion("1.3.2.0")]

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
            var args = Environment.GetCommandLineArgs().Skip(1).Select(x => x.ToLowerInvariant()).ToList();
            // verificação ao entrar no Windows (tarefa do Agendador): sem janela, só uma notificação se houver problema
            if (args.Contains("--verificar"))
            {
                try
                {
                    var c = Config.Carregar();
                    Agendador.Resultado ult;
                    string msg = Agendador.Problema(c, out ult);
                    if (msg != null && c.Notificacoes) Avisos.NotificarEsperando("Backup automático do PJe-Calc", msg, false);
                }
                catch { }
                return;
            }
            if (args.Contains("--desinstalar")) { Instalador.Desinstalar(); return; }

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
            // bordas finas inteiras em qualquer escala de tela (125%, 150%…): encaixa nos pixels em vez de arredondar o layout
            var estiloBorda = new Style(typeof(Border)); estiloBorda.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
            app.Resources[typeof(Border)] = estiloBorda;
            // sem o contorno pontilhado de foco nas áreas de rolagem e listas (não são controles que o usuário "aciona")
            foreach (var t in new[] { typeof(ScrollViewer), typeof(ItemsControl) })
            { var st = new Style(t); st.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null)); app.Resources[t] = st; }
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
            A<TextBlock>("txtRodape").Text = "Projeto independente e não oficial. Tudo roda só neste computador.   ·   Versão " + Atualizacao.Atual;
            A<Button>("lnkSobre").Click += (s, e) => MostrarCartao("cartaoSobre");
            A<Button>("lnkApoiar").Click += (s, e) => MostrarCartao("cartaoApoie");
            ConfigurarApoio();
            A<Button>("btnFecharLembrete").Click += (s, e) => A<Border>("avisoLembrete").Visibility = Visibility.Collapsed;
            A<Button>("btnLembreteBackup").Click += (s, e) => { A<Border>("avisoLembrete").Visibility = Visibility.Collapsed; A<RadioButton>("abaBackup").IsChecked = true; };
            A<Button>("btnFecharAtualizacao").Click += (s, e) => A<Border>("avisoAtualizacao").Visibility = Visibility.Collapsed;
            A<Button>("btnBaixarAtualizacao").Click += (s, e) => AtualizarAgora();
            A<Button>("btnNovidades").Click += (s, e) => Abrir(novidade != null ? novidade.Url : Atualizacao.UrlProjeto + "/releases/latest");
            Atualizacao.LimparAntigos();
            ConfigurarInstalacao();
            ConfigurarTeclado();
            A<Button>("btnFecharAgenda").Click += (s, e) => { A<Border>("avisoAgenda").Visibility = Visibility.Collapsed; config.AvisoAgendaVisto = DateTime.Now; config.Salvar(); };
            A<Button>("btnAgendaBackup").Click += (s, e) => { A<Border>("avisoAgenda").Visibility = Visibility.Collapsed; config.AvisoAgendaVisto = DateTime.Now; config.Salvar(); A<RadioButton>("abaBackup").IsChecked = true; };

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
                VerificarAgenda();
                janela.Focus(); // nenhum botão com foco ao abrir (Enter = Avançar, não "Instalar")
                OferecerInstalacao();
                if (Environment.GetCommandLineArgs().Contains("--atualizado"))
                {
                    A<TextBlock>("txtVersao").Text = "Versão instalada: " + Atualizacao.Atual + " · atualizado agora";
                    Avisar(TipoDialogo.Sucesso, "Atualizado", "O PJe-Calc Backup foi atualizado para a versão " + Atualizacao.Atual + ".");
                }
                else VerificarAtualizacao(false);
            };
        }

        // ============================================================ instalação

        private void ConfigurarInstalacao()
        {
            A<Button>("btnInstalar").Click += (s, e) => Instalar();
            A<Button>("btnFecharInstalar").Click += (s, e) => { A<Border>("avisoInstalar").Visibility = Visibility.Collapsed; config.NaoOferecerInstalar = true; config.Salvar(); };
            A<Button>("btnInstalacao").Click += (s, e) =>
            {
                if (!Instalador.RodandoInstalado) { Instalar(); return; }
                if (executando) { Avisar(TipoDialogo.Info, "Aguarde", "Termine a operação em andamento antes."); return; }
                try { Process.Start(Instalador.Exe, "--desinstalar"); Application.Current.Shutdown(); } catch { }
            };
            AtualizarTextoInstalacao();
            if (Instalador.RodandoInstalado) Instalador.Registrar(); // mantém a versão certa em "Aplicativos instalados"
        }

        private void AtualizarTextoInstalacao()
        {
            bool aqui = Instalador.RodandoInstalado;
            A<TextBlock>("txtInstalacao").Text = aqui
                ? "Instalado em " + Instalador.Pasta + ". Aparece em Configurações do Windows → Aplicativos instalados."
                : "Você está usando o programa direto de " + Path.GetDirectoryName(Assembly.GetEntryAssembly().Location) + ", sem instalar.";
            A<Button>("btnInstalacao").Content = aqui ? "Desinstalar…" : "Instalar neste computador";
        }

        /// <summary>Se o programa foi aberto fora da pasta de instalação (ex.: Downloads), oferece instalar.</summary>
        private void OferecerInstalacao()
        {
            var args = Environment.GetCommandLineArgs();
            if (Instalador.RodandoInstalado)
            {
                if (args.Contains("--instalado"))
                {
                    // o backup automático passa a usar o programa instalado
                    Task.Factory.StartNew(() =>
                    {
                        try
                        {
                            var e = Agendador.Consultar();
                            if (!Instalador.ModoTeste && e.Existe && !string.Equals(e.Comando, Agendador.ExeConsole(), StringComparison.OrdinalIgnoreCase))
                                Agendador.Criar(config.AgendaFrequencia, config.AgendaDias, config.AgendaHora, config.AgendaLimpar);
                        }
                        catch { }
                    });
                    Avisar(TipoDialogo.Sucesso, "Instalado", "O PJe-Calc Backup foi instalado neste computador.\n\nAtalhos: Área de Trabalho e menu Iniciar. " +
                        "Ele também aparece em Configurações do Windows → Aplicativos instalados, de onde pode ser desinstalado.");
                }
                return;
            }
            if (config.NaoOferecerInstalar) return;
            var instalada = Instalador.VersaoInstalada;
            if (instalada != null && instalada >= Atualizacao.Atual) return; // mesma versão ou mais nova já instalada: não oferece
            A<TextBlock>("txtInstalar").Text = instalada == null
                ? "Você está usando o programa direto da pasta onde ele foi baixado. Instale-o neste computador: ganha atalhos na Área de Trabalho e no menu Iniciar, e o backup automático continua funcionando mesmo que este arquivo seja apagado."
                : "Este é outro arquivo do programa (versão " + Atualizacao.Atual + "); o instalado é a versão " + instalada + ". Quer atualizar a instalação com este?";
            A<Button>("btnInstalar").Content = instalada == null ? "Instalar" : "Atualizar instalação";
            A<Border>("avisoInstalar").Visibility = Visibility.Visible;
        }

        private void Instalar()
        {
            if (executando) { Avisar(TipoDialogo.Info, "Aguarde", "Termine a operação em andamento antes de instalar."); return; }
            try
            {
                SalvarConfig();
                string exe = Instalador.Instalar();
                Process.Start(new ProcessStartInfo(exe, "--instalado") { WorkingDirectory = Instalador.Pasta });
                Application.Current.Shutdown();
            }
            catch (Exception ex) { Avisar(TipoDialogo.Erro, "Não consegui instalar", ex.Message); }
        }

        // ============================================================ aviso do backup automático

        private void VerificarAgenda()
        {
            Task.Factory.StartNew(() =>
            {
                string msg = null; Agendador.Resultado ult = null;
                try { msg = Agendador.Problema(config, out ult); } catch { }
                NaTela(() =>
                {
                    if (msg == null) return;
                    A<TextBlock>("txtAgenda").Text = msg;
                    var rel = A<Button>("btnAgendaRelatorio");
                    if (ult != null && !ult.Ok && !string.IsNullOrEmpty(ult.Pasta) && Directory.Exists(ult.Pasta))
                    {
                        rel.Visibility = Visibility.Visible;
                        rel.Click += (s, e) => { string r = Path.Combine(ult.Pasta, "relatorio.txt"); Abrir(File.Exists(r) ? r : ult.Pasta); };
                    }
                    A<Border>("avisoAgenda").Visibility = Visibility.Visible;
                });
            });
        }

        // ============================================================ teclado

        private Assistente AssistenteVisivel()
        {
            if (A<FrameworkElement>("painelBackup").Visibility == Visibility.Visible) return asBk;
            if (A<FrameworkElement>("painelRestaurar").Visibility == Visibility.Visible) return asRs;
            if (A<FrameworkElement>("painelMigrar").Visibility == Visibility.Visible) return asMg;
            return null;
        }

        private void ConfigurarTeclado()
        {
            foreach (var a in new[] { asBk, asRs, asMg })
            {
                a.Avancar.ToolTip = "Atalho: Enter";
                a.Voltar.ToolTip = "Atalho: Esc";
            }
            janela.PreviewKeyDown += (s, e) =>
            {
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                var foco = Keyboard.FocusedElement as DependencyObject;
                if (e.Key == Key.F1) { Abrir(Atualizacao.UrlProjeto + "#como-usar"); e.Handled = true; return; }
                if (ctrl && !executando)
                {
                    string aba = e.Key == Key.D1 || e.Key == Key.NumPad1 ? "abaBackup" : e.Key == Key.D2 || e.Key == Key.NumPad2 ? "abaRestaurar" :
                                 e.Key == Key.D3 || e.Key == Key.NumPad3 ? "abaMigrar" : e.Key == Key.D4 || e.Key == Key.NumPad4 ? "abaHistorico" :
                                 e.Key == Key.OemComma ? "abaConfig" : null;
                    if (aba != null) { A<RadioButton>(aba).IsChecked = true; e.Handled = true; return; }
                }
                if (ctrl && e.Key == Key.F)
                {
                    TextBox alvo = null;
                    if (A<FrameworkElement>("painelHistorico").Visibility == Visibility.Visible) alvo = A<TextBox>("txtBuscaHist");
                    else if (AssistenteVisivel() == asBk && asBk.Atual == 1) alvo = A<TextBox>("txtFiltroBk");
                    else if (AssistenteVisivel() == asMg && asMg.Atual == 1) alvo = A<TextBox>("txtFiltroMg");
                    if (alvo != null) { alvo.Focus(); alvo.SelectAll(); e.Handled = true; }
                    return;
                }
                if (e.Key == Key.Escape)
                {
                    var busca = A<TextBox>("txtBuscaHist");
                    if (foco == busca && !string.IsNullOrEmpty(busca.Text)) { busca.Text = ""; e.Handled = true; return; }
                    var a = AssistenteVisivel();
                    if (a != null && !a.Executando && a.Voltar.Visibility == Visibility.Visible)
                    {
                        a.Voltar.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); e.Handled = true;
                    }
                    return;
                }
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
                {
                    if (foco is ButtonBase) return; // Enter num botão/caixa: o próprio controle decide
                    var combo = foco as ComboBoxItem;
                    if (foco is ComboBox && ((ComboBox)foco).IsDropDownOpen || combo != null) return;
                    var a = AssistenteVisivel();
                    if (a != null && !a.Executando && a.Avancar.IsEnabled && a.Avancar.Visibility == Visibility.Visible)
                    {
                        a.Avancar.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); e.Handled = true;
                    }
                }
            };
        }

        // ============================================================ sobre e apoio

        /// <summary>Abre a engrenagem e rola até o cartão (Sobre ou Apoie).</summary>
        private void MostrarCartao(string nome)
        {
            A<RadioButton>("abaConfig").IsChecked = true;
            janela.Dispatcher.BeginInvoke(new Action(() =>
            {
                var c = A<FrameworkElement>(nome);
                var sv = A<ScrollViewer>("painelConfig");
                var p = c.TransformToAncestor(sv).Transform(new Point(0, 0));
                sv.ScrollToVerticalOffset(sv.VerticalOffset + p.Y - 8);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ConfigurarApoio()
        {
            A<TextBox>("txtChavePix").Text = Pix.Chave;
            A<Button>("btnLicenca").Click += (s, e) => Abrir(Atualizacao.UrlProjeto + "/blob/main/LICENSE");
            var aviso = A<TextBlock>("txtPixCopiado");
            var tempo = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            tempo.Tick += (s, e) => { tempo.Stop(); aviso.Visibility = Visibility.Collapsed; };
            Action<string, string> copiar = (texto, msg) =>
            {
                try { Clipboard.SetText(texto); aviso.Text = "✓  " + msg; }
                catch { aviso.Text = "Não consegui copiar. Selecione o texto e use Ctrl+C."; }
                aviso.Visibility = Visibility.Visible; tempo.Stop(); tempo.Start();
            };
            A<Button>("btnCopiarChave").Click += (s, e) => copiar(Pix.Chave, "Chave PIX copiada. Cole no app do banco em Pix → Pagar com chave.");
            A<Button>("btnCopiarPix").Click += (s, e) => copiar(Pix.CopiaECola(), "Código \"Pix Copia e Cola\" copiado. Cole no app do banco em Pix Copia e Cola.");

            // QR Code desenhado com quadradinhos (sempre preto no branco, também no tema escuro)
            try
            {
                var m = QrCode.Gerar(Pix.CopiaECola());
                int n = m.GetLength(0), margem = 2;
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                    for (int y = 0; y < n; y++)
                        for (int x = 0; x < n; x++)
                            if (m[y, x])
                            {
                                ctx.BeginFigure(new Point(x + margem, y + margem), true, true);
                                ctx.PolyLineTo(new[] { new Point(x + margem + 1, y + margem), new Point(x + margem + 1, y + margem + 1), new Point(x + margem, y + margem + 1) }, false, false);
                            }
                g.Freeze();
                var area = new System.Windows.Shapes.Rectangle { Width = n + 2 * margem, Height = n + 2 * margem, Fill = Brushes.White };
                var caminho = new System.Windows.Shapes.Path { Data = g, Fill = Brushes.Black, SnapsToDevicePixels = true };
                var tela = new Grid { Width = n + 2 * margem, Height = n + 2 * margem };
                tela.Children.Add(area); tela.Children.Add(caminho);
                RenderOptions.SetEdgeMode(tela, EdgeMode.Aliased);
                A<Border>("qrPix").Child = new Viewbox { Child = tela, Stretch = Stretch.Uniform };
                A<Border>("qrPix").ToolTip = "QR Code PIX — chave " + Pix.Chave;
            }
            catch { A<Border>("qrPix").Visibility = Visibility.Collapsed; }
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
            // cálculos de uma versão recolhida ficam ocultos, a não ser durante uma busca no filtro
            v.Filter = o =>
            {
                var c = (CalcItemVM)o;
                if (!c.Corresponde(tb.Text)) return false;
                return c.EhGrupo || c.Grupo == null || c.Grupo.Expandido || !string.IsNullOrWhiteSpace(tb.Text);
            };
            tb.TextChanged += (s, e) => v.Refresh();
        }

        /// <summary>Botão "Mostrar/Ocultar cálculos" dos cabeçalhos de versão.</summary>
        private void LigarRecolher(string lista, ObservableCollection<CalcItemVM> itens)
        {
            A<ItemsControl>(lista).AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                var b = e.OriginalSource as Button;
                var g = b == null ? null : b.Tag as CalcItemVM;
                if (g == null || !g.EhGrupo) return;
                g.Expandido = !g.Expandido;
                CollectionViewSource.GetDefaultView(itens).Refresh();
            }));
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
            LigarRecolher("lstCalcBk", vm.CalculosBackup);
            A<Button>("btnBkTodos").Click += (s, e) => Marcar(vm.CalculosBackup, true);
            A<Button>("btnBkNenhum").Click += (s, e) => Marcar(vm.CalculosBackup, false);
            A<Button>("btnBk7").Click += (s, e) => MarcarRecentes(7);
            A<Button>("btnBk30").Click += (s, e) => MarcarRecentes(30);
            A<Button>("btnBk90").Click += (s, e) => MarcarRecentes(90);
            A<ComboBox>("cmbOrdemBk").SelectionChanged += (s, e) => OrdenarCalculosBackup();
            A<Button>("btnProcurar").Click += (s, e) => ProcurarInstalacoes();
            A<Button>("btnAdicionar").Click += (s, e) => AdicionarPasta();
            A<Button>("btnPlanilha").Click += (s, e) => SalvarPlanilha();
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
                    int total = 0, lembrados = 0;
                    foreach (var kv in res)
                    {
                        // começa recolhido; com uma versão só, já vem aberto
                        var g = new CalcItemVM { EhGrupo = true, Titulo = kv.Key.Titulo, PastaInstalacao = kv.Key.Pasta, Expandido = res.Count == 1 };
                        List<string> desm;
                        var desmarcados = config.CalculosDesmarcados != null && config.CalculosDesmarcados.TryGetValue(kv.Key.Pasta, out desm)
                            ? new HashSet<string>(desm) : new HashSet<string>();
                        // lista que cobre a versão inteira = backup só do banco, não preferência: ignora
                        if (desmarcados.Count > 0 && kv.Value.All(c => desmarcados.Contains(c.Numero))) desmarcados.Clear();
                        foreach (var c in kv.Value)
                        {
                            var it = ItemDe(c, kv.Key.Pasta, g, null);
                            if (desmarcados.Contains(c.Numero)) { it.Selecionado = false; lembrados++; }
                            g.Filhos.Add(it); total++;
                        }
                        g.SincronizarGrupo();
                        vm.CalculosBackup.Add(g);
                        foreach (var it in g.Filhos) vm.CalculosBackup.Add(it);
                    }
                    OrdenarCalculosBackup();
                    st.Text = total + " cálculo(s) em " + res.Count + " versão(ões)." +
                              (lembrados > 0 ? "  " + lembrados + " vieram desmarcados, como no último backup." : "") +
                              (erros.Count > 0 ? "  Não consegui ler: " + string.Join("; ", erros) : "");
                });
            });
        }

        /// <summary>Reordena os cálculos dentro de cada versão (número, nome ou mais recentes).</summary>
        private void OrdenarCalculosBackup()
        {
            int modo = A<ComboBox>("cmbOrdemBk").SelectedIndex;
            var grupos = vm.CalculosBackup.Where(x => x.EhGrupo).ToList();
            if (grupos.Count == 0) return;
            vm.CalculosBackup.Clear();
            foreach (var g in grupos)
            {
                IEnumerable<CalcItemVM> f = g.Filhos;
                int n;
                if (modo == 1) f = f.OrderBy(x => x.Info.Reclamante ?? "", StringComparer.CurrentCultureIgnoreCase);
                else if (modo == 2) f = f.OrderByDescending(x => x.Info.Criacao ?? "").ThenByDescending(x => int.TryParse(x.Numero, out n) ? n : 0);
                else f = f.OrderBy(x => int.TryParse(x.Numero, out n) ? n : int.MaxValue);
                g.Filhos = f.ToList();
                vm.CalculosBackup.Add(g);
                foreach (var it in g.Filhos) vm.CalculosBackup.Add(it);
            }
        }

        /// <summary>Marca só os cálculos criados nos últimos N dias (e abre as versões que têm algum).</summary>
        private void MarcarRecentes(int dias)
        {
            string limite = DateTime.Today.AddDays(-dias).ToString("yyyy-MM-dd");
            int n = 0;
            foreach (var g in vm.CalculosBackup.Where(x => x.EhGrupo).ToList())
            {
                foreach (var it in g.Filhos)
                {
                    bool recente = string.CompareOrdinal(it.Info.Criacao ?? "", limite) >= 0;
                    it.Selecionado = recente;
                    if (recente) n++;
                }
                if (g.Filhos.Any(x => x.Selecionado)) g.Expandido = true;
            }
            CollectionViewSource.GetDefaultView(vm.CalculosBackup).Refresh();
            A<TextBlock>("txtStatusBk").Text = n == 0 ? "Nenhum cálculo foi criado nos últimos " + dias + " dias." : n + " cálculo(s) criados nos últimos " + dias + " dias ficaram marcados; os demais foram desmarcados.";
        }

        /// <summary>Guarda, por versão, os cálculos desmarcados, para virem desmarcados no próximo backup.</summary>
        private void LembrarDesmarcados()
        {
            if (config.CalculosDesmarcados == null) config.CalculosDesmarcados = new Dictionary<string, List<string>>();
            if (!config.ExportarPjc) return; // backup só do banco: não muda a preferência
            foreach (var g in vm.CalculosBackup.Where(x => x.EhGrupo))
            {
                var d = g.Filhos.Where(f => !f.Selecionado).Select(f => f.Numero).ToList();
                if (d.Count == 0 || d.Count == g.Filhos.Count) config.CalculosDesmarcados.Remove(g.PastaInstalacao); // todos desmarcados = só o banco
                else config.CalculosDesmarcados[g.PastaInstalacao] = d;
            }
        }

        private void IniciarBackup()
        {
            var selecionadas = vm.Instalacoes.Where(i => i.Selecionada && i.Selecionavel).Select(i => i.Inst).ToList();
            var op = new OpcoesBackup { Destino = config.Destino, ExportarPjc = config.ExportarPjc, CopiarBanco = config.CopiarBanco, ReabrirNoFinal = config.ReabrirPjeCalc, SoAlterados = config.BackupRapido };
            foreach (var g in vm.CalculosBackup.Where(x => x.EhGrupo))
                if (!g.Filhos.All(f => f.Selecionado))
                    op.Selecao[g.PastaInstalacao] = new HashSet<string>(g.Filhos.Where(f => f.Selecionado).Select(f => f.Numero));
            LembrarDesmarcados();
            SalvarConfig();

            PrepararExecucao(asBk, vm.LogBackup, "rolagemBk", "barraBk", "txtEtapaBk", "txtTituloBk", "Fazendo backup…");
            Rodar(() => Backup.Executar(selecionadas, op, this), res =>
            {
                bool ok = res.Falhas == 0;
                A<TextBlock>("txtTituloBk").Text = ok ? "Backup concluído" : "Backup concluído com falhas";
                string conf = res.Esperados > 0 ? res.Conferidos + " de " + res.Esperados + " conferidos" : "";
                Finalizar(ok, ok ? "Backup concluído" : "Backup concluído com falhas",
                    res.Exportados + " cálculo(s) exportado(s)" + (res.Copiados > 0 ? " + " + res.Copiados + " sem alteração" : "") + (conf != "" ? " (" + conf + ")" : "") + ", " + res.Bancos + " banco(s) copiado(s)" +
                    (res.Falhas > 0 ? ", " + res.Falhas + " falha(s)" : "") + ".", res);
                if (ok && !string.IsNullOrEmpty(config.Limpeza)) NaTela(() => OferecerLimpeza(false));
            });
        }

        // ============================================================ planilha

        private void SalvarPlanilha()
        {
            var sel = vm.Instalacoes.Where(x => x.Selecionada && x.Selecionavel).ToList();
            if (sel.Count == 0) { Avisar(TipoDialogo.Info, "Nenhuma versão marcada", "Marque as versões do PJe-Calc que devem entrar na planilha."); return; }
            var d = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Salvar a planilha dos cálculos", Filter = "Planilha do Excel (*.xlsx)|*.xlsx",
                FileName = "Cálculos PJe-Calc " + DateTime.Now.ToString("yyyy-MM-dd") + ".xlsx",
                InitialDirectory = Directory.Exists(config.Destino) ? config.Destino : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (d.ShowDialog(janela) != true) return;
            string arq = d.FileName;
            var botao = A<Button>("btnPlanilha");
            botao.IsEnabled = false; botao.Content = "Gerando…";
            Task.Factory.StartNew(() =>
            {
                var linhas = new List<Planilha.Linha>(); var erros = new List<string>(); Exception erro = null;
                try
                {
                    foreach (var i in sel)
                    {
                        try { linhas.AddRange(CalculosDe(i.Inst).Select(c => new Planilha.Linha { Versao = i.Inst.Versao, C = c })); }
                        catch (Exception ex) { erros.Add(i.Titulo + ": " + ex.Message); }
                    }
                    Planilha.Salvar(arq, linhas);
                }
                catch (Exception ex) { erro = ex; }
                NaTela(() =>
                {
                    botao.IsEnabled = true; botao.Content = "Planilha (Excel)…";
                    if (erro != null) { Avisar(TipoDialogo.Erro, "Não consegui salvar a planilha", erro.Message); return; }
                    if (Perguntar(TipoDialogo.Sucesso, "Planilha salva",
                        linhas.Count + " cálculo(s) de " + (sel.Count - erros.Count) + " versão(ões) em:\n" + arq +
                        (erros.Count > 0 ? "\n\nNão consegui ler: " + string.Join("; ", erros) : "") +
                        "\n\nA planilha tem nomes das partes: guarde-a em local seguro.", "Abrir a planilha", "Fechar"))
                        Abrir(arq);
                });
            });
        }

        // ============================================================ limpeza de backups antigos

        private void ConfigurarLimpeza()
        {
            var cmb = A<ComboBox>("cmbLimpeza");
            foreach (var c in Limpeza.Codigos)
            {
                var it = new ComboBoxItem { Content = Limpeza.Descrever(c), Tag = c };
                cmb.Items.Add(it);
                if (c == (config.Limpeza ?? "")) cmb.SelectedItem = it;
            }
            if (cmb.SelectedItem == null) cmb.SelectedIndex = 0;
            cmb.SelectionChanged += (s, e) =>
            {
                var it = cmb.SelectedItem as ComboBoxItem;
                if (it == null) return;
                config.Limpeza = Convert.ToString(it.Tag); config.Salvar();
                AtualizarEstadoAgenda();
            };
        }

        /// <summary>Mostra quais backups a regra manda apagar e, se o usuário confirmar, move para a Lixeira.</summary>
        private void OferecerLimpeza(bool manual)
        {
            string codigo = config.Limpeza;
            if (string.IsNullOrEmpty(codigo))
            {
                if (manual && Perguntar(TipoDialogo.Info, "Limpeza desligada", "Escolha uma regra (por exemplo, \"Manter só os últimos 10 backups\") em Configurações → Backups.", "Abrir Configurações", "Fechar"))
                    A<RadioButton>("abaConfig").IsChecked = true;
                return;
            }
            string destino = config.Destino;
            Task.Factory.StartNew(() =>
            {
                var cand = Limpeza.Candidatos(destino, codigo);
                long total = cand.Sum(c => Limpeza.Tamanho(c.PastaOperacao));
                bool lixeira = Limpeza.TemLixeira(destino);
                NaTela(() =>
                {
                    if (cand.Count == 0)
                    {
                        if (manual) Avisar(TipoDialogo.Sucesso, "Nada para limpar", "Nenhum backup se encaixa na regra \"" + Limpeza.Descrever(codigo) + "\".");
                        return;
                    }
                    var nomes = cand.OrderBy(c => c.Data).Take(8).Select(c => "•  " + c.Data.ToString("dd/MM/yyyy HH:mm") + "  (" + c.Calculos + " cálculo(s))").ToList();
                    if (cand.Count > 8) nomes.Add("•  … e mais " + (cand.Count - 8));
                    string msg = "Regra: " + Limpeza.Descrever(codigo) + ".\n\n" + cand.Count + " backup(s) antigo(s), " + Limpeza.TextoTamanho(total) + " no total:\n" +
                                 string.Join("\n", nomes) + "\n\n" +
                                 (lixeira ? "Eles vão para a Lixeira do Windows, de onde dá para recuperar."
                                          : "ATENÇÃO: a pasta de backups fica num pendrive, HD externo ou rede, que não têm Lixeira. Os backups serão apagados DEFINITIVAMENTE.");
                    if (!Perguntar(lixeira ? TipoDialogo.Pergunta : TipoDialogo.Aviso, "Limpar backups antigos?", msg,
                        lixeira ? "Mover para a Lixeira" : "Apagar definitivamente", "Agora não")) return;
                    var erros = new List<string>();
                    int n = Limpeza.MoverParaLixeira(cand, erros.Add);
                    CarregarHistorico();
                    if (erros.Count > 0) Avisar(TipoDialogo.Aviso, "Limpeza parcial", n + " backup(s) removido(s). Não consegui remover:\n" + string.Join("\n", erros.Take(5)));
                    else Avisar(TipoDialogo.Sucesso, "Limpeza concluída", n + " backup(s) antigo(s) " + (lixeira ? "movido(s) para a Lixeira" : "apagado(s)") + " (" + Limpeza.TextoTamanho(total) + ").");
                });
            });
        }

        // ============================================================ backup agendado

        private List<CheckBox> diasAgenda = new List<CheckBox>();

        private void ConfigurarAgenda()
        {
            var painel = A<Panel>("agDias");
            for (int i = 0; i < Agendador.Dias.Length; i++)
            {
                var c = new CheckBox { Content = Agendador.DiasPt[i], Tag = Agendador.Dias[i], Margin = new Thickness(0, 0, 16, 4), IsChecked = config.AgendaDias.Contains(Agendador.Dias[i]) };
                diasAgenda.Add(c); painel.Children.Add(c);
            }
            var diario = A<RadioButton>("agDiario"); var semanal = A<RadioButton>("agSemanal");
            (config.AgendaFrequencia == "diario" ? diario : semanal).IsChecked = true;
            painel.IsEnabled = semanal.IsChecked == true;
            semanal.Checked += (s, e) => painel.IsEnabled = true;
            diario.Checked += (s, e) => painel.IsEnabled = false;
            A<TextBox>("txtAgHora").Text = config.AgendaHora;
            A<CheckBox>("chkAgLimpar").IsChecked = config.AgendaLimpar;

            A<Button>("btnAgSalvar").Click += (s, e) =>
            {
                string hora = (A<TextBox>("txtAgHora").Text ?? "").Trim();
                TimeSpan h;
                if (hora.Length == 4 && hora[1] == ':') hora = "0" + hora;
                if (!TimeSpan.TryParseExact(hora, "hh\\:mm", System.Globalization.CultureInfo.InvariantCulture, out h))
                { Avisar(TipoDialogo.Info, "Horário inválido", "Digite o horário no formato 18:00."); return; }
                var dias = diasAgenda.Where(c => c.IsChecked == true).Select(c => Convert.ToString(c.Tag)).ToList();
                string freq = diario.IsChecked == true ? "diario" : "semanal";
                if (freq == "semanal" && dias.Count == 0) { Avisar(TipoDialogo.Info, "Escolha os dias", "Marque pelo menos um dia da semana."); return; }
                bool limpar = A<CheckBox>("chkAgLimpar").IsChecked == true;
                if (limpar && string.IsNullOrEmpty(config.Limpeza))
                { Avisar(TipoDialogo.Info, "Escolha a regra de limpeza", "Para limpar depois do backup automático, escolha antes uma regra em \"Limpeza de backups antigos\"."); return; }
                if (limpar && !Limpeza.TemLixeira(config.Destino))
                { Avisar(TipoDialogo.Aviso, "Sem Lixeira nessa pasta", "A pasta de backups fica num pendrive, HD externo ou rede, onde não existe Lixeira. A limpeza automática só funciona em disco interno."); return; }
                try
                {
                    SalvarConfig();
                    Agendador.Criar(freq, dias, hora, limpar); config.AgendaCriada = DateTime.Now;
                    config.AgendaFrequencia = freq; config.AgendaDias = dias.Count > 0 ? dias : config.AgendaDias; config.AgendaHora = hora; config.AgendaLimpar = limpar;
                    config.Salvar();
                    Avisar(TipoDialogo.Sucesso, "Backup automático ligado", "O backup vai rodar " + Agendador.Descrever(freq, dias, hora) + ", na pasta:\n" + config.Destino);
                }
                catch (Exception ex) { Avisar(TipoDialogo.Erro, "Não consegui agendar", ex.Message); }
                AtualizarEstadoAgenda();
            };
            A<Button>("btnAgRemover").Click += (s, e) =>
            {
                try { Agendador.Remover(); } catch (Exception ex) { Avisar(TipoDialogo.Erro, "Não consegui desligar", ex.Message); }
                AtualizarEstadoAgenda();
            };
            AtualizarEstadoAgenda();
        }

        private void AtualizarEstadoAgenda()
        {
            var txt = A<TextBlock>("txtAgEstado");
            Task.Factory.StartNew(() =>
            {
                var e = Agendador.Consultar();
                NaTela(() =>
                {
                    A<Button>("btnAgRemover").Visibility = e.Existe ? Visibility.Visible : Visibility.Collapsed;
                    if (!e.Existe) { txt.Text = "Desligado."; txt.Foreground = Tema.Recurso("TextoSuave"); return; }
                    string t = "Ligado: " + Agendador.Descrever(config.AgendaFrequencia, config.AgendaDias, config.AgendaHora) + ".";
                    if (!string.IsNullOrEmpty(e.Proxima) && !e.Proxima.StartsWith("N/A")) t += "  Próximo: " + e.Proxima + ".";
                    if (!string.IsNullOrEmpty(e.Resultado) && e.Resultado != "ainda não rodou") t += "  Último: " + e.Ultima + " (" + e.Resultado + ").";
                    if (!e.ExeExiste) t = "A tarefa existe, mas aponta para um programa que não está mais lá (" + e.Comando + "). Clique em \"Ligar / salvar\" para corrigir.";
                    txt.Text = t;
                    txt.Foreground = Tema.Recurso(e.ExeExiste ? "Ok" : "Aviso");
                });
            });
        }

        // ============================================================ atualização com um clique

        private void AtualizarAgora()
        {
            if (novidade == null) { Abrir(Atualizacao.UrlProjeto + "/releases/latest"); return; }
            if (executando) { Avisar(TipoDialogo.Info, "Aguarde", "Termine a operação em andamento antes de atualizar."); return; }
            if (!Perguntar(TipoDialogo.Pergunta, "Atualizar para a versão " + novidade.Versao + "?",
                "O programa baixa a versão nova do GitHub, confere o SHA-256 de cada arquivo e se reinicia. Suas configurações e backups não mudam.",
                "Atualizar", "Agora não")) return;
            var n = novidade;
            var botoes = new[] { A<Button>("btnBaixarAtualizacao"), A<Button>("btnAtualizarAgora") };
            foreach (var b in botoes) b.IsEnabled = false;
            Task.Factory.StartNew(() =>
            {
                string novo = null; Exception erro = null;
                try { novo = Atualizacao.Instalar(n, t => NaTela(() => { A<TextBlock>("txtAtualizacao").Text = t; A<TextBlock>("txtVersao").Text = t; })); }
                catch (Exception ex) { erro = ex; }
                NaTela(() =>
                {
                    foreach (var b in botoes) b.IsEnabled = true;
                    if (erro != null)
                    {
                        A<TextBlock>("txtAtualizacao").Text = "Não consegui atualizar.";
                        if (Perguntar(TipoDialogo.Erro, "Não consegui atualizar", erro.Message + "\n\nVocê pode baixar a versão nova pela página do projeto.", "Abrir a página", "Fechar"))
                            Abrir(n.Url);
                        return;
                    }
                    SalvarConfig();
                    try { Process.Start(novo, "--atualizado"); } catch { }
                    Application.Current.Shutdown();
                });
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

            // listas agrupadas por versão: os itens de um grupo recolhido ficam ocultos
            var vArq = CollectionViewSource.GetDefaultView(vm.ArquivosPjc);
            vArq.Filter = o => { var a = (ArquivoPjcVM)o; return a.EhGrupo || a.Grupo == null || a.Grupo.Expandido; };
            A<ItemsControl>("lstArquivosPjc").AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                var b = e.OriginalSource as Button;
                var g = b == null ? null : b.Tag as ArquivoPjcVM;
                if (g == null || !g.EhGrupo) return;
                g.Expandido = !g.Expandido; vArq.Refresh();
            }));
            CollectionViewSource.GetDefaultView(vm.CalculosRestaurar).Filter = o => { var c = (CalcItemVM)o; return c.EhGrupo || c.Grupo == null || c.Grupo.Expandido; };
            LigarRecolher("lstRevisaoRs", vm.CalculosRestaurar);
            A<Button>("btnZipBanco").Click += (s, e) => EscolherZip();
            A<ComboBox>("cmbDestinoRs").SelectionChanged += (s, e) => { if (asRs.Atual == 2) PrepararRevisaoRestauracao(); };

            asRs.AoAvancar = i =>
            {
                if (i == 1)
                {
                    if (RestaurarBancoEscolhido && string.IsNullOrEmpty(zipEscolhido)) { Avisar(TipoDialogo.Info, "Escolha o arquivo", "Escolha o arquivo .zip do backup do banco."); return false; }
                    if (!RestaurarBancoEscolhido && !vm.ArquivosPjc.Any(a => !a.EhGrupo && a.Selecionado)) { Avisar(TipoDialogo.Info, "Nenhum arquivo", "Escolha arquivos .PJC (ou uma pasta de backup) e deixe marcados os que quer restaurar."); return false; }
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
                    int n = vm.CalculosRestaurar.Count(x => !x.EhGrupo && x.Selecionado);
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

        /// <summary>Junta os arquivos novos aos já escolhidos e monta a lista agrupada por versão (recolhida quando há mais de uma).</summary>
        private void AdicionarArquivosPjc(IEnumerable<string> arquivos)
        {
            var atuais = vm.ArquivosPjc.Where(a => !a.EhGrupo).ToList();
            var gruposAntes = vm.ArquivosPjc.Where(a => a.EhGrupo).ToDictionary(g => g.Versao ?? "", g => g.Expandido);
            var existentes = new HashSet<string>(atuais.Select(a => a.Caminho), StringComparer.OrdinalIgnoreCase);
            foreach (var a in arquivos)
            {
                if (!existentes.Add(a)) continue;
                long t = 0; try { t = new FileInfo(a).Length; } catch { }
                atuais.Add(new ArquivoPjcVM { Caminho = a, Tamanho = Math.Max(1, t / 1024) + " KB", Versao = VersaoPorPasta.De(a) });
            }
            vm.ArquivosPjc.Clear();
            var grupos = atuais.GroupBy(a => a.Versao ?? "").OrderByDescending(g => VersaoPorPasta.Ordenavel(g.Key)).ToList();
            foreach (var g in grupos)
            {
                bool aberto;
                var cab = new ArquivoPjcVM
                {
                    EhGrupo = true, Versao = g.Key, Titulo = g.Key == "" ? "Versão não identificada" : "PJe-Calc " + g.Key,
                    Expandido = grupos.Count == 1 || (gruposAntes.TryGetValue(g.Key, out aberto) && aberto)
                };
                vm.ArquivosPjc.Add(cab);
                foreach (var a in g.OrderBy(x => x.Nome, StringComparer.OrdinalIgnoreCase))
                {
                    a.Grupo = cab; cab.Filhos.Add(a); vm.ArquivosPjc.Add(a);
                }
                cab.SincronizarGrupo();
            }
            AtualizarResumoPjc();
        }

        private void AtualizarResumoPjc()
        {
            int n = vm.ArquivosPjc.Count(a => !a.EhGrupo);
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
            var arquivos = vm.ArquivosPjc.Where(a => !a.EhGrupo && a.Selecionado).Select(a => a.Caminho).ToList();
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
                    var grupos = infos.GroupBy(ci => VersaoPorPasta.De(ci.Arquivo)).OrderByDescending(g => VersaoPorPasta.Ordenavel(g.Key)).ToList();
                    foreach (var grp in grupos)
                    {
                        var cab = new CalcItemVM { EhGrupo = true, Titulo = grp.Key == "" ? "Versão não identificada" : "Arquivos do PJe-Calc " + grp.Key, Expandido = grupos.Count == 1 };
                        vm.CalculosRestaurar.Add(cab);
                        foreach (var ci in grp)
                        {
                            string existe = null;
                            if (dup != null && dup.TryGetValue(ci.Chave, out existe)) ndup++;
                            var it = ItemDe(ci, null, cab, existe != null ? "Parece já existir (nº " + existe + ")" : null);
                            it.Detalhe = (it.Detalhe.Length > 0 ? it.Detalhe + "   ·   " : "") + Path.GetFileName(ci.Arquivo);
                            it.Selecionado = existe == null;
                            cab.Filhos.Add(it);
                            vm.CalculosRestaurar.Add(it);
                        }
                        cab.SincronizarGrupo();
                        cab.AtualizarResumo();
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
            var arquivos = vm.CalculosRestaurar.Where(x => !x.EhGrupo && x.Selecionado).Select(x => x.Arquivo).ToList();
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
            A<Button>("btnAtualizarHist").Click += (s, e) => { indiceBusca = null; CarregarHistorico(); Buscar(); };
            A<Button>("btnLimparHist").Click += (s, e) => OferecerLimpeza(true);
            var busca = A<TextBox>("txtBuscaHist");
            var espera = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            espera.Tick += (s, e) => { espera.Stop(); Buscar(); };
            busca.TextChanged += (s, e) => { espera.Stop(); espera.Start(); };
            A<ItemsControl>("lstBusca").AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                var b = e.OriginalSource as Button;
                var r = b == null ? null : b.Tag as BuscaVM;
                if (r == null) return;
                if (Convert.ToString(b.Content) == "Mostrar arquivo") { try { Process.Start("explorer.exe", "/select,\"" + r.A.Arquivo + "\""); } catch { } return; }
                // restaurar só este arquivo
                vm.ArquivosPjc.Clear();
                A<RadioButton>("rsTipoPjc").IsChecked = true;
                AdicionarArquivosPjc(new[] { r.A.Arquivo });
                A<RadioButton>("abaRestaurar").IsChecked = true;
                asRs.Terminou = false; asRs.Ir(1);
            }));
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

        private List<AchadoBusca> indiceBusca;
        private string indiceDe;
        private bool indexando;

        /// <summary>Busca o texto digitado nos .PJC de todos os backups (o índice é montado na primeira busca).</summary>
        private void Buscar()
        {
            string termo = (A<TextBox>("txtBuscaHist").Text ?? "").Trim();
            var st = A<TextBlock>("txtStatusBusca");
            bool buscando = termo.Length > 0;
            A<ScrollViewer>("rolagemBusca").Visibility = buscando ? Visibility.Visible : Visibility.Collapsed;
            A<ScrollViewer>("rolagemHist").Visibility = buscando ? Visibility.Collapsed : Visibility.Visible;
            st.Visibility = buscando ? Visibility.Visible : Visibility.Collapsed;
            if (!buscando) { vm.Busca.Clear(); return; }
            if (indiceBusca == null || indiceDe != config.Destino)
            {
                if (indexando) return;
                indexando = true;
                string destino = config.Destino;
                st.Text = "Lendo os arquivos .PJC dos backups…";
                Task.Factory.StartNew(() =>
                {
                    List<AchadoBusca> l;
                    try { l = BuscaBackups.Indexar(destino, (i, n) => NaTela(() => st.Text = "Lendo os arquivos .PJC dos backups… " + i + " de " + n), null); }
                    catch { l = new List<AchadoBusca>(); }
                    NaTela(() => { indiceBusca = l; indiceDe = destino; indexando = false; Buscar(); });
                });
                return;
            }
            var achados = BuscaBackups.Filtrar(indiceBusca, termo);
            vm.Busca.Clear();
            foreach (var a in achados.Take(200)) vm.Busca.Add(new BuscaVM(a));
            int calcs = achados.Select(a => a.Info.Chave).Distinct().Count();
            st.Text = achados.Count == 0
                ? "Nada encontrado em " + indiceBusca.Count + " arquivo(s) .PJC dos backups."
                : achados.Count + " arquivo(s) encontrado(s)" + (calcs < achados.Count ? " (" + calcs + " cálculo(s) diferente(s), em backups de datas diferentes)" : "") +
                  (achados.Count > 200 ? ". Mostrando os 200 mais recentes." : ". Mais recentes primeiro.");
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
                        A<Button>("btnAtualizarAgora").Visibility = Visibility.Visible;
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
            var paletas = A<Panel>("paletas");
            for (int i = 0; i < Tema.Paletas.Length; i++)
            {
                var r = new RadioButton
                {
                    GroupName = "paleta", Tag = i, Content = Tema.Paletas[i].Nome,
                    Style = (Style)janela.FindResource("Amostra"),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Tema.Paletas[i].P)),
                    ToolTip = Tema.Paletas[i].Nome
                };
                if (i == config.Paleta) r.IsChecked = true;
                r.Checked += (s, e) => { config.Paleta = Convert.ToInt32(((RadioButton)s).Tag); config.Salvar(); AplicarTema(); };
                paletas.Children.Add(r);
            }
            ConfigurarLimpeza();
            ConfigurarAgenda();
            var dias = A<ComboBox>("cmbDiasLembrete");
            foreach (ComboBoxItem it in dias.Items) if (Convert.ToInt32(it.Tag) == config.LembreteDias) dias.SelectedItem = it;
            dias.SelectionChanged += (s, e) => { var it = dias.SelectedItem as ComboBoxItem; if (it != null) { config.LembreteDias = Convert.ToInt32(it.Tag); config.Salvar(); } };
            A<Button>("btnEscolherCfg").Click += (s, e) => { var p = SeletorPasta.Escolher(janela, "Escolha a pasta padrão dos backups", vm.Destino); if (p != null) vm.Destino = p; };
            A<Button>("btnVerificarAgora").Click += (s, e) => VerificarAtualizacao(true);
            A<Button>("btnAtualizarAgora").Click += (s, e) => AtualizarAgora();
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
                    indiceBusca = null;
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
                case TipoLog.Titulo: icone = "▸"; cor = "PrimariaTexto"; peso = FontWeights.SemiBold; margem = new Thickness(0, 8, 0, 2); break;
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
