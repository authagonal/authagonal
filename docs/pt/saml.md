---
layout: default
title: SAML
locale: pt
---

# SP SAML 2.0

O Authagonal inclui uma implementação própria de Service Provider SAML 2.0. Não usa nenhuma biblioteca SAML de terceiros: assenta em `System.Security.Cryptography.Xml.SignedXml` (parte do .NET).

## Âmbito {#scope}

- **SSO iniciado pelo SP** (o utilizador começa no Authagonal e é redirecionado para o IdP)
- **Binding HTTP-Redirect** para o AuthnRequest (opcionalmente assinado, consulte abaixo)
- **Binding HTTP-POST** para a Response (ACS)
- **Asserções cifradas** (`EncryptedAssertion`) decifradas com um par de chaves do SP por ligação
- **Single Logout** (iniciado pelo SP e iniciado pelo IdP, bindings Redirect e POST)
- O Azure AD / Entra ID é o alvo principal, mas qualquer IdP compatível funciona (os nomes de atributos do Okta, OneLogin, Ping, Google Workspace, ADFS e Shibboleth são tratados)

### Não suportado {#not-supported}

- Binding Artifact
- Cifragem de asserções com AES-GCM (limitação do `EncryptedXml` do .NET; configure AES-CBC no IdP, consulte abaixo)

**O início de sessão iniciado pelo IdP funciona, e o mosaico não precisa de ser reconfigurado**, mas não é a asserção não solicitada que inicia a sessão do utilizador. Uma Response sem `InResponseTo` é descartada e o ACS redireciona o browser para `/saml/{connectionId}/login`, que emite um novo AuthnRequest vinculado a esse browser. O utilizador já está autenticado no IdP, pelo que este responde de imediato e a ida e volta é invisível; o `RelayState` do IdP é transportado como URL de retorno, pelo que o utilizador continua a chegar à ligação direta com que o mosaico foi configurado.

A asserção tem de ser descartada porque aceitar uma asserção não solicitada permite a qualquer pessoa com uma conta nesse IdP iniciar uma sessão em qualquer user-agent (todas as regras da secção 4.1.4.3 são cumpridas por uma asserção que o atacante obteve legitimamente para a sua própria conta) e porque exigir o cookie do pedido na via iniciada pelo SP não vale nada enquanto a mesma asserção puder ser reproduzida com o `InResponseTo` removido. Reiniciar o fluxo mantém o mosaico a funcionar sem aceitar nada disso: quem acaba com a sessão iniciada é quem o IdP indicar na *nova* troca.

O reinício acontece uma única vez por browser. Um IdP que responda ao AuthnRequest com outra Response não solicitada é recusado com `error=saml_unsolicited` em vez de ser reencaminhado de novo, pelo que um IdP mal configurado não pode provocar um ciclo de redirecionamentos.

Para aceitar a asserção não solicitada tal como chega, defina `allowUnsolicitedResponses: true` na ligação (**desativado por predefinição**). Com esta opção ativa, a verificação do ID do pedido é dispensada para as respostas não solicitadas, mas a utilização única do ID da asserção continua a ser imposta (consulte Segurança).

## Configuração do Azure AD {#azure-ad-setup}

### 1. Criar um fornecedor SAML {#1-create-a-saml-provider}

**Opção A: configuração (recomendada para cenários estáticos)**

