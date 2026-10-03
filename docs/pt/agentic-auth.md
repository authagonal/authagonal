---
layout: default
title: Autenticação agêntica
locale: pt
---

# Autenticação agêntica

O Authagonal disponibiliza os blocos de construção para delegar com segurança a autoridade de um utilizador em agentes de IA
(ou em qualquer carga de trabalho não humana): agentes registados, concessões de autoridade granulares, tokens de delegação
compostos, consentimento permanente do utilizador, aprovações just-in-time, tickets de capacidade e uma superfície de
auditoria que tem em conta a delegação. A biblioteca detém as primitivas e o invariante; a aplicação anfitriã monta-as
num produto (as implementações de conectores, a experiência de aprovação, o envio de notificações e a política de
negócio ficam do lado do anfitrião).

## O invariante {#the-invariant}

Todo o token delegado obedece a:

```
effective authority = admin ceiling ∩ user consent ∩ task request ∩ subject-token authority
```

Nada a jusante a pode alargar; cada salto de delegação adicional volta a intersetar, pelo que a autoridade só pode
estreitar-se. A interseção está implementada uma única vez (`AuthoritySet.Intersect`) e é usada em todo o lado.

## Entidades {#entities}

| Entidade | Tipo | Notas |
|---|---|---|
| Agente | `AgentProfile` num `OAuthClient` confidencial | É o registo de um perfil que torna um cliente num agente; eliminá-lo devolve o cliente ao OAuth simples. |
| Autoridade | `AuthoritySet` / `AuthorityGrant` | Forma `authorization_details` do RFC 9396: `type` do conector, `actions`, `locations`, restrições, políticas `auto`/`ask`/`deny` por ação. |
| Teto | `AgentProfile.Ceiling` | A autoridade mais ampla que qualquer delegação através do agente pode transportar. Gerido pelo administrador (`/api/v1/agents`). |
| Consentimento (piso) | `PersistedGrant` do tipo `agent_consent` | Por (utilizador, agente), gerido em `/consent/agents`. Guardado já intersetado com o teto e novamente intersetado em cada emissão. |
| Delegação | Troca de tokens RFC 8693 | Identidade composta: `sub` = utilizador, `act` = agente (aninhado por salto), `authorization_details` = a interseção efetiva. De curta duração, nunca renovável. |
| Aprovação | `PersistedGrant` do tipo `approval` | Barreira just-in-time para ações com política `ask`; semântica de polling do device flow; de utilização única, vinculada à forma do pedido. |
| Ticket de capacidade | `ICapabilityTicketService` | Identificador opaco de utilização única vinculado a um token: o ws-ticket do BFF generalizado, atómico sobre o armazenamento de concessões. |
| Auditoria | `IAuthHook` | `OnDelegationMintedAsync`, `OnApprovalRequested/ResolvedAsync`, `OnAgentConsentChangedAsync`, `OnCapabilityTicketRedeemedAsync`, mais a barreira anterior à emissão `OnTokenIssuingAsync`. |

## Registar um agente {#registering-an-agent}

1. Crie um cliente confidencial que permita `urn:ietf:params:oauth:grant-type:token-exchange`
   (modo delegado) e/ou `client_credentials` (modo de serviço).
2. `PUT /api/v1/agents/{clientId}`:

```json
{
  "mode": "delegated",
  "ceiling": [
    {
      "type": "email",
      "actions": ["send", "read"],
      "action_policies": { "send": "ask" },
      "recipient_domains": ["@acme.com", "*.partners.acme.com"]
    },
    { "type": "calendar", "actions": ["read"] }
  ],
  "maxDelegationDepth": 0,
  "maxTokenLifetimeSeconds": 300,
  "highRiskDefault": "ask"
}
```

`mode` é `delegated`, `service` ou `both` (numa atualização, um `mode` omitido mantém o valor existente). `maxDelegationDepth` tem de estar entre 0 e 8 (predefinição 0), `maxTokenLifetimeSeconds` entre 30 e 86400 (predefinição 300), e `highRiskDefault` tem de ser `auto`, `ask` ou `deny`; qualquer outro valor resulta num 400.

