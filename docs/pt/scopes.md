---
layout: default
title: Âmbitos OAuth
locale: pt
---

# Âmbitos OAuth

O Authagonal suporta tanto âmbitos OAuth/OIDC **integrados** como âmbitos **personalizados** geridos em tempo de execução. Os âmbitos personalizados são persistidos, anunciados através do documento de descoberta e apresentados no ecrã de consentimento juntamente com os integrados.

## Âmbitos integrados {#built-in-scopes}

Estes âmbitos estão sempre disponíveis e não precisam de ser registados:

| Âmbito | Finalidade |
|---|---|
| `openid` | Obrigatório para iniciar um fluxo OIDC. Emite um ID token. |
| `profile` | Claims de perfil padrão (name, family_name, given_name, etc.) |
| `email` | Claims do endereço de email e `email_verified` |
| `phone` | Claims `phone_number` e `phone_number_verified` (OIDC Core 5.4) |
| `roles` | A claim `roles`. Não é um âmbito padrão OIDC: a associação a funções é uma claim cuja divulgação o utilizador final consente |
| `groups` | A claim `groups` (associação a grupos SCIM). Não é um âmbito padrão OIDC; é controlado como `roles` |
| `offline_access` | Emite um token de atualização juntamente com o token de acesso |

Um cliente só pode pedir os âmbitos indicados nos seus próprios `AllowedScopes`. `/connect/authorize` rejeita um âmbito ausente dessa lista com `invalid_scope` em vez de o filtrar, pelo que acrescentar `roles` ao pedido de uma aplicação sem o acrescentar ao cliente faz falhar todos os inícios de sessão.

## Âmbitos personalizados {#custom-scopes}

Os âmbitos personalizados são geridos através da API de administração em `/api/v1/scopes`. Exigem um token de acesso JWT com o âmbito `authagonal-admin` (configurável através de `AdminApi:Scope`).

### Modelo de âmbito {#scope-model}

```csharp
public sealed class Scope
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Emphasize { get; set; }
    public string? Group { get; set; }
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public List<string> AllowedRoles { get; set; } = [];
    public List<string> UserClaims { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

| Campo | Descrição |
|---|---|
| `Name` | O identificador do âmbito enviado nos pedidos de token (por exemplo, `billing.read`) |
| `DisplayName` | Nome legível apresentado no ecrã de consentimento |
| `Description` | Descrição mais longa apresentada no ecrã de consentimento |
| `Emphasize` | Se for `true`, o ecrã de consentimento destaca este âmbito como sensível |
| `Group` | Título do ecrã de consentimento sob o qual este âmbito é apresentado. Apenas apresentação: nunca afeta o que é concedido |
| `Required` | Se for `true`, o utilizador não pode desmarcar este âmbito ao dar consentimento |
| `ShowInDiscoveryDocument` | Se for `true`, o âmbito aparece em `/.well-known/openid-configuration` sob `scopes_supported` |
| `AllowedRoles` | Funções que um utilizador tem de ter para lhe ser concedido este âmbito. Vazio (a predefinição) deixa-o sem controlo; consulte [Âmbitos controlados por funções](#role-gated-scopes) |
| `UserClaims` | Lista de permissões de nomes de claims de atributos personalizados libertadas nos tokens quando este âmbito é concedido. As claims de protocolo reservadas (como `org_id`) nunca são libertadas desta forma, pelo que um atributo guardado não as pode forjar |

### Âmbitos controlados por funções {#role-gated-scopes}

Os `AllowedScopes` de um cliente respondem a *esta aplicação pode pedir este âmbito?*, uma questão decidida
antes de alguém ter iniciado sessão. `AllowedRoles` responde à outra metade: *esta pessoa pode tê-lo?*. Ambos
os controlos se aplicam, e nenhum substitui o outro.

```json
{
  "name": "staff-admin",
  "displayName": "Staff administration",
  "allowedRoles": ["staff", "super-admin"]
}
```

A um utilizador que não tenha nenhuma das funções indicadas, o âmbito é **retirado da concessão**, não recusado: o
cliente pediu o seu conjunto completo e é informado, através do `scope` devolvido na resposta de token (RFC 6749,
secção 3.3), de que obteve menos. É isto que permite a uma aplicação servir tanto o pessoal interno como todos os outros: a
superfície do pessoal é um âmbito entre vários, e só as pessoas com direito a ele o recebem.

Um pedido em que *todos* os âmbitos pedidos são retirados falha com `access_denied`, porque não resta
nada para o qual emitir um token.

O controlo aplica-se sempre que é emitido um token para uma pessoa:

| Fluxo | Onde é executado |
|---|---|
| Authorization code | Em `/connect/authorize`, assim que o utilizador é conhecido e **antes** do consentimento, para que o ecrã nunca ofereça uma permissão que não pode ser concedida |
| Device code | Em `/api/auth/device/approve`, o primeiro ponto desse fluxo em que o titular é conhecido |
| Renovação | Em cada rotação, face às funções resolvidas de novo. É aqui que a revogação de uma função produz efeito, uma vez que a concessão continua a registar o que foi aprovado no início de sessão |
| Troca de tokens | Não é controlado separadamente: uma troca só pode restringir-se aos âmbitos do próprio subject token, pelo que nunca alcança um âmbito que não tenha sido concedido ao titular |

As concessões client-credentials não têm titular e não são deliberadamente afetadas: a
autoridade de um cliente máquina é o seu registo.

Inicializar um âmbito a partir da configuração pode acrescentar ou alterar `AllowedRoles`, mas não o pode apagar (tal como
com `UserClaims`, um campo omitido preserva o valor guardado). Para remover um controlo, faça `PUT` do âmbito com
um array vazio explícito.

## Inicialização a partir da configuração {#seeding-from-configuration}

Os âmbitos podem ser declarados na secção de configuração `Scopes`. São escritos no armazenamento de âmbitos no arranque, juntamente com o [inicialização de clientes](configuration#clients).

```json
{
  "Scopes": [
    {
      "Name": "billing.read",
      "DisplayName": "Billing (read-only)",
      "Description": "View invoices and payment history",
      "UserClaims": ["billing_plan"],
      "ShowInDiscoveryDocument": true,
      "Emphasize": false,
      "Group": "Billing",
      "Required": false,
      "AllowedRoles": ["finance"]
    }
  ]
}
```

| Campo | Descrição |
|---|---|
| `Name` | Obrigatório. Uma entrada sem nome é ignorada com um aviso |
| `DisplayName`, `Description`, `UserClaims`, `ShowInDiscoveryDocument`, `Emphasize`, `Group`, `Required`, `AllowedRoles` | Como no [modelo de âmbito](#scope-model) |

A inicialização é um upsert por `Name`. Um campo que defina prevalece sobre o valor guardado em cada arranque, pelo que uma edição feita através da API de administração a um campo que também é inicializado pela configuração é substituída no arranque seguinte. Um campo que omita mantém o que estiver guardado (ou a predefinição do modelo, para um âmbito novo). Como a omissão significa "manter", a configuração pode acrescentar ou alterar `UserClaims` e `AllowedRoles`, mas não os pode esvaziar: faça-o com `PUT /api/v1/scopes/{name}` e um array vazio explícito.

## Endpoints de administração {#admin-endpoints}

### Listar âmbitos {#list-scopes}

```
GET /api/v1/scopes
```

Devolve `{ "scopes": [ ... ] }`.

### Obter um âmbito {#get-scope}

```
GET /api/v1/scopes/{name}
```

Devolve o âmbito, ou `404` se não for encontrado.

### Criar um âmbito {#create-scope}

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing (read-only)",
  "description": "View invoices and payment history",
  "emphasize": false,
  "required": false,
  "showInDiscoveryDocument": true,
  "userClaims": ["billing_plan"]
}
```

