# PowerLinq.ConnectionPool

Executores e pool de conexões XMLA para executar as consultas DAX geradas pelo
PowerLinq em modelos semânticos do Power BI e do Analysis Services.

> Pacote em pré-lançamento, sob licença MIT. A API ainda pode mudar antes da
> versão estável.

## Licença e distribuição

Licença MIT — ver o arquivo `LICENSE`, incluído no pacote. A publicação
automática no NuGet.org público ainda não está ligada enquanto a API está em
pré-lançamento. O ADOMD.NET, do qual este pacote depende, é proprietário da
Microsoft e segue a própria licença.

O pacote referencia `PowerLinq.DaxConverter`, portanto não é necessário instalar
os dois separadamente.

## Contexto e injeção de dependência

Defina um contexto semelhante ao `DbContext` do Entity Framework:

```csharp
using PowerLinq.DaxConverter;
using PowerLinq.DaxConverter.Context;
using PowerLinq.DaxConverter.Execution;

public sealed class CatalogoContext(
    IDaxQueryExecutor executor,
    IDaxTableFactory tableFactory) : DaxContext(executor, tableFactory)
{
    public IDaxTable<Produto> Produtos => Set<Produto>();
}
```

Configure a seção `PowerBi`:

```json
{
  "PowerBi": {
    "XmlaEndpoint": "powerbi://api.powerbi.com/v1.0/myorg/MeuWorkspace",
    "Dataset": "MeuDataset",
    "TenantId": "<tenant-id>",
    "ClientId": "<client-id>",
    "ClientSecret": "<client-secret>",
    "MaxConnectionsPerModel": 4,
    "ConnectionIdleTimeoutMinutes": 15,
    "TokenRefreshMarginSeconds": 300,
    "QueryTimeoutSeconds": 120,
    "ConnectionPoolEnabled": true
  }
}
```

Registre o pool e o executor:

```csharp
using PowerLinq.ConnectionPool;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.Extensions.DependencyInjection;

builder.Services.AddXmlaConnectionPool(builder.Configuration);
builder.Services.AddScoped<IDaxQueryExecutor, PooledXmlaQueryExecutor>();
builder.Services.AddDaxContext<CatalogoContext>(builder.Configuration);
```

Depois, injete `CatalogoContext` nos serviços. O contexto depende de
`IDaxQueryExecutor` e `IDaxTableFactory`, enquanto suas propriedades expõem
`IDaxTable<T>`; nenhuma camada de negócio precisa conhecer as implementações
`XmlaQueryExecutor` ou `DaxTable<T>`.

### Idioma das mensagens

Por padrão, mensagens de validação e exceções usam inglês. Os textos ficam em
recursos `.resx` e o localizador é registrado como uma dependência imutável. Para
selecionar português, configure opcionalmente:

```json
{
  "PowerLinq": {
    "Localization": {
      "Language": "pt-BR"
    }
  }
}
```

`pt` e `pt-BR` selecionam português. `en`, configuração ausente ou qualquer
idioma não suportado selecionam inglês como fallback.

O pool limita a concorrência por endpoint e modelo, reutiliza conexões ociosas,
renova tokens antes da expiração e repete uma vez a consulta quando identifica
uma sessão quebrada. Defina `ConnectionPoolEnabled` como `false` para usar uma
conexão e um token novos a cada consulta.

### Cancelamento

O `CancellationToken` interrompe a consulta **em execução**, não apenas a espera:
ele é ligado ao comando ADOMD, e o cancelamento chega ao chamador como
`OperationCanceledException`.

A conexão cancelada é **descartada**, não devolvida ao pool. É a escolha
conservadora: depois de cancelar, o estado da sessão é indeterminado, e devolver
uma conexão possivelmente envenenada contaminaria a próxima consulta a alugá-la.
Cancelar não dispara a repetição — cancelamento é decisão do chamador, não falha
a recuperar.

### Timeout

`QueryTimeoutSeconds` vale nos **dois** modos. Antes só o pool o aplicava, então
desligar o pool mudava o timeout efetivo sem avisar.

O executor direto (modo sem pool) serializa o acesso à conexão com um semáforo,
porque o ADOMD.NET não é thread-safe e o executor é registrado por escopo — duas
consultas paralelas no mesmo escopo compartilhariam a conexão. Quem precisa de
concorrência real deve usar o pool.

## Autenticação

Com `TenantId`, `ClientId` e `ClientSecret`, o pacote usa um service principal do
Microsoft Entra ID. Quando `ClientSecret` está vazio no modo com pool, a
autenticação usa `DefaultAzureCredential`, permitindo cenários como Managed
Identity.

Nunca armazene o segredo no `appsettings.json` versionado. Em desenvolvimento,
prefira user-secrets:

```bash
dotnet user-secrets set "PowerBi:TenantId" "<tenant-id>"
dotnet user-secrets set "PowerBi:ClientId" "<client-id>"
dotnet user-secrets set "PowerBi:ClientSecret" "<client-secret>"
```

O workspace precisa permitir acesso XMLA e o service principal deve ter acesso
ao workspace e ao modelo semântico.

## Requisitos

- .NET 10 ou superior;
- endpoint XMLA acessível do Power BI Premium/PPU/Fabric ou Analysis Services.