Os membros de restrição são tipificados pela forma JSON: string/array de strings → lista de permissões (combinação por
interseção de conjuntos; as entradas suportam correspondência exata, curinga `*.host` e correspondência `@suffix`),
número → limite (combinação pelo mínimo), booleano → barreira (combinação por AND). Os membros que não podem ser
interpretados são preservados textualmente e falham de forma fechada na avaliação.
`GET /api/v1/agents/{clientId}/effective-grant?subjectId=…` pré-visualiza teto ∩ consentimento para a interface de
administração.

## Consentimento do utilizador (o piso) {#user-consent-the-floor}

- `GET /consent/agents/{clientId}/info`: o teto apresentado com base no catálogo de conectores
  (registe um `IConnectorCatalog` para nomes de apresentação, descrições de ações e indicadores de alto risco;
  os seus tipos são anunciados na descoberta como `authorization_details_types_supported`).
- `POST /consent/agents` `{ "clientId": …, "authority": […] }`: concede o piso (omita
  `authority` para consentir o teto completo). Um utilizador pode tornar uma política mais restritiva (`auto` → `ask`),
  mas nunca afrouxá-la nem alargá-la; o armazenamento interseta previamente com o teto em vigor.
- `GET /consent/agents` / `DELETE /consent/agents/{clientId}`: listar e revogar. A revogação
  impede a emissão seguinte; as delegações em circulação não são renováveis e expiram dentro do seu tempo de vida
  (curto). Sem consentimento → a troca falha com `invalid_grant` /
  `consent_required`; o teto, por si só, não concede nada.

## Emitir uma delegação {#minting-a-delegation}

O agente autentica-se com a sua própria identidade e troca o token do utilizador:

```
POST /connect/token
grant_type=urn:ietf:params:oauth:grant-type:token-exchange
client_id=agent&client_secret=…            (or private_key_jwt, below)
subject_token={user access token}
subject_token_type=urn:ietf:params:oauth:token-type:access_token
authorization_details=[{"type":"email","actions":["read"]}]   (the task slice; omit = everything grantable)
```

A emissão impõe, por esta ordem: o modo do agente, o consentimento permanente, a profundidade de subdelegação (cada
ator já presente na cadeia `act` precisa de margem em `maxDelegationDepth` para mais um salto), a interseção, as recusas
de pedidos explícitos (`invalid_target`: um agente não deve julgar que detém uma autoridade que não tem), a barreira de
pergunta e os limites de tempo de vida (tempo de vida do cliente ∩ tempo restante do token de sujeito ∩
`maxTokenLifetimeSeconds`). O token transporta `act` (RFC 8693; aninhado por salto) e `authorization_details`
(RFC 9396); a resposta devolve os detalhes concedidos; a introspeção emite ambos. Trocar um token delegado restringe-o
automaticamente ainda mais, porque a própria claim do token de sujeito entra na interseção.

Os clientes **sem** perfil de agente mantêm exatamente o comportamento atual da troca, exceto que um parâmetro de pedido
`authorization_details` passa a estreitar (nunca a alargar) o token trocado.

## Aprovações (barreira de pergunta) {#approvals-ask-gate}

Quando a fatia efetiva contém uma ação `ask`, a troca fica em espera:

```json
{ "error": "authorization_pending", "approval_id": "…", "interval": 5 }
```

O anfitrião é notificado através de `IAuthHook.OnApprovalRequestedAsync` (o envio por email, push ou chat fica do lado
do anfitrião). O utilizador resolve-a (`GET /approvals`, `POST /approvals/{id}`
`{ "decision": "approve" | "deny" }`) enquanto o agente repete o pedido idêntico acrescentando
`approval_id`, com o vocabulário do device flow do princípio ao fim (`slow_down`, `access_denied`,
`expired_token`). As aprovações são de utilização única (consumo atómico), expiram ao fim de
`ApprovalLifetimeSeconds` (predefinição 300) e estão vinculadas à forma exata do pedido *e ao estado atual da
política*: uma edição do teto pelo administrador entre a espera e o polling invalida a aprovação, em vez de emitir
autoridade desatualizada. Uma aprovação consumida emite com as suas ações `ask` resolvidas para `auto` (a pergunta já
foi feita e respondida).

