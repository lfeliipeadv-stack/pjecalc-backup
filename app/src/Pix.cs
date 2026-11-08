// "Apoie este projeto": código PIX (BR Code / "Pix Copia e Cola") e um gerador de QR Code próprio,
// sem bibliotecas externas e sem consultar nenhum site.
using System;
using System.Collections.Generic;
using System.Text;

namespace PjeCalcBackup
{
    public static class Pix
    {
        /// <summary>Chave aleatória PIX do autor, para quem quiser apoiar o projeto.</summary>
        public const string Chave = "7828ccd8-a4ad-4757-8acf-6d968c74c3a8";
        private const string Nome = "PJECALC BACKUP";
        private const string Cidade = "BRASIL";

        private static string Campo(string id, string valor) { return id + valor.Length.ToString("00") + valor; }

        /// <summary>Código "Pix Copia e Cola" estático (sem valor definido: quem paga escolhe o valor).</summary>
        public static string CopiaECola()
        {
            string s = "000201" +
                       Campo("26", Campo("00", "br.gov.bcb.pix") + Campo("01", Chave)) +
                       "52040000" + "5303986" + "5802BR" +
                       Campo("59", Nome) + Campo("60", Cidade) +
                       Campo("62", Campo("05", "***")) +
                       "6304";
            return s + Crc16(s).ToString("X4");
        }

        /// <summary>CRC16-CCITT (polinômio 0x1021, valor inicial 0xFFFF), como pede o padrão do BR Code.</summary>
        public static int Crc16(string s)
        {
            int crc = 0xFFFF;
            foreach (byte b in Encoding.UTF8.GetBytes(s))
            {
                crc ^= b << 8;
                for (int i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
            }
            return crc;
        }
    }

    /// <summary>Gerador de QR Code (modo byte, correção de erros nível M, versões 1 a 10), escrito a partir da norma ISO/IEC 18004.</summary>
    public static class QrCode
    {
        // nível M, por versão (índice = versão): códigos de correção por bloco e número de blocos
        private static readonly int[] EccPorBloco = { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
        private static readonly int[] Blocos = { 0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };
        private static readonly int[][] Alinhamento =
        {
            null, new int[0], new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 }, new[] { 6, 34 },
            new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 }
        };

        private static int ModulosDeDados(int v)
        {
            int r = (16 * v + 128) * v + 64;
            if (v >= 2) { int n = v / 7 + 2; r -= (25 * n - 10) * n - 55; if (v >= 7) r -= 36; }
            return r;
        }

        /// <summary>Matriz do QR Code (true = módulo escuro), sem a margem.</summary>
        public static bool[,] Gerar(string texto)
        {
            byte[] dados = Encoding.UTF8.GetBytes(texto);
            int versao = 0, total = 0, ecc = 0, nDados = 0;
            for (int v = 1; v <= 10; v++)
            {
                total = ModulosDeDados(v) / 8; ecc = EccPorBloco[v] * Blocos[v]; nDados = total - ecc;
                int bits = 4 + (v < 10 ? 8 : 16) + dados.Length * 8;
                if (bits <= nDados * 8) { versao = v; break; }
            }
            if (versao == 0) throw new ArgumentException("Texto grande demais para o QR Code.");

            // 1) sequência de bits: modo byte, tamanho, dados, terminador e enchimento
            var bits_ = new List<bool>();
            Action<int, int> por = (valor, n) => { for (int i = n - 1; i >= 0; i--) bits_.Add(((valor >> i) & 1) != 0); };
            por(4, 4);
            por(dados.Length, versao < 10 ? 8 : 16);
            foreach (byte b in dados) por(b, 8);
            por(0, Math.Min(4, nDados * 8 - bits_.Count));
            while (bits_.Count % 8 != 0) bits_.Add(false);
            var palavras = new List<int>();
            for (int i = 0; i < bits_.Count; i += 8) { int x = 0; for (int j = 0; j < 8; j++) x = (x << 1) | (bits_[i + j] ? 1 : 0); palavras.Add(x); }
            for (int p = 0; palavras.Count < nDados; p++) palavras.Add(p % 2 == 0 ? 0xEC : 0x11);

            // 2) blocos + Reed-Solomon, intercalados
            int nb = Blocos[versao], eb = EccPorBloco[versao];
            int curtos = nb - total % nb, tamCurto = total / nb;
            var gerador = Gerador(eb);
            var blocosDados = new List<int[]>(); var blocosEcc = new List<int[]>();
            for (int i = 0, k = 0; i < nb; i++)
            {
                int n = tamCurto - eb + (i < curtos ? 0 : 1);
                var d = palavras.GetRange(k, n).ToArray(); k += n;
                blocosDados.Add(d); blocosEcc.Add(Resto(d, gerador));
            }
            var final = new List<int>();
            for (int i = 0; i <= tamCurto - eb; i++)
                for (int b = 0; b < nb; b++)
                    if (i < blocosDados[b].Length) final.Add(blocosDados[b][i]);
            for (int i = 0; i < eb; i++) for (int b = 0; b < nb; b++) final.Add(blocosEcc[b][i]);

            // 3) matriz: padrões fixos, dados e a melhor máscara
            int tam = versao * 4 + 17;
            bool[,] melhor = null; int menorPena = int.MaxValue;
            for (int mascara = 0; mascara < 8; mascara++)
            {
                var m = new bool[tam, tam]; var fixo = new bool[tam, tam];
                Fixos(m, fixo, versao);
                Formato(m, fixo, mascara);
                Colocar(m, fixo, final);
                Mascarar(m, fixo, mascara);
                int pena = Penalidade(m);
                if (pena < menorPena) { menorPena = pena; melhor = m; }
            }
            return melhor;
        }

