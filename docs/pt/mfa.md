---
layout: default
title: Autenticação multifator
locale: pt
---

# Autenticação multifator (MFA)

O Authagonal suporta autenticação multifator. Estão disponíveis três métodos: TOTP (aplicações de autenticação), WebAuthn/chaves de acesso (chaves de hardware e biometria) e códigos de recuperação de utilização única. As chaves de acesso também podem ser usadas para [início de sessão sem palavra-passe](#passwordless-passkey-login).

Os inícios de sessão federados (SAML/OIDC) também estão abrangidos: uma asserção SAML ou OIDC comprova o primeiro fator, não o segundo. Um utilizador federado com MFA inscrita passa pelo mesmo desafio MFA local que um início de sessão com palavra-passe, e uma política `Required` obriga à inscrição antes de ser emitida qualquer sessão. A federação só é suficiente por si só quando a MFA não está inscrita nem é obrigatória. Uma ligação pode excluir-se do desafio local com `ChallengeMfaAfterLogin: false` (ver abaixo).

## Métodos suportados {#supported-methods}

| Método | Descrição |
|---|---|
| **TOTP** | Palavras-passe de utilização única baseadas no tempo (RFC 6238): 6 dígitos, intervalo de 30 segundos, SHA-1, verificadas com uma janela de tolerância de um intervalo para desvios de relógio. Funciona com qualquer aplicação de autenticação (Google Authenticator, Authy, 1Password, etc.). Um código que já tenha sido aceite não pode ser reutilizado dentro da sua janela de validade. |
| **WebAuthn / Chaves de acesso** | Chaves de segurança de hardware FIDO2, biometria da plataforma (Touch ID, Windows Hello) e chaves de acesso sincronizadas. Os utilizadores podem registar várias chaves de acesso, e as chaves de acesso permitem iniciar sessão sem palavra-passe. |
| **Códigos de recuperação** | 10 códigos de reserva de utilização única (10 caracteres de um alfabeto de 32 caracteres, apresentados como `XXXXX-XXXXX`) para recuperar a conta quando os outros métodos não estão disponíveis. Guardados com hash e cifrados em repouso. |

## Política de MFA {#mfa-policy}

A imposição da MFA é configurada **por cliente** através da propriedade `MfaPolicy` em `appsettings.json`:

| Valor | Comportamento |
|---|---|
| `Disabled` (predefinição) | Não obriga à inscrição; a interface de configuração self-service oculta a MFA quando todos os clientes estão em `Disabled` |
| `Enabled` | Oferece a inscrição em MFA, sem a impor |
| `Required` | Obriga à inscrição os utilizadores sem MFA |

Um utilizador com MFA inscrita é **sempre desafiado no início de sessão, independentemente da política do cliente**. A MFA é uma propriedade do utilizador e da sua sessão, não do cliente que faz o pedido, pelo que um pedido encaminhado através de um cliente `Disabled` não pode ser usado para saltar o segundo fator de um utilizador inscrito.

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "MfaPolicy": "Enabled"
    },
    {
      "ClientId": "admin-portal",
      "MfaPolicy": "Required"
    }
  ]
}
```

A predefinição é `Disabled`, pelo que os clientes existentes não são afetados até optar por ativá-lo.

### Substituição por utilizador {#per-user-override}

Implemente `IAuthHook.ResolveMfaPolicyAsync` para substituir a política do cliente para utilizadores específicos:

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    // Exempt service accounts
    if (email.EndsWith("@service.internal"))
        return Task.FromResult(MfaPolicy.Disabled);

    return Task.FromResult(clientPolicy);
}
```

A política resolvida rege a inscrição (se é oferecida ou imposta). Não isenta do desafio um utilizador já inscrito; os utilizadores inscritos são sempre desafiados.

Consulte [Extensibilidade](extensibility) para a documentação completa dos hooks.

## Fluxo de início de sessão {#login-flow}

O fluxo de início de sessão com MFA funciona da seguinte forma:

1. O utilizador envia o email e a palavra-passe para `POST /api/auth/login`
2. O servidor verifica a palavra-passe e depois resolve a política de MFA efetiva
3. Com base na política e no estado de inscrição do utilizador:

