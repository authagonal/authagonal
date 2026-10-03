---
layout: default
title: Escalabilidade
locale: pt
---

# Escalabilidade

O Authagonal foi concebido para escalar vertical e horizontalmente sem qualquer configuração especial.

## Sem estado por conceção {#stateless-by-design}

Todo o estado persistente é guardado no armazenamento subjacente (Azure Table Storage, DynamoDB no backend AWS, ou PostgreSQL no backend SQL auto-alojado). Não existe estado no processo que exija sessões fixas (sticky sessions) ou coordenação entre instâncias:

- **Chaves de assinatura**: carregadas do Table Storage, atualizadas de hora a hora
- **Códigos de autorização e tokens de atualização**: guardados no Table Storage, com imposição de utilização única
- **Prevenção de reprodução SAML**: IDs de pedido registados no Table Storage com eliminação atómica
- **State OIDC e verificadores PKCE**: guardados no Table Storage
- **Configuração de clientes e fornecedores**: obtida a cada pedido a partir do Table Storage

## Cifragem de cookies (proteção de dados) {#cookie-encryption-data-protection}

O key ring de Data Protection do ASP.NET Core protege o cookie de autenticação, pelo que todas as instâncias têm de partilhar um único. É persistido automaticamente, por esta ordem:

1. `DataProtection:BlobUri`, se estiver definido (um blob explícito, autenticado com `DefaultAzureCredential`).
2. Um contentor `dataprotection` na conta indicada por `Storage:ConnectionString`, a menos que seja o Azurite.
3. Na via de identidade gerida (`Storage:TableServiceUri`), o endpoint de blobs correspondente da mesma conta, `https://{account}.blob.…/dataprotection/keys.xml`. A identidade precisa da função Storage Blob Data Contributor na conta.

Só um endpoint de tabelas não reconhecido (Azurite, emuladores com estilo de caminho) recorre ao armazenamento em ficheiro por máquina, que é efémero e por pod: os reinícios terminam a sessão de todos e as réplicas não conseguem ler os cookies umas das outras. A verificação no arranque regista `Critical` quando isso acontece.

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