Adicione a `appsettings.json`:

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "acme-azure",
      "ConnectionName": "Acme Corp Azure AD",
      "EntityId": "https://auth.example.com/saml/acme-azure",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
      "AllowedDomains": ["acme.com"]
    }
  ]
}
```

Os fornecedores são inicializados no arranque. `ConnectionId`, `EntityId` e `MetadataLocation` são obrigatórios para uma nova ligação (o arranque falha sem eles). Os mapeamentos de domínios SSO são registados automaticamente a partir de `AllowedDomains`, exceto no caso de uma ligação limitada a uma organização, cujos domínios só são correspondidos dentro dessa organização. Um fornecedor acabado de carregar da configuração não recebe um par de chaves do SP (logo, não há AuthnRequests assinados, asserções cifradas nem mensagens de logout assinadas); use a API de administração para essas funcionalidades.

A inicialização por configuração também pode definir `OrganizationId`, `JitProvisioningEnabled` (predefinição `false`), `ChallengeMfaAfterLogin` (predefinição `true`), `ProvisioningAttributeParams`, `AllowUninvitedJit` e `AllowUnsolicitedResponses`. A inicialização lê a ligação guardada e funde-a, pelo que uma ligação existente mantém o seu par de chaves do SP, os metadados colados, o formato de NameID, `signAuthnRequests` e o ícone, para os quais a configuração não tem campo. Os indicadores de comportamento acima são escritos a partir da configuração em cada arranque, pelo que um indicador que omita volta à sua predefinição.

`EntityId` é o **ID de entidade do seu SP** (o identificador que regista no IdP), não o ID de entidade do IdP.

> **Um IdP na sua própria rede privada.** `MetadataLocation` tem de ser https e, por predefinição, tem de resolver para um endereço publicamente encaminhável: o documento de metadados contém os certificados com os quais todas as asserções são validadas, e o Authagonal recusa destinos internos em todos os URLs que obtém. Para federar com um IdP local (on-premises), indique-o em [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard). Se o IdP não publicar nenhum endpoint de metadados https, cole antes o documento em `MetadataXml` através da API de administração.

**Opção B: API de administração (para gestão em tempo de execução)**

```bash
curl -X POST https://auth.example.com/api/v1/saml/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Acme Corp Azure AD",
    "entityId": "https://auth.example.com/saml/acme-azure",
    "metadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
    "allowedDomains": ["acme.com"]
  }'
```

A API gera o `connectionId` (um GUID) e devolve-o no cabeçalho `Location` e no corpo da resposta. Campos opcionais adicionais: `metadataXml` (metadados colados, consulte abaixo), `nameIdFormat` (consulte abaixo), `signAuthnRequests` (forçar AuthnRequests assinados), `iconUrl` (ícone do botão de início de sessão), `jitProvisioningEnabled` (criar automaticamente utilizadores desconhecidos no primeiro início de sessão; **desativado por predefinição**, pelo que um utilizador desconhecido é rejeitado até o definir), `challengeMfaAfterLogin` (predefinição `true`; `false` confia na MFA do próprio IdP), `provisioningAttributeParams` e `allowUninvitedJit` (consulte [SSO self-service](self-service-sso)), `organizationId` (limitar a ligação a uma organização, consulte [SSO self-service](self-service-sso#organisation-scoped-connections)), `allowUnsolicitedResponses` (aceitar uma asserção iniciada pelo IdP tal como chega, em vez de reiniciar o fluxo; desativado por predefinição, consulte acima). As ligações criadas através da API recebem também um par de chaves do SP gerado automaticamente (consulte Par de chaves do SP abaixo).

As ligações são geridas com `POST` / `GET` / `PUT` / `DELETE` em `/api/v1/saml/connections[/{connectionId}]`. `PUT` é uma atualização parcial: só são modificados os campos enviados no pedido.

### 2. Configurar o Azure AD {#2-configure-azure-ad}

1. No Azure AD → Enterprise Applications → New Application → Create your own
2. Configure o Single Sign-On → SAML
3. **Identifier (Entity ID):** `https://auth.example.com/saml/acme-azure`
4. **Reply URL (ACS):** `https://auth.example.com/saml/acme-azure/acs`
5. **Sign on URL:** `https://auth.example.com/saml/acme-azure/login`

### 3. Encaminhamento por domínio SSO {#3-sso-domain-routing}

Quando `AllowedDomains` é especificado (na configuração ou através da API de criação), os mapeamentos de domínios SSO são registados automaticamente. Quando um utilizador introduz `user@acme.com` na página de início de sessão, a SPA deteta que o SSO é obrigatório e mostra "Continuar com SSO". Um domínio só pode ser associado a uma ligação; a API rejeita um domínio já reivindicado por outra ligação.

