# Contribuindo com o PowerLinq

> **Licença MIT.** O código está sob a [licença MIT](LICENSE); ao contribuir,
> você concorda em licenciar sua contribuição nos mesmos termos. Este documento
> é o guia de trabalho do projeto.

## Requisitos

- **SDK .NET 10**, qualquer banda de features. O `global.json` fixa o piso em
  `10.0.100` com `rollForward: latestFeature`, então um 10.0.1xx serve tanto
  quanto um 10.0.3xx. SDK de pré-lançamento é recusado.
- Nenhum endpoint XMLA é necessário: nada na suíte de testes toca a rede.

## O ciclo básico

```bash
dotnet build PowerLinq.slnx -c Release
dotnet test tests/PowerLinq.Tests/PowerLinq.Tests.csproj -c Release
```

**Antes de abrir PR, compile como o CI compila:**

```bash
dotnet build PowerLinq.slnx -c Release -warnaserror
```

O CI usa `-warnaserror`, então qualquer aviso reprova o check. Localmente os
avisos ficam como aviso, para não travar trabalho em andamento — a consequência é
que dá para esquecer deles até o PR. Rodar a linha acima antes evita a viagem.

Os analisadores do .NET e as regras de estilo do `.editorconfig` valem no build,
não só no IDE. Duas notas sobre o que está configurado:

- **`AnalysisMode` está em `Default`**, deliberadamente. Medido: em modo
  `Recommended` surgem 134 diagnósticos, e 93 deles são CA1707 por nome de teste
  com underscore — a convenção do xunit, que não é defeito. A justificativa está
  no `Directory.Build.props`.
- **CS1591 (membro público sem documentação) está ligado**, e com `-warnaserror`
  no CI isso significa que **membro público novo sem comentário XML reprova o
  build**. Houve um período em que estava suprimido, pela dívida que ligar a
  documentação expôs; ela foi paga e a supressão saiu. Se um membro novo não merece uma frase que
  informe, provavelmente não merece ser público.

## Testes

A suíte não depende de rede, de relógio de parede nem de ordem de execução. Três
consequências práticas:

- **Nada de `Sleep`.** Onde o comportamento depende do tempo — expiração de
  conexão ociosa, margem de renovação de token — injete `TimeProvider`. Há um
  `ControllableTimeProvider` em `tests/PowerLinq.Tests/ConnectionPool/Fakes.cs`
  que avança o relógio e dispara os timers vencidos.
- **Nada de ADOMD real.** O pool depende de `IXmlaConnectionFactory` e
  `IXmlaConnection` justamente para ser testável com falsos; use os que já
  existem em `Fakes.cs`.
- **Cultura explícita quando ela importa.** Se o teste envolve número ou data em
  texto, rode-o sob mais de uma cultura. O helper `UnderCulture` em
  `MaterializationTests` salva e restaura em `finally`.

### Verifique que o teste detecta a falha

Teste que passa não prova nada sozinho. Para invariante que importa — teto de
concorrência, ordem de operadores, citação de identificador — vale **quebrar a
implementação de propósito** e confirmar que o teste reprova, depois reverter.

Foi assim que se descobriu que remover o teto do semáforo do pool reprova 4
testes, e que desativar a expiração por ociosidade reprova outros 3. Sem essa
checagem, um teste que afirma o resultado certo pelo motivo errado passa
indefinidamente.

### As limitações do README são testadas

`tests/PowerLinq.Tests/Documentation/ReadmeLimitationsTests.cs` espelha a seção
**Limitações** do README raiz e a **Limitações atuais** do README do
`DaxConverter`: um teste por afirmação, verificando o **tipo** da exceção e algo da
**mensagem**.

Isso muda o ciclo de trabalho de um jeito específico: **ao implementar uma tradução
que remove uma limitação, é aquele arquivo que reprova primeiro.** Converta o teste
de limitação em teste de comportamento e atualize o README na mesma passada — a
documentação passa a ser verificada pelo CI em vez de pela memória de quem a
escreveu.

Que isso importa não é hipótese. A lista de limitações dava três exemplos de
construções que "devem falhar", e as três passaram a funcionar antes de alguém revisá-la.
Ela envelheceu prevendo exatamente o próprio envelhecimento.

Duas notas de forma:

