---
layout: default
title: Backend-for-Frontend (BFF)
locale: pt
---

# Backend-for-Frontend (BFF)

Uma SPA de browser que guarda um token de acesso ou um token de atualização num armazenamento acessível a JavaScript expõe ambos a XSS. O BFF é um **cliente OIDC confidencial alojado no seu próprio backend**. Executa o fluxo authorization code + PKCE do lado do servidor, guarda os tokens numa sessão do lado do servidor e não entrega ao browser mais do que um cookie de sessão httpOnly. As chamadas da SPA às suas APIs passam pelo proxy do BFF, que anexa o token de acesso da sessão à saída.

É distribuído em duas versões, que falam o mesmo protocolo:

| Pacote | Para | Código-fonte |
|---|---|---|
| `Authagonal.Bff` (NuGet) | Anfitriões ASP.NET Core | `src/Authagonal.Bff/` |
| `@authagonal/bff` (npm) | Express e Next.js (App Router) | `bff-lib/` |

O BFF é um cliente confidencial comum do anfitrião de autenticação: usa a descoberta OIDC e os endpoints de autorização, de token, de revogação e de fim de sessão, pelo que tudo o que precisa do anfitrião de autenticação é um cliente registado.

## 1. Registar um cliente BFF {#1-register-a-bff-client}

O cliente tem de ser **confidencial** (tem um segredo), exigir PKCE e ter `offline_access` permitido se quiser renovação do lado do servidor. Registe:

