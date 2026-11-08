# PJe-Calc Backup

Programa para Windows que faz **backup, restauração e migração dos cálculos do PJe-Calc Cidadão**, em todas as versões instaladas no computador, com uma tela moderna em etapas.

- **Backup:** exporta os cálculos em `.PJC` (todos ou só os escolhidos) e copia o banco de dados completo de cada versão, com **conferência** ("12 de 12 conferidos").
- **Restauração:** importa arquivos `.PJC` **avisando os que parecem já existir**, ou restaura o banco completo de um backup.
- **Migração entre versões:** leva os cálculos de uma versão para outra (ex.: da 2.14 para a 2.16) num clique, sem duplicar os que já existem.
- **Histórico** dos backups, **lembrete** de backup, aviso de **nova versão**, **temas** claro/escuro e 5 paletas de cores, **sons** e **notificações** do Windows.
- **Versão para o CMD**, com menus interativos e um modo automático para agendar.

> [!IMPORTANT]
> Projeto **independente e não oficial**. Não tem vínculo com o TRT da 8ª Região, o CSJT ou a equipe do PJe-Calc. Use por sua conta e risco.

Testado no PJe-Calc Cidadão **2.14.0** e **2.16.0**, no Windows 11.

---

## Download

**[⬇ Baixar a versão mais recente](../../releases/latest)**

| Arquivo | Para quê |
|---|---|
| `PJeCalcBackup.exe` | O programa com janela. Basta baixar e dar dois cliques. |
| `PJeCalcBackup-vX.Y.Z.zip` | O pacote completo: o programa com janela, a **versão para o CMD** (`PJeCalcBackup-Console.exe`) e o `backup-calculos.bat`. |

Não precisa instalar. Funciona no Windows 10 e 11.

> Na primeira vez o Windows pode mostrar **"O Windows protegeu o computador"**, porque o programa não tem assinatura digital, que é paga. Clique em **Mais informações → Executar assim mesmo**. Os arquivos são compilados automaticamente pelo GitHub a partir deste código, e os SHA-256 aparecem na página de Releases.

---

## Por que existe

O PJe-Calc guarda os cálculos num banco de dados **dentro da pasta de cada versão** (`.dados\pjecalc.h2.db`). Quando se instala uma versão nova em outra pasta, os cálculos antigos **não vão junto**, e não há backup automático. Exportar e importar cálculo por cálculo pela tela é demorado.

## Como usar

Cada operação é feita **em etapas**, com **Voltar**, **Avançar** e **Concluir**. Antes de começar, um aviso pede para você **salvar e fechar o que estiver editando** no PJe-Calc.

### Backup
1. **Versões:** o programa encontra sozinho as versões instaladas e mostra o estado (*Fechada* / *Aberta agora*) e o número de cálculos de cada uma.
2. **Cálculos:** a lista é lida direto do banco, sem abrir o PJe-Calc. Mostra número, reclamante, reclamado, processo e datas; tem filtro e "Marcar/Desmarcar todos".
3. **Destino:** a pasta e o que incluir (`.PJC` e/ou o banco completo).
4. **Backup:** barra de progresso, registro de cada cálculo e conferência no final.

Cada backup cria uma pasta nova com a data e a hora; nada é apagado:

```
BKP PJC PJECALC\
└── 2026-11-08_0130\
    ├── relatorio.txt   ·   backup.json (usado pelo histórico)
    ├── PJe-Calc 2.16.0\
    │   ├── banco-de-dados-pjecalc-2.16.0.zip
    │   └── CALCULO_1_FULANO DE TAL_DATA_..._HORA_....PJC  …
    └── PJe-Calc 2.14.0\  …
```

### Restaurar
- **Cálculos (.PJC):** escolha arquivos ou uma pasta de backup e a versão de destino. O programa lê os `.PJC`, compara com os cálculos do destino e mostra **"Parece já existir (nº 35)"**. Esses vêm desmarcados, para não duplicar. Cada arquivo importado vira um cálculo novo, com número novo.
- **Banco completo (.zip):** volta a versão inteira para como estava no backup. Só é permitido na mesma versão do PJe-Calc do backup.

Nos dois casos, **antes de mexer em qualquer coisa** o programa guarda uma cópia do banco atual numa pasta `… (antes da restauração)`, para você poder desfazer.

### Migrar entre versões
Escolha a versão de origem e a de destino. Os cálculos que já existem no destino vêm desmarcados. O programa exporta da origem (que não é alterada), guarda uma cópia do banco de destino e importa os escolhidos.

