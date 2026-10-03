---
layout: default
title: Front-Channel Logout
locale: pt
---

# Front-Channel Logout

O Authagonal implementa o **OpenID Connect Front-Channel Logout 1.0**, um mecanismo de logout conduzido pelo browser que complementa o [back-channel logout](index#key-features). Enquanto o back-channel logout é um POST de servidor para servidor, o front-channel logout apresenta o URL de logout de cada relying party num iframe oculto, para que a sessão de browser de cada aplicação (cookies, armazenamento local) seja limpa a partir do próprio browser do utilizador.

## Quando usar cada um {#when-to-use-which}

| Aspeto | Back-Channel | Front-Channel |
|---|---|---|
| Sessões do lado do servidor | ✅ | ❌ |
| Cookies do browser / armazenamento local | ❌ | ✅ |
| Funciona quando o browser do utilizador está offline | ✅ | ❌ |
| Resiste a erros de rede (nova tentativa) | ✅ | ❌ (uma única tentativa, na medida do possível) |

A maioria das aplicações beneficia de configurar **ambos**. O back-channel garante que o servidor é notificado; o front-channel limpa o browser.

## Configuração do cliente {#client-configuration}

Acrescente um URI de front-channel logout ao registo `OAuthClient`:

```json
{
  "clientId": "myapp",
  "frontChannelLogoutUri": "https://myapp.example.com/oidc/frontchannel",
  "frontChannelLogoutSessionRequired": true
}
```

| Campo | Descrição |
|---|---|
| `FrontChannelLogoutUri` | O endpoint de logout do cliente visível pelo browser |
| `FrontChannelLogoutSessionRequired` | Se for `true` (predefinição), o URL é chamado com os parâmetros de consulta `iss` e `sid`, para que o cliente possa associar o logout à sessão específica |

## Como funciona {#how-it-works}

Quando o browser visita `/connect/endsession` (GET ou POST):

1. **Confirmação (proteção contra CSRF).** Se o browser tiver uma sessão iniciada e o pedido não transportar um `id_token_hint` cujo `sub` corresponda a essa sessão, o servidor apresenta primeiro uma página "terminar sessão?" com um botão de confirmação, em vez de terminar a sessão do utilizador. O botão faz um POST de volta com um token de curta duração (15 minutos) vinculado a essa sessão. É isto que impede uma página de terceiros de terminar a sessão de um utilizador navegando até ao endpoint (o cookie de sessão é `SameSite=Lax`, pelo que acompanha um GET de nível superior entre sites). Um `id_token_hint` correspondente substitui a confirmação.
2. O servidor encontra todos os clientes com os quais o utilizador tem atualmente concessões.
3. Para cada cliente com um `FrontChannelLogoutUri` que passe a verificação de URLs de saída (o loopback é permitido porque é o próprio browser do utilizador que faz o pedido, mas os endereços de gamas privadas e link-local não), o servidor constrói um URL, acrescentando `iss=<issuer>` (e `sid=<session_id>`, quando a sessão tem um) se `FrontChannelLogoutSessionRequired` for `true`.
4. O servidor revoga as concessões emitidas para a sessão, termina a sessão do utilizador no cookie do servidor de autorização, despoleta as notificações de back-channel logout em segundo plano e, quando foi construído pelo menos um URL de front-channel, devolve uma página HTML que contém um `<iframe>` oculto para cada um:
   ```html
   <iframe src="https://myapp.example.com/oidc/frontchannel?iss=https%3A%2F%2Fauth.example.com&sid=abc123" style="display:none"></iframe>
   ```
   A página transporta uma `Content-Security-Policy` cujo `frame-src` está limitado às origens desses URLs, e não contém scripts.
5. O destino pós-logout é resolvido da mesma forma, haja ou não iframes envolvidos. O `post_logout_redirect_uri` só é respeitado quando o pedido identifica o cliente (através da audiência do `id_token_hint`, ou do parâmetro `client_id`) e o URI consta dos `PostLogoutRedirectUris` registados desse cliente (um parâmetro `state`, se for fornecido, é acrescentado). Com iframes, a página aguarda 2 segundos (um `meta refresh`) e depois redireciona, ou mostra uma mensagem de "sessão terminada" quando não há destino válido. Sem URLs de front-channel, o servidor redireciona de imediato (`302`), ou responde `200` com uma `message` JSON quando não há destino válido.

`id_token_hint` só é aceite se for um ID token assinado por este servidor (ES256, `typ: JWT`) com uma única audiência. Os tokens expirados são aceites. Os tokens de acesso, e os logout tokens, são rejeitados como hints. Se forem enviados `client_id` e `id_token_hint` e estes indicarem clientes diferentes, o pedido falha com `400 invalid_request`.

O endpoint JSON `POST /api/auth/logout` (usado pelo botão de terminar sessão da aplicação de início de sessão) executa os mesmos passos de revogação e notificação. Não apresenta iframes: devolve os URLs em `frontchannel_logout_uris` para que o chamador os carregue (consulte a [API de autenticação](auth-api#logout)).

## Handler de logout do lado do cliente {#client-side-logout-handler}

Cada relying party deve implementar o URL referido por `FrontChannelLogoutUri`. Um handler mínimo:

```http
GET /oidc/frontchannel?iss=https://auth.example.com&sid=abc123
```

1. Verifique se `iss` corresponde ao servidor de autorização esperado.
2. Se `sid` for fornecido, confirme que corresponde ao ID de sessão do cookie de sessão.
3. Limpe a sessão local (cookies, sessão do lado do servidor, armazenamento da SPA).
4. Responda com `200 OK` e um corpo vazio (ou uma página mínima); a resposta nunca é visível para o utilizador.

```csharp
app.MapGet("/oidc/frontchannel", (HttpContext ctx) =>
{
    var iss = ctx.Request.Query["iss"].ToString();
    var sid = ctx.Request.Query["sid"].ToString();
    // Validate iss/sid, then clear local session
    ctx.SignOutAsync();
    return Results.Ok();
});
```

## Documento de descoberta {#discovery-document}

O front-channel logout é anunciado em `/.well-known/openid-configuration`:

```json
{
  "frontchannel_logout_supported": true,
  "frontchannel_logout_session_supported": true
}
```

## Registo dinâmico de clientes {#dynamic-client-registration}

Os clientes registados através do [registo dinâmico de clientes](client-registration) podem incluir:

```json
{
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

O registo recusa um URI de logout que não seja um endereço externo (os nomes de loopback, link-local, de gamas privadas e `.localhost`/`.local`/`.internal` são rejeitados com `invalid_client_metadata`).

## Limitações {#limitations}

- **Na medida do possível**: os iframes são carregados uma única vez. Se um erro de rede ou uma extensão do browser os bloquear, não há nova tentativa. Combine-o com o back-channel logout para obter fiabilidade.
- **Cookies de terceiros**: alguns browsers bloqueiam por predefinição os cookies em iframes entre sites. Se a sua RP depende de cookies de primeira parte, confirme que o handler de logout não depende do envio de cookies.
- **Tempo limite**: a página aguarda ~2 segundos antes de redirecionar. Handlers de logout de RP pesados podem não terminar a tempo.

## Relacionado {#related}

- [Registo dinâmico de clientes](client-registration): parâmetros de front-channel no pedido de registo
- [Âmbitos OAuth](scopes): o consentimento sensível aos âmbitos complementa o fluxo de logout
