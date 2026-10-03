---
layout: default
title: Promover um utilizador
locale: pt
---

# Promover um utilizador (reivindicação de conta sem palavra-passe)

Algumas contas começam a existir sem palavra-passe:

- um **convidado** que abriu uma hiperligação de partilha e foi criado no momento por um início de sessão federado,
- um utilizador **aprovisionado por JIT** através de um início de sessão SSO ou de um convite de organização,
- um utilizador de diretório enviado por **SCIM**.

Cada um é um utilizador real do Authagonal (ID estável, geralmente com o acesso a jusante já aprovisionado) que
simplesmente não tem credencial local. A via de entrada *foi* a federação, a hiperligação de partilha ou o convite.

**Promover** um utilizador destes permite-lhe definir uma palavra-passe própria e, normalmente, eleva ao mesmo tempo
a sua relação com o seu produto (convidado → membro normal, avaliação → pago, "crie a sua organização").
O Authagonal fornece isto como um fluxo de primeira classe e opcional: a pessoa volta a registar-se com o mesmo email, comprova
que controla a caixa de correio e a sua conta **existente** é reivindicada *no próprio local* (mesmo ID de utilizador, pelo que todo o seu
acesso anterior se mantém), enquanto a sua aplicação executa a lógica de promoção de que precisar.

> Isto não é, deliberadamente, o mesmo que "repor a palavra-passe". Uma reposição de palavra-passe pressupõe uma conta
> com credencial e envia por email uma hiperligação de reposição. Uma reivindicação transforma uma conta *sem credencial* numa conta com credencial e
> volta a executar o aprovisionamento, para que os seus sistemas a jusante possam reagir à promoção.

## Quando usar {#when-to-use-it}

Ative o fluxo de reivindicação quando um produto a jusante trata "alguém que se regista com o email de uma identidade
federada" como uma via legítima de promoção, sendo o caso clássico um convidado de uma hiperligação de partilha que decide criar uma
conta real. Se a sua implementação não tiver essa via, mantenha-o desativado (a predefinição): todos os emails existentes são
então tratados como duplicados e o registo devolve a resposta normal, neutra quanto à enumeração.

## 1. Ativar {#1-enable-it}

A reivindicação é controlada por uma única opção opcional na secção de configuração `Auth` (associada a `AuthOptions`):

```json
{
  "Auth": {
    "AllowPasswordlessAccountClaim": true,
    "ClaimAllowedAttributeKeys": ["org_name", "plan"]
  }
}
```