- o URI de redirecionamento `https://app.example.com/bff/callback`
- o URI de redirecionamento pós-logout `https://app.example.com/` (e `https://app.example.com/bff/logout-callback` se usar `returnUrl` no logout; consulte [Logout](#logout))

Para um "terminar sessão em todo o lado" ao nível do sujeito através de [back-channel logout](index#key-features), registe o cliente com `BackChannelLogoutSessionRequired = false`. O BFF aceita logout tokens que transportem `sid` ou apenas `sub`.

## 2. Ligar tudo (.NET) {#2-wire-it-up-net}

```csharp
builder.Services.AddAuthagonalBff(o =>
{
    o.Authority    = "https://auth.example.com";
    o.ClientId     = builder.Configuration["Bff:ClientId"]!;
    o.ClientSecret = builder.Configuration["Bff:ClientSecret"]!;
    o.Scope        = ["openid", "profile", "email", "offline_access"];
    o.PostLogoutRedirectUri = "https://app.example.com/";
});

var app = builder.Build();
app.UseForwardedHeaders();   // required behind a reverse proxy or ingress
app.MapAuthagonalBff();
app.MapFallbackToFile("index.html");
app.Run();
```

`UseForwardedHeaders` é importante: atrás de um proxy que termina o TLS, o BFF vê http simples, pelo que, sem ele, o cookie de sessão `__Host-` é escrito sem `Secure` e os browsers descartam-no. Consulte [Instalação](installation#production-security-checklist) para saber como declarar o proxy.

### Node (Express) {#node-express}

```ts
import { authagonalBff } from '@authagonal/bff/express';

app.set('trust proxy', 1);
app.use(authagonalBff({
  authority: 'https://auth.example.com',
  clientId: process.env.BFF_CLIENT_ID!,
  clientSecret: process.env.BFF_CLIENT_SECRET!,
  scope: ['openid', 'profile', 'email', 'offline_access'],
  cookieSecret: process.env.BFF_COOKIE_SECRET!,   // encrypts the session cookie
  postLogoutRedirectUri: 'https://app.example.com/',
}));
```

Para Next.js, use `createBffRoute` de `@authagonal/bff/next` em `app/bff/[...bff]/route.ts`. Consulte `bff-lib/README.md` para ambos.

## Endpoints {#endpoints}

Montados sob `BasePath` (predefinição `/bff`).

| Rota | Finalidade |
|---|---|
| `GET /bff/login?returnUrl=/` | Inicia o início de sessão: define um cookie de correlação por início de sessão e redireciona para `/connect/authorize` com PKCE (`S256`), `state` e `nonce`. |
| `GET /bff/callback` | O URI de redirecionamento OIDC (`CallbackPath`). Troca o código e cria a sessão. |
| `GET /bff/user` | `{ isAuthenticated, claims, sessionExpiresAt }`. Exige o cabeçalho antifalsificação. Sempre `Cache-Control: no-store`. |
| `GET\|POST /bff/logout` | Termina a sessão localmente e no anfitrião de autenticação. Um `POST` exige o cabeçalho antifalsificação; um `GET` é uma navegação simples. |
| `GET /bff/logout-callback` | Destino do percurso de ida e volta de fim de sessão quando o logout recebeu um `returnUrl`. |
| `POST /bff/backchannel-logout` | Consumidor servidor a servidor do back-channel logout OIDC. Autenticado pelo logout token assinado, pelo que não recebe cabeçalho CSRF. |
| `GET /bff/ws-ticket` | Opcional (`WsTicketsEnabled`), apenas .NET. Consulte [Autenticação WebSocket](websocket-auth). |
| `GET /bff/token?resource=...` | Opcional (`TokenEndpointEnabled`), apenas .NET. Consulte [Tokens trocados para outra origem](#exchanged-tokens-for-another-origin). |
| `ANY /bff/api/**` | O proxy que injeta o token. Só é mapeado quando `Upstreams` não está vazio. |

`claims` em `/bff/user` é um mapa plano de strings com as claims do id_token, excluindo a maquinaria do protocolo (`iss`, `aud`, `exp`, `iat`, `nbf`, `nonce`, `at_hash`, `c_hash`, `s_hash`, `azp`, `jti`, `sid`, `auth_time`, `acr`, `amr`, `typ`). As claims em array, como `roles` e `groups`, são unidas por espaços. As claims são relidas de cada id_token renovado, pelo que uma função concedida depois do início de sessão chega à SPA na renovação seguinte e não apenas no início de sessão seguinte.

## A partir do browser {#from-the-browser}

Todas as chamadas que não sejam de navegação transportam um cabeçalho estático, que protege contra CSRF em conjunto com `SameSite=Lax`. Qualquer valor é aceite; apenas se verifica a sua presença.

```js
const me = await fetch('/bff/user', { headers: { 'X-Authagonal-Bff': '1' } }).then(r => r.json());
if (!me.isAuthenticated) location.href = '/bff/login?returnUrl=' + encodeURIComponent(location.pathname);
```

Inicie e termine sessão **navegando** (`location.href = '/bff/login'`), não com `fetch`. O nome do cabeçalho é definido por `AntiForgeryHeader`.

## O proxy {#the-proxy}

Configure os upstreams e a SPA chama `/bff/api/<prefix>/...`:

```csharp
o.Upstreams.Add(new BffUpstream
{
    Prefix = "/orders",
    TargetBaseUrl = "https://api.internal.example.com",
});
```

O proxy exige o cabeçalho antifalsificação e uma sessão ativa, renova o token de acesso se faltarem menos de `RefreshThresholdSeconds` para expirar, reencaminha o pedido com `Authorization: Bearer` e devolve a resposta em streaming. O cookie de sessão nunca é reencaminhado. Os cabeçalhos `X-Forwarded-*`, `Forwarded` e `X-Real-IP` recebidos são removidos e reafirmados a partir do estado do próprio BFF, pelo que um script na SPA não pode atestar um IP ou esquema de cliente. Os redirecionamentos do upstream são reenviados ao browser em vez de serem seguidos.

Por upstream (`BffUpstream`):

| Propriedade | Significado |
|---|---|
| `Prefix` | Caminho a seguir a `/bff/api` que seleciona este upstream. |
| `TargetBaseUrl` | Para onde são reencaminhados os pedidos correspondentes. |
| `StripPrefix` | Remove o prefixo correspondido antes de o acrescentar ao destino. Permite que um BFF distribua por vários backends que partilham um espaço de nomes de caminhos. |
| `RequiredAuthority` | Pares `"type:action"`. O proxy verifica o `authorization_details` RFC 9396 do token de saída e devolve 403 a menos que todos os pares sejam permitidos. Consulte [Autenticação agêntica](agentic-auth). |
| `AuthorityLocation` | A raiz `locations` pela qual este upstream é conhecido, quando difere de `TargetBaseUrl`. |
| `StrictAuthority` | Recusa a chamada quando uma concessão transporta uma restrição que o proxy não consegue avaliar, em vez de a deixar passar. |

Opções relacionadas: `AllowAnonymousProxyRequests` reencaminha um pedido sem sessão (ou com uma sessão que não pode ser renovada) sem cabeçalho `Authorization`, em vez de responder 401, para APIs que decidem por si próprias. Uma rota protegida por `RequiredAuthority` nunca é anónima. `ExchangeRoutes` associa rotas do proxy a uma [troca RFC 8693](agentic-auth), para que o upstream receba um token de âmbito reduzido e vinculado ao contexto, em vez do token principal da sessão: cada rota tem um `PathPattern` com exatamente um marcador de posição (a única restrição suportada é `:guid`), o segmento capturado é enviado como parâmetro da troca e uma troca recusada resulta num 403. Uma restrição desconhecida falha no arranque, em vez de reencaminhar silenciosamente o token mais amplo.

## Logout {#logout}

`/bff/logout` revoga o token de atualização da sessão (na medida do possível), remove a sessão, limpa o cookie e redireciona para o endpoint de fim de sessão do anfitrião de autenticação com o `id_token_hint` da sessão. Sem sessão, não há nada a terminar no anfitrião de autenticação, pelo que redireciona diretamente para `PostLogoutRedirectUri`. Com um `returnUrl`, o anfitrião de autenticação redireciona de volta para `/bff/logout-callback`, que volta a validar o destino face a `ReturnUrlAllowlist` e redireciona para lá. Registe esse callback como URI de redirecionamento pós-logout do cliente.

O back-channel logout remove sessões do lado do servidor: por `sid` quando o logout token o inclui; caso contrário, todas as sessões do `sub`. As remoções estão limitadas ao inquilino cujo emissor assinou o token, porque `sub` só é único dentro de um emissor. O logout token tem de transportar `iat` e ser recente.

## Referência de opções (.NET) {#options-reference-net}

| Opção | Predefinição | Notas |
|---|---|---|
| `Authority`, `ClientId`, `ClientSecret` | obrigatórios | Não obrigatórios quando `TenantQueryParam` está definido. |
| `Scope` | `openid profile offline_access` | `offline_access` ativa a renovação. |
| `BasePath` | `/bff` | |
| `CallbackPath` | `/bff/callback` | Tem de ser igual ao URI de redirecionamento registado. |
| `CookieName` | `__Host-agbff` | O prefixo `__Host-` obriga a Secure, `Path=/` e ausência de Domain, pelo que exige https. Substitua-o para desenvolvimento local em http. |
| `SessionLifetime` | 8 horas | Limite absoluto, independentemente das renovações. |
| `PersistentCookie` | `false` | Quando é true, o cookie recebe um `Max-Age` limitado a `SessionLifetime` e sobrevive a um reinício do browser ("manter sessão iniciada"). O back-channel logout continua a terminar a sessão. |
| `CorrelationLifetime` | 30 minutos | Quanto tempo pode demorar um início de sessão entre `/bff/login` e o callback. Abrange o registo, o email de verificação e o início de sessão. |
| `RefreshThresholdSeconds` | 60 | |
| `ReturnUrlAllowlist` | vazio | Origens que um `returnUrl` não relativo pode ter como destino. Os caminhos relativos são sempre permitidos; qualquer outro valor passa a `/`. |
| `LoginPassthroughParams` | vazio | Nomes de parâmetros de consulta copiados de `/bff/login` para `/connect/authorize` (por exemplo `idp_hint`). Os parâmetros padrão prevalecem sempre. |
| `AntiForgeryHeader` | `X-Authagonal-Bff` | |
| `PostLogoutRedirectUri` | nenhum | |
| `WsTicketsEnabled`, `WsTicketLifetime`, `TicketExchangeParams` | desativado, 30 s, vazio | Consulte [Autenticação WebSocket](websocket-auth). |
| `TokenEndpointEnabled`, `TokenEndpointResources`, `TokenEndpointExchangeParams` | desativado, vazio, vazio | Ativá-lo sem recursos falha no arranque. |
| `Upstreams`, `ExchangeRoutes`, `AllowAnonymousProxyRequests` | vazio, vazio, `false` | Consulte [O proxy](#the-proxy). |
| `TenantQueryParam` | nenhum | Modo multi-inquilino, abaixo. |

`SessionMode` só tem `Store` implementado; `Stateless` está reservado e falha no arranque.

O pacote Node aceita os equivalentes em camelCase de `authority`, `clientId`, `clientSecret`, `scope`, `basePath`, `callbackPath`, `cookieName`, `refreshThresholdSeconds`, `returnUrlAllowlist`, `postLogoutRedirectUri`, `antiForgeryHeader`, `sessionLifetimeSeconds`, `upstreams` e `tenantQueryParam`, mais `cookieSecret`, `sessionStore`, `cookieProtector`, `tenantResolver` e `clientIp`. Não tem os endpoints de ticket de websocket nem de token.

## Sessões e execução de mais do que uma instância {#sessions-and-running-more-than-one-instance}

As sessões são guardadas através de `IBffSessionStore`. A predefinição é `IDistributedCache`, em memória, a menos que registe uma cache real (Redis, por exemplo) **antes** de `AddAuthagonalBff`.

Uma cache partilhada não basta por si só. O single-flight da renovação é por processo, enquanto a sessão e o seu token de atualização rotativo residem na cache partilhada. Duas réplicas podem ler a mesma sessão, concluir ambas que precisa de ser renovada e resgatar ambas o mesmo token de atualização. O anfitrião de autenticação interpreta o segundo resgate como a repetição de um token roubado e revoga toda a família de concessões, terminando a sessão do utilizador em todo o lado. Forneça um bloqueio entre réplicas de uma de duas formas:

- **Registe um `ILeaseProvider`** (qualquer backend). Os fornecedores Azure, AWS e SQL disponibilizam um através de `AddAuthagonalClustering`. Consulte [Escalabilidade](scaling).
- **Implemente `IBffRefreshLockStore` no seu armazenamento de sessões** (`TryAcquireRefreshLockAsync(sessionId, ttl)` e `ReleaseRefreshLockAsync`). Trata-se de uma escrita condicional com TTL, por exemplo `SET NX PX` no Redis. O armazenamento predefinido não o pode oferecer porque `IDistributedCache` não tem escrita condicional à ausência. O armazenamento de sessões do Node tem os equivalentes `acquireRefreshLock` / `releaseRefreshLock`.

Sem nenhum dos dois, a implementação depende de `Auth:RefreshTokenReuseGraceSeconds` do anfitrião de autenticação, cuja predefinição é 0 (estrito) no anfitrião Server. O BFF regista um aviso no arranque quando o armazenamento de sessões parece partilhado e não existe bloqueio.

Um `IBffSessionStore` personalizado tem de respeitar o argumento `tenantKey` em `RemoveBySidAsync` e `RemoveBySubjectAsync`. Os outros pontos de extensão são `ICookieProtector` (predefinição: ASP.NET Data Protection) e `ITokenClient`.

## Vários inquilinos a partir de um BFF {#many-tenants-from-one-bff}

Defina `TenantQueryParam` (por exemplo `"slug"`) e registe um `IBffTenantResolver`. `/bff/login?slug=acme` resolve o `BffTenantConfig` do inquilino (authority, client id, segredo, âmbito), a chave é guardada na sessão para que os pedidos seguintes a voltem a resolver, e o back-channel logout resolve o inquilino a partir do `iss` do token através de `ResolveByIssuerAsync`. Com `TenantQueryParam` por definir, o BFF é de inquilino único e são usadas as opções estáticas.

## Tokens trocados para outra origem {#exchanged-tokens-for-another-origin}

O modelo de cookies não consegue chegar a um servidor de recursos noutra origem (uma aplicação num iframe que a SPA incorpora, por exemplo). `TokenEndpointEnabled` acrescenta `GET /bff/token?resource=<audience>`, que devolve `{ accessToken, expiresInSeconds }` para um token **trocado** RFC 8693: dirigido a um recurso de `TokenEndpointResources` (qualquer outro resulta num 400 `resource_not_allowed`), vinculado aos valores de `TokenEndpointExchangeParams` presentes na consulta, e de curta duração. O browser nunca recebe o token principal da sessão e deve guardar o token trocado apenas em memória. O cliente do inquilino precisa da concessão de troca de tokens e tem de declarar os recursos como as suas audiências. Uma troca recusada resulta num 403.