- **Verifique a mensagem, não só o tipo.** Se a exceção passar a vir de outro lugar,
  um teste que só checa o tipo continua verde afirmando um contrato que não existe —
  foi o caso de um `InvalidOperationException` acidental já corrigido.
- **Teste também as linhas marcadas `// ok`.** Um arquivo que só verifica recusas
  passaria com a ordem canônica quebrada, deixando o README certo sobre o que falha e
  errado sobre o que funciona.

### Cobertura

Para ver a cobertura localmente, do mesmo jeito que o CI a coleta:

```bash
dotnet test tests/PowerLinq.Tests/PowerLinq.Tests.csproj -c Release \
  --results-directory artifacts/test-results \
  --collect:"XPlat Code Coverage"
```

O relatório sai em `artifacts/test-results/<guid>/coverage.cobertura.xml`. No CI,
o resumo por assembly aparece no summary do job — não é preciso baixar artefato.

O CI reprova quando um assembly cai abaixo do seu piso, declarado no passo
**Piso de cobertura** do `ci.yml`. Três coisas a saber sobre ele:

- **O piso é por assembly, não sobre o total.** O total esconde a lacuna que
  importa: já houve momento em que ele marcava ~55% com o `ConnectionPool` em
  0,00%, e qualquer piso agregado plausível teria passado.
- **Assembly que desaparece do relatório reprova.** Apagar os testes de um
  projeto o faz sumir do cálculo em vez de aparecer como 0%.
- **O piso tem folga proposital** em relação à cobertura atual. Ele é guarda
  contra regressão de substância, não meta a perseguir; folga zero reprovaria
  refator legítimo e treinaria todo mundo a ignorar o check.
- **A cobertura do `ConnectionPool` oscila entre execuções**, e o piso dele tem
  folga maior por isso. Em três execuções do mesmo commit, as branches deram
  66,07%, 71,42% e 71,42% — 5,35 pontos de variação — enquanto o `DaxConverter`
  deu 78,50% nas três. A causa é concorrência: naquele assembly há caminhos que só
  são exercitados conforme o entrelaçamento das threads. Se um teste de
  concorrência seu depende de uma interleaving específica para cobrir um caminho,
  ele provavelmente também **detecta o bug só às vezes** — vale reescrevê-lo para
  forçar a ordem, como os testes de 50 rodadas em `XmlaConnectionPoolTests` fazem.

Subir um piso é ato deliberado: diff de uma linha no workflow.

## Benchmarks

Ficam em `tests/PowerLinq.Benchmark` — ver o
[README de lá](tests/PowerLinq.Benchmark/README.md) para os filtros disponíveis.

```bash
dotnet run --project tests/PowerLinq.Benchmark -c Release -- --filter '*Visitor*'
```

**Se o PR toca o caminho quente de tradução ou de materialização, meça antes e
depois.** Use `--artifacts` para guardar as duas rodadas:

```bash
# antes, na branch base
dotnet run --project tests/PowerLinq.Benchmark -c Release -- \
    --filter '*Visitor*' --artifacts ./baseline

# depois, na sua branch
dotnet run --project tests/PowerLinq.Benchmark -c Release -- \
    --filter '*Visitor*' --artifacts ./depois
```

Duas advertências que vêm de erro cometido:

- **Compare rodadas feitas em sequência**, na mesma máquina e sem carga
  concorrente. Uma comparação entre rodadas separadas por horas mediu um caso de
  controle "melhorando" 2,9x sem que nada nele tivesse mudado.
- **Prefira alocação a tempo** como evidência. Alocação é determinística e não
  depende de carga; o tempo oscila. O ganho do cache de `[DaxColumn]` foi provado
  por −264 B por propriedade atributada e exatamente 0 B sem atributo, o que
  nenhuma medição de nanossegundo teria mostrado com a mesma clareza.

O README raiz documenta os números publicados e **quais seções foram remedidas**
depois de cada otimização. Se sua mudança invalida algum número lá, atualize a
seção ou marque-a como anterior à mudança — não deixe número desatualizado
passando por atual.

## Mensagens de erro

Toda mensagem visível ao usuário vive em `.resx`, nunca literal no código:

- `src/PowerLinq.DaxConverter/Localization/Resources/Messages.resx` (inglês, padrão e fallback)
- `src/PowerLinq.DaxConverter/Localization/Resources/Messages.pt-BR.resx`

