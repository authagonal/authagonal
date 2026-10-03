---
layout: default
title: Federação OIDC
locale: pt
---

# Federação OIDC

O Authagonal pode federar a autenticação para fornecedores de identidade OIDC externos (Google, Apple, Azure AD, etc.). Isto permite fluxos do tipo "Iniciar sessão com o Google", mantendo o Authagonal como servidor de autenticação central.

## Como funciona {#how-it-works}

Há duas vias de entrada na federação:

**Baseada no domínio (início de sessão interativo):**

1. O utilizador introduz o seu email na página de início de sessão
2. A SPA chama `/api/auth/sso-check`; se o domínio do email estiver associado a um fornecedor OIDC, o SSO é obrigatório
3. O utilizador clica em "Continuar com SSO" e é redirecionado para o IdP externo (quando o email é o `login_hint` de um pedido de autorização e o seu domínio está encaminhado para uma ligação, o utilizador vai diretamente para o IdP, com o `login_hint` reencaminhado)
4. Depois de se autenticar, o IdP redireciona de volta para `/oidc/callback`
5. O Authagonal valida o id_token, associa o utilizador (ou cria-o, se a ligação permitir o aprovisionamento JIT) e define um cookie de sessão

**Indicado pela RP (`idp_hint`):**

A relying party a jusante pode encaminhar diretamente para um IdP a montante específico, sem passar pelo passo de email/domínio SSO. Acrescente `idp_hint={connectionId}` a `/connect/authorize`:

```
/connect/authorize?client_id=my-rp&scope=openid+email&...&idp_hint=google
```

