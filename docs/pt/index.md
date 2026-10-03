---
layout: default
title: Início
locale: pt
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Logótipo da Authagonal">
</p>

# Authagonal

Servidor de autenticação OAuth 2.0 / OpenID Connect / SAML 2.0 para .NET, com armazenamento conectável: o seu próprio PostgreSQL ou SQLite, Azure Table Storage ou AWS (DynamoDB / S3 / Secrets Manager).

Uma única implementação autónoma. O servidor e a interface de início de sessão são distribuídos numa só imagem Docker e a SPA é servida a partir da mesma origem que a API, pelo que a autenticação por cookie, os redirecionamentos e a CSP funcionam sem a complexidade de pedidos entre origens.

> **Prefere um serviço gerido?** A [Authagonal Cloud](https://authagonal.io) executa tudo isto por si, em multi-inquilino, com todas as funcionalidades em todos os planos e sem taxas de SSO por ligação. → [authagonal.io](https://authagonal.io)

## Funcionalidades principais {#key-features}

- **Fornecedor OIDC**: concessões authorization_code + PKCE, client_credentials, refresh_token e device_code, com rotação de utilização única
- **SAML 2.0 SP**: implementação própria com suporte completo para Azure AD (resposta assinada, asserção assinada ou ambas), um par de chaves de SP por ligação para AuthnRequests assinados e desencriptação de `EncryptedAssertion`, e Single Logout (iniciado pelo SP e pelo IdP)
- **Federação OIDC dinâmica**: ligue-se ao Google, à Apple, ao Azure AD ou a qualquer IdP compatível com OIDC
- **Autenticação multifator**: TOTP, WebAuthn/chaves de acesso, códigos de recuperação; política por cliente (`Disabled` / `Enabled` / `Required`) com substituição por utilizador através de `IAuthHook`, aplicada também aos inícios de sessão federados
- **Aprovisionamento SCIM 2.0**: aprovisionamento de entrada de utilizadores/grupos a partir do Entra ID, Okta e OneLogin; listagem paginada por cursor e filtros `eq` suportados por índice cego
- **Ecrã de consentimento OAuth**: consentimento por cliente, com novo pedido sensível aos âmbitos e gestão de concessões
- **Device Authorization Grant**: fluxo RFC 8628 para dispositivos com entrada limitada (smart TVs, CLIs, IoT)
- **Introspeção de tokens**: RFC 7662 para que os servidores de recursos verifiquem a validade de um token
- **Assinatura de tokens**: apenas ES256. Os tokens de acesso incluem o `typ: at+jwt` da RFC 9068 para que um servidor de recursos
  os distinga de id_tokens e de tokens de logout, mas **não se reivindica conformidade com a RFC 9068**: a §2.1
  exige RS256 entre os algoritmos suportados, e este servidor não o emite nem o aceita. Um
  único algoritmo é uma postura deliberada: cada algoritmo adicional aceite é mais uma forma de um
  verificador ser levado a usar o errado.
- **Back-Channel Logout**: notificações OIDC Back-Channel Logout 1.0 para as relying parties
- **Sessões do lado do servidor** *(opcional)*: `AddAuthagonalServerSideSessions` guarda o ticket de SSO no armazenamento, de modo que o cookie de autenticação transporta apenas um identificador opaco, e ativa a listagem self-service em `GET /api/auth/sessions` e a revogação por dispositivo ([API de autenticação](auth-api#sessions-self-service))
- **Backend-for-Frontend**: `Authagonal.Bff` (.NET) e `@authagonal/bff` (Node), um BFF de cliente confidencial para que uma SPA nunca detenha um token ([BFF](bff))
- **RGPD self-service** *(Authagonal Cloud)*: exportação de dados e eliminação agendada da conta a partir da página de conta
  alojada. A aplicação de início de sessão inclui a interface para isso, mas os endpoints que ela chama
  (`GET /api/v1/account/export`, `POST /api/v1/account/erasure`) são servidos pelo anfitrião de autenticação da Cloud e
  **não** fazem parte da superfície desta biblioteca. Uma implementação self-hosted tem de os implementar, ou deixar os dois
  botões fora da sua página de conta: `MapFallbackToFile` responde a uma rota não implementada com 200 e o HTML da própria
  SPA, pelo que uma exportação não implementada tem de ser reconhecida como tal, em vez de descarregada.
- **Aprovisionamento TCC**: aprovisionamento Try-Confirm-Cancel em aplicações a jusante no momento da autorização
- **Interface de início de sessão personalizável**: configurável em tempo de execução através de um ficheiro JSON, logótipo, cores e propriedades personalizadas de CSS, sem necessidade de recompilar; localizada em 11 idiomas
- **Auth Hooks**: extensibilidade `IAuthHook` para registo de auditoria, validação personalizada e webhooks
- **Pontos de extensão para encriptação de PII**: pontos de extensão `IFieldCipher` / `IIndexTokenizer` para encriptação ao nível do campo em repouso, com pesquisa por índice cego com chave (HMAC); códigos de recuperação encriptados através de `ISecretProvider`
- **Cliente HashiCorp Vault Transit**: assinatura/verificação, encriptação/desencriptação e HMAC com chave contra o motor Transit do Vault, para construir um `IFieldCipher` ou um `IIndexTokenizer`. A assinatura remota de JWT não está ligada: a chave de assinatura de tokens é sempre a que está em `ISigningKeyStore`.
- **Biblioteca componível**: `AddAuthagonal()` / `UseAuthagonal()` para alojar no seu próprio projeto com substituições de serviços personalizadas
- **Pronto para Native AOT**: IL trimming e serialização JSON gerada na origem para um arranque rápido
- **Armazenamento conectável**: PostgreSQL ou SQLite self-hosted (sem conta na cloud), ou Azure Table Storage / AWS (DynamoDB / S3 / Secrets Manager) para backends de baixo custo e adequados a serverless
- **Cópia de segurança e restauro**: cópias de segurança incrementais (orientadas pelo registo de alterações, com uma varredura completa de salvaguarda), verificação de integridade e rastreio de eliminações baseado em tombstones
- **APIs de administração**: CRUD de utilizadores, gestão de fornecedores SAML/OIDC, encaminhamento de SSO por domínio, personificação de tokens

## Integrações comuns {#common-integrations}

Guias orientados para tarefas, para os fluxos que as equipas constroem com mais frequência:

- **[Atualizar um utilizador](user-upgrade)**: transforme uma conta de convidado / SSO / convite numa conta com credenciais através da reivindicação de conta sem palavra-passe, e execute a sua promoção de convidado → membro normal na confirmação.
- **[SSO self-service](self-service-sso)**: aprovisionamento JIT para ligações empresariais: integração apenas por convite vs. self-service, como evitar que IdPs externos se tornem armadilhas, e ecrãs intermédios antes da federação.
- **[Sessões federadas](federated-sessions)**: revogue a sessão local quando o IdP a montante o fizer (`RevalidateOnRefresh`).
- **[Backend-for-Frontend (BFF)](bff)**: mantenha os tokens fora do browser: um cliente OIDC confidencial no seu backend, com um cookie de sessão httpOnly e um proxy de API que injeta o token, em .NET ou Node.
- **[Autenticação de WebSocket](websocket-auth)**: autentique WebSockets do browser através do BFF sem expor um token.
- **[Autenticação agêntica](agentic-auth)**: delegue a autoridade de um utilizador em agentes de IA: agentes registados, autoridade granular RFC 9396, tokens de delegação compostos (RFC 8693 `act`), consentimento permanente, aprovações just-in-time, tickets de capacidade.
- **[Organizações](organizations)**: sirva muitos clientes a partir de um único inquilino: registos `Organization` e de associação, o parâmetro de autorização `organization`, `org_id` / `org_slug` / `org_name` nos tokens, funções com âmbito de organização e recusa de não membros.

## Arquitetura {#architecture}

```
Client App                    Authagonal                         IdP (Azure AD, etc.)
    │                             │                                    │
    ├─ GET /connect/authorize ──► │                                    │
    │                             ├─ 302 → /login (SPA)                │
    │                             │   ├─ SSO check                     │
    │                             │   └─ SAML/OIDC redirect ─────────► │
    │                             │                                    │
    │                             │ ◄── SAML Response / OIDC callback ─┤
    │                             │   └─ Create user + cookie          │
    │                             │                                    │
    │                             ├─ TCC provisioning (try/confirm)    │
    │                             ├─ Issue authorization code          │
    │ ◄─ 302 ?code=...&state=... ┤                                    │
    │                             │                                    │
    ├─ POST /connect/token ─────► │                                    │
    │ ◄─ { access_token, ... } ──┤                                    │
```

Comece pelo guia de [Instalação](installation) ou passe diretamente para o [Início rápido](quickstart). Para alojar a Authagonal no seu próprio projeto, consulte [Extensibilidade](extensibility). Para a gestão de dados, consulte [Cópia de segurança e restauro](backup-restore). Para o histórico completo de alterações, consulte o [Changelog](https://github.com/authagonal/authagonal/blob/master/CHANGELOG.md).
