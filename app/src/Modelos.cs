// Modelos usados pela janela (o que aparece nas listas) e o controle de "etapas" (assistente).
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PjeCalcBackup
{
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
        public string TextoEstado { get { return estado == EstadoInstalacao.Aberta ? "Aberta agora" : estado == EstadoInstalacao.SemBanco ? "Sem cálculos" : "Fechada"; } }
        public Brush CorEstadoFundo { get { return Tema.Recurso(estado == EstadoInstalacao.Aberta ? "AvisoFundo" : estado == EstadoInstalacao.SemBanco ? "NeutroFundo" : "OkFundo"); } }
        public Brush CorEstadoTexto { get { return Tema.Recurso(estado == EstadoInstalacao.Aberta ? "Aviso" : estado == EstadoInstalacao.SemBanco ? "Neutro" : "Ok"); } }

        private string textoCalculos = "";
        public string TextoCalculos { get { return textoCalculos; } set { textoCalculos = value; Avisar("TextoCalculos"); } }

        public void AtualizarCores() { Avisar("CorEstadoFundo"); Avisar("CorEstadoTexto"); }
    }

    /// <summary>Linha da lista de cálculos: um cabeçalho de grupo (versão) ou um cálculo.</summary>
    public class CalcItemVM : Notificavel
    {
        public bool EhGrupo;
        public string Titulo { get; set; }
        public string Detalhe { get; set; }
        public string Numero;
        public string PastaInstalacao;
        public string Arquivo;
        public CalculoInfo Info;
        public CalcItemVM Grupo;
        public List<CalcItemVM> Filhos = new List<CalcItemVM>();

        public Visibility VisGrupo { get { return EhGrupo ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility VisItem { get { return EhGrupo ? Visibility.Collapsed : Visibility.Visible; } }

        private string textoDuplicado;
        public string TextoDuplicado { get { return textoDuplicado; } set { textoDuplicado = value; Avisar("TextoDuplicado"); Avisar("VisDuplicado"); } }
        public Visibility VisDuplicado { get { return string.IsNullOrEmpty(textoDuplicado) ? Visibility.Collapsed : Visibility.Visible; } }

        private bool selecionado = true;
        private bool propagando;
        public bool Selecionado
        {
            get { return selecionado; }
            set
            {
                if (selecionado == value) return;
                selecionado = value;
                Avisar("Selecionado");
                if (EhGrupo && !propagando)
                {
                    foreach (var f in Filhos) { f.propagando = true; f.Selecionado = value; f.propagando = false; }
                }
                else if (!EhGrupo && Grupo != null && !propagando)
                {
                    Grupo.propagando = true;
                    Grupo.Selecionado = Grupo.Filhos.All(x => x.Selecionado);
                    Grupo.propagando = false;
                }
                if (Grupo != null) Grupo.Avisar("ResumoGrupo");
                if (EhGrupo) Avisar("ResumoGrupo");
            }
        }

        public void AtualizarResumo() { Avisar("ResumoGrupo"); }
        /// <summary>Ajusta a caixa do cabeçalho conforme os itens, sem mexer nos itens.</summary>
        public void SincronizarGrupo() { propagando = true; Selecionado = Filhos.All(f => f.Selecionado); propagando = false; Avisar("ResumoGrupo"); }

        // cabeçalho de versão recolhível
        private bool expandido;
        public bool Expandido
        {
            get { return expandido; }
            set { expandido = value; Avisar("Expandido"); Avisar("TextoExpandir"); }
        }
        public string TextoExpandir { get { return expandido ? "Ocultar cálculos  ▴" : "Mostrar cálculos  ▾"; } }
        public string ResumoGrupo
        {
            get
            {
                int n = Filhos.Count, m = Filhos.Count(f => f.Selecionado);
                return n + " cálculo(s)   ·   " + (n == 0 ? "nenhum cálculo" : m == n ? "todos marcados" : m == 0 ? "nenhum marcado" : m + " de " + n + " marcados");
            }
        }

        public bool Corresponde(string filtro)
        {
            if (string.IsNullOrWhiteSpace(filtro)) return true;
            string f = CalculoInfo.Normalizar(filtro);
            if (EhGrupo) return Filhos.Any(x => x.Corresponde(filtro));
            return CalculoInfo.Normalizar(Titulo + " " + Detalhe + " " + Path.GetFileName(Arquivo ?? "")).Contains(f);
        }
    }

    public class ArquivoPjcVM : Notificavel
    {
        public string Caminho { get; set; }
        public string Nome { get { return Path.GetFileName(Caminho); } }
        public string Origem { get { return Path.GetDirectoryName(Caminho); } }
        public string Tamanho { get; set; }

        // agrupamento por versão (o cabeçalho é um ArquivoPjcVM com EhGrupo = true)
        public bool EhGrupo;
        public string Titulo { get; set; }
        public string Versao;
        public ArquivoPjcVM Grupo;
        public List<ArquivoPjcVM> Filhos = new List<ArquivoPjcVM>();
        public Visibility VisGrupo { get { return EhGrupo ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility VisItem { get { return EhGrupo ? Visibility.Collapsed : Visibility.Visible; } }

        private bool selecionado = true, propagando;
        public bool Selecionado
        {
            get { return selecionado; }
            set
            {
                if (selecionado == value) return;
                selecionado = value; Avisar("Selecionado");
                if (EhGrupo && !propagando) foreach (var f in Filhos) { f.propagando = true; f.Selecionado = value; f.propagando = false; }
                else if (!EhGrupo && Grupo != null && !propagando)
                {
                    Grupo.propagando = true; Grupo.Selecionado = Grupo.Filhos.All(x => x.Selecionado); Grupo.propagando = false;
                }
                if (Grupo != null) Grupo.Avisar("ResumoGrupo");
                if (EhGrupo) Avisar("ResumoGrupo");
            }
        }

        private bool expandido;
        public bool Expandido { get { return expandido; } set { expandido = value; Avisar("Expandido"); Avisar("TextoExpandir"); } }
        public string TextoExpandir { get { return expandido ? "Ocultar arquivos  ▴" : "Mostrar arquivos  ▾"; } }
        public void SincronizarGrupo() { propagando = true; Selecionado = Filhos.All(f => f.Selecionado); propagando = false; Avisar("ResumoGrupo"); }
        public string ResumoGrupo
        {
            get
            {
                int n = Filhos.Count, m = Filhos.Count(f => f.Selecionado);
                return n + " arquivo(s)   ·   " + (m == n ? "todos marcados" : m == 0 ? "nenhum marcado" : m + " de " + n + " marcados");
            }
        }
    }

    public static class VersaoPorPasta
    {
        /// <summary>Versão do PJe-Calc pelo nome das pastas ("PJe-Calc 2.16.0", "Exportados 2.14.0", "… (PJe-Calc 2.16.0)"). Vazio se não der para saber.</summary>
        public static string De(string arquivo)
        {
            var ms = System.Text.RegularExpressions.Regex.Matches(Path.GetDirectoryName(arquivo) ?? "",
                "(?:PJe-Calc|Exportados|Banco)\\s+(\\d+\\.\\d+(?:\\.\\d+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return ms.Count > 0 ? ms[ms.Count - 1].Groups[1].Value : "";
        }

        public static string Ordenavel(string v)
        {
            return string.Join(".", (v ?? "").Split('.').Select(x => x.PadLeft(5, '0')));
        }
    }

    public class RegistroVM
    {
        public RegistroHistorico R { get; private set; }
        public RegistroVM(RegistroHistorico r) { R = r; }
        public string DataTexto { get { return R.Data.ToString("dd/MM/yyyy HH:mm"); } }
        public string TipoTexto { get { return "   —   " + R.TipoTexto; } }
        public string Detalhe
        {
            get
            {
                string v = R.Versoes.Count == 0 ? "" : "PJe-Calc " + string.Join(", ", R.Versoes.Select(x => x.Versao).Distinct()) + "   ·   ";
                string c = R.Calculos + " cálculo(s)";
                var conf = R.Versoes.Where(x => x.Exportados > 0).ToList();
                if (R.Tipo == "backup" && conf.Count > 0)
                    c += " · " + conf.Sum(x => x.Conferidos) + " conferido(s)";
                if (R.Versoes.Any(x => x.BancoCopiado)) c += " · banco copiado";
                if (R.Falhas > 0) c += " · " + R.Falhas + " falha(s)";
                return v + c + "\n" + R.PastaOperacao;
            }
        }
        public Brush CorStatus { get { return Tema.Recurso(R.Falhas > 0 ? "Aviso" : "Ok"); } }
        public Visibility VisPjc { get { return R.TemPjc ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility VisBanco { get { return R.TemBanco ? Visibility.Visible : Visibility.Collapsed; } }
    }

    public class BuscaVM
    {
        public AchadoBusca A { get; private set; }
        public BuscaVM(AchadoBusca a) { A = a; }
        public string Titulo
        {
            get { return "nº " + A.Info.Numero + "  —  " + (A.Info.Reclamante ?? "?") + (string.IsNullOrEmpty(A.Info.Reclamado) ? "" : "  ×  " + A.Info.Reclamado); }
        }
        public string Detalhe
        {
            get
            {
                var p = new List<string>();
                if (!string.IsNullOrEmpty(A.Info.Processo)) p.Add("Proc. " + A.Info.Processo);
                if (!string.IsNullOrEmpty(A.Versao)) p.Add("PJe-Calc " + A.Versao);
                p.Add(A.TipoOperacao + " de " + A.Data.ToString("dd/MM/yyyy HH:mm"));
                return string.Join("   ·   ", p) + "\n" + Path.GetFileName(A.Arquivo);
            }
        }
    }

    public class LinhaLog
    {
        public string Chave { get; set; }
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
        public ObservableCollection<CalcItemVM> CalculosBackup { get; private set; }
        public ObservableCollection<CalcItemVM> CalculosRestaurar { get; private set; }
        public ObservableCollection<CalcItemVM> CalculosMigrar { get; private set; }
        public ObservableCollection<ArquivoPjcVM> ArquivosPjc { get; private set; }
        public ObservableCollection<LinhaLog> LogBackup { get; private set; }
        public ObservableCollection<LinhaLog> LogRestaurar { get; private set; }
        public ObservableCollection<LinhaLog> LogMigrar { get; private set; }
        public ObservableCollection<RegistroVM> Historico { get; private set; }
        public ObservableCollection<BuscaVM> Busca { get; private set; }

        private readonly Config c;
        public JanelaVM(Config config)
        {
            c = config;
            Instalacoes = new ObservableCollection<InstalacaoVM>();
            InstalacoesComBanco = new ObservableCollection<InstalacaoVM>();
            CalculosBackup = new ObservableCollection<CalcItemVM>();
            CalculosRestaurar = new ObservableCollection<CalcItemVM>();
            CalculosMigrar = new ObservableCollection<CalcItemVM>();
            ArquivosPjc = new ObservableCollection<ArquivoPjcVM>();
            LogBackup = new ObservableCollection<LinhaLog>();
            LogRestaurar = new ObservableCollection<LinhaLog>();
            LogMigrar = new ObservableCollection<LinhaLog>();
            Historico = new ObservableCollection<RegistroVM>();
            Busca = new ObservableCollection<BuscaVM>();
        }

        // opções (gravadas na configuração assim que mudam)
        public string Destino { get { return c.Destino; } set { c.Destino = value; Avisar("Destino"); } }
        public bool ExportarPjc { get { return c.ExportarPjc; } set { c.ExportarPjc = value; Avisar("ExportarPjc"); } }
        public bool CopiarBanco { get { return c.CopiarBanco; } set { c.CopiarBanco = value; Avisar("CopiarBanco"); } }
        public bool BackupRapido { get { return c.BackupRapido; } set { c.BackupRapido = value; Avisar("BackupRapido"); } }
        public bool Sons { get { return c.Sons; } set { c.Sons = value; Avisar("Sons"); } }
        public bool Notificacoes { get { return c.Notificacoes; } set { c.Notificacoes = value; Avisar("Notificacoes"); } }
        public bool Lembrete { get { return c.Lembrete; } set { c.Lembrete = value; Avisar("Lembrete"); } }
        public bool VerificarAtualizacoes { get { return c.VerificarAtualizacoes; } set { c.VerificarAtualizacoes = value; Avisar("VerificarAtualizacoes"); } }
        public bool VerificarPjeCalc { get { return c.VerificarPjeCalc; } set { c.VerificarPjeCalc = value; c.Salvar(); Avisar("VerificarPjeCalc"); } }
        public bool AbrirPastaAoTerminar { get { return c.AbrirPastaAoTerminar; } set { c.AbrirPastaAoTerminar = value; Avisar("AbrirPastaAoTerminar"); } }
        public bool ReabrirPjeCalc { get { return c.ReabrirPjeCalc; } set { c.ReabrirPjeCalc = value; Avisar("ReabrirPjeCalc"); } }
    }

    /// <summary>Controla um fluxo em etapas: indicador de passos, Voltar / Avançar / Concluir.</summary>
    public class Assistente
    {
        private readonly StackPanel indicador;
        private readonly string[] nomes;
        private readonly Func<int, FrameworkElement> painel;
        private readonly IEnumerable<FrameworkElement> todos;
        public readonly Button Voltar, Avancar, Extra;
        private readonly int passoConcluir;
        private readonly string textoConcluir, textoNovo;
        public int Atual { get; private set; }
        public bool Executando, Terminou;

        /// <summary>Validação/preparo ao sair do passo i para o próximo (false = fica).</summary>
        public Func<int, bool> AoAvancar = i => true;
        public Action<int> AoEntrar = i => { };
        public Action Executar = () => { };
        public Action Cancelar = () => { };
        public Action AoAbrirPasta = () => { };

        public Assistente(StackPanel indicador, string[] nomes, Func<int, FrameworkElement> painel, IEnumerable<FrameworkElement> todos,
            Button voltar, Button avancar, Button extra, string textoConcluir, string textoNovo)
        {
            this.indicador = indicador; this.nomes = nomes; this.painel = painel; this.todos = todos;
            Voltar = voltar; Avancar = avancar; Extra = extra;
            passoConcluir = nomes.Length - 2;
            this.textoConcluir = textoConcluir; this.textoNovo = textoNovo;
            voltar.Click += (s, e) => { if (!Executando && Atual > 0 && !Terminou) Ir(Atual - 1); };
            avancar.Click += (s, e) => Clique();
            extra.Click += (s, e) => AoAbrirPasta();
        }

        private void Clique()
        {
            if (Executando) { Cancelar(); return; }
            if (Terminou) { Terminou = false; Ir(0); return; }
            if (!AoAvancar(Atual)) return;
            if (Atual == passoConcluir) { Ir(Atual + 1); Executar(); }
            else Ir(Atual + 1);
        }

        public void Ir(int i)
        {
            Atual = i;
            foreach (var p in todos) p.Visibility = Visibility.Collapsed;
            painel(i).Visibility = Visibility.Visible;
            AtualizarBotoes();
            DesenharIndicador();
            AoEntrar(i);
        }

        public void AtualizarBotoes()
        {
            bool exec = Atual == nomes.Length - 1;
            Voltar.Visibility = Atual > 0 && !exec ? Visibility.Visible : Visibility.Hidden;
            Extra.Visibility = exec && Terminou ? Visibility.Visible : Visibility.Collapsed;
            Avancar.IsEnabled = true;
            if (exec) Avancar.Content = Executando ? "Cancelar" : (Terminou ? textoNovo : "…");
            else Avancar.Content = Atual == passoConcluir ? textoConcluir : "Avançar →";
        }

        public void DesenharIndicador()
        {
            indicador.Children.Clear();
            for (int i = 0; i < nomes.Length; i++)
            {
                bool feito = i < Atual || (Terminou && i == Atual);
                bool atual = i == Atual && !Terminou;
                var circ = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(2), Margin = new Thickness(0, 0, 8, 0) };
                var num = new TextBlock { Text = feito ? "✓" : (i + 1).ToString(), FontWeight = FontWeights.Bold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (feito || atual)
                {
                    circ.SetResourceReference(Border.BackgroundProperty, "Primaria");
                    circ.SetResourceReference(Border.BorderBrushProperty, "Primaria");
                    num.SetResourceReference(TextBlock.ForegroundProperty, "SobrePrimaria");
                }
                else
                {
                    circ.SetResourceReference(Border.BackgroundProperty, "Superficie");
                    circ.SetResourceReference(Border.BorderBrushProperty, "BordaControle");
                    num.SetResourceReference(TextBlock.ForegroundProperty, "TextoSuave");
                }
                circ.Child = num;
                var nome = new TextBlock { Text = nomes[i], VerticalAlignment = VerticalAlignment.Center, FontWeight = atual ? FontWeights.SemiBold : FontWeights.Normal };
                nome.SetResourceReference(TextBlock.ForegroundProperty, atual ? "Texto" : "TextoSuave");
                var item = new StackPanel { Orientation = Orientation.Horizontal };
                item.Children.Add(circ); item.Children.Add(nome);
                indicador.Children.Add(item);
                if (i < nomes.Length - 1)
                {
                    var linha = new Border { Width = 34, Height = 2, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                    linha.SetResourceReference(Border.BackgroundProperty, i < Atual ? "Primaria" : "Borda");
                    indicador.Children.Add(linha);
                }
            }
        }
    }
}
