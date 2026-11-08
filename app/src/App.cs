// PJe-Calc Backup - janela principal (WPF) e ligação com o motor.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

[assembly: AssemblyTitle("PJe-Calc Backup")]
[assembly: AssemblyProduct("PJe-Calc Backup")]
[assembly: AssemblyDescription("Backup e restauração dos cálculos do PJe-Calc Cidadão (projeto independente, não oficial)")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace PjeCalcBackup
{
    // ------------------------------------------------------------ modelos de tela

    public abstract class Notificavel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Avisar(string nome)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(nome));
        }
    }

    public class InstalacaoVM : Notificavel
    {
        public Instalacao Inst { get; private set; }
        public InstalacaoVM(Instalacao i) { Inst = i; }

        public string Titulo { get { return "PJe-Calc " + Inst.Versao; } }
        public string Pasta { get { return Inst.Pasta; } }
        public string PastaEntreParenteses { get { return "   " + Inst.Pasta; } }

        private bool selecionada = true;
        public bool Selecionada { get { return selecionada; } set { selecionada = value; Avisar("Selecionada"); } }

        private EstadoInstalacao estado;
        public EstadoInstalacao Estado
        {
            get { return estado; }
            set { estado = value; Avisar("Estado"); Avisar("TextoEstado"); Avisar("CorEstadoFundo"); Avisar("CorEstadoTexto"); Avisar("Selecionavel"); }
        }
        public bool Selecionavel { get { return estado != EstadoInstalacao.SemBanco; } }

        public string TextoEstado
        {
            get { return estado == EstadoInstalacao.Aberta ? "Aberta agora" : estado == EstadoInstalacao.SemBanco ? "Sem cálculos" : "Fechada"; }
        }
        public Brush CorEstadoFundo
        {
            get { return Cor(estado == EstadoInstalacao.Aberta ? "#FFF4E0" : estado == EstadoInstalacao.SemBanco ? "#EEF0F3" : "#E5F4EA"); }
        }
        public Brush CorEstadoTexto
        {
            get { return Cor(estado == EstadoInstalacao.Aberta ? "#9A5B00" : estado == EstadoInstalacao.SemBanco ? "#6B7685" : "#1E7A3D"); }
        }

        private string textoCalculos = "";
        public string TextoCalculos { get { return textoCalculos; } set { textoCalculos = value; Avisar("TextoCalculos"); } }

        public static Brush Cor(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
    }

    public class ArquivoPjcVM : Notificavel
    {
        public string Caminho { get; set; }
        public string Nome { get { return Path.GetFileName(Caminho); } }
        public string Origem { get { return Path.GetDirectoryName(Caminho); } }
        public string Tamanho { get; set; }
        private bool selecionado = true;
        public bool Selecionado { get { return selecionado; } set { selecionado = value; Avisar("Selecionado"); } }
    }

    public class LinhaLog
    {
        public string Icone { get; set; }
        public string Texto { get; set; }
        public Brush Cor { get; set; }
        public FontWeight Peso { get; set; }
        public Thickness Margem { get; set; }
    }

    public class JanelaVM : Notificavel
    {
        public ObservableCollection<InstalacaoVM> Instalacoes { get; private set; }
        public ObservableCollection<InstalacaoVM> InstalacoesComBanco { get; private set; }
        public ObservableCollection<ArquivoPjcVM> ArquivosPjc { get; private set; }
        public ObservableCollection<LinhaLog> Log { get; private set; }
        public ObservableCollection<LinhaLog> LogRestauracao { get; private set; }
        private string destino;
        public string Destino { get { return destino; } set { destino = value; Avisar("Destino"); } }
        private bool exportarPjc = true, copiarBanco = true;
        public bool ExportarPjc { get { return exportarPjc; } set { exportarPjc = value; Avisar("ExportarPjc"); } }
        public bool CopiarBanco { get { return copiarBanco; } set { copiarBanco = value; Avisar("CopiarBanco"); } }

        public JanelaVM()
        {
            Instalacoes = new ObservableCollection<InstalacaoVM>();
            InstalacoesComBanco = new ObservableCollection<InstalacaoVM>();
            ArquivosPjc = new ObservableCollection<ArquivoPjcVM>();
            Log = new ObservableCollection<LinhaLog>();
            LogRestauracao = new ObservableCollection<LinhaLog>();
        }
    }

    // ------------------------------------------------------------ aplicativo

    public class Programa : IRetornoBackup
    {
        private const string UrlProjeto = "https://github.com/lfeliipeadv-stack/pjecalc-backup";
        private const string Titulo = "PJe-Calc Backup";

        private Window janela;
        private readonly JanelaVM vm = new JanelaVM();
        private Config config;
        private volatile bool cancelado, executando;
        private CancellationTokenSource buscaCts;

        private Button btnBackup, btnProcurar, btnAdicionar, btnEscolher, btnAbrirPasta, btnRestaurar, btnArquivosPjc, btnPastaPjc, btnLimparPjc;
        private RadioButton abaBackup, abaRestaurar;
        private Grid painelBackup, painelRestaurar;
        private ComboBox cmbDestino;
        private TextBlock txtProcurando, txtSemPjc, txtResumoPjc;

        // destino atual das mensagens de progresso (aba de backup ou de restauração)
        private ObservableCollection<LinhaLog> logAtual;
        private ScrollViewer rolagemAtual;
        private ProgressBar barraAtual;
        private TextBlock etapaAtual;
        private Button botaoAtual;
        private string textoBotaoAtual;

        [STAThread]
        public static void Main()
        {
            var app = new Application();
            app.DispatcherUnhandledException += (s, e) =>
            {
                Dialogo.Mostrar(null, TipoDialogo.Erro, "Ocorreu um erro inesperado", e.Exception.Message, "Fechar", null);
                e.Handled = true;
            };
            var p = new Programa();
            p.Criar();
            app.Run(p.janela);
        }

        private static Stream Recurso(string nome)
        {
            var asm = Assembly.GetExecutingAssembly();
            string n = asm.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith(nome, StringComparison.OrdinalIgnoreCase));
            return n == null ? null : asm.GetManifestResourceStream(n);
        }

        private T Achar<T>(string nome) where T : class { return janela.FindName(nome) as T; }

        private bool Perguntar(TipoDialogo tipo, string titulo, string msg, string sim, string nao) { return Dialogo.Mostrar(janela, tipo, titulo, msg, sim, nao); }
        private void Avisar(TipoDialogo tipo, string titulo, string msg) { Dialogo.Mostrar(janela, tipo, titulo, msg, "OK", null); }

        private void Criar()
        {
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

            btnBackup = Achar<Button>("btnBackup");
            btnProcurar = Achar<Button>("btnProcurar");
            btnAdicionar = Achar<Button>("btnAdicionar");
            btnEscolher = Achar<Button>("btnEscolher");
            btnAbrirPasta = Achar<Button>("btnAbrirPasta");
            btnRestaurar = Achar<Button>("btnRestaurar");
            btnArquivosPjc = Achar<Button>("btnArquivosPjc");
            btnPastaPjc = Achar<Button>("btnPastaPjc");
            btnLimparPjc = Achar<Button>("btnLimparPjc");
            abaBackup = Achar<RadioButton>("abaBackup");
            abaRestaurar = Achar<RadioButton>("abaRestaurar");
            painelBackup = Achar<Grid>("painelBackup");
            painelRestaurar = Achar<Grid>("painelRestaurar");
            cmbDestino = Achar<ComboBox>("cmbDestino");
            txtProcurando = Achar<TextBlock>("txtProcurando");
            txtSemPjc = Achar<TextBlock>("txtSemPjc");
            txtResumoPjc = Achar<TextBlock>("txtResumoPjc");
            var lnk = Achar<Hyperlink>("lnkGithub");

            config = Config.Carregar();
            vm.Destino = config.Destino;
            vm.ExportarPjc = config.ExportarPjc;
            vm.CopiarBanco = config.CopiarBanco;

            abaBackup.Checked += (s, e) => { painelBackup.Visibility = Visibility.Visible; painelRestaurar.Visibility = Visibility.Collapsed; };
            abaRestaurar.Checked += (s, e) => { painelBackup.Visibility = Visibility.Collapsed; painelRestaurar.Visibility = Visibility.Visible; };

            btnBackup.Click += (s, e) => { if (executando) PedirCancelamento(); else IniciarBackup(); };
            btnRestaurar.Click += (s, e) => { if (executando) PedirCancelamento(); else IniciarRestauracao(); };
            btnProcurar.Click += (s, e) => ProcurarInstalacoes();
            btnAdicionar.Click += (s, e) => AdicionarPasta();
            btnEscolher.Click += (s, e) =>
            {
                string p = SeletorPasta.Escolher(janela, "Escolha onde salvar os backups", vm.Destino);
                if (p != null) vm.Destino = p;
            };
            btnAbrirPasta.Click += (s, e) =>
            {
                try { Directory.CreateDirectory(vm.Destino); Process.Start("explorer.exe", "\"" + vm.Destino + "\""); }
                catch (Exception ex) { Avisar(TipoDialogo.Erro, "Não consegui abrir a pasta", ex.Message); }
            };
            btnArquivosPjc.Click += (s, e) => EscolherArquivosPjc();
            btnPastaPjc.Click += (s, e) => EscolherPastaPjc();
            btnLimparPjc.Click += (s, e) => { vm.ArquivosPjc.Clear(); AtualizarResumoPjc(); };
            if (lnk != null) lnk.Click += (s, e) => { try { Process.Start(UrlProjeto); } catch { } };

            janela.Closing += (s, e) =>
            {
                if (executando)
                {
                    e.Cancel = true;
                    if (Perguntar(TipoDialogo.Pergunta, "Operação em andamento",
                        "Deseja cancelar?\n\nO programa termina o cálculo atual, fecha com segurança o PJe-Calc que abriu em segundo plano e depois pode ser fechado.",
                        "Cancelar a operação", "Continuar"))
                        PedirCancelamento();
                    return;
                }
                SalvarConfig();
            };

            janela.Loaded += (s, e) => ProcurarInstalacoes();
            AtualizarResumoPjc();
        }

        private void SalvarConfig()
        {
            config.Destino = vm.Destino;
            config.ExportarPjc = vm.ExportarPjc;
            config.CopiarBanco = vm.CopiarBanco;
            config.Desmarcadas = vm.Instalacoes.Where(i => !i.Selecionada).Select(i => i.Pasta).ToList();
            config.Salvar();
        }

        // -------------------------------------------------------- busca de instalações

        private void ProcurarInstalacoes()
        {
            if (executando) return;
            if (buscaCts != null) buscaCts.Cancel();
            var cts = new CancellationTokenSource();
            buscaCts = cts;
            vm.Instalacoes.Clear();
            vm.InstalacoesComBanco.Clear();
            txtProcurando.Visibility = Visibility.Visible;
            txtProcurando.Text = "Procurando instalações do PJe-Calc no computador…";
            btnProcurar.IsEnabled = false;
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
                        vmI.TextoCalculos = vmI.Estado == EstadoInstalacao.Fechada ? "contando…" : "";
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
                    if (cmbDestino.SelectedIndex < 0 && vm.InstalacoesComBanco.Count > 0) cmbDestino.SelectedIndex = 0;
                    txtProcurando.Visibility = itens.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    txtProcurando.Text = "Nenhuma instalação do PJe-Calc encontrada. Use \"Adicionar pasta…\" para indicar a pasta da versão.";
                    btnProcurar.IsEnabled = true;
                }));

                foreach (var i in itens.Where(x => x.Estado == EstadoInstalacao.Fechada))
                {
                    if (cts.IsCancellationRequested) return;
                    int? n = i.Inst.ContarCalculos();
                    var alvo = i;
                    janela.Dispatcher.BeginInvoke(new Action(() =>
                        alvo.TextoCalculos = n.HasValue ? (n.Value == 1 ? "1 cálculo" : n.Value + " cálculos") : ""));
                }
            }, TaskCreationOptions.LongRunning);
        }

        private void AtualizarEstados()
        {
            Task.Factory.StartNew(() =>
            {
                foreach (var i in vm.Instalacoes.ToList())
                {
                    EstadoInstalacao e;
                    try { e = i.Inst.Estado(); } catch { continue; }
                    int? n = e == EstadoInstalacao.Fechada ? i.Inst.ContarCalculos() : null;
                    var alvo = i;
                    janela.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        alvo.Estado = e;
                        alvo.TextoCalculos = n.HasValue ? (n.Value == 1 ? "1 cálculo" : n.Value + " cálculos") : "";
                    }));
                }
            });
        }

        private static string VersaoOrdenavel(string v)
        {
            return string.Join(".", (v ?? "").Split('.').Select(p => p.PadLeft(5, '0')));
        }

        private void AdicionarPasta()
        {
            string p = SeletorPasta.Escolher(janela, "Escolha a pasta da versão do PJe-Calc (onde fica o iniciarPjeCalc.bat)", null);
            if (p == null) return;
            var achadas = Instalacao.PareceInstalacao(p) ? new List<string> { p } : Instalacao.Procurar(new[] { p }, null)
                .Where(a => a.StartsWith(p, StringComparison.OrdinalIgnoreCase)).ToList();
            if (achadas.Count == 0)
            {
                Avisar(TipoDialogo.Info, "PJe-Calc não encontrado nessa pasta",
                    "Escolha a pasta que contém o arquivo iniciarPjeCalc.bat e as pastas bin e tomcat.");
                return;
            }
            foreach (var a in achadas)
                if (!config.PastasExtras.Contains(a, StringComparer.OrdinalIgnoreCase)) config.PastasExtras.Add(a);
            config.Salvar();
            ProcurarInstalacoes();
        }

        /// <summary>Texto sobre versões abertas, para os avisos iniciais.</summary>
        private string TextoVersoesAbertas()
        {
            // verifica na hora (o estado na tela pode estar desatualizado)
            var abertas = new List<string>();
            foreach (int porta in vm.Instalacoes.Select(i => i.Inst.PortaHttp).Distinct())
            {
                string dono = Instalacao.InstalacaoNaPorta(porta);
                if (dono == null) continue;
                var vmI = vm.Instalacoes.FirstOrDefault(i => string.Equals(i.Pasta, dono, StringComparison.OrdinalIgnoreCase));
                string nome = vmI != null ? vmI.Titulo : "PJe-Calc (" + dono + ")";
                if (!abertas.Contains(nome)) abertas.Add(nome);
            }
            if (abertas.Count == 0) return "";
            return "\n\nO " + string.Join(" e o ", abertas) + (abertas.Count == 1 ? " está aberto e será fechado" : " estão abertos e serão fechados") +
                   " automaticamente, com segurança (como o \"Sair\" do ícone na bandeja). No final, o programa o reabre.";
        }

        // -------------------------------------------------------------------- backup

        private void IniciarBackup()
        {
            var selecionadas = vm.Instalacoes.Where(i => i.Selecionada && i.Selecionavel).Select(i => i.Inst).ToList();
            if (selecionadas.Count == 0) { Avisar(TipoDialogo.Info, "Nenhuma versão marcada", "Marque pelo menos uma versão do PJe-Calc."); return; }
            if (!vm.ExportarPjc && !vm.CopiarBanco) { Avisar(TipoDialogo.Info, "Nada para fazer", "Escolha exportar os cálculos, copiar o banco, ou os dois."); return; }
            if (!DestinoValido()) return;
            if (vm.ExportarPjc && Navegador.Localizar() == null)
            {
                Avisar(TipoDialogo.Aviso, "Navegador não encontrado", "Para exportar em .PJC é preciso ter o Microsoft Edge ou o Google Chrome instalado.");
                return;
            }

            if (!Perguntar(TipoDialogo.Aviso, "Antes de começar",
                "Salve e feche os cálculos que estiver editando no PJe-Calc, para não perder nenhuma alteração." + TextoVersoesAbertas() +
                "\n\nDurante o backup, não abra o PJe-Calc.",
                "Já salvei, fazer backup", "Cancelar"))
                return;

            SalvarConfig();
            var op = new OpcoesBackup { Destino = vm.Destino, ExportarPjc = vm.ExportarPjc, CopiarBanco = vm.CopiarBanco };
            PrepararExecucao(vm.Log, Achar<ScrollViewer>("rolagemLog"), Achar<ProgressBar>("barra"), Achar<TextBlock>("txtEtapa"), btnBackup, "Fazer backup");

            Task.Factory.StartNew(() =>
            {
                ResultadoBackup res = null; Exception erro = null;
                try { res = Backup.Executar(selecionadas, op, this); }
                catch (Exception ex) { erro = ex; }
                janela.Dispatcher.BeginInvoke(new Action(() =>
                {
                    EncerrarExecucao();
                    if (erro != null) { Log(TipoLog.Erro, erro.Message); Avisar(TipoDialogo.Erro, "O backup foi interrompido", erro.Message); return; }
                    string msg = res.Exportados + " cálculo(s) exportado(s) em .PJC\n" + res.Bancos + " banco(s) de dados copiado(s)" +
                                 (res.Falhas > 0 ? "\n" + res.Falhas + " falha(s) — veja o relatorio.txt" : "") +
                                 (res.Reaberto != null ? "\n\nO " + res.Reaberto + " foi reaberto, como estava antes." : "");
                    if (Perguntar(res.Falhas == 0 ? TipoDialogo.Sucesso : TipoDialogo.Aviso,
                        res.Falhas == 0 ? "Backup concluído!" : "Backup concluído com falhas", msg, "Abrir a pasta", "Fechar"))
                    {
                        try { Process.Start("explorer.exe", "\"" + res.Pasta + "\""); } catch { }
                    }
                }));
            }, TaskCreationOptions.LongRunning);
        }

        private bool DestinoValido()
        {
            if (string.IsNullOrWhiteSpace(vm.Destino)) { Avisar(TipoDialogo.Info, "Escolha a pasta de backups", "Informe onde salvar os backups (aba Fazer backup → Onde salvar)."); return false; }
            try { Directory.CreateDirectory(vm.Destino); return true; }
            catch (Exception ex) { Avisar(TipoDialogo.Aviso, "Não consegui usar a pasta de destino", ex.Message); return false; }
        }

        // -------------------------------------------------------------- restauração

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
            txtSemPjc.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
            txtResumoPjc.Text = n == 0 ? "Escolha arquivos .PJC ou uma pasta de backup." : n + " arquivo(s). Desmarque os que não quiser restaurar.";
        }

        private void EscolherArquivosPjc()
        {
            var d = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Escolha os arquivos .PJC para restaurar",
                Filter = "Cálculos do PJe-Calc (*.PJC)|*.PJC;*.pjc",
                Multiselect = true
            };
            if (!string.IsNullOrEmpty(config.UltimaPastaPjc) && Directory.Exists(config.UltimaPastaPjc)) d.InitialDirectory = config.UltimaPastaPjc;
            else if (Directory.Exists(vm.Destino)) d.InitialDirectory = vm.Destino;
            if (d.ShowDialog(janela) == true)
            {
                config.UltimaPastaPjc = Path.GetDirectoryName(d.FileNames[0]);
                AdicionarArquivosPjc(d.FileNames);
            }
        }

        private void EscolherPastaPjc()
        {
            string p = SeletorPasta.Escolher(janela, "Escolha a pasta com os arquivos .PJC (inclui subpastas)",
                Directory.Exists(config.UltimaPastaPjc ?? "") ? config.UltimaPastaPjc : vm.Destino);
            if (p == null) return;
            config.UltimaPastaPjc = p;
            string[] arqs;
            try { arqs = Directory.GetFiles(p, "*.PJC", SearchOption.AllDirectories); }
            catch (Exception ex) { Avisar(TipoDialogo.Erro, "Não consegui ler a pasta", ex.Message); return; }
            if (arqs.Length == 0) { Avisar(TipoDialogo.Info, "Nenhum arquivo .PJC", "Não encontrei arquivos .PJC nessa pasta nem nas subpastas."); return; }
            AdicionarArquivosPjc(arqs);
        }

        private void IniciarRestauracao()
        {
            var arquivos = vm.ArquivosPjc.Where(a => a.Selecionado).Select(a => a.Caminho).Where(File.Exists).ToList();
            if (arquivos.Count == 0) { Avisar(TipoDialogo.Info, "Nenhum arquivo para restaurar", "Escolha arquivos .PJC (ou uma pasta de backup) e deixe marcados os que quer restaurar."); return; }
            var destino = cmbDestino.SelectedItem as InstalacaoVM;
            if (destino == null) { Avisar(TipoDialogo.Info, "Escolha a versão", "Escolha em qual versão do PJe-Calc os cálculos serão restaurados."); return; }
            if (!DestinoValido()) return;
            if (Navegador.Localizar() == null)
            {
                Avisar(TipoDialogo.Aviso, "Navegador não encontrado", "Para restaurar é preciso ter o Microsoft Edge ou o Google Chrome instalado.");
                return;
            }

            if (!Perguntar(TipoDialogo.Aviso, "Antes de restaurar",
                "Salve e feche os cálculos que estiver editando no PJe-Calc, para não perder nenhuma alteração." + TextoVersoesAbertas() +
                "\n\n" + arquivos.Count + " arquivo(s) serão importados no " + destino.Titulo + ". Cada um vira um cálculo NOVO (com número novo); se já existir, ficará duplicado." +
                "\n\nAntes de importar, uma cópia do banco atual será guardada em:\n" + vm.Destino,
                "Já salvei, restaurar", "Cancelar"))
                return;

            SalvarConfig();
            var inst = destino.Inst;
            string pastaBackups = vm.Destino;
            PrepararExecucao(vm.LogRestauracao, Achar<ScrollViewer>("rolagemLogR"), Achar<ProgressBar>("barraR"), Achar<TextBlock>("txtEtapaR"), btnRestaurar, "Restaurar");

            Task.Factory.StartNew(() =>
            {
                ResultadoBackup res = null; Exception erro = null;
                try { res = Backup.Restaurar(inst, arquivos, pastaBackups, this); }
                catch (Exception ex) { erro = ex; }
                janela.Dispatcher.BeginInvoke(new Action(() =>
                {
                    EncerrarExecucao();
                    if (erro != null) { Log(TipoLog.Erro, erro.Message); Avisar(TipoDialogo.Erro, "A restauração foi interrompida", erro.Message); return; }
                    string msg = res.Importados + " cálculo(s) importado(s) no PJe-Calc " + inst.Versao +
                                 (res.Falhas > 0 ? "\n" + res.Falhas + " falha(s) — veja a lista na tela" : "") +
                                 "\n\nCópia do banco anterior (para desfazer):\n" + res.Pasta +
                                 (res.Reaberto != null ? "\n\nO " + res.Reaberto + " foi reaberto, como estava antes." : "");
                    Perguntar(res.Falhas == 0 && res.Importados > 0 ? TipoDialogo.Sucesso : TipoDialogo.Aviso,
                        res.Falhas == 0 && res.Importados > 0 ? "Restauração concluída!" : "Restauração com falhas", msg, "OK", null);
                }));
            }, TaskCreationOptions.LongRunning);
        }

        // -------------------------------------------------------- execução (comum)

        private void PrepararExecucao(ObservableCollection<LinhaLog> log, ScrollViewer rolagem, ProgressBar barra, TextBlock etapa, Button botao, string textoBotao)
        {
            logAtual = log; rolagemAtual = rolagem; barraAtual = barra; etapaAtual = etapa; botaoAtual = botao; textoBotaoAtual = textoBotao;
            log.Clear();
            cancelado = false;
            executando = true;
            botao.Content = "Cancelar";
            barra.Value = 0;
            etapa.Text = "Iniciando…";
            foreach (var b in new[] { btnProcurar, btnAdicionar, btnEscolher, btnArquivosPjc, btnPastaPjc, btnLimparPjc })
                b.IsEnabled = false;
            (botao == btnBackup ? btnRestaurar : btnBackup).IsEnabled = false;
            abaBackup.IsEnabled = botao == btnBackup;
            abaRestaurar.IsEnabled = botao == btnRestaurar;
            Achar<ItemsControl>("lstVersoes").IsEnabled = false;
            cmbDestino.IsEnabled = false;
        }

        private void EncerrarExecucao()
        {
            executando = false;
            botaoAtual.Content = textoBotaoAtual;
            foreach (var b in new[] { btnBackup, btnRestaurar, btnProcurar, btnAdicionar, btnEscolher, btnArquivosPjc, btnPastaPjc, btnLimparPjc })
                b.IsEnabled = true;
            abaBackup.IsEnabled = abaRestaurar.IsEnabled = true;
            Achar<ItemsControl>("lstVersoes").IsEnabled = true;
            cmbDestino.IsEnabled = true;
            AtualizarEstados();
        }

        private void PedirCancelamento()
        {
            cancelado = true;
            botaoAtual.IsEnabled = false;
            botaoAtual.Content = "Cancelando…";
            etapaAtual.Text = "Cancelando: terminando o item atual e fechando o PJe-Calc com segurança…";
        }

        // ------------------------------------------------------------ IRetornoBackup

        public bool Cancelado { get { return cancelado; } }

        public void Log(TipoLog tipo, string texto)
        {
            string icone; string cor; FontWeight peso = FontWeights.Normal; Thickness margem = new Thickness(0, 1, 0, 1);
            switch (tipo)
            {
                case TipoLog.Ok: icone = "✓"; cor = "#1E7A3D"; break;
                case TipoLog.Erro: icone = "✗"; cor = "#B42318"; break;
                case TipoLog.Aviso: icone = "!"; cor = "#9A5B00"; break;
                case TipoLog.Titulo: icone = "▸"; cor = "#23476F"; peso = FontWeights.SemiBold; margem = new Thickness(0, 8, 0, 2); break;
                default: icone = "•"; cor = "#5B6775"; break;
            }
            var linha = new LinhaLog { Icone = icone, Texto = texto, Cor = InstalacaoVM.Cor(cor), Peso = peso, Margem = margem };
            janela.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (logAtual == null) return;
                logAtual.Add(linha);
                rolagemAtual.ScrollToEnd();
            }));
        }

        public void Progresso(double fracao, string etapa)
        {
            janela.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (barraAtual == null) return;
                if (fracao >= 0) barraAtual.Value = Math.Min(1, fracao);
                if (!cancelado) etapaAtual.Text = etapa;
            }));
        }

        public bool PedirParaFechar(string descricao)
        {
            bool tentar = false;
            janela.Dispatcher.Invoke(new Action(() =>
            {
                tentar = Perguntar(TipoDialogo.Aviso, "Feche o PJe-Calc aberto",
                    descricao + "\n\nFeche-o pelo ícone na bandeja do Windows (perto do relógio) → Sair, e clique em \"Já fechei\".",
                    "Já fechei", "Pular");
            }));
            if (tentar) Thread.Sleep(2000);
            return tentar;
        }
    }
}
