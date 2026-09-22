# CLAUDE.md

Guia para agentes que trabalham neste repositório. As convenções mecânicas
(indentação, chaves, ordenação de `using`, naming) estão no `.editorconfig` e
valem no build; o processo de contribuição está em `CONTRIBUTING.md`. Este
arquivo registra o que não cabe nesses lugares.

## Um tipo por arquivo

**Cada arquivo `.cs` declara no máximo um tipo.** "Tipo" aqui é qualquer
`class`, `record`, `struct`, `interface`, `enum` ou `delegate`.

- **Vale para tipos aninhados também.** Um fake ou DTO que hoje vive `private`
  dentro de outra classe sai para o próprio arquivo. A exceção é a fixture de
  uso único do código de teste, pelo motivo registrado abaixo.
- **O nome do arquivo é o nome do tipo.** `DaxParameter<T>` → `DaxParameter.cs`
  (a aridade genérica não entra no nome).
- **Interface e implementação nunca dividem arquivo.** `IXmlaConnection.cs` e
  `AdomdXmlaConnection.cs`, separados. O mesmo para uma fábrica e o que ela cria.
- **Um `enum` que acompanha um tipo também sai.** `DaxDivergenceKind` não fica no
  mesmo arquivo que `DaxDivergence`.
- **Sem exceção para hierarquia fechada.** Os `record` de `DaxStage`, os nós de
  `Syntax/`, os `record` de esquema em `Model/` — cada um no seu arquivo. A pasta
  (`Queries/`, `Syntax/`, `Model/`) passa a agrupar o que antes um arquivo
  agrupava.
- **Não se aplica a arquivo sem declaração de tipo** — `Program.cs` com
  top-level statements, `GlobalUsings.cs`, gerados em `obj/`.

### Por quê

- `git blame`, histórico e diff passam a falar de um tipo por vez. Mover um tipo
  é mover um arquivo, não recortar um trecho de um arquivo que continua vivo.
- Achar um tipo é achar o arquivo com o nome dele — sem "onde foi que
  declararam isso".
- Conflito de merge fica contido: dois tipos editados ao mesmo tempo são dois
  arquivos, e mergeiam limpo.
- O `.editorconfig` já força namespace `file_scoped`, `IDE0130` (namespace
  espelha a pasta) e doc obrigatória em membro público novo. "Um tipo por
  arquivo" é a peça que faltava para o layout do disco espelhar o modelo de
  tipos.

### Código de teste: fixture de uso único continua aninhada

"Um tipo por arquivo" para em `src/` e nos helpers compartilhados entre arquivos
de teste. **A fixture de uso único — a `Produto`, a `Venda`, o `NoopExecutor` que
só uma classe de teste usa — continua `private` dentro da classe de teste.**

A regra anterior mandava de-aninhar cada helper para um arquivo irmão
(`DaxSelectTests.Produto.cs`), no mesmo namespace, com o modificador **`file`**.
Isso não compila, e o motivo é o próprio `file`: ele escopa o tipo ao **arquivo
em que é declarado**, não à classe de teste. A fixture em um arquivo irmão fica
invisível para a classe que a usa — `DaxSelectTests.cs` deixa de enxergar
`Produto`, e o build para em `CS0246` em cada uso.

As quatro coisas que a regra pedia ao mesmo tempo — um tipo por arquivo, arquivo
irmão, mesmo namespace, e `file` preservando o nome curto — não são satisfazíveis
juntas em C#. Manter a fixture aninhada é a saída que menos custa. As outras duas
eram renomear ~237 tipos para nomes únicos (`DaxSelectTestsProduto`, repetido em
cada uso dentro do teste) ou dar um namespace por classe de teste; nenhuma paga o
que cobra para uma fixture de cinco linhas usada em um lugar só.

E o que a regra ganha — blame, merge e descoberta por tipo — vale para o tipo que
várias coisas referenciam. Uma fixture de uso único não é isso: ela é lida junto
com o teste, e é ali que ela ajuda.

Um helper genuinamente compartilhado entre arquivos de teste é outro caso, e esse
**sai**: vira `internal` de verdade, um por arquivo, com nome único. Foi o que
aconteceu com `Fakes.cs`, que virou `ControllableTimeProvider.cs`, `FakeTimer.cs`,
`FakeXmlaConnection.cs`, `FakeXmlaConnectionFactory.cs`,
`ThrowingXmlaConnectionFactory.cs` e `BrokenSession.cs`.