        // ---- campo de Galois GF(256), polinômio 0x11D
        private static int Mult(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--) { z = (z << 1) ^ ((z >> 7) * 0x11D); z ^= ((y >> i) & 1) * x; }
            return z & 0xFF;
        }

        private static int[] Gerador(int grau)
        {
            var r = new int[grau]; r[grau - 1] = 1; int raiz = 1;
            for (int i = 0; i < grau; i++)
            {
                for (int j = 0; j < r.Length; j++) { r[j] = Mult(r[j], raiz); if (j + 1 < r.Length) r[j] ^= r[j + 1]; }
                raiz = Mult(raiz, 0x02);
            }
            return r;
        }

        private static int[] Resto(int[] dados, int[] gerador)
        {
            var r = new int[gerador.Length];
            foreach (int b in dados)
            {
                int f = b ^ r[0];
                Array.Copy(r, 1, r, 0, r.Length - 1); r[r.Length - 1] = 0;
                for (int i = 0; i < r.Length; i++) r[i] ^= Mult(gerador[i], f);
            }
            return r;
        }

        // ---- padrões fixos (localizadores, sincronismo, alinhamento, versão)
        private static void Por(bool[,] m, bool[,] fixo, int x, int y, bool escuro) { m[y, x] = escuro; fixo[y, x] = true; }

