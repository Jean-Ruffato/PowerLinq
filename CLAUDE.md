# CLAUDE.md

Guia para agentes que trabalham neste repositório. As convenções mecânicas
(indentação, chaves, ordenação de `using`, naming) estão no `.editorconfig` e
valem no build; o processo de contribuição está em `CONTRIBUTING.md`. Este
arquivo registra o que não cabe nesses lugares.

## Um tipo por arquivo

**Cada arquivo `.cs` declara no máximo um tipo.** "Tipo" aqui é qualquer
`class`, `record`, `struct`, `interface`, `enum` ou `delegate`.

- **Vale para tipos aninhados também.** Um fixture, fake ou DTO que hoje vive
  `private` dentro de outra classe sai para o próprio arquivo. O mecanismo para
  o código de teste está abaixo.
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

### Código de teste: fixtures de-aninhados

A classe de teste (`DaxSelectTests`) fica sozinha no seu arquivo. Cada helper
dela — `Produto`, `RecordingExecutor`, `NoopExecutor`, … — sai para um arquivo
irmão, **na mesma pasta e no mesmo namespace**, nomeado
`<ClasseDeTeste>.<Helper>.cs` (ex.: `DaxSelectTests.Produto.cs`). A pasta não
vira subpasta por classe de teste — isso mudaria o namespace e acionaria
`IDE0130`.

O helper de-aninhado leva o modificador **`file`** (`file sealed class Produto`),
não `private` nem `internal`:

- `file` reproduz exatamente a visibilidade que `private` aninhado dava: o tipo
  só existe dentro daquele arquivo.
- Sem `file`, os helpers colidiriam. Há ~20 `Produto`, ~21 `NoopExecutor` e
  ~16 `Venda` no projeto de teste — cada um com uma forma diferente, um por
  arquivo de teste.
- Zero mudança de semântica: as regras de naming do `.editorconfig` e a
  descoberta do xUnit continuam enxergando o mesmo que antes.

Um helper genuinamente compartilhado entre arquivos de teste vira `internal` de
verdade, um por arquivo, com nome único.
