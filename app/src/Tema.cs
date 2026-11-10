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
            Gerar("Petróleo", "#11707A"),
            Gerar("Vinho",    "#8B2942"),
            Gerar("Índigo",   "#3949A3"),
            Gerar("Oliva",    "#5A6B24"),
            Gerar("Dourado",  "#8A6200"),
            Gerar("Rosa",     "#B03A73"),
            Gerar("Café",     "#6B4636"),
            Gerar("Oceano",   "#0A6FA6"),
            Gerar("Vermelho", "#C62828"),
            Gerar("Laranja",  "#BF5F00"),
            Gerar("Floresta", "#2E7D32"),
            Gerar("Turquesa", "#00897B"),
            Gerar("Céu",      "#1E7FD0"),
            Gerar("Marinho",  "#1F3A68"),
            Gerar("Lavanda",  "#7E57C2"),
            Gerar("Ameixa",   "#7B1F5E"),
            Gerar("Caramelo", "#9C6B3E"),
            Gerar("Preto",    "#1A1A1A"),
            new Paleta { Nome = "Branco", P = "#DCE0E5", PE = "#C3C9D0", PC = "#F4F6F8", CA = "#FFFFFF", CB = "#EEF1F4",
                         DP = "#F1F3F5", DPE = "#DEE2E6", DPC = "#2A2F35", DCA = "#2A2F35", DCB = "#3A4048" },
            Gerar("Amarelo",  "#F2C200"),
            Gerar("Pink",     "#E91E8C"),
            Gerar("Ciano",    "#00ACC1"),
            Gerar("Limão",    "#8BC34A"),
            Gerar("Salmão",   "#F28B6E"),
            Gerar("Lilás",    "#B39DDB"),
            Gerar("Menta",    "#3DBE9C"),
            Gerar("Royal",    "#2550D8"),
            Gerar("Roxo",     "#8E24AA"),
        };

        /// <summary>Luminância relativa (0 = preto, 1 = branco), para escolher texto claro ou escuro sobre a cor.</summary>
        private static double Luz(string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            Func<byte, double> l = v => { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); };
            return 0.2126 * l(c.R) + 0.7152 * l(c.G) + 0.0722 * l(c.B);
        }

        private static string Misturar(string a, string b, double t)
        {
            var x = (Color)ColorConverter.ConvertFromString(a); var y = (Color)ColorConverter.ConvertFromString(b);
            return string.Format("#{0:X2}{1:X2}{2:X2}", (int)Math.Round(x.R + (y.R - x.R) * t), (int)Math.Round(x.G + (y.G - x.G) * t), (int)Math.Round(x.B + (y.B - x.B) * t));
        }

        /// <summary>Texto escuro sobre cores claras (amarelo, branco, ciano…), branco sobre as demais.</summary>
        private static bool PedeTextoEscuro(string fundo) { return Luz(fundo) > 0.36; }

        /// <summary>Monta as variações (clara/escura, tema claro e escuro) a partir de uma cor base.</summary>
        private static Paleta Gerar(string nome, string baseHex)
        {
            var b = (Color)ColorConverter.ConvertFromString(baseHex);
            Func<string, double, string> mix = (outra, t) =>
            {
                var o = (Color)ColorConverter.ConvertFromString(outra);
                return string.Format("#{0:X2}{1:X2}{2:X2}",
                    (int)Math.Round(b.R + (o.R - b.R) * t), (int)Math.Round(b.G + (o.G - b.G) * t), (int)Math.Round(b.B + (o.B - b.B) * t));
            };
            return new Paleta
            {
                Nome = nome, P = baseHex, PE = mix("#000000", 0.22), PC = mix("#FFFFFF", 0.90), CA = mix("#000000", 0.22), CB = baseHex,
                DP = mix("#FFFFFF", 0.22), DPE = mix("#FFFFFF", 0.06), DPC = mix("#1D232A", 0.82), DCA = mix("#14181D", 0.78), DCB = mix("#14181D", 0.55)
            };
        }

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

        // ------------------------------------------------------------ logo
        // "Cofre do cálculo": um cofre cuja maçaneta é o sinal de igual (guardado com segurança + cálculo).
        // Azul-marinho e âmbar. Coordenadas num quadrado de 120 × 120.
        // O mesmo desenho está em ferramentas\gerar-icone.ps1 (app.ico e as imagens do site).

        private static Pen Traco(Brush b, double largura)
        {
            var p = new Pen(b, largura) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            p.Freeze();
            return p;
        }

        private static Geometry Ret(double x, double y, double l, double a, double raio) { return new RectangleGeometry(new Rect(x, y, l, a), raio, raio); }
        private static Geometry Circ(double x, double y, double raio) { return new EllipseGeometry(new Point(x, y), raio, raio); }

        /// <summary>Logo do programa. fundoBranco = versão clara, invertida (para cabeçalhos escuros);
        /// senão, a versão azul-marinho (a do ícone do programa, para cabeçalhos claros).</summary>
        public static DrawingImage Logo(bool fundoBranco)
        {
            Brush marinho = Pincel("#12355B"), ambar = Pincel("#F2A900"), branco = Pincel("#FFFFFF");
            var g = new DrawingGroup();
            if (!fundoBranco)
            {
                g.Children.Add(new GeometryDrawing(marinho, null, Ret(0, 0, 120, 120, 26)));                        // fundo
                g.Children.Add(new GeometryDrawing(branco, null, Ret(22, 18, 76, 78, 11)));                         // corpo do cofre
                g.Children.Add(new GeometryDrawing(branco, null, Ret(30, 94, 13, 8, 2)));                           // pés
                g.Children.Add(new GeometryDrawing(branco, null, Ret(77, 94, 13, 8, 2)));
                g.Children.Add(new GeometryDrawing(marinho, null, Ret(25, 32, 6, 12, 2)));                          // dobradiças
                g.Children.Add(new GeometryDrawing(marinho, null, Ret(25, 70, 6, 12, 2)));
                g.Children.Add(new GeometryDrawing(marinho, null, Circ(62, 57, 27)));                               // porta
                g.Children.Add(new GeometryDrawing(null, Traco(ambar, 4.5), Circ(62, 57, 20)));                     // aro
                g.Children.Add(new GeometryDrawing(ambar, null, Ret(48, 47.5, 28, 8, 4)));                          // "=" (maçaneta)
                g.Children.Add(new GeometryDrawing(ambar, null, Ret(48, 59.5, 28, 8, 4)));
            }
            else
            {
                g.Children.Add(new GeometryDrawing(branco, Traco(marinho, 3), Ret(1.5, 1.5, 117, 117, 25)));
                g.Children.Add(new GeometryDrawing(marinho, null, Ret(22, 18, 76, 78, 11)));
                g.Children.Add(new GeometryDrawing(marinho, null, Ret(30, 94, 13, 8, 2)));
                g.Children.Add(new GeometryDrawing(marinho, null, Ret(77, 94, 13, 8, 2)));
                g.Children.Add(new GeometryDrawing(branco, null, Ret(25, 32, 6, 12, 2)));
                g.Children.Add(new GeometryDrawing(branco, null, Ret(25, 70, 6, 12, 2)));
                g.Children.Add(new GeometryDrawing(null, Traco(ambar, 4.5), Circ(62, 57, 27.5)));                   // aro por fora da porta
                g.Children.Add(new GeometryDrawing(branco, null, Circ(62, 57, 22)));
                g.Children.Add(new GeometryDrawing(marinho, null, Ret(48, 47.5, 28, 8, 4)));
                g.Children.Add(new GeometryDrawing(marinho, null, Ret(48, 59.5, 28, 8, 4)));
            }
            g.Freeze();
            var img = new DrawingImage(g);
            img.Freeze();
            return img;
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

            // cores claras (amarelo, branco…) pedem texto escuro em cima delas
            string prim = escuro ? p.DP : p.P;
            r["SobrePrimaria"] = Pincel(PedeTextoEscuro(prim) ? "#1F2933" : "#FFFFFF");
            string cab = Misturar(escuro ? p.DCA : p.CA, escuro ? p.DCB : p.CB, 0.5);
            if (PedeTextoEscuro(cab)) { r["TextoCabecalho"] = Pincel("#1F2933"); r["TextoCabecalhoSuave"] = Pincel("#4A5663"); }
            // logo do cabeçalho: a versão branca sobre cabeçalho escuro, a azul-marinho sobre cabeçalho claro
            r["LogoCabecalho"] = Logo(!PedeTextoEscuro(cab));

            // a cor da paleta usada como texto (links, títulos do registro) precisa contrastar com o fundo
            string texto = prim;
            for (int i = 0; i < 8; i++)
            {
                double l = Luz(texto);
                if (!escuro && l > 0.16) texto = Misturar(texto, "#000000", 0.15);
                else if (escuro && l < 0.14) texto = Misturar(texto, "#FFFFFF", 0.15);
                else break;
            }
            r["PrimariaTexto"] = Pincel(texto);
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