Também pode gerir os domínios em tempo de execução através da API de administração; consulte a [API de administração](admin-api).

## XML de metadados colado {#pasted-metadata-xml}

Alguns IdPs não publicam nenhum URL de metadados (Google Workspace), ou o seu endpoint de metadados não é alcançável a partir do SP (ADFS numa rede privada). Nesses casos, cole antes o documento de metadados: indique `metadataXml` na criação/atualização. Tem de ser fornecido exatamente um de `metadataLocation` ou `metadataXml`; indicar um deles numa atualização apaga o outro.

Os metadados colados são validados no momento de guardar e **condensados** (`SamlMetadataParser.Condense`) num `EntityDescriptor` canónico mínimo que contém exatamente o que o SP consome: entityID, certificados de assinatura, o endpoint SSO, o endpoint SLO, se existir, e o indicador `WantAuthnRequestsSigned`. Os documentos dos fornecedores podem ultrapassar os 100 KB (o `FederationMetadata.xml` do ADFS), acima do limite de 64 KB por propriedade do Azure Table, enquanto as partes que o SP usa ocupam poucos KB. Os documentos colados que não podem ser interpretados são rejeitados com um 400; o documento tem de conter um `IDPSSODescriptor` com um certificado de assinatura e um `SingleSignOnService`.

## Formato de NameID {#nameid-format}

O campo `nameIdFormat` controla o Format de `NameIDPolicy` pedido no AuthnRequest:

| Valor | Comportamento |
|---|---|
| omitido / null | `urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress` (a predefinição histórica) |
| `"none"` | Omite totalmente o elemento `NameIDPolicy`. É a definição segura para o ADFS: o ADFS faz falhar todo o início de sessão (MSIS7070) quando as suas regras de claims não emitem o formato pedido. |
| qualquer outro valor | Enviado textualmente como URN do Format (tem de começar por `urn:`) |

Numa atualização, `""` repõe a predefinição emailAddress. Os metadados do SP anunciam o formato pedido pela ligação (e omitem `NameIDFormat` quando está definido como `"none"`).

## Endpoints {#endpoints}

