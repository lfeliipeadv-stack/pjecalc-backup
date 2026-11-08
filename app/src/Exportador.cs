// Exporta os cálculos de um PJe-Calc aberto, fazendo o mesmo caminho que
// o usuário faria na tela: Buscar -> Abrir -> Exportar -> Fechar.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PjeCalcBackup
{
    public class CalculoListado
    {
        public string Numero;
        public string Reclamante;
    }

    public class Exportador
    {
        private readonly Navegador nav;
        private readonly string urlBase;
        private readonly Func<bool> cancelado;

        private const string MENU = "Array.prototype.slice.call(document.querySelectorAll('#menupainel a'))";
        private const string LINHAS = @"Array.prototype.slice.call(document.querySelectorAll('#formulario\\:listagem tr.rich-table-row'))";

        private const string LER_LINHAS = LINHAS + @".map(function(r){
  var c = Array.prototype.slice.call(r.cells).map(function(td){ return td.innerText.replace(/\s+/g,' ').trim(); });
  var i = c.findIndex(function(t){ return /^\d+$/.test(t); });
  return { num: i < 0 ? '' : c[i], rec: i < 0 ? '' : (c[i+2] || '') };
})";

        private const string EXPORTAR = @"(async function(){
  window.__pjc = null;
  var f = document.getElementById('formulario');
  f.submit = function(){
    var fd = new URLSearchParams(new FormData(this));
    fetch(this.action, { method:'POST', body:fd, credentials:'same-origin' }).then(async function(r){
      var buf = new Uint8Array(await r.arrayBuffer()), s = '';
      for (var i = 0; i < buf.length; i += 8192) s += String.fromCharCode.apply(null, buf.subarray(i, i + 8192));
      window.__pjc = { b64: btoa(s), disp: r.headers.get('content-disposition') || '', len: buf.length, zip: buf[0] == 0x50 && buf[1] == 0x4B };
    }, function(e){ window.__pjc = { erro: String(e) }; });
  };
  document.getElementById('formulario:exportar').click();
  for (var i = 0; i < 360 && !window.__pjc; i++) await new Promise(function(r){ setTimeout(r, 500) });
  if (!window.__pjc) return null;
  if (window.__pjc.erro) return { erro: window.__pjc.erro };
  return { disp: window.__pjc.disp, len: window.__pjc.len, zip: window.__pjc.zip, tam: window.__pjc.b64.length };
})()";

        public Exportador(Navegador nav, string urlBase, Func<bool> cancelado)
        {
            this.nav = nav;
            this.urlBase = urlBase;
            this.cancelado = cancelado;
        }

        private object Js(string e) { return nav.Pagina.Js(e); }

        private void Esperar(string condicao, int segundos)
        {
            DateTime limite = DateTime.Now.AddSeconds(segundos);
            while (DateTime.Now < limite)
            {
                if (cancelado()) throw new OperationCanceledException();
                try
                {
                    object r = nav.Pagina.Js("!!(function(){ try { return " + condicao + " } catch(e) { return false } })()", false, 15000);
                    if (r is bool && (bool)r) { Thread.Sleep(300); return; }
                }
                catch (ErroNaPagina) { }
                catch (TimeoutException) { throw; }
                catch (Exception) { }
                Thread.Sleep(400);
            }
            throw new TimeoutException("a tela do PJe-Calc não respondeu como esperado");
        }

        private void Esperar(string condicao) { Esperar(condicao, 90); }

        private void Clicar(string seletor)
        {
            Js("(function(){ var e = " + seletor + "; if(!e) throw new Error('elemento não encontrado na tela'); e.click(); return 1 })()");
        }

        private void IrParaBusca()
        {
            nav.Ir(urlBase + "/pages/principal.jsf");
            Esperar("document.readyState=='complete' && document.querySelector('#menupainel')");
            if (Equals(Js("!!document.querySelector('#menupainel a.menuImageClose')"), true))
            {
                Clicar("document.querySelector('#menupainel a.menuImageClose')");
                Esperar("!document.querySelector('#menupainel a.menuImageClose') && " + MENU + ".some(function(a){return a.textContent.trim()=='Buscar'})");
            }
            Clicar(MENU + ".filter(function(a){return a.textContent.trim()=='Buscar'})[0]");
            Esperar("document.getElementById('formulario:buscar') && document.getElementById('formulario:numeroCalculoBusca')");
        }

        private void Buscar(string numero)
        {
            Js("document.getElementById('formulario:numeroCalculoBusca').value = '" + numero + "'; " +
               LINHAS + ".forEach(function(r){ r.setAttribute('data-velha','1') }); 1");
            Clicar("document.getElementById('formulario:buscar')");
            Esperar("(" + LINHAS + ".length > 0 && !" + LINHAS + ".some(function(r){ return r.getAttribute('data-velha') })) || /Nenhum registro|nenhum resultado/i.test(document.body.innerText)");
        }

        private List<CalculoListado> LerLinhas()
        {
            var lista = new List<CalculoListado>();
            var arr = Js(LER_LINHAS) as ArrayList;
            if (arr == null) return lista;
            foreach (Dictionary<string, object> d in arr)
                lista.Add(new CalculoListado { Numero = Convert.ToString(d["num"]), Reclamante = Convert.ToString(d["rec"]) });
            return lista;
        }

        public string LerVersaoTela()
        {
            object v = Js("(document.getElementById('formulario:versaoSistema')||{}).textContent || ''");
            return Regex.Replace(Convert.ToString(v), "[^0-9.]", "");
        }

        /// <summary>Lista todos os cálculos, passando por todas as páginas da busca.</summary>
        public List<CalculoListado> ListarTodos()
        {
            IrParaBusca();
            Buscar("");
            var todos = new List<CalculoListado>();
            for (int pagina = 1; pagina <= 1000; pagina++)
            {
                todos.AddRange(LerLinhas());
                if (!Equals(Js("!!document.querySelector('img[id*=pagProximolistagem]:not([id*=Off])')"), true)) break;
                Js(LINHAS + ".forEach(function(r){ r.setAttribute('data-velha','1') }); 1");
                Clicar("document.querySelector('img[id*=pagProximolistagem]:not([id*=Off])')");
                Esperar(LINHAS + ".length > 0 && !" + LINHAS + ".some(function(r){ return r.getAttribute('data-velha') })");
            }
            return todos.Where(c => !string.IsNullOrEmpty(c.Numero))
                        .GroupBy(c => c.Numero).Select(g => g.First())
                        .OrderBy(c => long.Parse(c.Numero)).ToList();
        }

        /// <summary>Exporta um cálculo e grava o .PJC na pasta. Devolve o caminho do arquivo.</summary>
        public string Exportar(CalculoListado calc, string pasta)
        {
            IrParaBusca();
            Buscar(calc.Numero);
            var linhas = LerLinhas();
            int idx = linhas.FindIndex(l => l.Numero == calc.Numero);
            if (idx < 0) throw new Exception("não apareceu na busca");
            Clicar(LINHAS + "[" + idx + "].querySelector('a.linkSelecionar')");
            Esperar("document.querySelector('#menupainel a.menuImageExport')");
            Clicar("document.querySelector('#menupainel a.menuImageExport')");
            Esperar("document.getElementById('formulario:exportar')");

            string resumo = Convert.ToString(Js("((document.querySelector('#conteudo fieldset')||{}).innerText||'').replace(/\\s+/g,' ')"));
            if (!Regex.IsMatch(resumo, "C.lculo:\\s*" + calc.Numero + "\\b"))
                throw new Exception("a tela abriu um cálculo diferente do esperado");

            var r = nav.Pagina.Js(EXPORTAR, true, 200000) as Dictionary<string, object>;
            if (r == null) throw new Exception("o PJe-Calc não gerou o arquivo");
            if (r.ContainsKey("erro")) throw new Exception("falha ao baixar: " + r["erro"]);
            if (!(r["zip"] is bool) || !(bool)r["zip"]) throw new Exception("a resposta do PJe-Calc não é um arquivo .PJC");

            int tam = Convert.ToInt32(r["tam"]);
            var b64 = new StringBuilder(tam);
            for (int p = 0; p < tam; p += 400000)
                b64.Append(Convert.ToString(Js("window.__pjc.b64.substr(" + p + ", 400000)")));
            byte[] bytes = Convert.FromBase64String(b64.ToString());
            Js("window.__pjc = null; 1");

            var m = Regex.Match(Convert.ToString(r["disp"]), "filename=\"?([^\";]+)");
            string nomeOriginal = m.Success ? m.Groups[1].Value : "CALCULO_" + calc.Numero + ".PJC";
            string rec = new string((calc.Reclamante ?? "").Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).ToArray()).Trim();
            if (rec.Length > 40) rec = rec.Substring(0, 40).Trim();
            string nome = rec.Length > 0 ? Regex.Replace(nomeOriginal, "^(CALCULO_\\d+)_", "$1_" + rec.Replace("$", "") + "_") : nomeOriginal;
            nome = new string(nome.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).ToArray());

            // o Windows limita o caminho completo a 260 caracteres: encurta o nome se preciso
            Directory.CreateDirectory(pasta);
            string pastaCompleta = Path.GetFullPath(pasta);
            if (pastaCompleta.Length + 1 + nome.Length > 245) nome = nomeOriginal;
            if (pastaCompleta.Length + 1 + nome.Length > 245) nome = "CALCULO_" + calc.Numero + ".PJC";
            if (pastaCompleta.Length + 1 + nome.Length > 255)
                throw new Exception("o caminho da pasta de backups é longo demais para o Windows; escolha uma pasta mais curta (ex.: C:\\Backups)");
            string arquivo = Path.Combine(pasta, nome);
            File.WriteAllBytes(arquivo, bytes);
            using (var z = System.IO.Compression.ZipFile.OpenRead(arquivo))
                if (z.Entries.Count < 1) throw new Exception("o arquivo .PJC veio vazio");
            return arquivo;
        }

        /// <summary>
        /// Importa um arquivo .PJC pela tela "Importar" do PJe-Calc. O PJe-Calc cria um
        /// cálculo NOVO (com número novo). Devolve o número do cálculo criado.
        /// </summary>
        public string Importar(string arquivo)
        {
            nav.Ir(urlBase + "/pages/principal.jsf");
            Esperar("document.readyState=='complete' && document.querySelector('#menupainel')");
            if (Equals(Js("!!document.querySelector('#menupainel a.menuImageClose')"), true))
            {
                Clicar("document.querySelector('#menupainel a.menuImageClose')");
                Esperar("!document.querySelector('#menupainel a.menuImageClose')");
            }
            Clicar(MENU + ".filter(function(a){return a.textContent.trim()=='Importar'})[0]");
            Esperar("document.getElementById('formulario:arquivo:file') && document.getElementById('formulario:confirmarImportacao')");

            nav.DefinirArquivo("document.getElementById('formulario:arquivo:file')", Path.GetFullPath(arquivo));
            const string ITENS = "((document.getElementById('formulario:arquivo:fileItems')||{}).innerText||'')";
            Esperar("/Feito|Erro|maior que o limite/i.test(" + ITENS + ")", 180);
            string itens = Convert.ToString(Js(ITENS));
            if (!itens.Contains("Feito")) throw new Exception("o PJe-Calc recusou o envio do arquivo (" + itens.Replace("\n", " ").Trim() + ")");

            Js("document.querySelectorAll('.rich-messages').forEach(function(m){ m.setAttribute('data-velha','1') }); 1");
            Clicar("document.getElementById('formulario:confirmarImportacao')");
            Esperar("document.readyState=='complete' && (!document.getElementById('formulario:confirmarImportacao') || " +
                    "Array.prototype.slice.call(document.querySelectorAll('.rich-messages')).some(function(m){ return !m.getAttribute('data-velha') && m.innerText.trim() }))", 300);

            string msgs = Convert.ToString(Js("Array.prototype.slice.call(document.querySelectorAll('.rich-messages')).map(function(m){ return m.innerText }).join(' ').replace(/\\s+/g,' ')"));
            string numero = Convert.ToString(Js("(document.getElementById('formulario:idCalculo')||{}).value || ''"));
            if (string.IsNullOrEmpty(numero) || !msgs.Contains("sucesso"))
                throw new Exception(string.IsNullOrWhiteSpace(msgs) ? "o PJe-Calc não confirmou a importação" : msgs.Trim());
            FecharCalculoAberto();
            return numero;
        }

        /// <summary>Fecha o cálculo aberto na sessão (só a tela; não altera nada).</summary>
        public void FecharCalculoAberto()
        {
            try { IrParaBusca(); } catch { }
        }
    }
}
