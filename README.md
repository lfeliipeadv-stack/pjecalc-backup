# PJe-Calc Backup

Programa para Windows que faz **backup e restauração dos cálculos do PJe-Calc Cidadão**. Ele encontra sozinho todas as versões instaladas no computador e, com um clique:

- **exporta todos os cálculos em `.PJC`**, o formato que o próprio PJe-Calc importa;
- **copia o banco de dados completo** de cada versão, compactado;
- **restaura arquivos `.PJC`** em qualquer versão instalada.

> [!IMPORTANT]
> Projeto **independente e não oficial**. Não tem vínculo com o TRT da 8ª Região, o CSJT ou a equipe do PJe-Calc. Use por sua conta e risco.

Testado no PJe-Calc Cidadão **2.14.0** e **2.16.0**, no Windows 11.

---

## Download

**[⬇ Baixar a versão mais recente (PJeCalcBackup.exe)](../../releases/latest)**

Não precisa instalar: é só baixar e dar dois cliques. Funciona no Windows 10 e 11.

> Na primeira vez o Windows pode mostrar **"O Windows protegeu o computador"**, porque o programa não tem assinatura digital, que é paga. Clique em **Mais informações → Executar assim mesmo**. O `.exe` é compilado automaticamente pelo GitHub a partir do código deste repositório, e o SHA-256 de cada versão aparece na página de Releases.

---

## Por que existe

O PJe-Calc guarda os cálculos num banco de dados **dentro da pasta de cada versão** (`.dados\pjecalc.h2.db`). Quando se instala uma versão nova em outra pasta, os cálculos antigos **não vão junto**, e não há backup automático. Exportar e importar cálculo por cálculo pela tela é demorado.

## Fazer backup

1. Abra o **PJeCalcBackup.exe**, na aba **Fazer backup**.
2. Ele lista as versões do PJe-Calc encontradas, com o estado (*Fechada* / *Aberta agora*) e o número de cálculos. Se alguma não aparecer, use **Adicionar pasta…**.
3. Escolha **onde salvar**. O padrão é `Área de Trabalho\BKP PJC PJECALC`.
4. Clique em **Fazer backup**. Um aviso pede para você **salvar e fechar o que estiver editando** no PJe-Calc antes de continuar.

Cada backup cria uma pasta nova com a data e a hora:

```
BKP PJC PJECALC\
└── 2026-11-07_2130\
    ├── relatorio.txt
    ├── PJe-Calc 2.16.0\
    │   ├── banco-de-dados-pjecalc-2.16.0.zip
    │   ├── CALCULO_1_FULANO DE TAL_DATA_..._HORA_....PJC
    │   └── ...
    └── PJe-Calc 2.14.0\
        └── ...
```

## Restaurar cálculos (.PJC)

1. Na aba **Restaurar cálculos (.PJC)**, clique em **Escolher arquivos…** ou em **Escolher pasta…**. Pode ser a pasta de um backup; as subpastas entram também.
2. Desmarque os arquivos que não quiser restaurar.
3. Em **Restaurar em**, escolha a versão do PJe-Calc que vai receber os cálculos.
4. Clique em **Restaurar**.

O que acontece:

- Cada arquivo é importado pela tela **Importar** do próprio PJe-Calc e vira um **cálculo novo**, com número novo. Se o cálculo já existir na versão, ele fica duplicado.
- **Antes de importar**, o programa guarda uma cópia do banco atual da versão na pasta de backups, numa subpasta `... (antes da restauração)`. Para desfazer, feche o PJe-Calc, extraia o `pjecalc.h2.db` desse zip e coloque-o na pasta `.dados` da instalação.

## Como funciona por dentro

Para cada versão:

1. **Se o PJe-Calc estiver aberto**, o programa o **fecha com segurança**. Ele envia ao Java do PJe-Calc o mesmo aviso que o Windows envia ao desligar o computador, e o PJe-Calc desliga o servidor e fecha o banco de forma ordenada, igual ao **Sair** do ícone na bandeja. O programa **nunca encerra à força**. No final, reabre o PJe-Calc que estava aberto.
2. Com a versão fechada, copia o banco de dados.
3. **Liga o PJe-Calc em segundo plano**, sem janela e sem abrir o Firefox.
4. Exporta ou importa cada cálculo fazendo o mesmo caminho da tela (**Buscar → Abrir → Exportar → Fechar**, ou **Importar → Confirmar**), usando o Microsoft Edge ou o Chrome em modo invisível. A busca passa por todas as páginas.
5. **Desliga o PJe-Calc pelo comando oficial do Tomcat**, que fecha o banco corretamente, e passa para a próxima versão.

> Enquanto o programa trabalha, **não abra o PJe-Calc pelo atalho**.

## Requisitos

- Windows 10 ou 11 (usa o .NET Framework 4.8, que já vem no Windows).
- Microsoft Edge ou Google Chrome.

## Privacidade

- Tudo roda **somente no seu computador** (`127.0.0.1`). Nada é enviado para a internet.
- Os arquivos `.PJC` e o banco contêm dados pessoais das partes (nomes, CPF etc.). Guarde os backups em local seguro e **nunca** os coloque em repositórios públicos. O [`.gitignore`](.gitignore) deste projeto já bloqueia esses arquivos.

## Limitações conhecidas

- O programa depende da estrutura das telas do PJe-Calc. Uma versão futura pode mudar algo e exigir ajuste. Confira sempre o `relatorio.txt`.
- Uma versão que nunca foi aberta pelo atalho oficial precisa ser aberta uma vez antes do primeiro uso.

## Código-fonte

| Pasta | O que é |
|---|---|
| [`app/`](app/) | Código do programa (C#/WPF). `app\compilar.ps1` gera o `.exe` usando o compilador que já vem no Windows. |
| [`backup-calculos/`](backup-calculos/) | Versão em script PowerShell do backup em `.PJC`, para quem prefere não usar o `.exe`. |

Para compilar você mesmo:

```powershell
powershell -ExecutionPolicy Bypass -File app\compilar.ps1
```

Gera `app\saida\PJeCalcBackup.exe`. Não precisa instalar Visual Studio nem SDK.

## Contribuindo

Encontrou um problema ou testou em outra versão? Abra uma *issue* dizendo a versão do PJe-Calc e do Windows e colando o `relatorio.txt`, **sem dados de clientes**.

## Licença

[MIT](LICENSE)