| Endpoint | Descrição |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...&loginHint=...` | Inicia o SSO iniciado pelo SP. Constrói um AuthnRequest (assinado quando aplicável) e redireciona para o IdP. `loginHint` é enviado como `login_hint` aos IdPs que o respeitam (Entra, Google). |
| `POST /saml/{connectionId}/acs` | Assertion Consumer Service. Recebe a Response SAML, valida-a e cria o utilizador ou inicia-lhe a sessão. |
| `GET /saml/{connectionId}/metadata` | XML de metadados do SP para configurar o IdP. |
| `GET /saml/{connectionId}/logout?returnUrl=...` | Single Logout iniciado pelo SP. Termina a sessão local e depois envia um LogoutRequest ao IdP, quando este suporta SLO. |
| `GET/POST /saml/{connectionId}/slo` | Endpoint de Single Logout. Recebe LogoutRequests iniciados pelo IdP (binding Redirect ou POST) e a etapa LogoutResponse do SLO iniciado pelo SP. |

O URL de retorno após o início de sessão é transportado do lado do servidor no AuthnRequest guardado (identificado pelo ID do pedido) e não no RelayState: a especificação SAML limita o RelayState a 80 bytes e alguns IdPs truncam-no. O RelayState só é consultado nos fluxos iniciados pelo IdP.

## Par de chaves do SP e asserções cifradas {#sp-keypair--encrypted-assertions}

Todas as ligações criadas através da API recebem um par de chaves do SP gerado automaticamente: um certificado RSA de 2048 bits autoassinado (validade de 10 anos), guardado como PKCS#12 e protegido em repouso pelo fornecedor de segredos do anfitrião. Existe apenas no servidor e nunca é devolvido pela API. O par de chaves permite:

- **AuthnRequests assinados** (assinatura da consulta `SigAlg`/`Signature` no binding Redirect). A assinatura é ativada automaticamente quando os metadados do IdP declaram `WantAuthnRequestsSigned`, ou sempre que a ligação define `signAuthnRequests: true`.
- **Decifragem de asserções cifradas.** Quando os metadados do SP anunciam um certificado de cifragem, o ADFS passa a cifrar as asserções por predefinição; o ACS decifra-as com a chave privada do SP e submete a asserção decifrada à mesma cadeia de validação de assinatura/condições que uma asserção em texto simples. Suportado: transporte de chaves RSA-OAEP (SHA-1/SHA-256); cifragem de dados AES-128/192/256-CBC e 3DES. **O transporte de chaves RSA-1.5 é recusado** (o desembrulhamento PKCS#1 v1.5 é um oráculo Bleichenbacher/ROBOT) e **o AES-GCM não é suportado** (limitação do `EncryptedXml` do .NET). Configure o IdP para RSA-OAEP e AES-CBC. Ambas as falhas devolvem a mesma mensagem constante ("Could not decrypt the assertion."), deliberadamente: indicar o algoritmo ou a etapa que falhou é precisamente o que constrói o oráculo, pelo que deve diagnosticar a partir da configuração do IdP e não a partir do erro.
- **Mensagens de logout assinadas** (LogoutRequest/LogoutResponse no binding Redirect).

Os metadados do SP publicam o certificado como `KeyDescriptor` de `signing` e de `encryption`, e definem `AuthnRequestsSigned="true"` quando a ligação força a assinatura.

## Single Logout {#single-logout}

O ACS regista a sessão SAML no cookie de autenticação (claims `saml_connection`, `saml_name_id`, `saml_name_id_format`, `saml_session_index`), para que o logout possa ser associado à sessão do IdP.

- **Iniciado pelo SP:** `GET /saml/{connectionId}/logout` termina sempre primeiro a sessão do cookie local (o utilizador pediu para terminar sessão; o SLO do IdP é feito na medida do possível). Se a sessão do browser tiver vindo desta ligação e os metadados do IdP anunciarem um `SingleLogoutService`, é enviado um LogoutRequest (NameID + SessionIndex, assinado quando o SP tem uma chave) através do binding Redirect; o LogoutResponse do IdP regressa a `/slo`, que leva o utilizador ao `returnUrl` guardado. Os IdPs sem endpoint SLO (Google) recebem apenas o término da sessão local.
- **Iniciado pelo IdP:** o IdP envia um LogoutRequest para `/saml/{connectionId}/slo` (binding Redirect GET ou POST). Os pedidos assinados são validados face aos certificados dos metadados do IdP. **Um LogoutRequest não assinado ou impossível de verificar é recusado com um 400** antes de qualquer sessão ser consultada. Não existe alternativa de recurso limitada à sessão: uma página de terceiros que encaminhe o browser *da vítima* para aqui fornece a sessão da vítima, não a do atacante, pelo que limitar essa alternativa à sessão atual não restringiria quem poderia ter a sessão terminada. A secção 4.4.3.1 dos Profiles exige de qualquer forma que o IdP assine um LogoutRequest nos bindings Redirect ou POST, e os metadados da ligação já fornecem os certificados, pelo que recusar um pedido não assinado não custa nada a nenhum IdP conforme. É devolvido um LogoutResponse assinado quando o IdP tem um endpoint SLO. Apenas front-channel: a mensagem chega ao browser do utilizador, pelo que terminar a sessão do cookie termina a sessão exatamente nesse browser.

## Cache de metadados e rotação de certificados {#metadata-caching--cert-rollover}

- Os metadados do IdP obtidos de `MetadataLocation` são guardados em cache na memória durante 60 minutos (configurável através de `Cache:SamlMetadataCacheMinutes`), identificados pelo URL dos metadados (não pelo ID da ligação, pelo que não é possível qualquer confusão de cache entre inquilinos).
- Os metadados colados são guardados em cache por conteúdo (hash do XML) e nunca voltam a ser obtidos.
- **Nova obtenção após falha de assinatura:** uma falha na validação da assinatura logo após uma rotação de certificado do IdP significa que os metadados em cache estão desatualizados. Perante essa falha específica, a entrada da cache é removida e os metadados são obtidos de novo uma vez, após o que a validação é repetida, com um intervalo mínimo de 5 minutos por localização de metadados, para que uma asserção inválida não possa ser usada para sobrecarregar o endpoint de metadados do IdP. Sem isto, uma rotação de certificado faria falhar os inícios de sessão até expirar o TTL da cache. (Apenas metadados obtidos por URL; os metadados colados não têm nada a obter de novo.)

## Compatibilidade com o Azure AD {#azure-ad-compatibility}

| Comportamento do Azure AD | Tratamento |
|---|---|
| Assina apenas a asserção (predefinição) | Valida a assinatura no elemento Assertion |
| Assina apenas a resposta | Valida a assinatura no elemento Response |
| Assina ambas | Valida ambas as assinaturas |
| SHA-256 (predefinição) | Suporta SHA-256 e SHA-1 |
| NameID: emailAddress | Extração direta do email |
| NameID: persistent (opaco) | Recorre à claim de email dos atributos |
| NameID: unspecified | Recorre à claim de email dos atributos |
| NameID: transient | Muda a cada início de sessão, pelo que nunca é usado como chave federada. É usado em vez disso o atributo estável de object-id do IdP; se nenhum for declarado, o início de sessão é rejeitado com um erro que indica como resolver (configurar um NameID persistent ou emailAddress, ou declarar um atributo de object-id). |

## Mapeamento de atributos {#attribute-mapping}

Os atributos são indexados sem distinção entre maiúsculas e minúsculas, tanto pelo seu `Name` como pelo seu `FriendlyName` (o Okta e o Shibboleth emitem Names OID com FriendlyNames legíveis; é a correspondência com qualquer um deles que faz funcionar o mapeamento dos fornecedores). Cada campo experimenta uma lista de alternativas por ordem; a primeira é o URI de claim da Microsoft, pelo que o comportamento do Entra/ADFS se mantém, e as restantes abrangem os nomes legíveis e OID que o Okta, OneLogin, Ping, Google e Shibboleth emitem por predefinição:

| Campo | Nomes de atributos aceites |
|---|---|
| email | `.../claims/emailaddress`, `email`, `mail`, `emailaddress`, `urn:oid:0.9.2342.19200300.100.1.3` |
| firstName | `.../claims/givenname`, `givenName`, `given_name`, `firstName`, `first_name`, `urn:oid:2.5.4.42` |
| lastName | `.../claims/surname`, `sn`, `surname`, `lastName`, `last_name`, `familyName`, `family_name`, `urn:oid:2.5.4.4` |
| displayName | `http://schemas.microsoft.com/identity/claims/displayname`, `displayName`, `urn:oid:2.16.840.1.113730.3.1.241`, `cn`, `urn:oid:2.5.4.3` |
| objectId | `http://schemas.microsoft.com/identity/claims/objectidentifier`, `objectGUID`, `user.objectid` |
| groups | `.../claims/groups`, `groups`, `memberOf`, `.../claims/role`, `urn:oid:1.3.6.1.4.1.5923.1.5.1.1` |