**Os dois arquivos precisam ser atualizados juntos.** Uma chave presente só no
inglês funciona silenciosamente em pt-BR — o `ResourceManager` cai no fallback e
ninguém percebe até alguém ler a mensagem no idioma errado.

Mensagem boa diz **o que** não funciona, **onde**, e **o que fazer**. Compare:

```
InvalidOperationException: variable 'p' of type 'Produto' referenced from
scope '', but it is not defined
```

com o que substituiu isso:

```
Produto.Nome.Length has no DAX translation. Only direct property access on the
lambda parameter becomes a column; nested members such as Length, Year or Value
are not translated yet.
```

## Commits

O histórico segue [Conventional Commits](https://www.conventionalcommits.org/)
de forma consistente. Os tipos em uso, por frequência:

`refactor` · `chore` · `feat` · `test` · `fix` · `docs` · `ci` · `style` · `perf`

Use `!` para mudança incompatível (`refactor!:`). O assunto na imperativa, em
minúscula, sem ponto final.

O corpo do commit é onde mora o **porquê**. Um commit que corrige comportamento
deve dizer o que estava errado, como se manifestava e como foi verificado —
diff mostra o quê, o corpo explica o resto.

## Branches e PRs

**A issue não fecha sozinha com texto em português nem entre crases.** O GitHub só reconhece
`closes`, `fixes` e `resolves` — em inglês — e não interpreta o que está dentro de crase como
palavra-chave. Já houve dois casos que passaram batido assim, e os dois tiveram de ser
fechados à mão depois do merge. A forma que funciona é `Closes #123`, em texto puro.

Nomeie a branch `tipo/descricao-em-kebab`, com o mesmo vocabulário dos commits:
`fix/quote-table-identifiers`, `perf/column-reference-cache`,
`test/connection-pool-coverage`.

O merge é por PR, com o CI verde. Ver o
[template de PR](.github/pull_request_template.md) para o que o descritivo deve
cobrir.

**Antes de mergear, traga o `master` para dentro da branch e rode o CI de novo:**

```bash
git checkout sua-branch
git merge master
git push
```

O CI de um PR roda contra a **base do PR**, não contra o `master` atual. Dois PRs que
tocam arquivos diferentes mergeiam limpo e podem quebrar o `master` juntos — é
**conflito semântico**, e nenhum dos dois CI tem como pegá-lo. Aconteceu: um PR
acrescentou um fake de `IDaxQueryExecutor` e outro acrescentou um método à interface,
atualizando os fakes que existiam quando foi escrito. Cada um verde, a combinação não
compilava. Quem acusou foi o CI do push em `master`, ou seja, depois de já estar
quebrado para todo mundo.

O jeito automático de evitar isso é a proteção de branch **"require branches to be up
to date before merging"**, e ela **não está disponível aqui**: regras de branch exigem
GitHub Pro ou repositório público, e este é privado em plano Free — a API responde
`403 Upgrade to GitHub Pro`. Então a guarda é o hábito acima. Custa um push e um ciclo
de CI, e é onde a falha custa menos.

Se o PR corrige um comportamento, **inclua o teste que reprovaria antes** — é a
diferença entre corrigir e afirmar que corrigiu.

## Estrutura

```
src/PowerLinq.DaxConverter/      LINQ -> DAX, sem dependências de projeto
src/PowerLinq.ConnectionPool/    execução XMLA sobre ADOMD.NET
tests/PowerLinq.Tests/           suíte unitária
tests/PowerLinq.Benchmark/       BenchmarkDotNet
```

O ponto central é `DaxConverter/Syntax`: a consulta **nunca existe como texto**
até o fim. Cada nó sabe escrever a si mesmo, e a árvore fica acessível antes da
escrita. Ao acrescentar tradução, componha nós — não concatene string.

Metadados de pacote, framework e versões de dependência são centralizados em
`Directory.Build.props`, `Directory.Build.targets` e `Directory.Packages.props`.
Não declare `TargetFramework` nem versão de `PackageReference` num `.csproj`
individual.

Uma armadilha registrada: propriedade consumida pelo SDK **antes** de
`Directory.Build.targets` ser importado não funciona lá — `GenerateDocumentationFile`
é aceito e simplesmente não gera arquivo. Propriedades desse tipo ficam no
`.csproj`, com a razão comentada ao lado.