        private static void Fixos(bool[,] m, bool[,] fixo, int versao)
        {
            int tam = m.GetLength(0);
            for (int i = 0; i < tam; i++) { Por(m, fixo, 6, i, i % 2 == 0); Por(m, fixo, i, 6, i % 2 == 0); }
            foreach (var c in new[] { new[] { 3, 3 }, new[] { tam - 4, 3 }, new[] { 3, tam - 4 } })
                for (int dy = -4; dy <= 4; dy++)
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int x = c[0] + dx, y = c[1] + dy;
                        if (x < 0 || y < 0 || x >= tam || y >= tam) continue;
                        int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        Por(m, fixo, x, y, d != 2 && d != 4);
                    }
            var al = Alinhamento[versao];
            for (int i = 0; i < al.Length; i++)
                for (int j = 0; j < al.Length; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == al.Length - 1) || (i == al.Length - 1 && j == 0)) continue;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                            Por(m, fixo, al[i] + dx, al[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }
            // reserva as áreas de formato (preenchidas depois)
            for (int i = 0; i < 9; i++) { fixo[8, i] = true; fixo[i, 8] = true; }
            for (int i = 0; i < 8; i++) { fixo[8, tam - 1 - i] = true; fixo[tam - 1 - i, 8] = true; }
            if (versao >= 7)
            {
                int r = versao;
                for (int i = 0; i < 12; i++) r = (r << 1) ^ ((r >> 11) * 0x1F25);
                long bits = ((long)versao << 12) | (uint)r;
                for (int i = 0; i < 18; i++)
                {
                    bool b = ((bits >> i) & 1) != 0;
                    int a = tam - 11 + i % 3, c = i / 3;
                    Por(m, fixo, a, c, b); Por(m, fixo, c, a, b);
                }
            }
        }

        private static void Formato(bool[,] m, bool[,] fixo, int mascara)
        {
            int tam = m.GetLength(0);
            int dados = (0 << 3) | mascara; // nível M = 00
            int r = dados;
            for (int i = 0; i < 10; i++) r = (r << 1) ^ ((r >> 9) * 0x537);
            int bits = ((dados << 10) | r) ^ 0x5412;
            Func<int, bool> bit = i => ((bits >> i) & 1) != 0;
            for (int i = 0; i <= 5; i++) Por(m, fixo, 8, i, bit(i));
            Por(m, fixo, 8, 7, bit(6)); Por(m, fixo, 8, 8, bit(7)); Por(m, fixo, 7, 8, bit(8));
            for (int i = 9; i < 15; i++) Por(m, fixo, 14 - i, 8, bit(i));
            for (int i = 0; i < 8; i++) Por(m, fixo, tam - 1 - i, 8, bit(i));
            for (int i = 8; i < 15; i++) Por(m, fixo, 8, tam - 15 + i, bit(i));
            Por(m, fixo, 8, tam - 8, true); // módulo escuro fixo
        }

        private static void Colocar(bool[,] m, bool[,] fixo, List<int> palavras)
        {
            int tam = m.GetLength(0), i = 0, totalBits = palavras.Count * 8;
            for (int dir = tam - 1; dir >= 1; dir -= 2)
            {
                if (dir == 6) dir = 5;
                for (int v = 0; v < tam; v++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = dir - j;
                        bool subindo = ((dir + 1) & 2) == 0;
                        int y = subindo ? tam - 1 - v : v;
                        if (!fixo[y, x] && i < totalBits)
                        {
                            m[y, x] = ((palavras[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                            i++;
                        }
                    }
            }
        }

        private static void Mascarar(bool[,] m, bool[,] fixo, int k)
        {
            int tam = m.GetLength(0);
            for (int y = 0; y < tam; y++)
                for (int x = 0; x < tam; x++)
                {
                    if (fixo[y, x]) continue;
                    bool inv;
                    switch (k)
                    {
                        case 0: inv = (x + y) % 2 == 0; break;
                        case 1: inv = y % 2 == 0; break;
                        case 2: inv = x % 3 == 0; break;
                        case 3: inv = (x + y) % 3 == 0; break;
                        case 4: inv = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: inv = x * y % 2 + x * y % 3 == 0; break;
                        case 6: inv = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        default: inv = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    if (inv) m[y, x] = !m[y, x];
                }
        }

        /// <summary>Pontuação da norma para escolher a máscara mais fácil de ler.</summary>
        private static int Penalidade(bool[,] m)
        {
            int tam = m.GetLength(0), p = 0, escuros = 0;
            for (int eixo = 0; eixo < 2; eixo++)
                for (int a = 0; a < tam; a++)
                {
                    int seq = 1;
                    for (int b = 1; b <= tam; b++)
                    {
                        bool igual = b < tam && Pega(m, eixo, a, b) == Pega(m, eixo, a, b - 1);
                        if (igual) seq++;
                        else { if (seq >= 5) p += 3 + seq - 5; seq = 1; }
                    }
                    for (int b = 0; b + 7 <= tam; b++)
                    {
                        bool padrao = Pega(m, eixo, a, b) && !Pega(m, eixo, a, b + 1) && Pega(m, eixo, a, b + 2) && Pega(m, eixo, a, b + 3) &&
                                      Pega(m, eixo, a, b + 4) && !Pega(m, eixo, a, b + 5) && Pega(m, eixo, a, b + 6);
                        if (!padrao) continue;
                        bool claroAntes = true, claroDepois = true;
                        for (int c = 1; c <= 4; c++)
                        {
                            if (b - c >= 0 && Pega(m, eixo, a, b - c)) claroAntes = false;
                            if (b + 6 + c < tam && Pega(m, eixo, a, b + 6 + c)) claroDepois = false;
                        }
                        if (claroAntes || claroDepois) p += 40;
                    }
                }
            for (int y = 0; y < tam; y++)
                for (int x = 0; x < tam; x++)
                {
                    if (m[y, x]) escuros++;
                    if (x + 1 < tam && y + 1 < tam && m[y, x] == m[y, x + 1] && m[y, x] == m[y + 1, x] && m[y, x] == m[y + 1, x + 1]) p += 3;
                }
            int pct = escuros * 100 / (tam * tam);
            p += Math.Abs(pct - 50) / 5 * 10;
            return p;
        }

        private static bool Pega(bool[,] m, int eixo, int a, int b) { return eixo == 0 ? m[a, b] : m[b, a]; }
    }
}
