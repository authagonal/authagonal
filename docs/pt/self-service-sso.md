---
layout: default
title: SSO self-service
locale: pt
---

# Integração de SSO self-service

Depois de [federar uma ligação](oidc-federation) com o IdP de um cliente, a questão seguinte
é: **o que acontece quando aparece alguém que nunca iniciou sessão?** O Authagonal oferece-lhe
três posturas para esse utilizador desconhecido, da mais estrita à mais aberta (rejeitar todos os utilizadores desconhecidos, exigir
contexto de convite, ou aprovisionar automaticamente a partir de um domínio permitido), além dos controlos que impedem que um IdP *externo* se torne
uma armadilha. As duas primeiras são tratadas em conjunto na Postura 1, a terceira na Postura 2. Este guia mostra como escolher e ligar a postura que pretende.

Tudo isto é configuração por ligação: as secções de configuração de inicialização `OidcProviders` e `SamlProviders`, os
`OidcProviderConfig` / `SamlProviderConfig` guardados e a API de administração. As opções relevantes:

| Opção | Efeito | Protocolos |
|---|---|---|
| `JitProvisioningEnabled` | Pode ser criado um utilizador desconhecido? | OIDC, SAML |
| `ProvisioningAttributeParams` | Exigir *contexto de convite* no pedido antes de o criar. | OIDC, SAML |
| `AllowUninvitedJit` | Permitir a criação self-service **sem** convite (marcada com a ligação). | OIDC, SAML |
| `IsExternalConnection` | Marcar um IdP de terceiros para que os indicadores exclusivos de ligações próprias não se possam aplicar. | Apenas OIDC |
| `InteractionPath` | Mostrar uma página da aplicação de início de sessão (nome/termos) *antes* de federar. | Apenas OIDC |

Importa onde cada uma pode ser definida, porque a API de administração não as expõe todas:

- **Configuração de inicialização (`OidcProviders`, `SamlProviders`):** todas as opções acima que existem para o protocolo. As
  ligações inicializadas são reaplicadas a partir da configuração em cada arranque, pelo que, numa ligação inicializada,
  `JitProvisioningEnabled` e `AllowUninvitedJit` vêm da configuração e não do que tiver sido guardado por último.
- **API de administração SAML** (`POST` / `PUT /api/v1/saml/connections`): `JitProvisioningEnabled`,
  `ProvisioningAttributeParams` e `AllowUninvitedJit`.
- **API de administração OIDC** (`POST /api/v1/oidc/connections`): `JitProvisioningEnabled` e `InteractionPath`
  (que tem de começar por `/`). `ProvisioningAttributeParams`, `AllowUninvitedJit` e `IsExternalConnection`
  só podem ser definidos na configuração de inicialização em OIDC, e não existe rota de atualização para uma ligação OIDC. Consulte
  a [API de administração](admin-api) e [Federação OIDC](oidc-federation).

## Postura 1: apenas por convite (rejeitar quem não foi convidado) {#posture-1-invite-only-reject-the-uninvited}

A predefinição. Com `JitProvisioningEnabled: false`, um utilizador SSO desconhecido é rejeitado de imediato
(`access_denied`, "contacte o seu administrador"), que é o que pretende quando todos os utilizadores têm de ser criados previamente
por um administrador ou por SCIM.