Devolve `201 Created` com o âmbito. Devolve `400` (`invalid_request`) se `name` faltar ou contiver espaços em branco, e `409` (`scope_exists`) se já existir um âmbito com o mesmo nome.

### Atualizar um âmbito {#update-scope}

```
PUT /api/v1/scopes/{name}
Content-Type: application/json

{
  "displayName": "Billing (read)",
  "description": "View invoices",
  "emphasize": true
}
```

Só são atualizados os campos fornecidos; os campos omitidos mantêm os seus valores atuais.

### Eliminar um âmbito {#delete-scope}

```
DELETE /api/v1/scopes/{name}
```

Devolve `204 No Content` (`404` se o âmbito não existir). Os tokens já emitidos que incluem este âmbito continuam válidos até expirarem; revogue-os explicitamente através de `/connect/revocation`, se necessário.

## Documento de descoberta {#discovery-document}

Os âmbitos com `ShowInDiscoveryDocument = true` aparecem sob `scopes_supported` em `/.well-known/openid-configuration`. Os sete âmbitos integrados são sempre anunciados.

```json
{
  "scopes_supported": ["openid", "profile", "email", "phone", "roles", "groups", "offline_access", "billing.read"]
}
```

## Ecrã de consentimento {#consent-screen}

Quando um cliente pede um âmbito que não está na sua lista de dispensa de consentimento, a página de consentimento apresenta cada âmbito pedido pelo seu `DisplayName` (ou, na sua falta, pelo `Name`) com a `Description` por baixo. Os âmbitos com `Emphasize = true` recebem um tratamento visual distinto. Os âmbitos `Required` não podem ser desmarcados.

Consulte [Ecrã de consentimento OAuth](index#key-features) para o fluxo do lado do utilizador.

## Registo dinâmico de clientes {#dynamic-client-registration}

Os clientes registados através do [registo dinâmico de clientes](client-registration) só podem declarar os âmbitos OIDC integrados (`openid`, `profile`, `email`, `phone`, `offline_access`) mais qualquer âmbito indicado em `Auth:DynamicClientRegistrationScopes`. O simples facto de um âmbito existir no armazenamento não dá permissão a um cliente autorregistado para o declarar, e os âmbitos controlados por funções (os que têm `AllowedRoles`) nunca podem ser registados. Tudo o resto é rejeitado com `invalid_scope`.
