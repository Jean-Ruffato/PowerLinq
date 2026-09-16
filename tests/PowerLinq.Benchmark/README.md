# PowerLinq.Benchmark

Baseline de performance da conversão LINQ -> DAX (hoje em
`PowerLinq.DaxConverter`), criado **antes** da migração de
concatenação de strings para árvore sintática. O objetivo é ter números comparáveis
para provar que a refatoração não regride performance.

## Como rodar

Sempre em `Release` — o BenchmarkDotNet recusa assemblies não otimizados.

```bash
# Tudo (demorado: ~30-60 min)
dotnet run -c Release --project tests/PowerLinq.Benchmark -- --filter '*'

# Um grupo específico
dotnet run -c Release --project tests/PowerLinq.Benchmark -- --filter '*Visitor*'
dotnet run -c Release --project tests/PowerLinq.Benchmark -- --filter '*EndToEnd*'

# Listar sem executar
dotnet run -c Release --project tests/PowerLinq.Benchmark -- --list flat

# Validação rápida (1 invocação por benchmark, números não confiáveis)
dotnet run -c Release --project tests/PowerLinq.Benchmark -- --filter '*' --job dry
```

Resultados em `BenchmarkDotNet.Artifacts/results/`.

## Cobertura

| Classe | Alvo |
|---|---|
| `DaxExpressionVisitorBenchmarks` | Cada ramo de `VisitBinary`/`VisitUnary`/`VisitMember`/`DaxLiteral.From` |
| `DaxExpressionVisitorScalingBenchmarks` | Custo da tradução vs. tamanho da árvore (1→64 predicados) |
| `DaxPipelineBuilderBenchmarks` | `Build` e `BuildCount` em todas as combinações de FILTER/TOPN/ORDER BY |
| `DaxPipelineBuilderScalingBenchmarks` | Escala por nº de ordenações e por tamanho de filtro |
| `DaxQueryCompositionBenchmarks` | Encadeamento imutável de `Where`/`ThenBy`/`Take` (1→32 elos) |
| `EntityMapperBenchmarks` | `GetTableName`, `GetColumnMappings`, `MapRow` |
| `EntityMapperMaterializationBenchmarks` | Materialização de 1→10.000 linhas |
| `EndToEndQueryBenchmarks` | `DaxTable` → `ToDaxString` e os métodos `*Async` |

`DaxContext` não é coberto: construtor privado em classe `sealed`, sem factory
pública — não há como instanciá-lo. Ver "Observações" abaixo.

## Gráficos

`RPlotExporter` está configurado e gera PNG por benchmark, **mas exige R
instalado**:

```bash
sudo apt install r-base   # Debian/Ubuntu
```

Sem R, a execução funciona normalmente e o exporter é ignorado. Os demais
exporters não dependem de nada externo:

- `report.html` — tabela navegável
- `report-github.md` — para colar em PR/issue
- `report-full.json` — para diff programático entre baseline e pós-refatoração
- `*-measurements.csv` — medições cruas (é a fonte que o R consome)

As classes `*Scaling*` e `*Composition*` usam `[Params]` justamente para
produzir um eixo X real nos gráficos.

## Comparando baseline vs. pós-refatoração

```bash
# Antes da refatoração
dotnet run -c Release --project tests/PowerLinq.Benchmark -- --filter '*' \
  --artifacts ./artifacts/baseline

# Depois
dotnet run -c Release --project tests/PowerLinq.Benchmark -- --filter '*' \
  --artifacts ./artifacts/syntax
```

O `MemoryDiagnoser` está sempre ligado: a migração para a árvore muda o perfil de
**alocação** tanto quanto o de tempo, e `Allocated` costuma ser o sinal mais
estável entre máquinas.

### Cuidado ao comparar classes isoladas entre as duas versões

Só `EndToEndQueryBenchmarks` é diretamente comparável entre o baseline anterior à árvore
sintática e a versão com ela, porque parte de um lambda nos dois casos.

`DaxPipelineBuilderBenchmarks` **não** é: no baseline o `Filter` era uma
`const string` já renderizada, e `Build` apenas a interpolava. Com a árvore o mesmo
método precisa percorrer a árvore do predicado e escrevê-la. O trabalho não
aumentou — ele migrou do visitor para o builder. Comparar as duas tabelas lado a
lado superestima a regressão.

## Observações levantadas ao montar os benchmarks

Situação após a migração para a árvore sintática:

1. ~~**`AppendValue` depende de cultura.**~~ **Corrigido.** `DaxWriter` formata
   todo literal numérico com `InvariantCulture`. Antes, em `pt-BR`, `100.5m`
   virava `100,5` e quebrava o DAX, já que a vírgula separa argumentos.
   Regressão coberta por testes em quatro culturas.

2. **`new DateTime(...)` inline não é traduzido.** Continua em aberto: vira
   `NewExpression`, que o visitor não trata. Hoje lança `NotSupportedException`
   com a expressão nomeada, em vez de emitir inteiros soltos silenciosamente.
   Só funciona via variável capturada — por isso `DateTimeLiteral` monta um
   `ConstantExpression` à mão.

3. ~~**Variável capturada custa ordens de grandeza a mais.**~~ **Corrigido.**
   `VisitMember` agora lê campo/propriedade do fechamento por reflexão e só cai
   em `Compile().DynamicInvoke()` no caso geral. Medido em `CapturedVariable`:
   84,5 µs → 0,95 µs.

4. ~~**Indentação quebra ao aninhar.**~~ **Corrigido.** `DaxWriter` indenta por
   profundidade; `TOPN` sobre `FILTER` reindenta inclusive o parêntese de
   fechamento. Fixado por teste de string exata.

5. **Componente hoje dominante: leitura de atributo.** Traduzir uma comparação
   simples custa ~950 ns, dos quais ~700 ns são
   `prop.GetCustomAttribute<DaxColumnAttribute>()`, chamado a cada acesso de
   membro e sem cache. Comparar `'Igualdade string (literal)'` (~950 ns) com
   `'Propriedade sem [DaxColumn]'` (~179 ns) isola o efeito. Um cache por
   `PropertyInfo` vale mais, em performance pura, do que qualquer ajuste no
   writer.

6. **`DaxContext` não é instanciável.** Construtor privado, classe `sealed`,
   `OnConfiguring` privado, vazio e não-virtual — mesmo por reflexão sempre
   lança `InvalidOperationException`, pois nenhum executor chega a ser
   configurado.

7. **`BuildCount` ficou mais caro no caso trivial.** Sem filtro, montar os nós
   da árvore custa mais que a interpolação única que existia antes: 22 ns → 166 ns
   (`DaxTable.CountAsync`). Em termos absolutos são ~144 ns antes de uma ida à
   rede medida em milissegundos, então o custo foi aceito em favor da estrutura.
