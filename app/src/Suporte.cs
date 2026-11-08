// Configuração salva, seletor de pasta moderno e caixa de diálogo no visual do programa.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PjeCalcBackup
{
    // ------------------------------------------------------------ configuração

    public class Config
    {
        public string Destino { get; set; }
        public List<string> PastasExtras { get; set; }
        public List<string> Desmarcadas { get; set; }
        public bool ExportarPjc { get; set; }
        public bool CopiarBanco { get; set; }
        public string UltimaPastaPjc { get; set; }
        public string Tema { get; set; }               // Sistema | Claro | Escuro
        public int Paleta { get; set; }
        public bool Sons { get; set; }
        public bool Notificacoes { get; set; }
        public bool Lembrete { get; set; }
        public int LembreteDias { get; set; }
        public bool VerificarAtualizacoes { get; set; }
        public bool AbrirPastaAoTerminar { get; set; }
        public bool ReabrirPjeCalc { get; set; }
        public int VersaoConfig { get; set; }

        private static string Arquivo
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"PJeCalcBackup\config.json"); }
        }

        public static Config Carregar()
        {
            try
            {
                if (File.Exists(Arquivo))
                {
                    var c = new JavaScriptSerializer().Deserialize<Config>(File.ReadAllText(Arquivo));
                    if (c.PastasExtras == null) c.PastasExtras = new List<string>();
                    if (c.Desmarcadas == null) c.Desmarcadas = new List<string>();
                    if (string.IsNullOrEmpty(c.Destino)) c.Destino = DestinoPadrao();
                    if (c.VersaoConfig < 2) c.AplicarPadroesNovos();
                    return c;
                }
            }
            catch { }
            var n = new Config
            {
                Destino = DestinoPadrao(),
                PastasExtras = new List<string>(), Desmarcadas = new List<string>(), ExportarPjc = true, CopiarBanco = true
            };
            n.AplicarPadroesNovos();
            return n;
        }

        /// <summary>Valores padrão das opções que surgiram na versão 1.2.</summary>
        private void AplicarPadroesNovos()
        {
            Tema = "Sistema"; Paleta = 0; Sons = true; Notificacoes = true; Lembrete = true; LembreteDias = 7;
            VerificarAtualizacoes = true; AbrirPastaAoTerminar = false; ReabrirPjeCalc = true; VersaoConfig = 2;
        }

        private static string DestinoPadrao()
        {
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "BKP PJC PJECALC");
        }

        public void Salvar()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Arquivo));
                File.WriteAllText(Arquivo, new JavaScriptSerializer().Serialize(this));
            }
            catch { }
        }
    }

    // ------------------------------------------------------------ seletor de pasta moderno

    internal static class SeletorPasta
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] private class FileOpenDialogCom { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint c, IntPtr f); void SetFileTypeIndex(uint i); void GetFileTypeIndex(out uint i);
            void Advise(IntPtr e, out uint c); void Unadvise(uint c);
            void SetOptions(uint fos); void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem si); void SetFolder(IShellItem si);
            void GetFolder(out IShellItem si); void GetCurrentSelection(out IShellItem si);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string n); void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string n);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string t); void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string t);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string t);
            void GetResult(out IShellItem si);
            void AddPlace(IShellItem si, int fdap); void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string e);
            void Close(int hr); void SetClientGuid(ref Guid g); void ClearClientData(); void SetFilter(IntPtr f);
            void GetResults(out IntPtr e); void GetSelectedItems(out IntPtr e);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem si);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attrs);
            void Compare(IShellItem si, uint hint, out int order);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

        public static string Escolher(Window dono, string titulo, string inicial)
        {
            try
            {
                var d = (IFileOpenDialog)new FileOpenDialogCom();
                d.SetOptions(0x20 | 0x40); // escolher pasta, só sistema de arquivos
                d.SetTitle(titulo);
                if (!string.IsNullOrEmpty(inicial) && Directory.Exists(inicial))
                {
                    try
                    {
                        Guid g = typeof(IShellItem).GUID; IShellItem si;
                        SHCreateItemFromParsingName(inicial, IntPtr.Zero, ref g, out si);
                        d.SetFolder(si);
                    }
                    catch { }
                }
                int hr = d.Show(new WindowInteropHelper(dono).Handle);
                if (hr != 0) return null;
                IShellItem r; d.GetResult(out r);
                string caminho; r.GetDisplayName(0x80058000, out caminho);
                return caminho;
            }
            catch
            {
                using (var f = new System.Windows.Forms.FolderBrowserDialog())
                {
                    f.Description = titulo;
                    if (!string.IsNullOrEmpty(inicial) && Directory.Exists(inicial)) f.SelectedPath = inicial;
                    return f.ShowDialog() == System.Windows.Forms.DialogResult.OK ? f.SelectedPath : null;
                }
            }
        }
    }

    // ------------------------------------------------------------ caixa de diálogo

    public enum TipoDialogo { Info, Aviso, Sucesso, Erro, Pergunta }

    /// <summary>Caixa de diálogo no mesmo visual da janela principal.</summary>
    public static class Dialogo
    {
        /// <summary>Mostra a mensagem. Devolve true se o botão principal foi clicado.</summary>
        public static bool Mostrar(Window dono, TipoDialogo tipo, string titulo, string mensagem, string botaoPrincipal, string botaoSecundario)
        {
            string chave, simbolo;
            switch (tipo)
            {
                case TipoDialogo.Aviso: chave = "Aviso"; simbolo = "!"; break;
                case TipoDialogo.Sucesso: chave = "Ok"; simbolo = "✓"; break;
                case TipoDialogo.Erro: chave = "Erro"; simbolo = "✗"; break;
                default: chave = "Primaria"; simbolo = tipo == TipoDialogo.Pergunta ? "?" : "i"; break;
            }

            var w = new Window
            {
                Title = "PJe-Calc Backup",
                Width = 520,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = dono != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                UseLayoutRounding = true
            };
            w.SetResourceReference(Control.BackgroundProperty, "Superficie");
            w.SetResourceReference(Control.ForegroundProperty, "Texto");
            if (dono != null && dono.IsVisible) { w.Owner = dono; w.Icon = dono.Icon; }
            w.SourceInitialized += (s, e) => Tema.BarraDeTitulo(w, Application.Current != null && Equals(Application.Current.TryFindResource("Escuro"), true));

            var raiz = new DockPanel();

            // rodapé com os botões
            var rodape = new Border { Padding = new Thickness(20, 12, 20, 12) };
            rodape.SetResourceReference(Border.BackgroundProperty, "Fundo");
            DockPanel.SetDock(rodape, Dock.Bottom);
            var botoes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            bool resultado = false;
            if (!string.IsNullOrEmpty(botaoSecundario))
            {
                var b2 = new Button { Content = botaoSecundario, Margin = new Thickness(0, 0, 8, 0), IsCancel = true, MinWidth = 100 };
                if (dono != null) b2.Style = (Style)dono.TryFindResource("BotaoBase");
                b2.Click += (s, e) => { resultado = false; w.Close(); };
                botoes.Children.Add(b2);
            }
            var b1 = new Button { Content = botaoPrincipal, IsDefault = true, MinWidth = 110 };
            if (dono != null)
            {
                b1.Style = (Style)dono.TryFindResource("BotaoPrimario");
                b1.FontSize = 13; b1.Padding = new Thickness(18, 8, 18, 8);
            }
            b1.Click += (s, e) => { resultado = true; w.Close(); };
            botoes.Children.Add(b1);
            rodape.Child = botoes;
            raiz.Children.Add(rodape);

            // corpo: ícone + título + mensagem
            var corpo = new DockPanel { Margin = new Thickness(22, 20, 22, 18) };
            var circulo = new Grid { Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 16, 0) };
            var el = new Ellipse();
            el.SetResourceReference(Shape.FillProperty, chave);
            circulo.Children.Add(el);
            var sim = new TextBlock
            {
                Text = simbolo, FontSize = 20, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Symbol")
            };
            sim.SetResourceReference(TextBlock.ForegroundProperty, "Superficie");
            circulo.Children.Add(sim);
            DockPanel.SetDock(circulo, Dock.Left);
            corpo.Children.Add(circulo);

            var textos = new StackPanel();
            var t1 = new TextBlock { Text = titulo, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
            t1.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
            textos.Children.Add(t1);
            var t2 = new TextBlock { Text = mensagem, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
            t2.SetResourceReference(TextBlock.ForegroundProperty, "TextoSuave");
            textos.Children.Add(t2);
            corpo.Children.Add(textos);
            raiz.Children.Add(corpo);

            w.Content = raiz;
            w.ShowDialog();
            return resultado;
        }
    }
}
