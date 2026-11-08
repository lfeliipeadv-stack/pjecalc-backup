// Temas (Claro, Escuro, Seguir o Windows) e paletas de cores do programa.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace PjeCalcBackup
{
    public class Paleta
    {
        public string Nome;
        // claro: primária, escura, clara (fundos suaves), cabeçalho início/fim
        public string P, PE, PC, CA, CB;
        // escuro
        public string DP, DPE, DPC, DCA, DCB;
    }

    public static class Tema
    {
        public static readonly Paleta[] Paletas =
        {
            new Paleta { Nome = "Azul",      P = "#2F5D8F", PE = "#23476F", PC = "#E9F0F8", CA = "#23476F", CB = "#2F5D8F", DP = "#4A7FC1", DPE = "#3A68A3", DPC = "#1F2B3A", DCA = "#1A2A3D", DCB = "#25405F" },
            new Paleta { Nome = "Esmeralda", P = "#1E7A5A", PE = "#165E45", PC = "#E5F3EC", CA = "#165E45", CB = "#1E7A5A", DP = "#2E9C74", DPE = "#24805F", DPC = "#17302A", DCA = "#12281F", DCB = "#1C4434" },
            new Paleta { Nome = "Violeta",   P = "#5B4B9A", PE = "#463A7A", PC = "#EEEBF7", CA = "#463A7A", CB = "#5B4B9A", DP = "#7B69C2", DPE = "#6554A8", DPC = "#26223A", DCA = "#221D36", DCB = "#3A3166" },
            new Paleta { Nome = "Terracota", P = "#B4532A", PE = "#8E3F1F", PC = "#FBEDE6", CA = "#8E3F1F", CB = "#B4532A", DP = "#C9683D", DPE = "#A9532E", DPC = "#3A241B", DCA = "#331C14", DCB = "#5A3020" },
            new Paleta { Nome = "Grafite",   P = "#3F4C5A", PE = "#2C3641", PC = "#ECEFF2", CA = "#2C3641", CB = "#3F4C5A", DP = "#5F7184", DPE = "#4D5D6E", DPC = "#252C33", DCA = "#1A2026", DCB = "#2F3943" },
        };

        private static readonly Dictionary<string, string> Claro = new Dictionary<string, string>
        {
            { "Fundo", "#F3F5F8" }, { "Superficie", "#FFFFFF" }, { "SuperficieAlt", "#FBFCFD" }, { "Borda", "#DDE3EA" },
            { "BordaSuave", "#E6EBF1" }, { "BordaControle", "#C5CFDB" }, { "Texto", "#1F2933" }, { "TextoSuave", "#5B6775" },
            { "Trilho", "#E6EBF1" }, { "Rolagem", "#C2CAD4" }, { "Rodape", "#FFFFFF" },
            { "Ok", "#1E7A3D" }, { "OkFundo", "#E5F4EA" }, { "Aviso", "#9A5B00" }, { "AvisoFundo", "#FFF4E0" }, { "AvisoBorda", "#F3D9A4" },
            { "Erro", "#B42318" }, { "ErroFundo", "#FDECEA" }, { "Neutro", "#6B7685" }, { "NeutroFundo", "#EEF0F3" },
            { "TextoCabecalho", "#FFFFFF" }, { "TextoCabecalhoSuave", "#D6E2F0" }, { "SobrePrimaria", "#FFFFFF" }
        };

        private static readonly Dictionary<string, string> Escuro = new Dictionary<string, string>
        {
            { "Fundo", "#14181D" }, { "Superficie", "#1D232A" }, { "SuperficieAlt", "#222931" }, { "Borda", "#2E3740" },
            { "BordaSuave", "#2A323B" }, { "BordaControle", "#3B4651" }, { "Texto", "#E6EAEE" }, { "TextoSuave", "#9AA5B1" },
            { "Trilho", "#2E3740" }, { "Rolagem", "#46525E" }, { "Rodape", "#1A1F25" },
            { "Ok", "#5CC98A" }, { "OkFundo", "#173326" }, { "Aviso", "#F0B45A" }, { "AvisoFundo", "#33281A" }, { "AvisoBorda", "#5C4520" },
            { "Erro", "#F07A6E" }, { "ErroFundo", "#3D1D1A" }, { "Neutro", "#9AA5B1" }, { "NeutroFundo", "#2A323B" },
            { "TextoCabecalho", "#FFFFFF" }, { "TextoCabecalhoSuave", "#B9C6D4" }, { "SobrePrimaria", "#FFFFFF" }
        };

        public static bool WindowsEscuro()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        public static bool EscuroAtivo(string modo)
        {
            return modo == "Escuro" || (modo != "Claro" && WindowsEscuro());
        }

        private static SolidColorBrush Pincel(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        /// <summary>Aplica o tema nos recursos do aplicativo (as telas usam DynamicResource e mudam na hora).</summary>
        public static void Aplicar(ResourceDictionary r, string modo, int paleta)
        {
            bool escuro = EscuroAtivo(modo);
            var p = Paletas[Math.Max(0, Math.Min(Paletas.Length - 1, paleta))];
            foreach (var kv in escuro ? Escuro : Claro) r[kv.Key] = Pincel(kv.Value);
            r["Primaria"] = Pincel(escuro ? p.DP : p.P);
            r["PrimariaEscura"] = Pincel(escuro ? p.DPE : p.PE);
            r["PrimariaClara"] = Pincel(escuro ? p.DPC : p.PC);
            var g = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(escuro ? p.DCA : p.CA),
                                            (Color)ColorConverter.ConvertFromString(escuro ? p.DCB : p.CB), new Point(0, 0), new Point(1, 1));
            g.Freeze();
            r["Cabecalho"] = g;
            r["Escuro"] = escuro;
            foreach (Window w in Application.Current.Windows) BarraDeTitulo(w, escuro);
        }

        public static Brush Recurso(string chave)
        {
            var b = Application.Current != null ? Application.Current.TryFindResource(chave) as Brush : null;
            return b ?? Brushes.Gray;
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int valor, int tamanho);

        /// <summary>Barra de título escura no Windows 10/11 quando o tema é escuro.</summary>
        public static void BarraDeTitulo(Window w, bool escuro)
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                int v = escuro ? 1 : 0;
                if (DwmSetWindowAttribute(h, 20, ref v, 4) != 0) DwmSetWindowAttribute(h, 19, ref v, 4);
            }
            catch { }
        }
    }
}