- **`AllowPasswordlessAccountClaim`** (predefinição `false`): ativa o fluxo.
- **`ClaimAllowedAttributeKeys`** (predefinição vazia = permitir todas as chaves não reservadas): uma lista de permissões das
  chaves de atributos personalizados que uma reivindicação pode levar para a conta (consulte
  [Transmitir o contexto da promoção](#4-pass-upgrade-context-safely)). Indique as chaves que o seu aprovisionador espera, para que uma
  reivindicação não possa injetar atributos arbitrários. Apesar do nome, a mesma lista filtra também os
  `customAttributes` de um registo self-service normal.

Com o indicador desativado, um email existente é um duplicado. Com ele ativo, uma conta existente **sem credencial**
(sem `PasswordHash`) pode ser reivindicada; uma conta que já tem palavra-passe **nunca** é alterada: um
novo registo não pode substituir uma credencial real.

## 2. A reivindicação, do princípio ao fim {#2-the-claim-end-to-end}

O utilizador chama o endpoint de registo normal com o email da conta que pretende reivindicar:

```bash
# 1. The user re-registers with the SAME email as their guest/SSO/invite account.
curl -X POST https://auth.example.com/api/auth/register \
  -H "Content-Type: application/json" \
  -d '{
    "email": "grace@acme.com",
    "password": "a-strong-passphrase",
    "firstName": "Grace",
    "lastName": "Hopper",
    "customAttributes": { "org_name": "Acme Inc" }
  }'
# → 201 Created (enumeration-neutral: the same response a brand-new signup returns)
```

A resposta é `201` com `{ "success": true, "userId": "..." }`. Na via de reivindicação, o `userId` é um
valor descartável e não o ID real da conta, pelo que a resposta não pode ser usada para distinguir uma reivindicação de um
registo totalmente novo.

Ainda nada está ativo. O servidor **prepara** a palavra-passe e o perfil/atributos e envia por email uma nova
hiperligação de verificação. O utilizador abre-a:

```
GET https://auth.example.com/api/auth/confirm-email?token=<from the email>
```

Esse `GET` apenas apresenta uma página de confirmação com um clique (para que os analisadores de correio e os mecanismos de pré-carregamento de hiperligações que obtêm
o URL não consumam o token). É **premir o botão** dessa página que submete
`POST /api/auth/confirm-email` e que promove a credencial preparada e executa a promoção. O mesmo
`POST` aceita também o token como parâmetro de consulta ou num corpo JSON (`{ "token": "..." }`); um chamador JSON
recebe `{ "message": "Email confirmed successfully.", "appLink": ... }`, enquanto o envio do formulário a partir da página
redireciona para `/login?email_confirmed=1`. Depois disso, o utilizador inicia sessão normalmente com a sua nova palavra-passe.

### O que o servidor faz {#what-the-server-does}

1. **Registo**: como a conta existe e não tem palavra-passe, o pedido é tratado como uma reivindicação. A
   palavra-passe escolhida é transformada em hash em `PendingPasswordHash` (inerte, nenhuma via de autenticação a lê), e o nome próprio e o
   apelido, mais os `customAttributes` permitidos, são preparados em `PendingClaimJson`. O security
   stamp da conta é rodado no mesmo momento, o que invalida qualquer hiperligação de verificação que já esteja numa
   caixa de correio. A conta **não** é modificada de outra forma. É enviado um email de verificação mesmo que o
   email da conta já tenha sido confirmado pelo seu fluxo original: essa prova anterior pertencia a *outro*
   interveniente, e a reivindicação precisa da sua própria. A hiperligação transporta um resumo `pc=` da credencial preparada para ela.
2. **Confirmação**: a confirmação é a prova de propriedade. O servidor verifica se a hiperligação está vinculada à
   credencial *atualmente* preparada, aplica o perfil/atributos preparados, executa **`ReprovisionAsync`** (consulte a
   secção seguinte) e depois promove `PendingPasswordHash` a `PasswordHash` e roda de novo o security stamp.
   Se o aprovisionamento rejeitar a promoção, a credencial e o perfil preparados são descartados e a
   conta continua sem palavra-passe e pode voltar a ser reivindicada, pelo que nada fica persistido pela metade.

Se for submetida uma segunda reivindicação antes de a primeira ser confirmada, esta substitui a credencial preparada e a
primeira hiperligação deixa de funcionar: confirmá-la responde `claim_superseded` (JSON `400`, ou um redirecionamento para
`/login?error=claim_superseded` a partir da página de confirmação). Uma hiperligação sem resumo `pc=`, como uma proveniente
de uma ação de administração "enviar email de verificação", também falha desta forma enquanto houver uma credencial preparada. Em ambos os casos,
o utilizador pede uma nova hiperligação registando-se de novo.

O ID de utilizador nunca muda, pelo que o acesso de convidado a projetos, a vinculação SCIM, as associações a grupos, tudo isso, sobrevive
à promoção.

## 3. Fazer a promoção a jusante {#3-do-the-upgrade-downstream}

A confirmação de uma reivindicação chama `ReprovisionAsync`, que, ao contrário do aprovisionamento normal, volta a executar o
ciclo [TCC Try/Confirm/Cancel](provisioning) **mesmo para as aplicações em que o utilizador já está
aprovisionado**. É precisamente esse o objetivo: a sua aplicação já tinha aprovisionado este utilizador como *convidado*, pelo que um
aprovisionamento simples ignorá-lo-ia; o reaprovisionamento dá-lhe um segundo Try, agora com o contexto do registo, para
que o possa promover.

O seu handler `Try` de aprovisionamento distingue o "primeiro aprovisionamento" da "promoção" consoante já tenha ou não um
registo para esse `userId`, e reage ao contexto que a reivindicação transportou (aqui, `org_name`):

```javascript
// POST {CallbackUrl}/try
app.post('/provisioning/try', async (req, res) => {
  const { transactionId, userId, email, customAttributes } = req.body;
  const existing = await db.members.findByAuthId(userId);

  if (!existing) {
    // First time we've seen this user: a plain new signup.
    stagePending(transactionId, { userId, email, role: 'member' });
    return res.json({ approved: true });
  }

  if (existing.kind === 'guest') {
    // UPGRADE: the guest is claiming a real account. Create their org from the signup context,
    // and stage the promotion (applied in /confirm). Reject to abort the whole claim if it can't proceed.
    const orgName = customAttributes?.org_name;
    if (!orgName) return res.json({ approved: false, reason: 'Organization name is required' });

    stagePending(transactionId, { userId, upgradeTo: 'standard', orgName });
    // Return org_id so Authagonal stamps it on the user's tokens (org_id claim).
    const orgId = deterministicOrgId(userId);
    return res.json({ approved: true, organizationId: orgId });
  }

  // Already a full member: nothing to do, but approve so the claim completes.
  res.json({ approved: true });
});

// POST {CallbackUrl}/confirm: all apps approved; commit the promotion.
app.post('/provisioning/confirm', async (req, res) => {
  const p = takePending(req.body.transactionId);
  if (p?.upgradeTo === 'standard') {
    await db.orgs.create({ id: deterministicOrgId(p.userId), name: p.orgName, ownerAuthId: p.userId });
    await db.members.promote(p.userId, { kind: 'standard' });
  }
  res.sendStatus(200);
});

// POST {CallbackUrl}/cancel: the claim failed elsewhere; drop the staged promotion.
app.post('/provisioning/cancel', (req, res) => { takePending(req.body.transactionId); res.sendStatus(200); });
```

Um `approved: false` de qualquer aplicação (ou um callback falhado) faz falhar a confirmação com `400 provisioning_rejected`
(um corpo JSON para os chamadores da API, ou um redirecionamento para `/login?error=provisioning_rejected&error_description=...` a partir
da página de confirmação) e deixa a conta por promover, ainda sem palavra-passe e ainda passível de reivindicação. Um
registo que não é uma reivindicação e que uma aplicação de aprovisionamento rejeita dá um `422`; consulte [Aprovisionamento TCC](provisioning).
Um `organizationId` (ou `customAttributes` adicionais) na resposta de aprovação é fundido no utilizador e
acompanha os seus tokens.

## 4. Transmitir o contexto da promoção com segurança {#4-pass-upgrade-context-safely}

Os `customAttributes` da chamada de registo são a forma como a reivindicação transporta o contexto do registo (nome da organização, plano,
referência) até ao seu aprovisionador. São **preparados**, aplicados apenas no clique de verificação e filtrados por
`ClaimAllowedAttributeKeys`. Mantenha essa lista de permissões restrita: é a fronteira que impede alguém que apenas
*conhece* o email de um utilizador federado de injetar atributos que acompanhariam os tokens do verdadeiro titular. Uma
lista de permissões vazia permite todas as chaves não reservadas (prático para fluxos próprios de confiança); uma lista preenchida
descarta tudo o que não estiver indicado.

Diga a lista de permissões o que disser, o filtro aplica sempre estes limites e descarta silenciosamente tudo o que
os violar (o registo tem êxito na mesma):

- no máximo 32 atributos, com chaves até 64 caracteres e valores até 1024 caracteres;
- estas chaves reservadas nunca são aceites, mesmo que constem de `ClaimAllowedAttributeKeys`: `federated_connection`,
  `org_id`, `roles`, `groups`, `sub`, `iss`, `aud`, `scope`, `client_id`, `sid`, `acr`, `amr`, `email`,
  `email_verified`.

O mesmo filtro é aplicado ao registo self-service normal (que não é uma reivindicação).

## Propriedades de segurança {#security-properties}

- **Conhecer o email não basta.** A reivindicação só é concluída quando a caixa de correio da própria conta recebe e
  confirma a hiperligação de verificação. Um atacante que conheça o endereço nunca recebe o email.
- **Uma credencial preparada de cada vez.** A hiperligação está vinculada à credencial preparada para ela, pelo que uma reivindicação posterior
  não pode ser promovida por uma hiperligação anterior (`claim_superseded`).
- **Uma credencial real nunca é substituída.** Só uma conta sem `PasswordHash` pode ser reivindicada; uma reivindicação
  contra uma conta com credencial recebe a resposta normal de duplicado, neutra quanto à enumeração.
- **Nada fica ativo antes da confirmação.** A palavra-passe preparada não permite autenticar, e o
  perfil/atributos preparados não são aplicados, até à confirmação. Uma promoção rejeitada reverte tudo.
- **A injeção de atributos é limitada** por `ClaimAllowedAttributeKeys`.

## Relacionado {#related}

- [Aprovisionamento TCC](provisioning): o contrato Try/Confirm/Cancel que o seu handler implementa.
- [SSO self-service](self-service-sso): os fluxos JIT que criam, à partida, as contas sem palavra-passe.