Se quiser JIT, mas *apenas* quando existe um convite, ative o JIT **e** declare
`ProvisioningAttributeParams`. Estes indicam os parâmetros de consulta de `/authorize` permitidos que transportam o contexto
do convite (por exemplo, `acceptKind`, `acceptToken`). Um utilizador desconhecido só é aprovisionado quando pelo menos um desses
parâmetros chegou efetivamente com um valor; um início de sessão SSO simples sem convite é rejeitado com `access_denied`
("This login requires an invitation"), para que um início de sessão avulso não possa aprovisionar silenciosamente uma nova conta/organização.
Os parâmetros são lidos da consulta do URL `/authorize` para o qual o utilizador está a regressar (o `RelayState` no caso do
SAML); em OIDC, recorre-se também à consulta do próprio pedido de callback.

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "acme-entra",
      "ConnectionName": "Acme (Entra)",
      "MetadataLocation": "https://login.microsoftonline.com/<tenant>/v2.0/.well-known/openid-configuration",
      "ClientId": "…", "ClientSecret": "…",
      "AllowedDomains": ["acme.com"],
      "JitProvisioningEnabled": true,
      "ProvisioningAttributeParams": ["acceptKind", "acceptToken"]
    }
  ]
}
```

Não há nenhum `RedirectUrl` a definir: o `redirect_uri` do callback é derivado por pedido como
`{issuer}/oidc/callback`, pelo que é esse o URI a registar no IdP a montante. Um `RedirectUrl` na configuração é ignorado.

Os parâmetros captados ficam nos `CustomAttributes` do utilizador JIT e chegam ao seu
[handler `Try` de aprovisionamento](provisioning), que é o verdadeiro controlo sobre os *valores* (por exemplo,
"este token de convite corresponde a este email?"). O Authagonal capta as chaves permitidas; o seu aprovisionador decide
se são válidas. Se o `Try` responder `approved: false`, o utilizador acabado de criar é eliminado e o browser recebe
`400 provisioning_rejected`.

## Postura 2: self-service (aprovisionar automaticamente um utilizador de um domínio permitido) {#posture-2-self-service-auto-provision-an-allowed-domain-user}

Para "qualquer colaborador de um cliente pode simplesmente iniciar sessão e obter uma conta", defina `AllowUninvitedJit: true`. A partir daí, um
utilizador desconhecido de um **domínio permitido** é aprovisionado mesmo sem contexto de convite, e o Authagonal marca-o
com a ligação pela qual chegou, para que o seu aprovisionador o possa colocar no inquilino certo em vez de
criar um novo. A verificação do domínio só se aplica quando `AllowedDomains` não está vazio: uma ligação que
não indica domínios aceita qualquer domínio que o seu IdP declare, pelo que deve indicá-los em todas as ligações self-service.

```json
{
  "ConnectionId": "acme-entra",
  "AllowedDomains": ["acme.com"],
  "JitProvisioningEnabled": true,
  "ProvisioningAttributeParams": ["acceptKind", "acceptToken"],
  "AllowUninvitedJit": true
}
```

A marca chega como atributo personalizado `federated_connection`. O seu valor é o `ConnectionName` da ligação
(não o seu `ConnectionId`), e só é escrito quando o utilizador foi criado sem contexto de convite; um
utilizador convidado transporta, em vez disso, os parâmetros captados. O seu handler `Try` ramifica com base nisso:

```javascript
app.post('/provisioning/try', async (req, res) => {
  const { userId, email, customAttributes } = req.body;

  if (customAttributes?.acceptToken) {
    // Invited: validate the invite and add them to that org.
    const org = await validateInvite(customAttributes.acceptToken, email);
    if (!org) return res.json({ approved: false, reason: 'Invalid invite' });
    stage(userId, { orgId: org.id, role: customAttributes.acceptKind ?? 'member' });
    return res.json({ approved: true, organizationId: org.id });
  }

  if (customAttributes?.federated_connection) {
    // Self-service: no invite, but they came through a known enterprise connection.
    const org = await orgForConnection(customAttributes.federated_connection);
    stage(userId, { orgId: org.id, role: 'member' });
    return res.json({ approved: true, organizationId: org.id });
  }

  return res.json({ approved: false, reason: 'No invite and no known connection' });
});
```

`AllowUninvitedJit` é opcional por ligação: uma ligação que declare `ProvisioningAttributeParams` mas
**não** o defina continua a ser apenas por convite.

São executadas mais duas verificações antes de criar qualquer utilizador desconhecido, seja qual for a postura escolhida:

- **O domínio não pode pertencer a outra ligação.** Se o índice de domínios SSO encaminhar o domínio do email do utilizador
  para outra ligação, o início de sessão é recusado com `access_denied` ("This email domain is
  managed by a different identity provider").
- **Apenas OIDC: o fornecedor a montante tem de ter verificado o email.** Quando o fornecedor a montante não indica `email_verified` como verdadeiro (lido do id_token, ou da
  resposta userinfo, quando o email veio de lá), o início de sessão é
  recusado com `access_denied`. Uma asserção SAML não tem esse indicador, pelo que o SAML se apoia antes em `AllowedDomains`.

`federated_connection` é um nome de atributo reservado. Nunca é emitido num token, uma claim do id_token de um fornecedor OIDC a montante
com esse nome é descartada e o registo self-service anónimo não o pode definir, pelo que só os callbacks
SSO podem declarar por que ligação uma conta chegou.

## Impedir que os IdPs externos se tornem armadilhas {#keep-external-idps-from-becoming-foot-guns}

Alguns indicadores de ligações OIDC são seguros numa ligação que **o próprio** controla, mas perigosos num IdP de terceiros
arbitrário:

- **`UseUpstreamSubjectAsUserId`**: o fornecedor a montante escolhe o ID do utilizador local. No seu próprio fornecedor de
  hiperligações de partilha, isso mantém os IDs alinhados; no IdP de um cliente, permite que seja *ele* a escolher os seus IDs de utilizador.
- **`AutoLinkExistingByEmail`**: associar um início de sessão federado a uma conta local pré-existente pelo email,
  dispensando a verificação de propriedade do domínio. Com a caixa de correio verificada e numa ligação própria, não há problema; num IdP externo, é uma
  alavanca de apropriação de contas.

Marque as ligações de terceiros como **externas** e esses indicadores ficam neutralizados, mesmo que estejam definidos:

```json
{
  "ConnectionId": "acme-entra",
  "IsExternalConnection": true,
  "UseUpstreamSubjectAsUserId": false,
  "AutoLinkExistingByEmail": false
}
```

`IsExternalConnection` tem a predefinição `false` (ligação própria), pelo que as ligações existentes não são afetadas. Defina-o em
todas as ligações OIDC que apontem para o IdP de outra entidade; assim, uma configuração errada posterior não pode entregar a esse IdP
o controlo sobre as identidades locais. As ligações SAML não têm nenhum destes indicadores, pelo que não há nada a neutralizar
aí. (Associar uma identidade federada a uma conta pré-existente continua a exigir adicionalmente que os
`AllowedDomains` da ligação abranjam o domínio do email: consulte
[Federação OIDC: Segurança](oidc-federation).)

## Recolher algo antes de federar {#collect-something-before-federating}

Por vezes é preciso mostrar ao utilizador uma página **antes** de o reencaminhar para o IdP: o nome a apresentar de um convidado, uma
caixa de verificação de termos, um seletor de plano. `InteractionPath` (apenas em ligações OIDC) indica uma rota da aplicação de início de sessão a apresentar
primeiro:

```json
{ "ConnectionId": "guest-link", "InteractionPath": "/guest" }
```

Quando um pedido `idp_hint={ConnectionId}` não autenticado chega a `/connect/authorize`, o Authagonal redireciona para
`{LoginAppUrl}{InteractionPath}?returnUrl=<authorize url>&connection={id}` em vez de ir diretamente para o IdP
(`LoginAppUrl` tem a predefinição `/login`, e o caminho tem de começar por `/`). O mesmo redirecionamento acontece quando
a ligação única, ou correspondente ao domínio, de uma [organização](#organisation-scoped-connections) é desafiada automaticamente,
e quando `prompt=login` força uma nova autenticação através de um `idp_hint`. A sua página recolhe o que precisa,
acrescenta os valores à consulta do `returnUrl` (de onde `PassthroughParams` /
`ProvisioningAttributeParams` os leem) e prossegue ela própria para `/oidc/{id}/login`. Uma página que
decida que não é necessária nenhuma interação pode prosseguir de imediato.

## Ligações limitadas a uma organização {#organisation-scoped-connections}

Tudo o que foi descrito acima diz respeito a uma ligação **ao nível do inquilino**: uma ligação partilhada por todo o inquilino, cujos
`AllowedDomains` reivindicam um domínio de email para todos os ecrãs de início de sessão que o inquilino serve. É a forma
certa quando federa com um cliente por inquilino. É a forma errada quando um inquilino serve muitas
[organizações](organizations) clientes e cada uma traz o seu próprio IdP: dois clientes não podem ambos reivindicar
`contoso.com`, e o botão "Continuar com Contoso Entra" de um cliente não tem nada que aparecer no
ecrã de início de sessão de outro.

Defina `OrganizationId` numa ligação e esta passa a pertencer a essa organização (na criação e, para SAML, na
atualização, através da API de administração; uma organização inexistente dá `400 unknown_organization`):

```json
{
  "ConnectionId": "acme-entra",
  "OrganizationId": "org_7f3a9c",
  "AllowedDomains": ["acme.com"],
  "JitProvisioningEnabled": true
}
```

Mudam três coisas, e mais nenhuma.

**Só é oferecida quando essa organização está selecionada.** Uma ligação limitada a uma organização nunca aparece no
ecrã de início de sessão do próprio inquilino e nunca é alcançada por um pedido que não resolveu para nenhuma organização,
mesmo que o seu `login_hint` corresponda exatamente aos domínios dela.

**Os seus domínios só são correspondidos dentro dessa organização.** Uma ligação limitada a uma organização *não* é,
deliberadamente, escrita no índice de domínios SSO de todo o inquilino, pelo que um domínio pode ser reivindicado uma vez ao nível do inquilino e
uma vez por organização. Uma segunda reivindicação dentro da mesma organização continua a ser recusada com `domain_claimed`,
em ambos os protocolos, para que um endereço não possa ser encaminhado para dois IdPs de uma organização. Mover uma ligação
para dentro de uma organização remove as suas linhas do índice; movê-la de volta (envie `"organizationId": ""` para o endpoint
de atualização SAML) volta a registá-las. As ligações OIDC não têm rota de atualização, pelo que o seu âmbito é definido na criação ou
na configuração de inicialização `OidcProviders`.

**Todos os que iniciam sessão através dela são membros dela.** O ACS SAML e o callback OIDC marcam a
organização da ligação como `org_id`, sobrepondo-se ao `AuthUser.OrganizationId` da própria conta (que
é um artefacto do aprovisionamento a jusante e não uma afirmação sobre este início de sessão), e criam uma
associação ativa se o utilizador não tiver nenhuma. Uma associação **convidada** é aceite: passa a `active` e
mantém as suas funções, quem convidou e a data do convite, porque o próprio IdP da organização garantiu agora a identidade da
pessoa. Qualquer outra associação existente não é alterada: uma linha `suspended` continua suspensa, pelo que iniciar sessão
de novo não pode restaurar um acesso que um administrador revogou.

### Para que organização é um pedido, antes de alguém iniciar sessão {#which-organization-a-request-is-for-before-anyone-signs-in}

A descoberta do domínio de origem (home-realm discovery) tem de responder a isto antes de existir um utilizador, pelo que a resolução é feita separadamente do
[seletor pós-autenticação](organizations#precedence) (mas pela mesma ordem):

1. O **parâmetro `organization`** do pedido (um slug ou um ID).
2. **`OAuthClient.RestrictedToOrganizationIds`**, quando contém exatamente uma entrada. Duas ou mais não constituem uma
   seleção: o cliente serve várias e o pedido não indicou nenhuma.
3. **`ITenantContext.OrganizationId`**: um anfitrião que fixa uma por pedido, por exemplo um domínio personalizado
   por organização. `null` em todas as implementações de inquilino único.

A organização tem de existir e estar ativa, e a restrição do cliente tem de a permitir. Tudo o resto
resolve para *nenhuma organização* e o pedido prossegue pela via de todo o inquilino, exatamente como antes. Em
particular, um parâmetro que indique uma organização da qual o cliente está excluído não é recusado aqui:
a recusa já existe depois da autenticação (`access_denied`), e antecipá-la para antes do ecrã de início de sessão
alteraria os pedidos que um chamador não autenticado consegue distinguir.

### O que `/connect/authorize` faz com ela {#what-connectauthorize-does-with-it}

Com uma organização resolvida, e antes de qualquer regra de todo o inquilino:

- Um `idp_hint` que indique uma das ligações **dela** vai diretamente para essa ligação. Isso inclui o SAML, que
  a via de indicação de todo o inquilino (apenas OIDC) não consegue alcançar. Uma indicação de qualquer outra coisa segue para as regras seguintes.
- **Exatamente uma ligação e nenhum `login_hint` contraditório** → diretamente para ela. Uma ligação que não indica
  domínios reivindica toda a organização; uma que indique domínios continua a ser desafiada automaticamente, a menos que o
  domínio do endereço indicado não esteja entre eles.
- **Várias ligações** → o domínio do email indicado escolhe entre elas.
- **Nenhuma correspondência** → o comportamento de `login_hint` e do cartão de início de sessão de todo o inquilino, inalterado.

Uma federação que falhou e regressou com `error=` na consulta devolve esse erro à relying
party em vez de federar de novo, pelo que um desafio automático não pode entrar em ciclo.

### O que a aplicação de início de sessão vê {#what-the-login-app-sees}

`/api/auth/providers` e `/api/auth/sso-check` aceitam ambos um parâmetro de consulta `organization` (e recorrem
a `ITenantContext.OrganizationId`), resolvido pelas mesmas regras. Com uma organização:

- `providers` lista primeiro as ligações com botão **dessa organização** e depois as do próprio inquilino. Uma ligação
  só é um botão quando não indica `AllowedDomains` (e, em OIDC, tem `ShowOnLogin` ativo); as encaminhadas por domínio
  são alcançadas começando pelo email, através de `sso-check`. As ligações limitadas a uma organização são totalmente excluídas da lista
  quando não é resolvida nenhuma organização, e as ligações de outras organizações nunca são listadas.
- `providers` ganha **`autoChallenge`** quando a organização tem exatamente uma ligação: um registo de fornecedor
  completo (`connectionId`, `name`, `type`, `loginUrl`, `iconUrl`) da ligação para a qual a aplicação
  deve ir diretamente, dispensando o cartão. Transporta o registo completo e não apenas um ID, porque
  essa ligação pode ser encaminhada por domínio ou estar oculta e, por isso, estar ausente de `providers`. Caso contrário, o campo é
  omitido, e é **indicativo**: `/connect/authorize` faz ele próprio o mesmo desafio automático,
  pelo que uma aplicação que o ignore chega na mesma ao mesmo IdP.
- `sso-check` faz corresponder os domínios das ligações da organização **antes** do índice de todo o inquilino, e
  recorre a este quando a organização não reivindica nada para esse endereço. Uma ligação única que não indique
  domínios reivindica todos os endereços.

### Na emissão de tokens {#at-token-issuance}

Uma sessão estabelecida através de uma ligação limitada a uma organização transporta a sua organização como a fonte de maior prioridade
a seguir ao valor transportado por uma renovação (acima do parâmetro `organization` e acima da restrição
do cliente), porque é a única que foi *comprovada*: o utilizador autenticou-se num IdP que pertence
exatamente a essa organização. Um pedido que indique outra é recusado com `access_denied`, em vez de
lhe ser emitida discretamente a outra. O `RequireMembershipForTokens` da organização continua a aplicar-se, e é
por isso que o callback cria a associação.

## Relacionado {#related}

- [Organizações](organizations): os registos, as associações, as claims e as regras de seleção.
- [Federação OIDC](oidc-federation): configurar a ligação e o modelo de segurança.
- [Aprovisionamento TCC](provisioning): o handler `Try` que estes fluxos chamam.
- [Manter as sessões federadas sincronizadas](federated-sessions): revogar as sessões locais quando o fornecedor a montante o faz.