Quando o pedido não está autenticado, o Authagonal redireciona para `/oidc/{connectionId}/login`, preservando o URL `/authorize` original como `returnUrl`. Depois de a federação terminar, o utilizador regressa a `/authorize` com um cookie de sessão e o fluxo prossegue normalmente. Se a ligação definir `InteractionPath`, o utilizador é primeiro enviado para essa página da aplicação de início de sessão (consulte [Recolher algo antes de federar](self-service-sso#collect-something-before-federating)). Uma ligação com `ShowOnLogin: false` nunca é oferecida como botão de início de sessão e só é alcançável desta forma.

## Configuração {#setup}

### 1. Criar um fornecedor OIDC {#1-create-an-oidc-provider}

**Opção A, configuração (recomendada para cenários estáticos):**

Adicione a `appsettings.json`:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-google-client-id",
      "ClientSecret": "your-google-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

Os fornecedores são inicializados no arranque. `ConnectionId`, `MetadataLocation`, `ClientId` e `ClientSecret` são obrigatórios (o arranque falha sem eles). `RedirectUrl` é aceite por compatibilidade e ignorado: o URI de redirecionamento é derivado por pedido como `{Issuer}/oidc/callback`, uma vez que tem de estar na origem em que o browser se encontra, e é esse o URI a registar no IdP (um valor inicializado diferente é registado no log como ignorado). O `ClientSecret` é protegido através de `ISecretProvider` (Key Vault quando configurado; caso contrário, texto simples). Os mapeamentos de domínios SSO são registados automaticamente a partir de `AllowedDomains`, exceto no caso de uma ligação limitada a uma organização, cujos domínios só são correspondidos dentro dessa organização.

A inicialização por configuração também pode definir todos os indicadores de comportamento da tabela abaixo. **Uma entrada inicializada pela configuração substitui a ligação guardada em cada arranque**: um indicador que omita volta à sua predefinição, pelo que deve declarar na configuração todos os indicadores que pretende manter (`ConnectionName`, `IconUrl` e `OrganizationId` são os únicos valores que sobrevivem a uma omissão, e `CreatedAt` é preservado).

| Campo | Predefinição | Efeito |
|---|---|---|
| `JitProvisioningEnabled` | `false` | Cria um utilizador federado desconhecido no primeiro início de sessão. Desativado, um utilizador desconhecido é rejeitado com `access_denied` |
| `AllowUninvitedJit` | `false` | Com `ProvisioningAttributeParams` declarado, aprovisiona também um utilizador que chegue sem esse contexto. Consulte [SSO self-service](self-service-sso) |
| `ProvisioningAttributeParams` | nenhum | Chaves de consulta do pedido de autorização guardadas num utilizador aprovisionado por JIT como atributos de aprovisionamento (o espelho, no sentido de entrada, de `PassthroughParams`) |
| `PassthroughParams` | nenhum | Chaves de consulta reencaminhadas para o URL de autorização a montante; consulte [Parâmetros de consulta reencaminhados](#passthrough-query-parameters) |
| `SessionExpClaim` | nenhum | Consulte [Limite do tempo de vida da sessão](#session-lifetime-cap) |
| `ShowOnLogin` | `true` | `false` oculta o botão "Continuar com"; a ligação só é alcançada através de `idp_hint` |
| `ChallengeMfaAfterLogin` | `true` | `false` confia na MFA do próprio fornecedor a montante e dispensa o desafio local |
| `IsExternalConnection` | `false` | Marca um IdP de terceiros pertencente ao cliente. Neutraliza `UseUpstreamSubjectAsUserId` e `AutoLinkExistingByEmail`, mesmo que estejam definidos |
| `UseUpstreamSubjectAsUserId` | `false` | O ID local de um utilizador JIT é o `sub` a montante em vez de um novo GUID. Apenas ligações próprias |
| `AutoLinkExistingByEmail` | `false` | Associa a uma conta local existente pelo email, mesmo quando `AllowedDomains` não abrange o domínio. Apenas ligações próprias |
| `RevalidateOnRefresh` | `false` | Consulte [Sessões federadas](federated-sessions) |
| `InteractionPath` | nenhum | Caminho da aplicação de início de sessão apresentado antes de federar um pedido `idp_hint` (tem de começar por `/`) |
| `OrganizationId` | nenhum | Limita a ligação a uma organização; consulte [SSO self-service](self-service-sso#organisation-scoped-connections) |

> **Um IdP na sua própria rede privada.** `MetadataLocation` tem de ser https e, por predefinição, tem de resolver para um endereço publicamente encaminhável: o Authagonal recusa destinos internos em todos os URLs que obtém, ao nível do URL e de novo ao nível do socket. Para federar com um IdP local (on-premises), indique-o em [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard). Isso abrange toda a troca, incluindo o `token_endpoint`, o `userinfo_endpoint` e o `jwks_uri` que o documento de descoberta indica. O https continua a ser obrigatório: este documento fornece as chaves com as quais é validado cada `id_token` a montante, e uma rede privada não é um canal seguro.

**Opção B, API de administração (para gestão em tempo de execução):**

```bash
curl -X POST https://auth.example.com/api/v1/oidc/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Google",
    "metadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
    "clientId": "your-google-client-id",
    "clientSecret": "your-google-client-secret",
    "redirectUrl": "https://auth.example.com/oidc/callback",
    "allowedDomains": ["example.com"],
    "jitProvisioningEnabled": true
  }'
```

O corpo de criação aceita `connectionName`, `metadataLocation`, `clientId` e `clientSecret` (todos obrigatórios), mais `iconUrl`, `redirectUrl` (ignorado, opcional), `organizationId`, `allowedDomains`, `passthroughParams`, `jitProvisioningEnabled` (predefinição `false`), `challengeMfaAfterLogin` (predefinição `true`) e `interactionPath`. O ID da ligação é gerado pelo servidor e devolvido no corpo `201` (o segredo do cliente nunca é devolvido). `metadataLocation` tem de ser https e é verificado face à proteção de pedidos de saída no momento da criação. Os restantes indicadores da tabela acima (`SessionExpClaim`, `ShowOnLogin`, `IsExternalConnection`, `RevalidateOnRefresh` e os demais) não podem ser definidos através da rota de criação: carregue-os a partir da configuração ou escreva-os através de `IOidcProviderStore` a partir do código do anfitrião. Não existe rota de atualização para uma ligação OIDC; para alterar uma, elimine-a e crie-a de novo (ou edite a configuração de inicialização). `GET /api/v1/oidc/connections/{connectionId}` e `DELETE` completam o conjunto.

### 2. Encaminhamento por domínio SSO {#2-sso-domain-routing}

Quando `AllowedDomains` é especificado (na configuração ou através da API de criação), os mapeamentos de domínios SSO são registados automaticamente. Sem encaminhamento por domínio, os utilizadores podem continuar a ser direcionados para o início de sessão OIDC através de `/oidc/{connectionId}/login`.

## Endpoints {#endpoints}

| Endpoint | Descrição |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...&loginHint=...` | Inicia o início de sessão OIDC. Gera PKCE + state + nonce, deriva o âmbito a montante e os parâmetros reencaminhados a partir de `returnUrl` e redireciona para o endpoint de autorização do IdP (`loginHint`, quando presente, é enviado a montante como `login_hint`). `404` para uma ligação desconhecida. |
| `GET /oidc/callback` | Trata o callback do IdP. Troca o código por tokens, valida o id_token, guarda todas as claims que não são de protocolo no cookie como `federated:*` e cria o utilizador ou inicia-lhe a sessão. |

## Propagação de âmbitos e claims {#scope-and-claim-flow-through}

O conjunto de âmbitos pedido pela RP a jusante em `/connect/authorize` é reencaminhado para o IdP a montante, **filtrado para o conjunto OIDC padrão**: `openid`, `profile`, `email`, `address`, `phone`, com `openid` sempre incluído. Tudo o resto que a RP tenha pedido (âmbitos de API personalizados, `offline_access`, …) é descartado antes da chamada a montante (a única exceção é uma ligação com `RevalidateOnRefresh`, que volta a acrescentar `offline_access` para poder obter um token de atualização a montante): um IdP estrito como o Google devolve `invalid_scope` para valores desconhecidos, e o fornecedor a montante só precisa de identificar o utilizador; os âmbitos próprios da RP são respeitados nos tokens emitidos pelo Authagonal, não nos do fornecedor a montante. As claims que o IdP a montante inclua no id_token em função dos âmbitos regressam ao Authagonal, são guardadas no ticket do cookie como claims `federated:<name>` e transitam para `OidcSubject.FederationClaims` na passagem seguinte por `/connect/authorize`. A partir daí, `ProtocolTokenService` volta a emiti-las nos tokens emitidos pelo Authagonal, filtradas pela mesma lista de permissões `Scope.UserClaims` que filtra `CustomAttributes`. Em caso de colisão de chaves, prevalece o valor do armazenamento de utilizadores do próprio Authagonal: estas claims chegam textualmente do IdP a montante, pelo que permitir que se sobrepusessem deixaria um IdP controlado pelo cliente redefinir qualquer claim libertada por âmbito sobre o seu próprio utilizador e sobrepor-se ao registo que este servidor tem dela. Uma claim a montante sem equivalente guardado continua a passar.

Efeito final: não há nenhuma lista de permissões de claims a preservar por ligação. Todas as claims que não são de protocolo que o fornecedor a montante coloca no id_token são captadas; quais delas chegam aos tokens a jusante é controlado pelas `UserClaims` do âmbito a jusante: declare lá a claim e o valor passa.

`FederationClaims` sobrevive às rotações de renovação separadamente de `CustomAttributes`, pelo que o contexto de federação por sessão (por exemplo, um token de hiperligação de partilha captado na autorização original) se mantém intacto, enquanto os atributos por utilizador continuam a ser relidos atualizados a partir do armazenamento de utilizadores.

## Parâmetros de consulta reencaminhados {#passthrough-query-parameters}

`OidcProviderConfig.PassthroughParams` é uma lista de permissões, por ligação, de chaves de consulta que passam do pedido `/authorize` original para o URL de autorização do IdP a montante. O conjunto padrão (`scope`, `state`, `nonce`, PKCE) é sempre reencaminhado; esta lista destina-se a valores adicionais especificados pela RP, como uma credencial de utilização única de que o fornecedor a montante precisa para autenticar (por exemplo, `link_token` para IdPs de hiperligações de partilha).

Quando uma chave está na lista de permissões, o Authagonal obtém o seu valor da consulta `/authorize` original (transportada através de `returnUrl`) e acrescenta-o ao URL a montante. Tudo o que não está na lista de permissões é descartado silenciosamente.

## Limite do tempo de vida da sessão {#session-lifetime-cap}

`OidcProviderConfig.SessionExpClaim` é o nome opcional de uma claim do id_token (segundos Unix) cujo valor limita o tempo de vida da sessão local. Quando está presente, o valor a montante transita como `session_max_exp` no ticket do cookie e para o código de autorização emitido; os access / id / tokens de atualização são limitados para que nenhum token, incluindo os emitidos a partir de rotações, sobreviva à sessão a montante. É útil quando o IdP a montante impõe prazos de sessão mais curtos do que os que o Authagonal aplicaria por predefinição.

## Funcionalidades de segurança {#security-features}

- **PKCE**: code_challenge com S256 em todos os pedidos de autorização
- **Validação do nonce**: o nonce é guardado com o state e tem de estar presente no id_token e corresponder
- **Validação do state**: de utilização única (consumido atomicamente através de `IOidcStateStore`, persistido com expiração) **e vinculado ao browser**: um cookie `SameSite=Lax` limitado a `/oidc` é definido no início de sessão e tem de corresponder ao `state` no callback, para que um atacante não possa concluir um fluxo de federação que iniciou e entregar o URL de callback a uma vítima (CSRF de início de sessão)
- **Validação da assinatura do id_token**: chaves obtidas do endpoint JWKS do IdP; emissor, audiência e tempo de vida validados
- **Recurso ao userinfo**: se o id_token não contiver um email, é consultado o endpoint userinfo. O `sub` do userinfo tem de corresponder ao `sub` do id_token (OIDC Core 5.3.2); caso contrário, a resposta é ignorada
- **Associação estável de identidade**: um utilizador que regressa é identificado pelo fornecedor + `sub`, nunca apenas pelo email. Associar uma identidade federada a uma conta local **pré-existente** pelo email exige que os `AllowedDomains` da ligação abranjam o domínio desse email (a garantia explícita do administrador de que o IdP é dono dele) ou `AutoLinkExistingByEmail` numa ligação própria, e é recusado quando o domínio está encaminhado para outra ligação. Uma conta já vinculada à identidade federada de outra ligação só é adotada quando esta ligação é a autoridade para o domínio, caso em que a vinculação antiga é removida. Um `email_verified` declarado pelo fornecedor a montante *não* basta para apropriar uma conta existente
- **Imposição de domínios**: quando `AllowedDomains` está definido, a ligação só pode declarar identidades dentro desses domínios (caso contrário, `access_denied`)
- **O JIT é opcional**: a menos que a ligação defina `JitProvisioningEnabled`, um utilizador desconhecido é rejeitado com `access_denied`. Quando o JIT se aplica, um fornecedor a montante que não declare `email_verified` não pode criar uma conta, nem pode fazê-lo uma ligação cujo domínio de email esteja encaminhado para outra ligação
- **Proteção contra redirecionamento aberto**: `returnUrl` tem de ser um caminho relativo do mesmo site; as formas relativas ao protocolo (`//`) e com barra invertida são rejeitadas
- **A MFA local continua a aplicar-se por predefinição**: a federação comprova apenas o primeiro fator. Um utilizador inscrito em MFA (ou cuja política de cliente exija MFA) passa pelas páginas locais de desafio/configuração de MFA depois do callback, em vez de iniciar sessão diretamente; só então a sessão transporta a marca de MFA. Uma ligação com `ChallengeMfaAfterLogin: false` dispensa este passo e inicia a sessão do utilizador como autenticado com MFA apenas com base na federação
- **Os metadados merecem uma confiança restrita**: o documento de descoberta tem de ser https e o seu URL tem de estar vinculado ao emissor que indica, e os id_tokens a montante só são aceites com algoritmos de assinatura assimétricos (RS/PS/ES 256, 384, 512)
- **Vinculação à organização**: um utilizador que inicia sessão através de uma ligação limitada a uma organização torna-se membro dessa organização, e a sessão transporta o respetivo `org_id`

## Particularidades do Azure AD {#azure-ad-specifics}

O Azure AD devolve por vezes os emails como array JSON na claim `emails` (sobretudo no B2C). O Authagonal trata este caso verificando tanto a claim `email` como o array `emails` (um array JSON ou uma única string).

## Fornecedores suportados {#supported-providers}

Qualquer fornecedor compatível com OIDC que suporte:
- Fluxo Authorization Code
- PKCE (S256)
- Documento de descoberta (`.well-known/openid-configuration`)

Testado com:
- Google
- Apple
- Azure AD / Entra ID
- Azure AD B2C

## Guias relacionados {#related-guides}

- [SSO self-service](self-service-sso): modalidades de aprovisionamento JIT (apenas por convite vs. self-service), o nível de confiança da ligação e os passos intermédios antes da federação.
- [Sessões federadas](federated-sessions): propagar a revogação a montante para a sessão local com `RevalidateOnRefresh`.
- [Promover um utilizador](user-upgrade): permitir que uma conta federada / de convidado reivindique uma palavra-passe própria.