O modo de serviço (`client_credentials`) não tem nenhum utilizador no circuito: aplica-se apenas o teto e
`ask` degrada-se em `deny`.

## Imposição do lado do recurso {#resource-side-enforcement}

- `AuthorityEvaluator.Permits(user, type, action, context, location, strict)` em qualquer servidor de recursos
  (as chaves de contexto são comparadas com os nomes das restrições; passe o que conseguir derivar,
  por exemplo `recipient_domains` ao enviar correio). Um token sem a claim é avaliado sem
  restrições (compatibilidade com o comportamento anterior); uma claim corrompida é avaliada como recusa total.
  - `location` é o valor `locations` do RFC 9396 no qual está a atuar. Uma concessão que indica
    localizações só é respeitada nessas; uma localização concedida é uma **raiz**, pelo que
    `https://api.example.com/orders` abrange `/orders/17`, mas não `/orders-admin`.
  - `strict: true` recusa quando o chamador não forneceu contexto para uma restrição, em vez de a
    ignorar. Use-o sempre que consiga enumerar todas as chaves que suporta:
    `AuthoritySet.UncheckedConstraints(type, context)` indica as que não verificou.
- Ponto de controlo do BFF: `BffUpstream.RequiredAuthority = ["email:send"]` faz com que o proxy verifique o
  bearer de saída antes de reencaminhar, com 403 em caso de falha e sem passagem anónima. A localização que
  apresenta é o upstream que o pedido vai efetivamente alcançar (`AuthorityLocation` substitui a
  raiz quando a autoridade é emitida para um identificador público em vez do endereço interno);
  `StrictAuthority` faz com que o proxy recuse uma restrição que não consegue avaliar, em vez de a deixar
  para o upstream.

## Tickets de capacidade {#capability-tickets}

`ICapabilityTicketService` (predefinição `GrantStoreCapabilityTicketService`, registado com `TryAdd`
por `AddAuthagonalCore`, pelo que `AddAuthagonal` também o recebe) emite identificadores opacos de utilização única
vinculados a um token, resgatados atomicamente através da eliminação condicional do armazenamento de concessões,
duradouros e seguros contra repetição entre pods, ao contrário do obter-e-remover de uma cache simples. O ws-ticket do
BFF mantém o seu contrato de cache distribuída existente (`WsTicketKey` / `TryRedeemWsTicketAsync`) porque quem o
resgata é normalmente um anfitrião separado que partilha apenas o Redis; os brokers alojados no mesmo anfitrião devem
preferir o serviço de tickets de capacidade.

## private_key_jwt {#private_key_jwt}

Os agentes são cargas de trabalho; os segredos partilhados são o elo mais fraco da cadeia. Defina
`OAuthClient.JwksJson` (JWKS inline) ou `JwksUri` (obtido e mantido em cache durante ~10 min) e autentique-se
com uma asserção de cliente RFC 7523 (`client_assertion_type=…:jwt-bearer`). É imposto o seguinte:
assinatura verificada face ao JWKS registado, `iss` = `sub` = `client_id`, audiência = emissor ou
endpoint de token, `exp` limitado (≤ 10 min) e `jti` de utilização única (cache de repetições sobre
`IRevokedTokenStore`). Quando há uma asserção, nunca se recorre ao caminho do segredo.

## Compatibilidade {#compatibility}

- Sem perfil de agente → nenhuma alteração de comportamento em nenhum fluxo. Todas as novas tabelas/colunas são
  anuláveis com predefinição e aprovisionadas automaticamente em ambos os fornecedores de armazenamento (tabela
  `AgentProfiles`; `JwksJson`/`JwksUri` nos clientes; consentimentos, aprovações e tickets usam a tabela de concessões
  existente).
- Os novos membros de `IAuthHook` são métodos de interface com implementação predefinida; os hooks existentes compilam
  sem alterações.
- `ITokenExchangeSubjectTransformer` continua a ser executado em todas as trocas e pode rejeitar ou vincular
  claims de contexto; nunca pode alargar a delegação (o seu resultado é novamente intersetado) nem mexer na
  cadeia `act` (claim reservada).
