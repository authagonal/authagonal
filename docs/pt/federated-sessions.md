---
layout: default
title: Sessões federadas
locale: pt
---

# Manter as sessões federadas sincronizadas com o fornecedor a montante

Quando um utilizador inicia sessão através de um [IdP externo](oidc-federation), o Authagonal emite a sua *própria*
sessão e os seus próprios tokens. Por predefinição, essa sessão local passa depois a ter vida própria: se o cliente
desativar o utilizador no seu diretório, ou se a hiperligação de partilha do convidado for revogada a montante, a sessão
local do Authagonal continua a funcionar até o seu cookie expirar.

Para que a saída de utilizadores e as revogações tenham efeito rapidamente, ative **`RevalidateOnRefresh`**. A partir
daí, em cada renovação local de token, o Authagonal resgata o token de atualização a montante junto do IdP e, se o fornecedor
a montante indicar que a credencial deixou de existir, a renovação local é recusada e a sessão deixa de receber novos
tokens no prazo de um tempo de vida de token de acesso.

`RevalidateOnRefresh` é uma definição exclusiva das **ligações OIDC**. O SAML não tem token de atualização para resgatar,
pelo que uma ligação SAML não pode revalidar; limite essas sessões com a expiração de sessão da própria asserção
(consulte [SAML](saml)).

## Ativar {#enable-it}

Por ligação (inicializada a partir da configuração, como abaixo, ou definida ao criar a ligação através da
[API de administração](admin-api)), e o fornecedor a montante tem de emitir efetivamente um token de atualização:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "acme-entra",
      "MetadataLocation": "https://login.microsoftonline.com/<tenant>/v2.0/.well-known/openid-configuration",
      "ClientId": "...", "ClientSecret": "...",
      "AllowedDomains": ["acme.com"],
      "RevalidateOnRefresh": true
    }
  ]
}
```

É toda a configuração necessária. Com o indicador ativo, o Authagonal acrescenta `offline_access` ao âmbito que pede ao
fornecedor a montante (se o pedido a jusante ainda não o incluir), guarda o token de atualização que o fornecedor a montante
devolve e resgata-o de servidor para servidor em cada renovação local. O token de atualização a montante **nunca** é entregue
a um cliente. Fica cifrado num armazenamento duradouro por sessão, criado no início de sessão com uma validade fixa de sete dias
(o limite absoluto da sessão), e é usado apenas para revalidação.

O fornecedor a montante tem de colaborar: se o registo da aplicação nunca receber `offline_access` (por exemplo, porque
o consentimento não foi concedido), não é devolvido nenhum token de atualização e não há nada para resgatar. O Authagonal
regista um aviso (`RevalidateOnRefresh is enabled for connection ... but no upstream refresh token is held`) em cada
renovação nesse estado, e o fornecedor a montante **não** é reverificado.

## O que acontece na renovação {#what-happens-on-refresh}

1. A RP renova um token do Authagonal como habitualmente (`grant_type=refresh_token` em `/connect/token`).
2. O Authagonal resgata o token de atualização a montante no endpoint de token do IdP:
   - **Sucesso** → a renovação local prossegue; se o fornecedor a montante tiver rodado o seu token, o novo é guardado
     e partilhado por todas as concessões de RP da sessão.
   - **`invalid_grant`** → a credencial federada deixou de existir (utilizador desativado, sessão revogada, token
     expirado). A renovação local é **recusada**: a RP recebe `invalid_grant` de `/connect/token`, e o token a
     montante guardado é eliminado. A recusa faz falhar apenas esse pedido; não revoga a concessão do Authagonal,
     pelo que o token de atualização da RP fica por consumir e continua a ser recusado enquanto o fornecedor a montante se
     mantiver revogado.
   - **Qualquer outro 4xx** (por exemplo, `invalid_client` causado por um segredo rodado ou mal configurado, ou um
     429), um 5xx, um corpo de erro que não pode ser interpretado, uma falha de transporte, ou uma ligação que não pode
     ser carregada (eliminada, falha da descoberta ou do segredo) → tratado como **transitório**: a sessão sobrevive,
     para que uma falha do operador não termine em massa a sessão de todos os utilizadores federados. Corrija a
     configuração; nada se perde. A sessão continua limitada pelo limite absoluto da sessão.

Como existe **um** único token a montante por sessão de browser (identificado por utilizador + ligação + sessão), uma
segunda RP que o utilizador abra lê e roda o *mesmo* token: assim, a renovação de uma aplicação não pode deixar outra
aplicação com uma cópia já inválida.

## Nada a implementar {#nothing-to-implement}

Não há aqui nenhuma interface a escrever. O armazenamento duradouro
(`IUpstreamRefreshTokenStore`) é registado automaticamente pelos fornecedores de armazenamento Azure, AWS e SQL, e o
resgate é interno. Só tem de ativar `RevalidateOnRefresh` nas ligações cujo fornecedor a montante detém uma credencial
revogável.

O token guardado é removido quando a sessão termina, por qualquer via que a alcance: terminar sessão, revogar uma
sessão a partir da página da conta, "terminar sessão em todo o lado" e a limpeza por expiração. Se um anfitrião não
registar nenhum armazenamento, a cópia transportada no cookie de sessão é a alternativa de recurso.

Todas as sessões federadas por OIDC registam também a ligação a que pertencem (a claim `upstream_connection_id`),
quer essa ligação revalide quer não. Trata-se apenas de registo interno: nada é resgatado para uma ligação sem o
indicador.

> **Alcance:** esta funcionalidade está ativa onde quer que o armazenamento esteja registado (os fornecedores Azure
> Table, DynamoDB e SQL). Ative-a em ligações a um fornecedor a montante **de confiança**, em particular um que rode os
> seus tokens de atualização com utilização única (Entra, Auth0), e combine-a com `IsExternalConnection` para IdPs de
> terceiros (consulte [SSO self-service](self-service-sso)).

## Complemento: um limite rígido de sessão {#complementary-a-hard-session-cap}

`RevalidateOnRefresh` garante que uma sessão reflete as revogações a montante. Se, em vez disso (ou além disso),
quiser que a sessão local nunca *sobreviva* à sessão declarada pelo fornecedor a montante, defina `SessionExpClaim`
com o nome de uma claim do id_token que transporte uma expiração (segundos Unix). O Authagonal limita a sessão local e
todos os tokens emitidos a partir dela (incluindo as rotações de renovação, e incluindo uma concessão device-code
aprovada através dessa sessão) a esse prazo. Consulte [Federação OIDC → Limite do tempo de vida da sessão](oidc-federation).

Vale a pena mencionar o device flow porque foi a exceção até há pouco tempo: o registo de device code não tinha onde
guardar o prazo da sessão que o aprovou, pelo que um dispositivo aprovado através de uma sessão federada continuava a
emitir tokens durante todo o tempo de vida absoluto de renovação do cliente depois de essa sessão ter terminado, e
`RevalidateOnRefresh` também nunca voltava a consultar o fornecedor a montante por ele. Ambos seguem agora a sessão que
aprovou. Um dispositivo aprovado através de uma sessão *não federada* não tem nenhum prazo a herdar, o que corresponde
ao mesmo resultado que o fluxo de autorização produz.