| Política | O utilizador tem MFA? | Resultado |
|---|---|---|
| Qualquer | Sim | Devolve `mfaRequired`: o utilizador tem de se verificar |
| `Disabled` / `Enabled` | Não | Cookie definido, início de sessão concluído |
| `Required` | Não | Devolve `mfaSetupRequired`: o utilizador tem de se inscrever |

### Desafio MFA {#mfa-challenge}

Quando é devolvido `mfaRequired`, a resposta de início de sessão inclui um `challengeId`, os `methods` disponíveis do utilizador e (quando o utilizador tem chaves de acesso) as opções de asserção `webAuthn`. O cliente redireciona para uma página de desafio MFA, na qual o utilizador se verifica com um dos seus métodos inscritos através de `POST /api/auth/mfa/verify`:

```json
{
  "challengeId": "...",
  "method": "totp",
  "code": "123456"
}
```

`method` é `totp`, `recovery` ou `webauthn` (o WebAuthn envia uma `assertion` em vez de um `code`).

Os desafios expiram ao fim de 5 minutos (configurável através de `Auth:MfaChallengeExpiryMinutes`) e são consumidos quando a verificação é bem-sucedida.

#### Limite de tentativas {#retry-budget}

Um código errado não inutiliza o desafio. O endpoint de verificação valida primeiro o código e só consome o desafio em caso de sucesso, pelo que um dígito TOTP mal introduzido pode simplesmente ser repetido com o mesmo `challengeId`. As tentativas falhadas devolvem `invalid_code` (ou `assertion_failed` no caso do WebAuthn) com um 401 e incrementam um contador limitado no desafio; a quinta tentativa errada consome o desafio e devolve `too_many_attempts`, obrigando a um novo início de sessão. Isto aplica-se aos três métodos.

O limite por desafio é uma primeira barreira rápida, não o limite de segurança, pelo que se aplicam mais duas barreiras a `POST /api/auth/mfa/verify`:

- **Limite de pedidos por utilizador.** Mais de 10 tentativas de verificação por minuto para o mesmo utilizador devolvem `too_many_attempts` com um 429, seja qual for o `challengeId` usado.
- **Bloqueio de conta partilhado.** Cada código falhado conta também para o mesmo contador de tentativas falhadas que o passo da palavra-passe (`Auth:MaxFailedAttempts`, `Auth:LockoutDurationMinutes`). Quando dispara, o desafio é consumido e a resposta é `locked_out` (423). Enquanto a conta estiver bloqueada, a verificação é recusada com `locked_out` antes de o código ser verificado.

Só as credenciais confirmadas podem satisfazer uma verificação; uma inscrição iniciada mas nunca concluída não conta como fator.

Um desafio em falta, expirado ou já consumido devolve `invalid_challenge`.

### Inícios de sessão federados {#federated-logins}

Após uma asserção SAML ou OIDC bem-sucedida, o servidor resolve a mesma política de MFA efetiva. Um utilizador inscrito em MFA é redirecionado para a página alojada de desafio MFA (com um `challengeId`) em vez de receber uma sessão; um utilizador sem MFA sujeito a uma política `Required` é redirecionado para a página de configuração de MFA (com um `setupToken`). A sessão só é marcada como autenticada com MFA quando a verificação é concluída.

Este desafio é definido por ligação: uma ligação SAML ou OIDC com `ChallengeMfaAfterLogin` definido como `false` dispensa o desafio local para os utilizadores que chegam através dela. A predefinição é `true`.

### Inscrição obrigatória {#forced-enrollment}

Quando é devolvido `mfaSetupRequired`, a resposta inclui um `setupToken`. Este token autentica o utilizador junto dos endpoints de configuração de MFA (através do cabeçalho `X-MFA-Setup-Token`), para que possa inscrever um método antes de obter uma sessão por cookie. Os tokens de configuração expiram ao fim de 15 minutos (configurável através de `Auth:MfaSetupTokenExpiryMinutes`).

## Inscrever a MFA {#enrolling-mfa}

Os utilizadores inscrevem a MFA através dos endpoints de configuração self-service. Estes exigem uma sessão por cookie autenticada ou um token de configuração.

### Configuração de TOTP {#totp-setup}

1. Chame `POST /api/auth/mfa/totp/setup`, que devolve um código QR (`data:image/png;base64,...`), uma `manualKey` (Base32 para introdução manual) e um token de configuração
2. O utilizador lê o código QR com a sua aplicação de autenticação
3. O utilizador introduz o código de 6 dígitos para confirmar: `POST /api/auth/mfa/totp/confirm`

