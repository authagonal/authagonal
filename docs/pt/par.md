---
layout: default
title: Pushed Authorization Requests
locale: pt
---

# Pushed Authorization Requests (PAR)

O [RFC 9126](https://www.rfc-editor.org/rfc/rfc9126) permite que um cliente envie por POST os parâmetros do seu pedido de autorização diretamente para o servidor, com a autenticação de cliente padrão, e receba um `request_uri` opaco de curta duração para entregar ao browser. O browser visita então `/connect/authorize?request_uri=...&client_id=...` em vez de transportar todos os parâmetros no URL.

Porquê usá-lo:

- Os parâmetros de autorização nunca aparecem no histórico do browser, nos logs do servidor nem nos cabeçalhos `Referer`.
- O servidor autentica o cliente no momento do envio, pelo que a integridade dos parâmetros é verificada antes de ocorrer qualquer redirecionamento.
- Conjuntos de parâmetros longos (pedidos `claims` extensos, fluxos com vários recursos) não ultrapassam os limites de comprimento dos URLs.

## Endpoint {#endpoint}

```
POST /connect/par
Content-Type: application/x-www-form-urlencoded
```

A autenticação é igual à de `/connect/token`: HTTP Basic com `client_id`/`client_secret`, ou credenciais codificadas no formulário. Os clientes confidenciais têm de se autenticar; os clientes públicos enviam sem segredo. As falhas de autenticação do cliente devolvem `401` (conforme o RFC 9126, ao contrário do endpoint de token, onde apenas `invalid_client` é um 401).

O corpo do formulário transporta os mesmos parâmetros que normalmente iriam para `/connect/authorize` (`response_type`, `redirect_uri`, `scope`, `state`, `code_challenge`, `code_challenge_method`, `nonce`, `resource`, etc.). O próprio `request_uri` é rejeitado: encadear um PAR é proibido pela secção 2.1 da especificação. Se o corpo incluir um `client_id`, este tem de corresponder ao cliente autenticado. Tal como o endpoint de token, a rota recusa um pedido `http` em texto simples, a menos que `AuthagonalProtocolOptions.AllowInsecureHttp` esteja definido.

O pedido é validado no momento do envio, da mesma forma que `/connect/authorize` o validaria (`redirect_uri` registado, âmbitos permitidos, PKCE, valores de `prompt`, etc.). Um pedido inválido é recusado de imediato com `400 invalid_request` e não é emitido nenhum `request_uri`, para que o erro seja comunicado ao cliente e não ao utilizador final a meio do fluxo. `authorization_details` é recusado com `invalid_authorization_details` (os rich authorization requests pertencem ao endpoint de token, não a este).

### Limites {#limits}

- O corpo está limitado a 32 KB, com um máximo de 64 campos de formulário, nomes de 256 caracteres e 8 KB por valor. Tudo o que for maior é recusado com `413 invalid_request`.
- Os pedidos estão sujeitos a um limite de 60 por minuto por cliente e endereço de origem, e de 300 por minuto por cliente no total, com a resposta `429 temporarily_unavailable`.

### Resposta {#response}

```
HTTP/1.1 201 Created
```
```json
{
  "request_uri": "urn:ietf:params:oauth:request_uri:abc123...",
  "expires_in": 90
}
```

O `request_uri` é de utilização única. É removido do armazenamento quando o código de autorização é emitido para ele. Se nunca for resgatado, expira ao fim de 90 segundos.

### Passo de autorização {#authorization-step}

```
GET /connect/authorize?client_id=my-rp&request_uri=urn:ietf:params:oauth:request_uri:abc123...
```

Quando `request_uri` está presente, todos os outros parâmetros são obtidos do conteúdo enviado e tudo o resto no URL é ignorado (exceto `client_id`, que tem de corresponder ao cliente que enviou o conteúdo, e o parâmetro `error` que uma ida e volta de federação falhada acrescenta). Um `request_uri` desconhecido, expirado, já consumido ou enviado por outro cliente é recusado com `invalid_request`. Só são aceites os URN opacos emitidos pelo próprio endpoint PAR deste servidor: qualquer outro valor de `request_uri` é recusado com `request_uri_not_supported`, e o parâmetro `request` do RFC 9101 com `request_not_supported`.

Os valores de `prompt` e `max_age` enviados são respeitados. Um pedido PAR com `prompt=login` (ou um `max_age` que a sessão já ultrapassou) só é satisfeito por uma sessão cujo `auth_time` seja igual ou posterior ao momento em que o pedido foi enviado. Assim, uma sessão pré-existente é terminada e autenticada de novo uma única vez, e o regresso do início de sessão emite um código em vez de entrar em ciclo.

## Exigir PAR por cliente {#requiring-par-per-client}

Defina `RequirePushedAuthorizationRequests = true` num cliente para recusar pedidos simples a `/connect/authorize` provenientes dele. Qualquer tentativa de autorização sem PAR devolve `invalid_request` com a descrição "This client requires requests to be pushed via /connect/par".

```csharp
new OAuthClient
{
    ClientId = "high-risk-rp",
    RequirePushedAuthorizationRequests = true,
    // ...
}
```

É a postura recomendada para clientes que lidam com âmbitos sensíveis: combinada com PKCE, elimina a barra de endereços como superfície de ataque.

## Tempo de vida e armazenamento {#lifetime-and-storage}

O `expires_in` devolvido pelo envio é de 90 segundos, e essa janela abrange o salto entre o envio e o primeiro pedido a `/connect/authorize`. Quando o registo é obtido pela primeira vez, é prolongado (uma única vez) até um prazo absoluto de 15 minutos a contar do envio, para que o utilizador possa concluir o início de sessão, a MFA e o consentimento. Os valores de 90 segundos e 15 minutos são constantes, não configuração. Os conteúdos enviados são guardados através do mesmo `IGrantStore` que os códigos de autorização e os tokens de atualização, pelo que herdam automaticamente a estratégia de persistência e replicação do anfitrião.

## Descoberta {#discovery}

O endpoint PAR anuncia-se em `.well-known/openid-configuration` como:

```json
{
  "pushed_authorization_request_endpoint": "https://auth.example.com/connect/par"
}
```
