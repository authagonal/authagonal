---
layout: default
title: Registo dinâmico de clientes
locale: pt
---

# Registo dinâmico de clientes

O Authagonal implementa o **OAuth 2.0 Dynamic Client Registration** ([RFC 7591](https://datatracker.ietf.org/doc/html/rfc7591)), que permite às aplicações cliente registarem-se a si próprias em tempo de execução, sem intervenção de um administrador.

## Ativar o endpoint {#enabling-the-endpoint}

O registo dinâmico está **desativado por predefinição**. Ative-o através da configuração:

```json
{
  "Auth": {
    "DynamicClientRegistrationEnabled": true
  }
}
```

Ou defina `Auth__DynamicClientRegistrationEnabled=true` como variável de ambiente. Um anfitrião multi-inquilino pode substituir a definição por inquilino através de `ITenantContext.DynamicClientRegistrationEnabled`: a resposta do próprio inquilino prevalece, e `null` recorre à opção global do anfitrião.

Quando está ativado, o documento de descoberta anuncia o endpoint:

```
GET /.well-known/openid-configuration
```
```json
{
  "registration_endpoint": "https://auth.example.com/connect/register"
}
```

## Registar um cliente {#registering-a-client}

```
POST /connect/register
Content-Type: application/json

{
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "token_endpoint_auth_method": "client_secret_basic",
  "scope": "openid profile email offline_access",
  "audiences": ["https://api.myapp.example.com"],
  "allowed_cors_origins": ["https://myapp.example.com"],
  "backchannel_logout_uri": "https://myapp.example.com/oidc/backchannel",
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

### Resposta {#response}

```
HTTP/1.1 201 Created
Content-Type: application/json

{
  "client_id": "a1b2c3d4e5f6...",
  "client_secret": "xkCd2_base64url...",
  "client_id_issued_at": 1745000000,
  "client_secret_expires_at": 0,
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "response_types": ["code"],
  "scope": "openid profile email offline_access",
  "token_endpoint_auth_method": "client_secret_basic"
}
```

O `client_secret` é devolvido **uma única vez** e não pode ser obtido mais tarde. Guarde-o em segurança. A resposta é enviada com `Cache-Control: no-store`. `client_id` tem 32 caracteres hexadecimais em minúsculas, e `client_secret_expires_at` é sempre `0` (os segredos não expiram). Os clientes públicos (`none`) e os clientes `private_key_jwt` não recebem `client_secret` na resposta. A resposta devolve apenas os campos mostrados: `audiences`, `jwks`, `jwks_uri`, `allowed_cors_origins` e os campos de logout são guardados, mas não devolvidos.

## Parâmetros do pedido {#request-parameters}

| Parâmetro | Obrigatório | Notas |
|---|---|---|
| `client_name` | não | Se for omitido, assume o `client_id` gerado |
| `redirect_uris` | condicional | Obrigatório quando `grant_types` contém `authorization_code`. Têm de ser URIs absolutos; os esquemas `javascript:`/`data:`/`vbscript:`/`file:` são rejeitados (os esquemas personalizados nativos para deep links móveis são aceites). Um fragmento é rejeitado (RFC 6749 §3.1.2), e `http` em texto simples só é aceite para anfitriões de loopback (RFC 8252 §7.3). No máximo 20 entradas, cada uma com no máximo 2048 caracteres. |
| `post_logout_redirect_uris` | não | Destinos de redirecionamento válidos após o logout. Os mesmos limites de 20 entradas / 2048 caracteres que `redirect_uris`. |
| `grant_types` | não | Predefinição `["authorization_code"]`. **Só `authorization_code` e `refresh_token` podem ser registados**: `client_credentials`, `implicit`, device e qualquer outro tipo de concessão são rejeitados com `invalid_client_metadata`, pelo que o registo aberto nunca pode criar um cliente máquina a máquina. `refresh_token` é acrescentado automaticamente se for pedido `offline_access`. |
| `token_endpoint_auth_method` | não | `client_secret_basic` (predefinição), `client_secret_post`, `private_key_jwt` ou `none` para clientes públicos. Qualquer outro valor é recusado com `invalid_client_metadata`. |
| `jwks` / `jwks_uri` | com `private_key_jwt` | As chaves públicas do cliente. Um dos dois é obrigatório para `private_key_jwt` (caso contrário, `invalid_client_metadata`); `jwks_uri` tem de passar a proteção de URLs de saída (um endereço externo). Um cliente `private_key_jwt` não recebe segredo. |
| `scope` | não | Âmbitos separados por espaços. Só podem ser registados os cinco âmbitos OIDC incorporados (`openid`, `profile`, `email`, `phone`, `offline_access`) e os que `Auth:DynamicClientRegistrationScopes` indicar: existir no armazenamento de âmbitos **não** basta (consulte [Âmbitos](scopes)). Os âmbitos condicionados a funções e o âmbito administrativo (`AdminApi:Scope`, predefinição `authagonal-admin`) nunca podem ser registados. |
| `audiences` | não | Valores JWT `aud` acrescentados aos tokens de acesso. No máximo 20 entradas com no máximo 512 caracteres, cada uma um URI absoluto sem fragmento; um valor inválido resulta em `invalid_client_metadata`. |
| `allowed_cors_origins` | não | Cada entrada tem de ser uma origem válida (caso contrário, `invalid_client_metadata`), mas o valor **não é guardado tal como é enviado**: as origens permitidas do cliente são derivadas das origens dos seus próprios `redirect_uris` `https`, pelo que quem regista só consegue alcançar origens para as quais já comprovou um URI de redirecionamento. |
| `backchannel_logout_uri` | não | Ativa o [Back-Channel Logout](index#key-features) |
| `frontchannel_logout_uri` | não | Ativa o [Front-Channel Logout](front-channel-logout) |
| `frontchannel_logout_session_required` | não | Predefinição `true`; quando é `true`, o URL de logout transporta os parâmetros `iss` e `sid` |

## Predefinições e invariantes {#defaults--invariants}

- **PKCE obrigatório**: `RequirePkce` é sempre `true` para clientes registados dinamicamente.
- **Consentimento obrigatório**: `RequireConsent` é sempre `true`, pelo que um utilizador vê o ecrã de consentimento de um cliente autorregistado mesmo nos casos em que um cliente inicializado estaticamente pela configuração o dispensaria.
- **Clientes públicos**: `token_endpoint_auth_method: "none"` produz um cliente sem segredo. O PKCE continua a ser obrigatório.
- **Acesso offline**: pedir o âmbito `offline_access` acrescenta implicitamente `refresh_token` a `grant_types`.

## Respostas de erro {#error-responses}

| HTTP | `error` | Causa |
|---|---|---|
| `400` | `invalid_redirect_uri` | Um dos `redirect_uris` não é um URI absoluto válido, usa um pseudoesquema script/data/file, transporta um fragmento, é `http` em texto simples para um anfitrião que não é de loopback, ou (em qualquer das listas de URIs) tem mais de 2048 caracteres |
| `400` | `invalid_client_metadata` | Foi pedido um tipo de concessão não registável, falta `redirect_uris` para um tipo de concessão que o exige, `token_endpoint_auth_method` não é suportado, `private_key_jwt` não tem `jwks`/`jwks_uri` (ou tem um `jwks_uri` inseguro), `audiences` é inválido, uma entrada de `allowed_cors_origins` não é uma origem, ou um URI de logout não é um endereço externo |
| `400` | `invalid_scope` | Um âmbito pedido não é incorporado nem está registado |
| `400` | `invalid_client_metadata` | Mais de 20 `redirect_uris` / `post_logout_redirect_uris` |
| `403` | `invalid_scope` | Um âmbito pedido não é registável: não consta de `Auth:DynamicClientRegistrationScopes`, ou está condicionado a funções |
| `403` | `invalid_scope` | Foi pedido o âmbito administrativo, que nunca pode ser concedido através do registo |
| `403` | `invalid_scope` | Um `IClientScopeGuard` registado recusou um âmbito pedido (recebe o chamador anónimo) |
| `403` | `not_supported` | O registo dinâmico de clientes não está ativado |
| `429` | `rate_limited` | Demasiados registos a partir deste IP (10 por hora) |

## Considerações de segurança {#security-considerations}

O endpoint de registo **não é autenticado**, mas é restringido por conceção:

- **Limite de pedidos**: 10 registos por endereço de origem numa hora deslizante (`429 rate_limited`), para que o armazenamento de clientes não possa ser inundado. O endereço considerado é o que o chamador não pode escolher (o valor reencaminhado não é aceite cegamente).
- **Tipos de concessão restritos**: apenas `authorization_code` + `refresh_token`; um cliente registado exige sempre um fluxo mediado pelo utilizador e nunca pode atuar como cliente máquina a máquina.
- **Âmbitos numa lista de permissões, não herdados**: quem regista pode declarar os cinco âmbitos OIDC incorporados e mais nenhum, a menos que um operador inclua um âmbito em `Auth:DynamicClientRegistrationScopes`. Existir no armazenamento de âmbitos não é uma permissão: um âmbito existe porque algum cliente precisa dele, não porque qualquer registante anónimo o possa reivindicar.
- **Âmbito de administração reservado**: o âmbito `authagonal-admin` (ou o valor definido em `AdminApi:Scope`) é recusado, pelo que o registo nunca pode produzir um cliente que alcance a [API de administração](admin-api).
- **URIs de logout validados**: `backchannel_logout_uri` e `frontchannel_logout_uri` são acedidos pelo servidor, pelo que têm de ser endpoints http(s) externos: os anfitriões de loopback, RFC1918, link-local (incluindo o endereço de metadados da cloud) e `.internal`/`.local` são recusados.
- **Registos limitados**: no máximo 20 URIs de redirecionamento com no máximo 2048 caracteres cada, para que um registo não possa ser usado para inflacionar o armazenamento de clientes.
- **Origens CORS derivadas, não confiadas**: as origens guardadas provêm dos próprios URIs de redirecionamento `https` do cliente, nunca do corpo do pedido.
- **PKCE sempre obrigatório** e **consentimento sempre obrigatório** nos clientes registados.

O que **não** é restringido, a menos que quem regista o decida, é a audiência. O RFC 7591 não tem nenhum campo para ela, pelo que um registo comum omite totalmente `audiences` (uma extensão do Authagonal): nunca se perguntou nada ao cliente, a sua lista está "por definir", e ele pode indicar qualquer URI absoluto como `resource` no endpoint de autorização e receber um token que transporta esse valor como `aud`. Isto é deliberado (a especificação de autorização MCP exige que os clientes indiquem o servidor MCP como recurso, e um cliente MCP é um cliente DCR), e torna o servidor de recursos responsável por autorizar com base em `scope` e não em `iss` + `aud` + `sub`. **Enviar** `audiences`, mesmo como lista vazia, é uma resposta e fixa o cliente a ela: uma lista não vazia é a lista de permissões para `resource`, e um `[]` explícito significa que o cliente não pode indicar nenhum recurso. A troca de tokens é a exceção: aí, um `Audiences` por definir recusa à partida, pelo que um cliente registado não pode dirigir um token trocado a lado nenhum. Consulte [Audiências e indicadores de recurso](configuration#audiences-and-resource-indicators-rfc-8707).

Para um controlo mais forte (token de acesso inicials, mTLS, software statements), coloque à frente do endpoint o seu próprio middleware ou um `IAuthHook`. Considere desativar totalmente o registo dinâmico e gerir os clientes através da API de administração nos ambientes em que o registo self-service não é um requisito.