O passo de confirmação é limitado tal como a verificação: mais de 10 tentativas por minuto para o mesmo utilizador devolvem `too_many_attempts` (429) e, com um token de configuração, o quinto código errado consome o desafio de configuração. Uma inscrição não confirmada expira ao fim de 30 minutos (`setup_expired`).

### Configuração de WebAuthn / chave de acesso {#webauthn--passkey-setup}

1. Chame `POST /api/auth/mfa/webauthn/setup`, que devolve um `setupToken` e `PublicKeyCredentialCreationOptions`
2. O cliente chama `navigator.credentials.create()` com as opções
3. Envie a resposta de atestação para `POST /api/auth/mfa/webauthn/confirm`

A inscrição de chaves de acesso exige primeiro uma credencial TOTP confirmada (`totp_required_first`). As chaves de acesso são uma comodidade por dispositivo, sobreposta a um fator de base portátil, pelo que todas as contas mantêm um fator independente do dispositivo e uma política `Required` não pode ser satisfeita apenas por uma chave de acesso.

Os utilizadores podem registar várias chaves de acesso (uma por dispositivo). Um ID de credencial já registado (em qualquer conta, incluindo a do próprio utilizador que se está a inscrever) é rejeitado com `credential_already_registered` (409). Voltar a inscrever um autenticador já inscrito criaria uma segunda linha de credencial com o mesmo ID de credencial: o seu contador de assinaturas recomeçaria, enfraquecendo a deteção de clones, e eliminar qualquer uma das linhas removeria a entrada de pesquisa de que ambas dependem. A entrada de pesquisa é reivindicada com uma escrita que só insere se não existir, pelo que dois registos do mesmo ID de credencial não podem ser ambos bem-sucedidos. Os utilizadores cujo domínio de email é encaminhado para um IdP externo através de SSO forçado não podem inscrever uma chave de acesso local (`sso_managed`), uma vez que esta contornaria o IdP e o respetivo desaprovisionamento.

### Anfitrião da relying party {#relying-party-host}

O ID da relying party FIDO2 e a origem são resolvidos por pedido a partir do anfitrião, pelo que cada nome de anfitrião de inquilino é a sua própria relying party. Defina `Auth:WebAuthnAllowedHosts` com os nomes de anfitrião que serve, para que um anfitrião fora dessa lista não possa atuar como relying party. Uma lista vazia (a predefinição) mantém o comportamento anterior, em vez de bloquear os utilizadores de chaves de acesso existentes na atualização, e é registada no log como lacuna na primeira utilização. Não é um estado seguro em que se possa ficar. Definir também `AllowedHosts` em `appsettings.json`, para que a filtragem de anfitriões do ASP.NET Core rejeite cabeçalhos `Host` não reconhecidos antes de qualquer handler ser executado, é a camada exterior mais barata.

Independentemente dessa lista, cada credencial regista a relying party sob a qual foi inscrita e é recusada em qualquer outra. Essa é a parte que o pedido não pode influenciar: de outro modo, ambas as cerimónias constroem as suas expectativas a partir do mesmo cabeçalho `Host` que estão a verificar, pelo que a origem e o `rpIdHash` eram comparados com um valor fornecido pelo chamador, e um anfitrião intermédio que reencaminhasse o seu próprio `Host` faria com que a vinculação à origem, a propriedade que torna uma chave de acesso resistente a phishing, o validasse em vez de o impedir. As credenciais inscritas antes de o ID da RP ser registado não o têm e continuam a funcionar; ganham a vinculação quando forem inscritas de novo.

### Códigos de recuperação {#recovery-codes}

Chame `POST /api/auth/mfa/recovery/generate` para gerar 10 códigos de utilização única. Tem de estar primeiro inscrito pelo menos um método principal confirmado (TOTP ou WebAuthn) (`primary_method_required`), e a chamada exige uma sessão autenticada real: um token de configuração recebe `session_required` (403).

Cada código tem 10 caracteres de um alfabeto de 32 caracteres, apresentados como dois grupos de cinco (`XXXXX-XXXXX`).

Gerar novos códigos substitui todos os códigos de recuperação existentes. Cada código só pode ser usado uma vez; um código resgatado é marcado como consumido e deixa de ser aceite.