(`.../claims/...` abrevia o URI completo `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/...` ou `http://schemas.microsoft.com/ws/2008/06/identity/claims/...`.)

Prioridade de resolução do email: atributo de email explícito (qualquer alternativa) → NameID quando o seu formato é emailAddress → a claim `name`, se contiver `@` → rejeição (é obrigatório um email).

**Os grupos têm vários valores:** é captado cada elemento `AttributeValue` (um por associação a grupo), e não apenas o primeiro.

## Aprovisionamento JIT {#jit-provisioning}

O aprovisionamento JIT está **desativado por predefinição**. Uma ligação com `jitProvisioningEnabled: true` cria automaticamente os utilizadores desconhecidos no primeiro início de sessão (email e nome próprio/apelido a partir da asserção, com o email marcado como confirmado) e associa-os à ligação pela sua identidade federada estável (`saml:{connectionId}` + NameID, ou o object-id no caso de NameIDs transient). Sem isso, um utilizador desconhecido é rejeitado. Uma ligação que declare `provisioningAttributeParams` exige adicionalmente esse contexto de convite no início de sessão, a menos que `allowUninvitedJit` esteja definido; consulte [SSO self-service](self-service-sso). Os utilizadores que regressam são identificados primeiro pela associação federada e nunca apenas pelo email; uma conta local existente só é associada pelo email quando os `AllowedDomains` da ligação abrangem o domínio desse email (a declaração explícita do administrador de que este IdP é dono do domínio), o que impede a apropriação de contas através de um IdP malicioso.

