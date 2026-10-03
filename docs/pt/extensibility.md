---
layout: default
title: Extensibilidade
locale: pt
---

# Extensibilidade

A Authagonal pode ser alojada como biblioteca no seu próprio projeto ASP.NET Core, com controlo total sobre as implementações dos serviços.

## Métodos de extensão {#extension-methods}

Três métodos integram a Authagonal em qualquer aplicação ASP.NET Core:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);  // Services + auth + storage

var app = builder.Build();
app.UseAuthagonal();              // Middleware pipeline
app.MapAuthagonalEndpoints();     // All endpoints
app.MapFallbackToFile("index.html");
app.Run();
```

### Alojamento multi-inquilino {#multi-tenant-hosting}

Para implementações multi-inquilino, utilize antes `AddAuthagonalCore()`. Regista os endpoints, o middleware e os serviços principais, mas omite o armazenamento e os serviços em segundo plano; é o anfitrião que os fornece por inquilino. A gestão das chaves de assinatura recorre por predefinição ao singleton `ProtocolKeyManager` de `Authagonal.Protocol`, e um anfitrião que registe o seu próprio `IKeyManager` antes de `AddAuthagonalCore()` mantém-no:

```csharp
builder.Services.AddScoped<ITenantContext, MyTenantContext>();
builder.Services.AddScoped<IKeyManager, MyPerTenantKeyManager>();
builder.Services.AddAuthagonalCore(builder.Configuration);
```

`IKeyManager` e as interfaces de armazenamento (`IClientStore`, `IScimTokenStore`, etc.) são resolvidos a partir de `HttpContext.RequestServices` no momento do pedido, pelo que os registos scoped funcionam corretamente para o isolamento por inquilino.

### Incorporar apenas `Authagonal.Protocol` {#embedding-authagonalprotocol-alone}

Um anfitrião que pretenda apenas a superfície do protocolo OIDC (a sua própria autenticação, o seu próprio pipeline e endpoints `/connect/*` prontos a usar) chama `AddAuthagonalProtocol()` + `MapAuthagonalProtocolEndpoints()` sem nada de `Authagonal.Server`.

`/connect/authorize`, `/connect/token`, `/connect/userinfo` e `/connect/par` também recusam http sem encriptação neste formato, conforme a RFC 6749 §3.1/§3.2. Como o pacote é mapeado num pipeline que não lhe pertence, o requisito acompanha os endpoints como filtro e não como middleware, pelo que se mantém independentemente da forma como compõe o pipeline e quer mapeie a superfície inteira, quer um endpoint de cada vez. Duas consequências a conhecer antes de atualizar:

- **Atrás de um proxy que termina o TLS, chame `UseForwardedHeaders` com o proxy declarado.** O filtro lê o esquema depois do encaminhamento, pelo que um `X-Forwarded-Proto: https` reencaminhado o satisfaz. Sem esse middleware, o seu anfitrião vê tráfego sem encriptação, o que também significa que os seus cookies não estão a ser marcados como `Secure` e que os URLs absolutos gerados estão errados, por isso vale a pena corrigir o problema em vez de o contornar. Preencha `KnownProxies` / `KnownNetworks` quando o registar: o ASP.NET Core interpreta um conjunto de confiança vazio como "todos os autores de chamadas são proxies de confiança", o que entrega o esquema a qualquer pessoa que consiga chegar ao seu anfitrião. Se o corpo da recusa mencionar um `X-Forwarded-Proto` não aplicado, é este o middleware que está a pedir.
- **Um anfitrião que serve efetivamente a superfície do protocolo por http ativa a opção**, da mesma forma que o servidor:

```csharp
builder.Services.AddAuthagonalProtocol(o =>
{
    o.AuthenticationScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    o.AllowInsecureHttp = builder.Environment.IsDevelopment();   // never in production
});
```

A descoberta e o JWKS não são deliberadamente condicionados: são metadados públicos, e um cliente que não os consiga ler não pode sequer saber que precisa de https.

Quando utiliza `AddAuthagonal()` (o servidor completo), não define isto separadamente: `Auth:AllowInsecureHttp` é transmitido para as opções do protocolo por si, pelo que um único interruptor rege toda a superfície.

## Substituir serviços {#overriding-services}

Registe as suas implementações personalizadas **antes** de chamar `AddAuthagonal()`. A Authagonal utiliza `TryAdd` internamente, pelo que os seus registos têm precedência:

```csharp
// Custom implementations, registered first so they won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

`IAuthHook` é um caso especial: é um pipeline de registo múltiplo. Registe quantos hooks quiser (com qualquer tempo de vida, incluindo `AddScoped`) e todos são executados pela ordem de registo. O `NullAuthHook`, que não faz nada, só é adicionado quando nenhum hook foi registado até ao momento em que `AddAuthagonal()` / `AddAuthagonalCore()` é executado, pelo que deve registar sempre os seus hooks primeiro.

### Pontos de extensibilidade {#extensibility-points}

| Interface | Predefinição | Finalidade |
|---|---|---|
| `IAuthHook` | `NullAuthHook` (não faz nada; só é adicionado quando nenhum hook está registado) | Hooks do ciclo de vida para eventos de autenticação: registo de auditoria, validação personalizada, webhooks. Podem ser registados vários hooks; todos são executados por ordem |
| `IEmailService` | `NullEmailService` (não faz nada), ou o remetente Resend integrado quando `Email:ResendApiKey` está configurado | Envio de emails de verificação, de reposição da palavra-passe e de aviso de conta existente |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator` (scoped) | Aprovisionamento de utilizadores em aplicações a jusante |
| `ISecretProvider` | `PlaintextSecretProvider`, ou o `KeyVaultSecretProvider` integrado quando `SecretProvider:VaultUri` está configurado | Armazenamento reversível de segredos (Key Vault, AWS Secrets Manager, Vault Transit, etc.) |
| `ITenantContext` | `DefaultTenantContext` (lê a partir de `IConfiguration`) | Resolução do inquilino em implementações multi-inquilino |
| `IKeyManager` | `ProtocolKeyManager` (singleton, de `Authagonal.Protocol`) | Gestão das chaves de assinatura; substitua-o para isolar as chaves por inquilino |
| `IProvisioningAppProvider` | `ConfigProvisioningAppProvider` (scoped) | Resolve as aplicações de aprovisionamento disponíveis; substitua-o para uma resolução dinâmica ou por inquilino |
| `IAuditLogger` | `NullAuditLogger` (não faz nada) | Registo de auditoria das alterações de configuração e dos eventos relevantes para a segurança |
| `IClientCredentialsClaimsTransformer` | `NullClientCredentialsClaimsTransformer` (singleton, de `Authagonal.Protocol`) | Validar o contexto indicado por quem chama numa emissão `client_credentials` e impor claims no token, ou recusá-la |
| `ITokenExchangeSubjectTransformer` | `NullTokenExchangeSubjectTransformer` (singleton, de `Authagonal.Protocol`) | Mapeamento do titular na troca de tokens RFC 8693; consulte [Autenticação agêntica](agentic-auth) |
| `ITurnstileKeyProvider` | `OptionsTurnstileKeyProvider` (scoped, lê `TurnstileOptions`) | Que sitekey e segredo do Turnstile se aplicam a este pedido |
| `IInteractiveCorsOriginPolicy` | `DenyInteractiveCorsOriginPolicy` (singleton, recusa todas as origens) | Origens autorizadas a fazer chamadas entre origens com credenciais para `/api/auth/*` |

Há mais três pontos de extensão que vivem ao **nível do armazenamento** e não na injeção de dependências: `IFieldCipher`, `IIndexTokenizer` e `IChangeWriter` (todos em `Authagonal.Core.Services`). Os fornecedores de armazenamento aceitam-nos como parâmetros opcionais do construtor; consulte as respetivas secções abaixo.

## IAuthHook {#iauthhook}

A interface `IAuthHook` disponibiliza hooks para o ciclo de vida da autenticação. Os métodos no caminho crítico (autenticação, criação de utilizadores, emissão de tokens) podem lançar uma exceção para interromper a operação; os métodos mais recentes são notificações posteriores ao facto. Podem ser registadas várias implementações de `IAuthHook`, e todas são executadas pela ordem de registo.

```csharp
public interface IAuthHook
{
    // Core lifecycle: implement these
    Task OnUserAuthenticatedAsync(string userId, string email, string method,
        string? clientId = null, CancellationToken ct = default);
    Task OnUserCreatedAsync(string userId, string email, string createdVia,
        CancellationToken ct = default);
    Task OnLoginFailedAsync(string email, string reason,
        CancellationToken ct = default);
    Task OnTokenIssuedAsync(string? subjectId, string clientId, string grantType,
        CancellationToken ct = default);
    Task<MfaPolicy> ResolveMfaPolicyAsync(string userId, string email,
        MfaPolicy clientPolicy, string clientId, CancellationToken ct = default);
    Task OnMfaVerifiedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default);
    Task OnUserUpdatedAsync(string userId, string email, string updatedVia,
        CancellationToken ct = default);
    Task OnUserDeletedAsync(string userId, string email, string deletedVia,
        CancellationToken ct = default);

    // Additive notifications: default no-op implementations, so existing
    // hooks keep compiling as the interface grows
    Task OnMfaVerifyFailedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnEmailConfirmedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaEnrolledAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaCredentialRemovedAsync(string userId, string email, string mfaMethod,
        bool mfaDisabled, CancellationToken ct = default) => Task.CompletedTask;
    Task OnRecoveryCodesRegeneratedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnPasswordChangedAsync(string userId, string email, string changedVia,
        CancellationToken ct = default) => Task.CompletedTask;

    // Token gate and agentic / consent notifications (also default no-ops)
    Task OnTokenIssuingAsync(TokenIssuanceContext context,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnDelegationMintedAsync(DelegationAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnApprovalRequestedAsync(ApprovalAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnApprovalResolvedAsync(ApprovalAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnAgentConsentChangedAsync(string subjectId, string clientId, string change,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnConsentRevokedAsync(string subjectId, string clientId, int grantsRemoved,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnCapabilityTicketRedeemedAsync(string ticketId, string? subjectId, string clientId,
        CancellationToken ct = default) => Task.CompletedTask;
}
```

### Parâmetros {#parameters}

| Método | Notas e valores de `method` / `via` |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`, `"passkey"`, `"saml"`, `"oidc"` |
| `OnUserCreatedAsync` | `"admin"`, `"saml"`, `"oidc"` |
| `OnUserUpdatedAsync` | `"admin"`, `"self"` (os anfitriões podem passar os seus próprios valores, por exemplo uma origem SCIM) |
| `OnUserDeletedAsync` | `"admin"`; apenas notificação, o registo pode já não ser legível |
| `OnLoginFailedAsync` | `"user_not_found"`, `"invalid_password"`, etc. |
| `OnTokenIssuedAsync` | Tipos de concessão: `"authorization_code"`, `"refresh_token"`, `"client_credentials"` |
| `ResolveMfaPolicyAsync` | Chamado depois da verificação da palavra-passe; devolve a política de MFA efetiva para o utilizador. Predefinição: devolver `clientPolicy` sem alterações. |
| `OnMfaVerifiedAsync` | `"totp"`, `"webauthn"`, `"recovery"` |
| `OnMfaVerifyFailedAsync` | Os mesmos métodos que `OnMfaVerifiedAsync`. Só é acionado depois de credenciais válidas do primeiro fator, pelo que uma rajada destes eventos é um forte sinal de tentativa de contornar a MFA (distinto de `OnLoginFailedAsync`, a fase da palavra-passe) |
| `OnEmailConfirmedAsync` | O utilizador confirmou o seu email através da hiperligação de verificação; já está persistido |
| `OnMfaEnrolledAsync` | `"totp"`, `"webauthn"`; a credencial já está ativa |
| `OnMfaCredentialRemovedAsync` | `"totp"`, `"webauthn"`, `"recoverycode"`; `mfaDisabled` é true quando a remoção não deixou nenhum fator principal |
| `OnRecoveryCodesRegeneratedAsync` | O conjunto anterior de códigos de recuperação é invalidado |
| `OnPasswordChangedAsync` | Por exemplo `"reset"`; a alteração está persistida e as sessões existentes foram invalidadas |
| `OnTokenIssuingAsync` | Barreira anterior à emissão, ao contrário de `OnTokenIssuedAsync`. É acionado em `authorization_code`, `refresh_token` e `device_code`, e nas duas emissões agênticas (troca de tokens delegada e `client_credentials` para um cliente com perfil de agente). Lance uma exceção para recusar: uma exceção simples torna-se `access_denied` com a respetiva mensagem; lance `ProtocolTokenException` para indicar o seu próprio erro OAuth. Na atualização é executado antes da rotação, pelo que uma recusa deixa o token de atualização apresentado utilizável. O contexto contém `ClientId`, `SubjectId`, `GrantType`, `Scopes`, `RequestedAuthorityJson` e ainda `OrganizationId` / `OrganizationSlug` quando o pedido selecionou uma organização |
| `OnDelegationMintedAsync` | Foi emitido um token delegado (de identidade composta) através da troca de tokens; apenas notificação |
| `OnApprovalRequestedAsync` | Uma troca delegada ficou suspensa numa ação com política de pedir aprovação e foi criada uma aprovação pendente |
| `OnApprovalResolvedAsync` | Uma aprovação pendente foi aprovada ou recusada pelo utilizador |
| `OnAgentConsentChangedAsync` | `change` é `"granted"` ou `"revoked"` (consentimento permanente para agentes) |
| `OnConsentRevokedAsync` | Um utilizador revogou uma aplicação autorizada; o consentimento e as concessões do cliente vinculadas à sessão já desapareceram. `grantsRemoved` indica quantas foram removidas (0 significa nenhuma) |
| `OnCapabilityTicketRedeemedAsync` | Um ticket de capacidade foi trocado pelo token a que está vinculado |

### Exemplo: registo de auditoria {#example-audit-logger}

```csharp
public sealed class AuditAuthHook(ILogger<AuditAuthHook> logger) : IAuthHook
{
    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Login: {Email} via {Method}", email, method);
        return Task.CompletedTask;
    }

    public Task OnUserCreatedAsync(string userId, string email,
        string createdVia, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] User created: {Email} via {Via}", email, createdVia);
        return Task.CompletedTask;
    }

    public Task OnLoginFailedAsync(string email, string reason, CancellationToken ct)
    {
        logger.LogWarning("[AUDIT] Login failed: {Email} ({Reason})", email, reason);
        return Task.CompletedTask;
    }

    public Task OnTokenIssuedAsync(string? subjectId, string clientId,
        string grantType, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Token issued: {ClientId} ({GrantType})",
            clientId, grantType);
        return Task.CompletedTask;
    }

    // ... remaining required methods return Task.CompletedTask
}
```

### Exemplo: restrição de domínio {#example-domain-restriction}

```csharp
public sealed class DomainRestrictionHook : IAuthHook
{
    private static readonly HashSet<string> BlockedDomains = ["competitor.com"];

    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        var domain = email.Split('@').Last();
        if (BlockedDomains.Contains(domain))
            throw new InvalidOperationException($"Domain {domain} is not allowed");

        return Task.CompletedTask;
    }

    // ... other methods return Task.CompletedTask
}
```

## IClientCredentialsClaimsTransformer {#iclientcredentialsclaimstransformer}

Um token `client_credentials` não tem titular, pelo que o ponto de extensão da troca de tokens não o consegue alcançar. Este ponto de extensão destina-se a um serviço próprio que chama a API e cujo token tem de indicar o contexto em que age (uma organização, um inquilino) sem haver um utilizador. É executado depois de o cliente, os seus âmbitos e quaisquer recursos RFC 8707 terem sido validados, e antes de o token ser emitido.

```csharp
public interface IClientCredentialsClaimsTransformer
{
    Task<ClientCredentialsClaimsResult> TransformAsync(
        OAuthClient client,
        IReadOnlyList<string> grantedScopes,
        IReadOnlyDictionary<string, string> extraParameters,
        CancellationToken ct = default);
}
```

- `extraParameters` contém os parâmetros de formulário do pedido de token que não pertencem ao protocolo (com valor único; prevalece o primeiro), por exemplo um `organization_id` enviado por quem chama.
- Devolva `ClientCredentialsClaimsResult.Allow(claims)` para impor `claims` no token (null ou vazio deixa-o inalterado), ou `ClientCredentialsClaimsResult.Reject(error, description)` para recusar a emissão com esse erro OAuth.
- Os nomes de claims reservados do protocolo continuam bloqueados na emissão.
- Valide a vinculação indicada por quem chama contra a sua própria fonte de autoridade; não a copie para o token sem verificação.
- O `NullClientCredentialsClaimsTransformer` predefinido é registado com `TryAddSingleton`, pelo que deve registar o seu primeiro para o substituir.

## ITurnstileKeyProvider {#iturnstilekeyprovider}

As duas chaves do Turnstile provêm de um único objeto para que o widget que o browser apresenta e o segredo com que o servidor faz a verificação nunca possam divergir. O `OptionsTurnstileKeyProvider` predefinido lê `SiteKey` e `SecretKey` de `TurnstileOptions`, o que serve um anfitrião que sirva um único domínio. Um anfitrião que sirva domínios fornecidos pelos clientes, em que a Cloudflare limita o número de nomes de anfitrião de um widget, regista a sua própria implementação scoped, que devolve o par de chaves do widget atribuído ao anfitrião que faz o pedido.

```csharp
public interface ITurnstileKeyProvider
{
    string? SiteKey { get; }     // null when disabled
    string? SecretKey { get; }   // null or empty disables enforcement
}
```

É registado com `TryAddScoped`, pelo que prevalece um registo feito antes de `AddAuthagonal`.

## IInteractiveCorsOriginPolicy {#iinteractivecorsoriginpolicy}

A API de autenticação interativa (`/api/auth/*`) recusa por predefinição chamadas entre origens com credenciais, porque é utilizada pela aplicação de início de sessão servida a partir da mesma origem. Um anfitrião que permita a um inquilino construir o seu próprio ecrã de início de sessão noutra origem implementa esta interface para garantir origens específicas.

```csharp
public interface IInteractiveCorsOriginPolicy
{
    ValueTask<bool> IsAllowedAsync(HttpContext context, string origin, string path);
}
```

- É consultada por pedido e por origem; a resolução do inquilino já foi executada quando é chamada.
- Devolver true permite que essa origem leia as respostas autenticadas dos endpoints de conta, sessão, perfil e configuração de MFA para quem tiver sessão iniciada. Responda apenas por origens que o anfitrião controla ou verificou, nunca por uma retirada do pedido.
- A predefinição (`DenyInteractiveCorsOriginPolicy`, `TryAddSingleton`) devolve false para todas as origens.

## ISecretProvider {#isecretprovider}

`ISecretProvider` (em `Authagonal.Core.Services`) é o ponto de extensão de encriptação reversível para segredos guardados, como segredos de cliente SSO, palavras-passe SMTP e sementes TOTP. `ProtectAsync` transforma um texto simples numa referência que o armazenamento persiste; `ResolveAsync` transforma a referência de volta no texto simples. O `PlaintextSecretProvider` predefinido guarda os valores tal como estão (a referência É o valor).

```csharp
public interface ISecretProvider
{
    Task<string> ResolveAsync(string secretReference, CancellationToken ct = default);
    Task<string> ProtectAsync(string name, string plaintext, CancellationToken ct = default);
}
```

Definir `SecretProvider:VaultUri` liga automaticamente o `KeyVaultSecretProvider` integrado (Azure Key Vault através de `DefaultAzureCredential`). Para qualquer outra coisa, registe a sua própria implementação antes de `AddAuthagonal()`.

## Encriptação de campos PII: IFieldCipher {#pii-field-encryption-ifieldcipher}

`IFieldCipher` encripta em repouso valores individuais de campos PII dos utilizadores (telefone, empresa, atributos personalizados, email e nomes na linha do perfil). É um ponto de extensão ao nível do armazenamento: os fornecedores de armazenamento recebem-no como parâmetro opcional do construtor (por exemplo, `TableUserStore`) e, quando está ausente, aplica-se o `NullFieldCipher`, que deixa os valores passar inalterados, pelo que a encriptação é estritamente opcional e os anfitriões não configurados continuam a guardar texto simples.

```csharp
public interface IFieldCipher
{
    Task<string> ProtectAsync(string plaintext, CancellationToken ct = default);
    Task<string> ResolveAsync(string stored, CancellationToken ct = default);

    // Batch variants have default loop implementations; override for backends
    // with a one-round-trip batch primitive (e.g. Vault Transit)
    Task<IReadOnlyList<string>> ProtectManyAsync(IReadOnlyList<string> plaintexts,
        CancellationToken ct = default);
    Task<IReadOnlyList<string>> ResolveManyAsync(IReadOnlyList<string> stored,
        CancellationToken ct = default);
}
```

Há dois pontos do contrato que importam. `ProtectAsync` tem de devolver um token de texto cifrado autodescritivo (por exemplo, o `vault:v{n}:...` do Vault Transit), e `ResolveAsync` tem de devolver inalterado um valor que não reconheça como texto cifrado seu. É esta regra de passagem que permite introduzir a encriptação gradualmente sobre as linhas existentes: a leitura de uma linha não migrada devolve o texto simples antigo, e a escrita seguinte volta a protegê-lo.

## Pesquisa por índice cego: IIndexTokenizer {#blind-index-search-iindextokenizer}

`IIndexTokenizer` mantém os campos encriptados pesquisáveis. Transforma um valor de texto simples normalizado num token de índice cego determinístico e seguro para chaves de tabela, normalmente um HMAC com chave em que a chave está fora da base de dados. O determinismo significa que uma pesquisa por igualdade continua a funcionar ("email = x" passa a "token = HMAC(x)"), ao passo que um dump da base de dados não consegue nem recalcular nem reverter um token. A pesquisa por prefixo é construída por cima, convertendo separadamente em token cada prefixo de um valor, uma vez que um HMAC com chave destrói a ordenação e as varreduras por intervalo.

> **O que um dump ainda revela.** "Nem recalcular nem reverter" é verdade para um token isolado e não
> para o índice no seu conjunto. Sobrevivem três resíduos, e convém conhecê-los antes de confiar nisto:
>
>   *(Corrigido.)* ~~**Estrutura.** O índice de prefixos escreve uma linha por prefixo, pelo que o número de linhas
>   de um registo é igual ao comprimento do campo indexado.~~ Cada valor indexado escreve agora um número fixo de linhas,
>   completado com linhas fictícias que nenhuma consulta consegue produzir e que um dump não consegue distinguir de prefixos reais.
> - **Igualdade e frequência.** Os tokens são determinísticos por construção, que é o que permite que a
>   pesquisa funcione, pelo que um dump mostra que registos partilham um valor e quão comum é cada valor. O índice de domínios
>   agrupa a sua população por empregador, o que muitas vezes identifica pessoas sem recuperar um endereço.
> - **Texto simples escolhido.** Um atacante que consiga ler o armazenamento *e* fazer com que valores sejam indexados
>   (registar uma conta, ser aprovisionado por SCIM) pode submeter um candidato e procurar o respetivo token.
>   Isso recupera qualquer valor adivinhável (domínios comuns, nomes próprios comuns), independentemente de onde a chave
>   esteja, porque o oráculo é o caminho de escrita e não a cifra.
>
> A conversão em tokens protege contra o caso para o qual foi construída: alguém que tem um dump e mais nada,
> a tentar ler endereços. Os dois resíduos que restam são exatamente o que um oráculo de registo revela
> de qualquer forma. Se forem inaceitáveis, deixe as tabelas de índice de prefixos e de domínios por configurar
> (a pesquisa por correspondência exata não tem nenhum dos dois) em vez de presumir que o HMAC os cobre.

```csharp
public interface IIndexTokenizer
{
    Task<string> TokenizeAsync(string value, CancellationToken ct = default);
    Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values,
        CancellationToken ct = default);
}
```

Tal como `IFieldCipher`, é um parâmetro opcional do construtor do armazenamento, com uma predefinição de passagem (`NullIndexTokenizer`), pelo que as linhas de índice continuam indexadas por texto simples até optar por ativá-lo. Os tokens devolvidos têm de ser seguros como valores PartitionKey/RowKey do Azure Table (sem nenhum de `/ \ # ?` nem caracteres de controlo).

## Captura do registo de alterações: IChangeWriter {#change-log-capture-ichangewriter}

`IChangeWriter` (anteriormente `ITombstoneWriter`, renomeado na 0.6.0) regista a chave de cada linha alterada numa tabela dedicada de registo de alterações, para que as cópias de segurança incrementais consigam encontrar o que mudou sem varrer a coluna `Timestamp` não indexada das tabelas ativas. As eliminações são capturadas para todas as tabelas (uma varredura das linhas ativas não consegue ver uma linha que já não existe); as inserções/atualizações são capturadas para as tabelas que a cópia de segurança lê a partir do registo em vez de as varrer. Implementações integradas: `TableChangeWriter` (Azure Table Storage), `DynamoChangeWriter` (DynamoDB) e `SqlChangeWriter` (PostgreSQL / SQLite).

```csharp
public interface IChangeWriter
{
    // Deletes
    Task WriteAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);

    // Upserts
    Task WriteUpsertAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteUpsertBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);
}
```

Contrato de ordenação para quem implementa e para quem chama: escreva o tombstone da eliminação ANTES de eliminar a linha de dados. Uma falha na ordem inversa perde a eliminação em todas as cópias de segurança futuras, uma vez que as eliminações são a única classe de alteração que uma nova varredura não consegue corrigir por si. A falha inversa é segura: uma escrita posterior na chave volta a marcar um carimbo temporal mais recente, e a fusão e o restauro mantêm as linhas escritas depois do tombstone.

## Endpoints personalizados {#custom-endpoints}

Adicione os seus próprios endpoints junto dos da Authagonal:

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## Integração com o HashiCorp Vault Transit {#hashicorp-vault-transit-integration}

> **A assinatura de JWT não é delegada no Vault.** Esta secção mostrava anteriormente um excerto de DI que parecia
> ativá-la. Registar `VaultTransitCryptoProvider` **não tem qualquer efeito na assinatura de tokens**:
> `ProtocolKeyManager` chama `ProtocolSigningKeyOps.BuildSigningCredentials`, que constrói uma
> `ECDsaSecurityKey` a partir do material em `ISigningKeyStore`, e nada a substitui por uma
> `VaultTransitSecurityKey`. Um anfitrião que seguisse o excerto antigo via os tokens ES256 a ser verificados contra o JWKS
> e concluía, com razão aparente, que era o Vault a assiná-los, quando a chave privada tinha sido gerada localmente no primeiro arranque
> e persistida no armazenamento de dados principal, em texto simples, a menos que por acaso estivesse registado um `IFieldCipher`.
> O acesso de leitura a esse armazenamento equivale a poder personificar totalmente o emissor. Se tiver um requisito de conformidade segundo o qual
> as chaves de assinatura nunca saem de um HSM, isto não o satisfaz.
>
> O servidor regista agora um erro no arranque se encontrar `VaultTransitCryptoProvider` registado, para que o
> equívoco não possa persistir sem aviso.
>
> Torná-lo real exige mais do que um registo de DI: `ISigningKeyStore` teria de representar uma chave sem
> material local (o *nome* de uma chave Transit em vez de um escalar privado), `BuildSigningCredentials` precisaria de um
> ponto de extensão para devolver uma `VaultTransitSecurityKey`, `BuildJwksAsync` teria de publicar a chave pública lida
> do Vault, e a rotação e a publicação antecipada teriam de criar e promover versões de chaves Transit em vez de
> as gerar localmente. `VaultTransitClient`, `VaultTransitSecurityKey`, `VaultTransitSignatureProvider` e
> `VaultTransitCryptoProvider` são mantidos porque são as peças que funcionam; o que falta é a ligação entre elas.

Aquilo para que o `VaultTransitClient` **serve** hoje são os pontos de extensão de encriptação e de HMAC: um
`IFieldCipher` suportado pelo Vault para PII em repouso, ou um `IIndexTokenizer` para índices cegos com chave:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("Vault", client =>
{
    client.BaseAddress = new Uri("https://vault.example.com");
    client.DefaultRequestHeaders.Add("X-Vault-Token", "hvs.xxx");
});

builder.Services.AddSingleton<VaultTransitClient>();

// Your own adapters over the client. These are the seams Authagonal actually consumes.
builder.Services.AddSingleton<IFieldCipher, MyVaultFieldCipher>();
builder.Services.AddSingleton<IIndexTokenizer, MyVaultIndexTokenizer>();

builder.Services.AddAuthagonal(builder.Configuration);
```

Registar um `IFieldCipher` é também o que silencia `PlaintextSigningKeyWarning`, porque os armazenamentos de chaves de assinatura
encaminham o seu material de chave através desse mesmo ponto de extensão, que é o mais próximo da afirmação original
que existe hoje: a chave privada continua a existir localmente, mas não em claro.

O `VaultTransitClient` disponibiliza estas operações:

| Método | Descrição |
|---|---|
| `SignAsync(keyName, data)` | Assina dados com uma chave do Vault Transit |
| `VerifyAsync(keyName, data, signature)` | Verifica uma assinatura no formato JWS através do endpoint de verificação do Transit |
| `EncryptAsync` / `DecryptAsync` (+ `EncryptBatchAsync` / `DecryptBatchAsync`) | Encriptação simétrica com uma chave `aes256-gcm96`; devolve tokens `vault:v{n}:...` para guardar tal como estão |
| `HmacAsync` / `HmacBatchAsync` | HMAC com chave sob uma chave `hmac` (tokens de índice cego) |
| `CreateKeyAsync(keyName, type)` | Cria uma nova chave Transit (predefinição: `ecdsa-p256`) |
| `EnsureKeyTypeAsync(keyName, type)` | Garante de forma idempotente que existe uma chave com o tipo pretendido (recria-a se o tipo não corresponder; as chaves Transit não podem mudar de tipo no próprio local) |
| `RotateKeyAsync(keyName)` | Roda uma chave para uma nova versão |
| `DeleteKeyAsync(keyName)` | Elimina uma chave (ativa primeiro `deletion_allowed`) |
| `ReadKeyAsync(keyName)` | Lê os metadados, as versões e as chaves públicas de uma chave |
| `KeyExistsAsync(keyName)` | Verifica se uma chave existe |

O `VaultTransitCryptoProvider` integra-se com o `JsonWebTokenHandler` do .NET para que a assinatura de JWT utilize o Vault de forma transparente. O `VaultTransitSecurityKey` e o `VaultTransitSignatureProvider` tratam da integração de baixo nível.

## Email {#email}

O remetente Resend integrado é ativado automaticamente quando `Email:ResendApiKey` está configurado (defina também `Email:SenderEmail`). Sem nenhum `IEmailService`, o correio é descartado através de `NullEmailService` e, como a barreira de início de sessão que exige email confirmado está ativa por predefinição, os utilizadores que se registassem por si próprios nunca conseguiriam iniciar sessão; `UseAuthagonal()` regista um aviso bem visível no arranque nesse estado.

Para utilizar outro fornecedor, registe o seu próprio `IEmailService` antes de `AddAuthagonal()`:

```csharp
public sealed class SmtpEmailService(SmtpClient smtp) : IEmailService
{
    public async Task SendVerificationEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Verify your email", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }

    public async Task SendPasswordResetEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Reset your password", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }
}
```

`IEmailService` declara também `SendAccountExistsEmailAsync` (enviado quando alguém tenta registar um email já registado, mantendo a resposta do registo neutra face à enumeração de contas). Tem uma implementação predefinida que não faz nada, pelo que as implementações existentes continuam a compilar.

## Consulte também {#see-also}

- [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server): exemplo completo e funcional
- [demos/sample-app/](https://github.com/authagonal/authagonal/tree/master/demos/sample-app): exemplo de aplicação cliente