Os códigos nunca são guardados em texto simples: cada código passa por hash, e o hash é ainda cifrado em repouso com o fornecedor de segredos do inquilino, pelo que uma cópia do armazenamento produz texto cifrado em vez de um hash que se possa atacar por força bruta offline.

## Início de sessão sem palavra-passe com chave de acesso {#passwordless-passkey-login}

As chaves de acesso não são apenas um segundo fator: um utilizador com uma chave de acesso inscrita pode iniciar sessão sem palavra-passe.

1. `POST /api/auth/mfa/passwordless/begin` devolve um `challengeId` e `options` de asserção para credenciais detetáveis, para que o autenticador ofereça qualquer chave de acesso residente para o site
2. O cliente chama `navigator.credentials.get()` com as opções
3. `POST /api/auth/mfa/passwordless/complete` com `{ challengeId, assertion }`: o servidor identifica o utilizador a partir da própria chave de acesso e inicia-lhe a sessão

A página de início de sessão alojada integra isto no campo de email através de mediação condicional (preenchimento automático de chaves de acesso): quando o browser o suporta, uma chave de acesso disponível é oferecida como sugestão de preenchimento automático, sem qualquer interface adicional.

Uma chave de acesso é uma autenticação forte resistente a phishing, pelo que a sessão resultante transporta a marca de MFA e não volta a ser desafiada. Se o domínio de email do utilizador for encaminhado para um IdP externo através de SSO forçado, o início de sessão sem palavra-passe é recusado com uma resposta 409 `sso_required` que inclui o URL de redirecionamento SSO, para que uma chave de acesso local não possa contornar o IdP.

## Gerir a MFA {#managing-mfa}

### Self-service do utilizador {#user-self-service}

- `GET /api/auth/mfa/status`: ver os métodos inscritos (indica também se algum cliente oferece MFA)
- `DELETE /api/auth/mfa/credentials/{id}`: remover uma credencial específica

Remover uma credencial exige uma sessão autenticada real; um token de configuração só autoriza a adição de um primeiro fator e recebe aqui `session_required`, para que um token de configuração divulgado não possa enfraquecer a MFA de um utilizador.

Se o último método principal for removido, a MFA é desativado para o utilizador.

### API de administração {#admin-api}

Os administradores podem gerir a MFA de qualquer utilizador através da [API de administração](admin-api):

- `GET /api/v1/profile/{userId}/mfa`: ver o estado de MFA de um utilizador
- `DELETE /api/v1/profile/{userId}/mfa`: repor todo a MFA (para utilizadores bloqueados)
- `DELETE /api/v1/profile/{userId}/mfa/{id}`: remover uma credencial específica

### Hooks de auditoria {#audit-hooks}

Implemente `IAuthHook.OnMfaVerifiedAsync` para registar eventos de MFA:

```csharp
public Task OnMfaVerifiedAsync(
    string userId, string email, string mfaMethod, CancellationToken ct)
{
    logger.LogInformation("MFA verified for {Email} via {Method}", email, mfaMethod);
    return Task.CompletedTask;
}
```

Todo o ciclo de vida da MFA pode ser intercetado por hooks: `OnMfaVerifyFailedAsync` (uma tentativa de verificação falhada), `OnMfaEnrolledAsync` (um método confirmado), `OnMfaCredentialRemovedAsync` (uma credencial removida, com um indicador de se isso desativou a MFA) e `OnRecoveryCodesRegeneratedAsync`.

## Interface de início de sessão personalizada {#custom-login-ui}

Se estiver a construir uma interface de início de sessão personalizada, trate estas respostas de `POST /api/auth/login`:

1. **Início de sessão normal**: `{ userId, email, name }` com o cookie definido. Redirecione para `returnUrl`.
2. **MFA obrigatória**: `{ mfaRequired: true, challengeId, methods, webAuthn? }`. Mostre o formulário de desafio MFA.
3. **Configuração de MFA obrigatória**: `{ mfaSetupRequired: true, setupToken }`. Mostre o fluxo de inscrição em MFA.

Ao tratar os erros de `POST /api/auth/mfa/verify`: `invalid_code` e `assertion_failed` podem ser repetidos com o mesmo `challengeId` (até ao limite de tentativas); `too_many_attempts` e `invalid_challenge` são definitivos, pelo que deve enviar o utilizador de volta para o formulário de início de sessão.

Consulte [API de autenticação](auth-api) para a referência completa dos endpoints.