## Tempo de vida da sessão {#session-lifetime}

Se o `AuthnStatement` da asserção contiver um `SessionNotOnOrAfter`, esse é o limite superior que o próprio IdP impõe à sessão que acabou de estabelecer, e o Authagonal respeita-o. O cookie de início de sessão expira no máximo nesse instante (quando este se situa nos próximos 30 dias), e o mesmo limite acompanha a sessão como `session_max_exp`, que limita todos os access, ID e tokens de atualização emitidos a partir dela. Uma asserção sem `SessionNotOnOrAfter` não impõe nenhum limite adicional. O SAML não tem token de atualização a montante, pelo que esta é a única forma de um IdP limitar uma sessão depois do início de sessão; para as ligações OIDC, consulte [Sessões federadas](federated-sessions).

## Segurança {#security}

- **Prevenção de reprodução:** nos fluxos iniciados pelo SP, `InResponseTo` é validado face a um ID de pedido guardado (de utilização única). Independentemente disso, o ID de cada asserção aceite é guardado e imposto como de utilização única, o que abrange também as respostas iniciadas pelo IdP e as respostas cujo `InResponseTo` foi removido (o ID da asserção está dentro da asserção assinada, pelo que não pode ser alterado sem quebrar a assinatura).
- **Desvio de relógio:** tolerância de 5 minutos em NotBefore/NotOnOrAfter
- **Idade máxima da asserção:** uma asserção apresentada mais de uma hora (mais o desvio) depois do seu próprio `IssueInstant` é recusada, seja qual for o seu `NotOnOrAfter`, e um `IssueInstant` no futuro é recusado
- **Emissor, destino e audiência:** o `Issuer` da Response e da Assertion tem de ser igual ao ID de entidade do IdP da ligação, uma Response assinada tem de conter um `Destination` que corresponda a este URL do ACS, e a audiência tem de ser o ID de entidade do SP desta ligação
- **Validade do certificado do IdP:** um certificado de assinatura do IdP fixado que esteja fora da sua própria janela `NotBefore`/`NotAfter` (desvio de 5 minutos) é ignorado, tanto para as asserções como para as assinaturas de logout do binding Redirect, pelo que deve atualizar os metadados depois de uma rotação
- **Prevenção de ataques de wrapping:** o URI de Reference da assinatura tem de corresponder ao ID do elemento assinado
- **Proteção contra redirecionamento aberto:** o URL de retorno após o início de sessão tem de ser um caminho relativo à raiz (a começar por `/`, sem `//`, sem barras invertidas, uma vez que os browsers tratam `\` como `/`)
- **Garantia de domínio:** quando `AllowedDomains` está configurado, as asserções para emails fora desses domínios são rejeitadas, pelo que uma ligação não pode declarar o domínio de outra nem o email de um utilizador local
- **MFA:** a federação comprova apenas o primeiro fator. Se a política efetiva do utilizador exigir MFA, o início de sessão passa pelo desafio/configuração de MFA local em vez de emitir uma sessão totalmente autenticada, a menos que a ligação defina `challengeMfaAfterLogin: false`.