### Histórico
Lista os backups, restaurações e migrações feitos na pasta de backups, com data, versões, quantidade de cálculos e conferência. Cada item tem os botões **Abrir pasta**, **Restaurar .PJC** e **Restaurar banco**.

### Configurações
- **Aparência:** tema **Claro**, **Escuro** ou **Seguir o Windows**, e 5 paletas de cores (Azul, Esmeralda, Violeta, Terracota e Grafite).
- **Avisos ao terminar:** som do próprio Windows (sucesso ou erro), notificação do Windows, abrir a pasta ao terminar e reabrir o PJe-Calc no final.
- **Backups:** a pasta padrão e o **lembrete** ("Seu último backup foi há 12 dias"), a cada 7, 15 ou 30 dias.
- **Atualizações:** verifica no GitHub se há versão nova. Só consulta a página do projeto; nenhum dado seu é enviado.

Durante uma operação, o ícone na **barra de tarefas** mostra o progresso.

## Versão para o CMD

Dê dois cliques em **`backup-calculos.bat`** (vem no pacote `.zip`). Ele abre o `PJeCalcBackup-Console.exe`, com menus navegados pelas setas, Enter, Espaço (para marcar) e Esc. Tem as mesmas funções: backup escolhendo versões, cálculos e pasta; restauração com aviso de duplicados; migração; restauração do banco; histórico; configurações.

**Modo automático**, sem perguntas, para agendar no Agendador de Tarefas do Windows:

```
PJeCalcBackup-Console.exe --backup [--destino "pasta"] [--versoes 2.16.0,2.14.0]
                          [--pasta "C:\pjecalc-..."] [--sem-pjc] [--sem-banco] [--nao-reabrir]
```

O código de saída é 0 quando deu tudo certo e 1 quando houve falhas.

## Como funciona por dentro

1. **Se o PJe-Calc estiver aberto**, o programa o **fecha com segurança**. Ele envia ao Java do PJe-Calc o mesmo aviso que o Windows envia ao desligar o computador, e o PJe-Calc desliga e fecha o banco de forma ordenada, igual ao **Sair** do ícone na bandeja. O programa **nunca encerra à força**. No final, reabre o PJe-Calc.
2. **Lê os cálculos** a partir de uma cópia temporária do banco, com o H2 que já vem no PJe-Calc. Não abre o programa nem altera o banco.
3. **Liga o PJe-Calc em segundo plano**, sem janela e sem abrir o Firefox, e exporta ou importa fazendo o mesmo caminho da tela (**Buscar → Abrir → Exportar**, **Importar → Confirmar**), com o Edge ou o Chrome em modo invisível.
4. **Desliga o PJe-Calc pelo comando oficial do Tomcat**, que fecha o banco corretamente.

> Enquanto o programa trabalha, **não abra o PJe-Calc pelo atalho**.

## Requisitos

- Windows 10 ou 11 (usa o .NET Framework 4.8, que já vem no Windows).
- Microsoft Edge ou Google Chrome, para exportar e importar `.PJC`.

## Privacidade

- Tudo roda **somente no seu computador** (`127.0.0.1`). A única consulta à internet é a verificação de versão nova no GitHub, que pode ser desligada.
- Os arquivos `.PJC` e o banco contêm dados pessoais das partes (nomes, CPF etc.). Guarde os backups em local seguro e **nunca** os coloque em repositórios públicos. O [`.gitignore`](.gitignore) deste projeto já bloqueia esses arquivos.

## Limitações conhecidas

- O programa depende da estrutura das telas do PJe-Calc. Uma versão futura pode mudar algo e exigir ajuste. Confira sempre o relatório.
- A detecção de duplicados compara reclamante, reclamado, processo e as datas do contrato e do cálculo. Dois cálculos idênticos nesses dados são tratados como o mesmo.
- Uma versão que nunca foi aberta pelo atalho oficial precisa ser aberta uma vez antes do primeiro uso.

## Código-fonte

| Pasta | O que é |
|---|---|
| [`app/`](app/) | O código do programa (C#/WPF). `app\compilar.ps1` gera os dois executáveis usando o compilador que já vem no Windows. |
| [`backup-calculos/`](backup-calculos/) | O `.bat` que abre a versão para o CMD. |

```powershell
powershell -ExecutionPolicy Bypass -File app\compilar.ps1
```

## Contribuindo

Encontrou um problema ou testou em outra versão? Abra uma *issue* dizendo a versão do PJe-Calc e do Windows e colando o `relatorio.txt`, **sem dados de clientes**.

## Licença

[MIT](LICENSE)
