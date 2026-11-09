// Aba "Numeração": reserva de números do contador de cálculos e mudança do número de cada cálculo.
// O trabalho no banco fica em Numeracao.cs; aqui só a tela.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PjeCalcBackup
{
    /// <summary>Uma linha da lista: número atual → novo número.</summary>
    public class LinhaCalculo : Notificavel
    {
        public CalculoNumerado C { get; private set; }
        public Action Mudou;
        public LinhaCalculo(CalculoNumerado c, bool editavel) { C = c; novo = c.Numero.ToString(CultureInfo.InvariantCulture); Editavel = editavel; }

        public bool Editavel { get; private set; }
        public string NumeroTexto { get { return C.Numero.ToString(CultureInfo.InvariantCulture); } }
        public string NomeCampo { get { return "Novo número do cálculo " + C.Numero; } }

        private string novo;
        public string NovoTexto
        {
            get { return novo; }
            set { novo = value; Avisar("NovoTexto"); AtualizarCores(); var h = Mudou; if (h != null) h(); }
        }

        /// <summary>Define o texto sem avisar a tela a cada linha (usado ao preencher todas de uma vez).</summary>
        public void Definir(string texto) { novo = texto; Avisar("NovoTexto"); AtualizarCores(); }

        public long? Novo
        {
            get
            {
                long n;
                string t = (novo ?? "").Trim();
                if (t.Length == 0 || !t.All(char.IsDigit) || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out n)) return null;
                return n;
            }
        }
        public bool Alterado { get { return Novo.HasValue && Novo.Value != C.Numero; } }

        private bool problema;
        public bool Problema { get { return problema; } set { if (problema != value) { problema = value; AtualizarCores(); } } }

        public Brush Fundo { get { return Tema.Recurso(Problema ? "ErroFundo" : Alterado ? "PrimariaClara" : "SuperficieAlt"); } }
        public Brush Contorno { get { return Tema.Recurso(Problema ? "Erro" : Alterado ? "Primaria" : "BordaSuave"); } }
        public void AtualizarCores() { Avisar("Fundo"); Avisar("Contorno"); }

        public string Titulo
        {
            get
            {
                string a = string.IsNullOrEmpty(C.Reclamante) ? "(sem reclamante)" : C.Reclamante;
                return string.IsNullOrEmpty(C.Reclamado) ? a : a + "  ×  " + C.Reclamado;
            }
        }
        public string Detalhe { get { return string.IsNullOrEmpty(C.Processo) ? "Sem número de processo" : "Proc. " + C.Processo; } }
        public string CriacaoTexto { get { return CalculoInfo.DataBr(C.Criacao); } }
        public Visibility VisExcluido { get { return C.Ativo ? Visibility.Collapsed : Visibility.Visible; } }
    }

    public class PainelNumeracao
    {
        private readonly Window janela;
        private readonly Config config;
        private readonly Action<bool> definirOcupado;   // trava/destrava as outras abas
        private readonly Action aoTerminar;             // a janela relê as instalações (números e quantidades mudaram)
        private readonly ObservableCollection<LinhaCalculo> linhas = new ObservableCollection<LinhaCalculo>();
        private readonly ObservableCollection<LinhaLog> log = new ObservableCollection<LinhaLog>();
        private Instalacao atual;
        private EstadoNumeracao estado;
        private bool aberta, preenchendo;
        private List<Numeracao.MudancaFeita> feitas = new List<Numeracao.MudancaFeita>();
        private int leitura, verificacao;
        private string pastaSeguranca;
        private List<Instalacao> avisoPendentes = new List<Instalacao>();
        private System.ComponentModel.ICollectionView visao;

        /// <summary>Uma mudança no banco está em andamento (a janela não deve fechar).</summary>
        public bool Ocupado { get; private set; }

        public PainelNumeracao(Window janela, Config config, Action<bool> definirOcupado, Action aoTerminar)
        {
            this.janela = janela; this.config = config; this.definirOcupado = definirOcupado; this.aoTerminar = aoTerminar;
            A<ItemsControl>("nuLista").ItemsSource = linhas;
            A<ItemsControl>("nuLog").ItemsSource = log;

            A<ComboBox>("nuVersao").SelectionChanged += (s, e) => { if (Visivel) CarregarSelecionada(); else { atual = null; estado = null; } };
            A<Button>("nuRecarregar").Click += (s, e) => Recarregar();
            A<Button>("nuSequencia").Click += (s, e) => PreencherSequencia();
            A<Button>("nuDesfazerEdicao").Click += (s, e) => DesfazerEdicao();
            A<Button>("nuReserva").Click += (s, e) => CorrigirReserva();
            A<Button>("nuAplicar").Click += (s, e) => Aplicar();
            A<Button>("nuDesfazer").Click += (s, e) => DesfazerUltima();
            A<CheckBox>("nuChkReserva").Checked += (s, e) => Revalidar();
            A<CheckBox>("nuChkReserva").Unchecked += (s, e) => Revalidar();
            A<Button>("nuConcluir").Click += (s, e) => { A<Border>("nuAndamento").Visibility = Visibility.Collapsed; Recarregar(); };
            A<Button>("nuAbrirSeguranca").Click += (s, e) => { try { if (pastaSeguranca != null) Process.Start("explorer.exe", "\"" + pastaSeguranca + "\""); } catch { } };
            A<Button>("nuFazerBackup").Click += (s, e) =>
            {
                A<Border>("nuAndamento").Visibility = Visibility.Collapsed;
                estado = null; // relê ao voltar para a aba
                A<RadioButton>("abaBackup").IsChecked = true;
            };

            // aviso laranja: pode ser recolhido (continua na confirmação de cada mudança)
            if (config.NumeracaoAvisoVisto) A<Border>("nuAvisoBanco").Visibility = Visibility.Collapsed;
            A<Button>("nuFecharAviso").Click += (s, e) => { A<Border>("nuAvisoBanco").Visibility = Visibility.Collapsed; config.NumeracaoAvisoVisto = true; config.Salvar(); };

            // ordem de "Colocar em sequência"
            var ordem = A<ComboBox>("nuOrdem");
            for (int i = 0; i < Numeracao.Ordens.GetLength(0); i++)
            {
                var it = new ComboBoxItem { Content = Numeracao.Ordens[i, 1], Tag = Numeracao.Ordens[i, 0] };
                ordem.Items.Add(it);
                if (Numeracao.Ordens[i, 0] == config.NumeracaoOrdem) ordem.SelectedItem = it;
            }
            if (ordem.SelectedIndex < 0) ordem.SelectedIndex = 0;
            ordem.SelectionChanged += (s, e) => { var it = ordem.SelectedItem as ComboBoxItem; if (it != null) { config.NumeracaoOrdem = (string)it.Tag; config.Salvar(); } };

            // filtro
            visao = System.Windows.Data.CollectionViewSource.GetDefaultView(linhas);
            visao.Filter = o => Combina(o as LinhaCalculo);
            A<TextBox>("nuFiltro").TextChanged += (s, e) => { visao.Refresh(); AtualizarContagem(); };

            // Enter e ↓ vão para o campo do cálculo seguinte, ↑ para o anterior; ao entrar num campo, o número fica selecionado
            var lista = A<ItemsControl>("nuLista");
            lista.PreviewKeyDown += (s, e) =>
            {
                if (!(e.OriginalSource is TextBox)) return;
                System.Windows.Input.FocusNavigationDirection? dir = null;
                if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Down) dir = System.Windows.Input.FocusNavigationDirection.Next;
                else if (e.Key == System.Windows.Input.Key.Up) dir = System.Windows.Input.FocusNavigationDirection.Previous;
                if (dir == null) return;
                ((UIElement)e.OriginalSource).MoveFocus(new System.Windows.Input.TraversalRequest(dir.Value));
                e.Handled = true;
            };
            lista.AddHandler(UIElement.GotKeyboardFocusEvent, new System.Windows.Input.KeyboardFocusChangedEventHandler((s, e) =>
            {
                var tb = e.OriginalSource as TextBox;
                if (tb != null) tb.Dispatcher.BeginInvoke(new Action(tb.SelectAll));
            }));

            // faixa no topo da janela: alguma versão ainda reserva 32 números
            A<Button>("btnVerNumeracao").Click += (s, e) =>
            {
                var alvo = avisoPendentes.FirstOrDefault();
                A<RadioButton>("abaNumeracao").IsChecked = true;
                if (alvo == null) return;
                var combo = A<ComboBox>("nuVersao");
                var item = combo.Items.OfType<InstalacaoVM>().FirstOrDefault(v => string.Equals(v.Pasta, alvo.Pasta, StringComparison.OrdinalIgnoreCase));
                if (item != null && combo.SelectedItem != item) combo.SelectedItem = item;
            };
            A<Button>("btnFecharNumeracao").Click += (s, e) =>
            {
                foreach (var i in avisoPendentes)
                    if (!config.NumeracaoAvisoIgnorado.Contains(i.Pasta, StringComparer.OrdinalIgnoreCase)) config.NumeracaoAvisoIgnorado.Add(i.Pasta);
                config.Salvar();
                avisoPendentes.Clear(); AtualizarFaixa();
            };
        }

        /// <summary>O cálculo aparece com o filtro atual? (nome do reclamante ou do reclamado, número atual ou novo, processo)</summary>
        private bool Combina(LinhaCalculo l)
        {
            if (l == null) return false;
            string f = CalculoInfo.Normalizar(A<TextBox>("nuFiltro").Text);
            if (f.Length == 0) return true;
            string texto = CalculoInfo.Normalizar(l.C.Reclamante + " " + l.C.Reclamado + " " + l.C.Processo) + " " + l.C.Numero + " " + (l.NovoTexto ?? "") + " " +
                           new string((l.C.Processo ?? "").Where(char.IsDigit).ToArray());
            return f.Split(' ').All(p => texto.Contains(p));
        }

        private void AtualizarContagem()
        {
            int total = linhas.Count, vistos = linhas.Count(Combina);
            A<TextBlock>("nuContagem").Text = vistos == total ? "" : vistos + " de " + total;
        }

        /// <summary>
        /// Confere em segundo plano quais versões ainda reservam 32 números e mostra a faixa no topo da janela
        /// (menos nas versões em que o aviso foi fechado). Chamado depois de procurar e de cada operação.
        /// </summary>
        public void VerificarReservas()
        {
            // outra operação (backup, restauração, migração) pode ter mudado os bancos: a aba relê ao ser aberta
            if (!Visivel && !Ocupado) estado = null;
            var insts = A<ComboBox>("nuVersao").Items.OfType<InstalacaoVM>().Select(v => v.Inst).ToList();
            int minha = ++verificacao;
            var ui = TaskScheduler.FromCurrentSynchronizationContext();
            Task.Factory.StartNew(() =>
            {
                var r = new List<Tuple<Instalacao, EstadoNumeracao>>();
                foreach (var i in insts)
                {
                    try { var e = Numeracao.Ler(i); if (e.Reserva > 1 && e.BloqueioReserva == null) r.Add(Tuple.Create(i, e)); } catch { }
                }
                return r;
            }).ContinueWith(t =>
            {
                if (minha != verificacao || t.Exception != null) return;
                var pend = t.Result.Where(x => !config.NumeracaoAvisoIgnorado.Contains(x.Item1.Pasta, StringComparer.OrdinalIgnoreCase)).ToList();
                avisoPendentes = pend.Select(x => x.Item1).ToList();

                if (pend.Count == 0) { AtualizarFaixa(); return; }
                var versoes = pend.Select(x => x.Item1.Versao).Distinct().ToList();
                string quais = versoes.Count == 1 ? versoes[0] : string.Join(", ", versoes.Take(versoes.Count - 1)) + " e " + versoes.Last();
                long pulados = pend.Count == 1 ? pend[0].Item2.Buracos : 0;
                var reservas = pend.Select(x => x.Item2.Reserva).Distinct().ToList();
                A<TextBlock>("txtAvisoNumeracao").Text = "A numeração dos cálculos do PJe-Calc " + quais + " pode pular: " + (versoes.Count == 1 ? "ele reserva " : "elas reservam ") +
                    (reservas.Count == 1 ? reservas[0] + " números" : "vários números") + " de cada vez" + (pulados > 0 ? " e já pulou " + pulados + " número(s)" : "") +
                    ". Dá para corrigir na aba Numeração.";
                AtualizarFaixa();
            }, ui);
        }

        private T A<T>(string nome) where T : class { return janela.FindName(nome) as T; }
        private bool Visivel { get { return A<FrameworkElement>("painelNumeracao").Visibility == Visibility.Visible; } }
        private bool Perguntar(TipoDialogo tipo, string titulo, string msg, string sim, string nao) { return Dialogo.Mostrar(janela, tipo, titulo, msg, sim, nao); }
        private void NaTela(Action a) { janela.Dispatcher.BeginInvoke(a); }

        /// <summary>A aba foi aberta: carrega a versão escolhida (ou escolhe a primeira).</summary>
        public void Ativar()
        {
            if (Ocupado) return;
            var combo = A<ComboBox>("nuVersao");
            if (combo.SelectedItem == null && combo.Items.Count > 0) { combo.SelectedIndex = 0; return; } // SelectionChanged carrega
            if (estado == null || atual == null) CarregarSelecionada();
        }

        public void Recarregar()
        {
            if (Ocupado) return;
            CarregarSelecionada();
        }

        /// <summary>Tema trocado: refaz as cores calculadas.</summary>
        public void AtualizarCores()
        {
            foreach (var l in linhas) l.AtualizarCores();
            var ls = log.ToList(); log.Clear();
            foreach (var l in ls) { l.Cor = Tema.Recurso(l.Chave); log.Add(l); }
            if (estado != null && atual != null) MostrarReserva();
        }

        // ============================================================ leitura

        /// <summary>Mostra a faixa do topo se houver versão pendente, menos com a aba Numeração aberta (ali o cartão da reserva já diz o mesmo).</summary>
        public void AtualizarFaixa()
        {
            A<Border>("avisoNumeracao").Visibility = avisoPendentes.Count > 0 && !Visivel ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Mensagem(string titulo, string texto)
        {
            foreach (var n in new[] { "nuCartaoReserva", "nuCartaoNumeros", "nuAndamento", "nuFaixaUltima" }) A<Border>(n).Visibility = Visibility.Collapsed;
            A<TextBlock>("nuMensagemTitulo").Text = titulo;
            A<TextBlock>("nuMensagemTexto").Text = texto ?? "";
            A<TextBlock>("nuMensagemTexto").Visibility = string.IsNullOrEmpty(texto) ? Visibility.Collapsed : Visibility.Visible;
            A<Border>("nuMensagem").Visibility = Visibility.Visible;
        }

        private void CarregarSelecionada()
        {
            var vmI = A<ComboBox>("nuVersao").SelectedItem as InstalacaoVM;
            linhas.Clear();
            A<Border>("nuPilula").Visibility = Visibility.Collapsed;
            if (vmI == null)
            {
                atual = null; estado = null;
                if (A<ComboBox>("nuVersao").Items.Count == 0)
                    Mensagem("Nenhuma versão do PJe-Calc com cálculos", "Quando o programa terminar de procurar as instalações (aba Backup), elas aparecem aqui.");
                else Mensagem("Escolha a versão do PJe-Calc acima", null);
                return;
            }
            var inst = vmI.Inst;
            atual = inst;
            int minha = ++leitura;
            Mensagem("Lendo o banco do PJe-Calc " + inst.Versao + "…", null);
            A<Button>("nuRecarregar").IsEnabled = false;
            string destino = config.Destino;
            var ui = TaskScheduler.FromCurrentSynchronizationContext();
            Task.Factory.StartNew(() =>
            {
                var e = Numeracao.Ler(inst);
                bool ab = inst.EstaAberta();
                List<Numeracao.MudancaFeita> f;
                try { f = Numeracao.Feitas(destino, inst); } catch { f = new List<Numeracao.MudancaFeita>(); }
                return Tuple.Create(e, ab, f);
            }).ContinueWith(t =>
            {
                if (minha != leitura) return;
                A<Button>("nuRecarregar").IsEnabled = true;
                if (t.Exception != null)
                {
                    Mensagem("Não consegui ler o banco do PJe-Calc " + inst.Versao, t.Exception.InnerException.Message);
                    return;
                }
                estado = t.Result.Item1; aberta = t.Result.Item2; feitas = t.Result.Item3;
                Mostrar();
            }, ui);
        }

        // ============================================================ tela

        private void Mostrar()
        {
            A<Border>("nuMensagem").Visibility = Visibility.Collapsed;
            A<Border>("nuAndamento").Visibility = Visibility.Collapsed;
            A<Border>("nuCartaoReserva").Visibility = Visibility.Visible;
            A<Border>("nuCartaoNumeros").Visibility = Visibility.Visible;
            // sem edição liberada, o botão de aplicar não tem função
            A<Button>("nuAplicar").Visibility = estado.BloqueioEdicao == null ? Visibility.Visible : Visibility.Collapsed;

            var pilula = A<Border>("nuPilula");
            pilula.Visibility = Visibility.Visible;
            pilula.SetResourceReference(Border.BackgroundProperty, aberta ? "AvisoFundo" : "OkFundo");
            var tEstado = A<TextBlock>("nuEstado");
            tEstado.Text = aberta ? "Aberta agora" : "Fechada";
            tEstado.SetResourceReference(TextBlock.ForegroundProperty, aberta ? "Aviso" : "Ok");
            pilula.ToolTip = aberta ? "Ao aplicar uma mudança, o PJe-Calc é fechado com segurança e reaberto no fim." : null;

            MostrarReserva();

            bool editavel = estado.BloqueioEdicao == null;
            var ativos = estado.Calculos.Where(c => c.Ativo).ToList();
            A<TextBlock>("nuNumeros").Text = estado.Calculos.Count == 0 ? "Nenhum cálculo neste banco." :
                ativos.Count + " cálculo(s)" + (estado.Calculos.Count > ativos.Count ? " e " + (estado.Calculos.Count - ativos.Count) + " excluído(s)" : "") +
                ", do nº " + estado.Calculos.Min(c => c.Numero) + " ao nº " + estado.Maior + "   ·   " +
                (estado.Buracos == 0 ? "sem números pulados" : estado.Buracos + " número(s) pulados");
            A<TextBlock>("nuNumeros").ToolTip = editavel ? "Digite o novo número de um cálculo na coluna \"Novo nº\" ou use \"Colocar em sequência\"." : null;
            A<Border>("nuAvisoBloqueio").Visibility = editavel ? Visibility.Collapsed : Visibility.Visible;
            A<TextBlock>("nuBloqueio").Text = editavel ? "" : "Mudar o número não está disponível aqui: " + estado.BloqueioEdicao;
            A<Button>("nuSequencia").IsEnabled = editavel && estado.Calculos.Count > 0;
            A<Button>("nuDesfazerEdicao").IsEnabled = editavel && estado.Calculos.Count > 0;

            preenchendo = true;
            linhas.Clear();
            foreach (var c in estado.Calculos.OrderBy(x => x.Numero))
            {
                var l = new LinhaCalculo(c, editavel);
                l.Mudou = Revalidar;
                linhas.Add(l);
            }
            preenchendo = false;
            AtualizarContagem();

            var ultima = feitas.FirstOrDefault();
            var faixa = A<Border>("nuFaixaUltima");
            if (ultima != null)
            {
                faixa.Visibility = Visibility.Visible;
                A<TextBlock>("nuUltima").Text = "Última mudança: " + ultima.Historico.Data.ToString("dd/MM/yyyy HH:mm") + " — " +
                    DescreverMudanca(ultima.Registro) + (ultima.Registro.Desfeito ? "  (desfeita)" : "");
                A<Button>("nuDesfazer").Visibility = ultima.Registro.Desfeito ? Visibility.Collapsed : Visibility.Visible;
            }
            else faixa.Visibility = Visibility.Collapsed;
            Revalidar();
        }

        private static string DescreverMudanca(RegistroNumeracao r)
        {
            var p = new List<string>();
            int n = r.Mudancas == null ? 0 : r.Mudancas.Count;
            if (n > 0) p.Add(n + " cálculo(s) com número novo");
            if (r.ReservaDepois != r.ReservaAntes) p.Add("reserva de " + r.ReservaDepois + " número por vez");
            p.Add("próximo nº " + r.ProximoDepois);
            return string.Join(", ", p) + ".";
        }

        private void MostrarReserva()
        {
            bool ok = estado.Reserva <= 1;
            var circulo = A<System.Windows.Shapes.Ellipse>("nuCirculoReserva");
            var simbolo = A<TextBlock>("nuSimboloReserva");
            circulo.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, ok ? "OkFundo" : "AvisoFundo");
            simbolo.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Ok" : "Aviso");
            simbolo.Text = ok ? "✓" : "!";
            long seguinte = estado.Maior + 1;
            var texto = A<TextBlock>("nuReservaTexto");
            if (ok)
            {
                // já está certo: uma linha só, para sobrar espaço para a lista
                A<TextBlock>("nuReservaTitulo").Text = "Reserva de 1 número por vez   ·   próximo cálculo: nº " + estado.Proximo;
                A<TextBlock>("nuReservaTitulo").ToolTip = "A numeração não pula quando o PJe-Calc fecha de forma anormal.";
                texto.Text = estado.Proximo > seguinte ? "O último cálculo é o nº " + estado.Maior + "; aplicar uma mudança de número também acerta o contador." : "";
                texto.Visibility = texto.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                A<Button>("nuReserva").Visibility = Visibility.Collapsed;
            }
            else
            {
                A<TextBlock>("nuReservaTitulo").Text = "O PJe-Calc " + atual.Versao + " reserva " + estado.Reserva + " números de cada vez";
                A<TextBlock>("nuReservaTitulo").ToolTip = null;
                texto.Visibility = Visibility.Visible;
                texto.Text = "Se ele fechar de forma anormal (queda de energia, computador desligado com ele aberto), os números reservados se perdem " +
                    "e a numeração pula, por exemplo do 2 para o " + (2 + estado.Reserva - 1) + ". Corrigindo, o próximo cálculo criado será o nº " + seguinte +
                    (estado.Proximo != seguinte ? " (hoje seria o " + estado.Proximo + ")" : "") + ".";
                var b = A<Button>("nuReserva");
                b.Visibility = Visibility.Visible;
                b.IsEnabled = estado.BloqueioReserva == null;
                b.ToolTip = estado.BloqueioReserva == null ? null : "Não disponível: " + estado.BloqueioReserva;
            }
            A<CheckBox>("nuChkReserva").Visibility = !ok && estado.BloqueioReserva == null && estado.BloqueioEdicao == null ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool ReservaMarcada
        {
            get { var c = A<CheckBox>("nuChkReserva"); return c.IsChecked == true && c.Visibility == Visibility.Visible; }
        }

        /// <summary>Confere o que foi digitado, pinta as linhas com problema e atualiza o resumo e o botão.</summary>
        private void Revalidar()
        {
            if (preenchendo || estado == null) return;
            var erros = new List<string>();
            var mapa = new Dictionary<long, long>();
            var invalidas = new HashSet<LinhaCalculo>();
            foreach (var l in linhas)
            {
                var n = l.Novo;
                if (!n.HasValue) { invalidas.Add(l); erros.Add("Cálculo " + l.C.Numero + ": digite só números (a partir de 1)."); continue; }
                mapa[l.C.Numero] = n.Value;
            }
            // números repetidos e fora da faixa (só dá para conferir quando todos os campos têm um número)
            if (invalidas.Count == 0) erros.AddRange(Numeracao.Validar(estado, mapa));
            var repetidos = new HashSet<long>(mapa.Values.GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key));
            foreach (var l in linhas)
                l.Problema = invalidas.Contains(l) || (l.Novo.HasValue && (repetidos.Contains(l.Novo.Value) || l.Novo.Value < 1));

            var mudam = linhas.Where(l => l.Alterado).ToList();
            var tErros = A<TextBlock>("nuErros");
            tErros.Text = string.Join("\n", erros.Take(3)) + (erros.Count > 3 ? "\n… e mais " + (erros.Count - 3) + " problema(s)." : "");
            tErros.Visibility = erros.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            long proximo = (mapa.Count == 0 ? 0 : mapa.Values.Max()) + 1;
            string resumo;
            if (estado.BloqueioEdicao != null) resumo = "";
            else if (mudam.Count == 0) resumo = "Nenhum número foi mudado ainda.";
            else resumo = mudam.Count + " cálculo(s) vão mudar de número. Depois de aplicar, o próximo cálculo criado será o nº " + proximo +
                          (ReservaMarcada ? ", e o PJe-Calc passa a reservar 1 número por vez." : ".");
            var tResumo = A<TextBlock>("nuResumo");
            tResumo.Text = resumo;
            tResumo.Visibility = resumo.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            A<Button>("nuAplicar").IsEnabled = !Ocupado && estado.BloqueioEdicao == null && mudam.Count > 0 && erros.Count == 0;
        }

        private void PreencherSequencia()
        {
            if (estado == null) return;
            var it = A<ComboBox>("nuOrdem").SelectedItem as ComboBoxItem;
            var mapa = Numeracao.EmSequencia(estado, it != null ? (string)it.Tag : "numero");
            preenchendo = true;
            foreach (var l in linhas) l.Definir(mapa[l.C.Numero].ToString(CultureInfo.InvariantCulture));
            preenchendo = false;
            Revalidar();
        }

        private void DesfazerEdicao()
        {
            preenchendo = true;
            foreach (var l in linhas) l.Definir(l.C.Numero.ToString(CultureInfo.InvariantCulture));
            preenchendo = false;
            Revalidar();
        }

        // ============================================================ operações

        private string TextoSeguranca()
        {
            return "\n\nAntes, o banco atual é guardado na pasta de backups:\n" + config.Destino +
                   (aberta ? "\n\nO PJe-Calc " + atual.Versao + " está aberto: ele será fechado com segurança (como pelo \"Sair\" da bandeja) e reaberto no fim." : "") +
                   "\n\nIsto não é uma função oficial do PJe-Calc. Se algo não ficar como você esperava, use \"Desfazer esta mudança\".";
        }

        private void CorrigirReserva()
        {
            if (estado == null || Ocupado) return;
            var inst = atual;
            string msg = "O contador de cálculos do PJe-Calc " + inst.Versao + " passa a reservar 1 número por vez, e o próximo cálculo criado será o nº " + (estado.Maior + 1) + "." +
                         "\n\nOs números dos cálculos que já existem não mudam." + TextoSeguranca();
            if (!Perguntar(TipoDialogo.Pergunta, "Reservar 1 número por vez?", msg, "Corrigir a reserva", "Cancelar")) return;
            string destino = config.Destino;
            Executar("Corrigindo a reserva de números…", inst, (lg, pg) => Numeracao.Aplicar(inst, new List<Mudanca>(), true, destino, lg, pg));
        }

        private void Aplicar()
        {
            if (estado == null || Ocupado) return;
            Revalidar();
            if (!A<Button>("nuAplicar").IsEnabled) return;
            var inst = atual;
            var mudam = linhas.Where(l => l.Alterado).OrderBy(l => l.Novo.Value).ToList();
            bool reserva = ReservaMarcada;
            long proximo = linhas.Max(l => l.Novo.Value) + 1;
            var lista = mudam.Take(8).Select(l => "   " + l.C.Numero + "  →  " + l.Novo.Value + (string.IsNullOrEmpty(l.C.Reclamante) ? "" : "   (" + l.C.Reclamante + ")")).ToList();
            if (mudam.Count > 8) lista.Add("   … e mais " + (mudam.Count - 8) + ".");
            string msg = mudam.Count + " cálculo(s) vão mudar de número:\n" + string.Join("\n", lista) +
                         "\n\nO próximo cálculo criado será o nº " + proximo + "." + (reserva ? " O PJe-Calc passa a reservar 1 número por vez." : "") +
                         TextoSeguranca();
            if (!Perguntar(TipoDialogo.Pergunta, "Mudar a numeração do PJe-Calc " + inst.Versao + "?", msg, "Aplicar", "Cancelar")) return;
            var mudancas = mudam.Select(l => new Mudanca { De = l.C.Numero, Para = l.Novo.Value }).ToList();
            string destino = config.Destino;
            Executar("Mudando a numeração…", inst, (lg, pg) => Numeracao.Aplicar(inst, mudancas, reserva, destino, lg, pg));
        }

        private void DesfazerUltima()
        {
            var m = feitas.FirstOrDefault();
            if (m == null || m.Registro.Desfeito || Ocupado) return;
            var inst = atual;
            bool intacto = Numeracao.BancoIntactoDesde(inst, m);
            string quando = m.Historico.Data.ToString("dd/MM/yyyy HH:mm");
            string msg = intacto
                ? "O banco do PJe-Calc " + inst.Versao + " volta a ser exatamente o de antes da mudança de " + quando + "."
                : "O banco do PJe-Calc " + inst.Versao + " mudou depois de " + quando + " (o PJe-Calc foi usado). Desfazer volta o banco inteiro para como estava antes da mudança: " +
                  "cálculos criados ou alterados depois disso deixam de aparecer.";
            msg += "\n\nAntes, o banco atual também é guardado na pasta de backups, então dá para voltar atrás:\n" + config.Destino +
                   (aberta ? "\n\nO PJe-Calc " + inst.Versao + " está aberto: ele será fechado com segurança e reaberto no fim." : "");
            if (!Perguntar(intacto ? TipoDialogo.Pergunta : TipoDialogo.Aviso, "Desfazer a mudança de " + quando + "?", msg, "Desfazer", "Cancelar")) return;
            string destino = config.Destino;
            Executar("Desfazendo a mudança…", inst, (lg, pg) => Numeracao.Desfazer(inst, m, destino, lg, pg));
        }

        private void Log(NivelLog n, string texto)
        {
            string chave = n == NivelLog.Ok ? "Ok" : n == NivelLog.Erro ? "Erro" : n == NivelLog.Aviso ? "Aviso" : "TextoSuave";
            string icone = n == NivelLog.Ok ? "✓" : n == NivelLog.Erro ? "✗" : n == NivelLog.Aviso ? "!" : "•";
            NaTela(() =>
            {
                log.Add(new LinhaLog { Chave = chave, Icone = icone, Texto = texto, Cor = Tema.Recurso(chave), Peso = FontWeights.Normal, Margem = new Thickness(0, 1, 0, 1) });
                A<ScrollViewer>("nuRolagemLog").ScrollToEnd();
            });
        }

        /// <summary>Fecha o PJe-Calc (se aberto), roda a operação em segundo plano e reabre no fim.</summary>
        private void Executar(string titulo, Instalacao inst, Func<Action<NivelLog, string>, Action<double, string>, ResultadoNumeracao> acao)
        {
            Ocupado = true;
            definirOcupado(true);
            log.Clear();
            foreach (var n in new[] { "nuCartaoReserva", "nuCartaoNumeros", "nuMensagem", "nuFaixaUltima" }) A<Border>(n).Visibility = Visibility.Collapsed;
            A<Border>("nuAndamento").Visibility = Visibility.Visible;
            A<TextBlock>("nuAndamentoTitulo").Text = titulo;
            A<TextBlock>("nuEtapa").Text = "";
            A<Button>("nuConcluir").Visibility = Visibility.Collapsed;
            A<Button>("nuFazerBackup").Visibility = Visibility.Collapsed;
            A<Button>("nuAbrirSeguranca").Visibility = Visibility.Collapsed;
            A<ComboBox>("nuVersao").IsEnabled = false;
            A<Button>("nuRecarregar").IsEnabled = false;
            var barra = A<ProgressBar>("nuBarra");
            barra.Value = 0; barra.IsIndeterminate = true;
            Action<double, string> progresso = (v, etapa) => NaTela(() =>
            {
                barra.IsIndeterminate = v < 0;
                if (v >= 0) barra.Value = v;
                A<TextBlock>("nuEtapa").Text = etapa;
            });

            bool reabrir = config.ReabrirPjeCalc;
            Task.Factory.StartNew(() =>
            {
                var res = Numeracao.ComPjeCalcFechado(inst, reabrir, Log, progresso, () => acao(Log, progresso));
                if (res.Ok && res.Mudancas > 0)
                    Log(NivelLog.Info, "Os backups feitos antes guardam os .PJC com os números antigos. Para ter um backup já com os números novos, use \"Fazer backup agora\".");
                return res;
            }).ContinueWith(t =>
            {
                var res = t.Result;
                Ocupado = false;
                definirOcupado(false);
                barra.IsIndeterminate = false; barra.Value = 1;
                A<TextBlock>("nuAndamentoTitulo").Text = res.Ok ? "Pronto" : "Não foi possível concluir";
                A<TextBlock>("nuEtapa").Text = res.Resumo ?? "";
                A<Button>("nuConcluir").Visibility = Visibility.Visible;
                A<ComboBox>("nuVersao").IsEnabled = true;
                A<Button>("nuRecarregar").IsEnabled = true;
                A<Button>("nuFazerBackup").Visibility = res.Ok && res.Mudancas > 0 ? Visibility.Visible : Visibility.Collapsed;
                pastaSeguranca = res.PastaSeguranca;
                if (!string.IsNullOrEmpty(pastaSeguranca) && Directory.Exists(pastaSeguranca))
                    A<Button>("nuAbrirSeguranca").Visibility = Visibility.Visible;
                if (config.Sons) Avisos.Som(res.Ok);
                aoTerminar();
                A<Button>("nuConcluir").Focus();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