No backend AWS, passe um cliente S3 + bucket a `AddAuthagonalAwsStorage` para persistir o key ring no S3; sem isso, o key ring fica em memória e os cookies deixam de funcionar após um reinício e entre nós. Consulte [Instalação → Backend AWS](installation#aws-backend). No backend SQL, o key ring é persistido por `AddAuthagonalPostgres` / `AddAuthagonalSqlite`.

Persistir não é cifrar: o key ring é XML em texto simples, a menos que `DataProtection:KeyVaultKeyId` ou `DataProtection:CertificateThumbprint` esteja definido. No arranque, um key ring sem cifragem e ainda sem chaves é recusado, e um que já tenha chaves arranca com um registo `Critical` (`DataProtection:AllowUnencryptedKeyRing=true` aceita-o deliberadamente). Consulte [Configuração](configuration) para a tabela completa de `DataProtection:*`.

## Caches por instância {#per-instance-caches}

Um pequeno número de valores muito lidos e que mudam pouco é guardado em cache na memória de cada instância, para reduzir as idas e voltas ao Table Storage:

| Dados | Duração da cache | Impacto de estarem desatualizados |
|---|---|---|
| Documentos de descoberta OIDC | 60 minutos (configurável) | Deteção tardia da rotação de chaves do IdP |
| Metadados do IdP SAML | 60 minutos (configurável) | Igual |
| Origens permitidas por CORS | 60 minutos (configurável) | As novas origens demoram até uma hora a propagar-se |

Estas caches são aceitáveis para utilização em produção. Todas as durações são configuráveis através da secção de configuração `Cache`; consulte [Configuração](configuration). Se precisar de propagação imediata, reinicie as instâncias afetadas.

## Limitação de taxa {#rate-limiting}

Os endpoints propensos a abuso (registo por IP, reposição de palavra-passe por email de destino, SCIM por cliente, registo dinâmico de clientes por IP; consulte [Configuração → Limitação de taxa](configuration#rate-limiting)) estão protegidos por um limitador de taxa integrado.

Por predefinição, os limites são impostos **no processo, por nó**, atrás da abstração `IRateLimiter`, pelo que com N instâncias o teto efetivo é N vezes o valor configurado. É deliberado: o limitador é uma salvaguarda contra o abuso descontrolado de um único nó, e o limite global autoritativo pertence à periferia (WAF / ingress / CDN), que vê todo o tráfego antes de este ser distribuído pelo balanceador de carga.

Este compromisso é o certo para os limites de volume e o errado num caso: um orçamento que protege um **segredo adivinhável**. O `user_code` do device flow é uma cadeia curta de um alfabeto pequeno, e o limite de tentativas é a única coisa entre um atacante e um código que concede uma sessão ativa. Um teto que se multiplica pelo número de réplicas tem aí a forma errada, e faz do limite real uma propriedade da configuração do seu ingress em vez de uma propriedade do servidor.

Defina **`Auth:DurableRateLimiting=true`** para mover os contadores para o armazenamento que já utiliza, de modo que todas as réplicas partilhem um único orçamento. Custa uma ida e volta ao armazenamento por cada verificação de limite, usa janelas fixas (um orçamento de N permite até 2N na fronteira entre janelas) e falha de forma aberta se o armazenamento estiver inacessível, pelo que se sobrepõe à regra da periferia em vez de a substituir. As linhas de contadores são recolhidas automaticamente nos três backends. Consulte [Configuração → Limites ao nível do cluster](configuration#cluster-wide-limits-authdurableratelimiting).

## Clustering {#clustering}

Várias instâncias coordenam-se através de uma **eleição de líder** e de um **barramento de eventos entre nós**, ambos assentes em backends intercambiáveis:

- **Eleição de líder**: uma eleição baseada num lease (`Cluster:LeaseTtlSeconds`, predefinição 30 s, renovado aproximadamente a metade desse intervalo). Exatamente um nó detém o lease; a liderança transfere-se automaticamente quando o líder morre. O trabalho reservado ao líder só é executado no líder: a *desativação* de chaves de assinatura na expiração (quando `Auth:KeyRotationEnabled` está ativo), a passagem de reconciliação de concessões (apenas no backend Azure), o preenchimento retroativo da cifragem em repouso (quando `Auth:AtRestBackfillEnabled` está ativo; um nó que não é líder aguarda brevemente pela liderança e depois desiste) e a limpeza dos contadores de limitação de taxa (backend Azure com `Auth:DurableRateLimiting`). Com `Cluster:Enabled=false`, o nó único é líder permanente, pelo que uma implementação autónoma continua a executar todas estas tarefas.
- **Barramento de eventos**: notificações entre nós (por exemplo, a invalidação de cache em anfitriões multi-inquilino), consultadas a cada `Cluster:PollIntervalSeconds` (predefinição 3 s).

Cada instância gera no arranque um ID de nó aleatório de 12 caracteres hexadecimais para se identificar; não é persistido.

### Backends {#backends}

A **predefinição é no processo**: um nó único é sempre o seu próprio líder e os eventos são apenas locais, o que é correto para uma instância sem qualquer configuração. As implementações com vários nós substituem-na por um backend real através do callback `configureClustering` de `AddAuthagonal`:

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS: leadership + event bus via DynamoDB (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));

// PostgreSQL: leadership via a conditional-upsert lease row, event bus via an
// append-only log in the same database (Authagonal.SqlProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseSql(sqlDataSource));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` registam apenas o barramento de eventos, mantendo o lease em processo (sempre líder); use-os em nós que têm de receber eventos do cluster mas nunca devem disputar a liderança.

> **Nota:** com a predefinição no processo em vários nós, *todos* os nós julgam ser o líder. Isso é inofensivo para a maioria das cargas de trabalho, mas ative um backend de lease real antes de ligar `Auth:KeyRotationEnabled` em várias instâncias.

A **geração** de chaves de assinatura é independente dessa desativação reservada ao líder e não é conduzida por ela: todos os nós chamam `EnsureActiveKeyAsync` no arranque e em cada atualização de `Auth:SigningKeyCacheRefreshMinutes`, pelo que, com `KeyRotationEnabled` desativado (a predefinição), a substituição na expiração de 90 dias é conduzida inteiramente por essa via. A geração usa o seu próprio lease curto do cluster, pelo que tem um único escritor sempre que esteja configurado um backend de lease real. Com a predefinição no processo em vários nós, não existe essa coordenação, e dois nós que encontrem uma chave expirada no mesmo momento podem gerar cada um a sua; ambas acabam no JWKS e os tokens assinados por qualquer uma delas são verificados, mas a chave indicada como ativa pode oscilar. É mais uma razão para configurar um backend de lease real em implementações com vários nós.

Consulte a página [Configuração](configuration#cluster) para todas as definições do cluster.

### Implementações multi-inquilino {#multi-tenant-deployments}

No modo multi-inquilino (`AddAuthagonalCore()`), `TokenCleanupService`, `GrantReconciliationService`, `SigningKeyRotationService` e os serviços de inicialização da configuração (clientes, fornecedores, âmbitos, funções) não são registados: fazem parte da composição de inquilino único `AddAuthagonal()`, e o anfitrião gere esse trabalho por inquilino.

## Partição sobrecarregada do índice de nomes {#name-index-hot-partition}

A pesquisa por prefixo de nome na administração assenta nas tabelas de índice `UserFirstNames` / `UserLastNames`, que usam uma **única partição sobrecarregada**. Em escala, isto limita o débito de escrita no índice a cerca de 2000 operações/s, o que pode tornar-se um estrangulamento na criação/atualização de utilizadores sob carga elevada. Se não expuser a pesquisa por nome na administração, defina `Storage:NameIndexesEnabled = false` para dispensar totalmente estas escritas. Consulte [Configuração](configuration).

## Proxy de confiança e endpoints internos {#trusted-proxy-and-internal-endpoints}

Ao executar várias instâncias atrás de um balanceador de carga:

- **Cabeçalhos encaminhados**: a limitação de taxa e o bloqueio de contas baseiam-se no IP do cliente, resolvido a partir de `X-Forwarded-For`. Defina `ForwardedHeaders:KnownNetworks` com o CIDR do seu ingress / dos seus pods, para que o IP do cliente não possa ser falsificado entre instâncias. `ForwardedHeaders:ForwardLimit` tem a predefinição `1`. Consulte [Configuração](configuration#forwarded-headers-trusted-proxy).
- **Endpoints internos**: `/_internal/backchannel-logout` exige `Cluster:Secret` no cabeçalho `X-Cluster-Secret` (comparado em tempo constante). Sem ele, o endpoint não autoriza ninguém e responde 404; o IP de origem não é tratado como credencial, porque o loopback é o que um proxy inverso no mesmo anfitrião apresenta em todos os pedidos encaminhados, e uma gama privada corresponde a todas as cargas de trabalho vizinhas numa rede de cluster partilhada. `Cluster:AllowLoopbackWithoutSecret` é uma opção exclusiva para desenvolvimento que volta a admitir um par de loopback antes do encaminhamento. O produto tal como é fornecido nunca chama esta rota (a propagação para as sessões é feita no processo através de `SessionTermination`), pelo que só importa para uma propagação que construa por si.

## Recomendações de escalabilidade {#scaling-recommendations}

**Escalabilidade vertical**: aumente a CPU e a memória de uma única instância. É útil para tratar mais pedidos simultâneos por instância.

**Escalabilidade horizontal**: execute várias instâncias atrás de um balanceador de carga. Não são necessárias sessões fixas nem caches partilhadas. Cada instância é totalmente independente.

**Escalar até zero**: o Authagonal suporta implementações que escalam até zero (por exemplo, Azure Container Apps com `minReplicas: 0`). O primeiro pedido após um período de inatividade terá um arranque a frio de alguns segundos, enquanto o runtime .NET é inicializado e as chaves de assinatura são carregadas do armazenamento.
