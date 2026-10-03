---
layout: default
title: Organizações
nav_order: 14
locale: pt
---

# Organizações

Uma organização é um cliente dentro do seu inquilino. Uma implementação pode servir muitas: cada uma tem a sua própria identidade, os seus próprios membros e o seu próprio `org_id` nos tokens que as suas aplicações recebem.

## Descrição geral {#overview}

Antes das organizações, um registo de utilizador continha uma cadeia `OrganizationId` (escrita pelo aprovisionamento TCC ou por uma vinculação de token SCIM), e essa cadeia era emitida como a claim `org_id`. Não havia onde indicar o que a organização *era*, quem lhe pertencia, ou se era sequer possível autenticar em nome dela.

Uma `Organization` dá-lhe um registo: um id opaco imutável, um slug imutável e único no inquilino, um nome de apresentação, um indicador de ativação, um conjunto de metadados e uma substituição da personalização visual. Uma `OrganizationMembership` regista quem pertence à organização, e é o que efetivamente autoriza a emissão de um token para essa organização.

**Para que serve.** Um ISV cujo produto é implementado por cliente (uma instância da aplicação, uma base de dados, resolvida pelo nome do anfitrião) regista um inquilino e uma organização por cliente. A sua aplicação lê o `org_id` do token de acesso e recusa tudo o que não seja a instância que está a servir. A decisão de encaminhamento que a aplicação costumava tomar por si passa a ser tomada pelo servidor de autorização e comprovada por uma claim assinada.

**O inquilino continua a ser a fronteira de isolamento.** Uma chave de assinatura, um emissor, um armazenamento de utilizadores. Uma organização particiona a identidade *dentro* dessa fronteira; não cria uma segunda. Duas organizações no mesmo inquilino partilham um diretório de utilizadores, e um utilizador pode pertencer a várias.

**Ainda não suportado:**

- **Sem seletor de organização.** Um utilizador que pertença a várias organizações, num pedido que não indique nenhuma, recebe `account_selection_required`, um erro a que a relying party pode reagir repetindo o pedido com um parâmetro. Não existe nenhum ecrã alojado que lhe peça para escolher.
- **Sem administração delegada de organizações.** Não existe nenhuma permissão que permita ao próprio administrador de um cliente gerir os seus membros.
- **Sem isolamento SCIM com âmbito de organização.** Um token SCIM vinculado a uma organização (`ScimToken.OrganizationId`) etiqueta os utilizadores que cria e, quando o id identifica uma organização real, torna-os membros ativos (consulte [Associação a partir de um token SCIM](#membership-from-a-scim-token)). As verificações de propriedade continuam a basear-se no cliente OAuth, não na organização, pelo que a vinculação decide a etiquetagem, não o acesso.
- **Sem fluxo de convites nesta biblioteca.** Não existe nenhum endpoint de convite nem nenhum email de convite. Um anfitrião escreve ele próprio uma associação `invited`; as formas de esta passar a `active` estão em [Convites](#invitations).
- **Sem grupos com âmbito de organização.** A claim `groups` e a associação a grupos SCIM permanecem ao nível de todo o inquilino; só as funções têm âmbito de organização.
- **Sem eventos de webhook de organização e sem auditoria com âmbito de organização.** `IAuthHook` não tem eventos do ciclo de vida das organizações (criação, associação concedida ou revogada), os payloads dos hooks existentes não incluem `organizationId`, e não existe nenhuma coluna nem índice de organização no registo de auditoria.
- **Os âmbitos condicionados por funções são filtrados contra as funções do inquilino na autorização.** `Scope.AllowedRoles` é aplicado em `/connect/authorize` contra as funções atribuídas diretamente à conta, antes de a organização ser resolvida, pelo que um âmbito cujo `AllowedRoles` só é satisfeito por uma função com âmbito de organização é descartado na autorização, ou recusado com `access_denied` se nenhum âmbito pedido sobreviver. Na atualização, a mesma verificação é feita contra as funções do titular resolvido, que incluem as da organização. Até as duas coincidirem, condicione os âmbitos a funções do inquilino.
- **Sem personalização visual de organização nesta biblioteca.** `Organization.BrandingJson` é guardado para que o anfitrião o funda sobre a personalização visual do inquilino; nada nesta biblioteca o lê. A aplicação de início de sessão mostra o nome da organização ("A entrar em {name}") quando o payload de arranque do anfitrião inclui uma `organization` (`{ id, slug, name }`); a própria biblioteca não resolve nenhuma organização antes da autenticação, exceto através do parâmetro `organization`, de uma restrição de cliente com uma única entrada, de uma ligação com âmbito de organização ou do `ITenantContext.OrganizationId` de um anfitrião.
- **Sem API REST de administração de organizações.** `IOrganizationStore` e `IOrganizationMembershipStore` são a superfície; um anfitrião que queira endpoints constrói-os. Um anfitrião que liste organizações ou membros deve utilizar `ListPageAsync` / `ListByOrganizationPageAsync` (ver abaixo).

## Criar uma organização {#creating-an-organization}

As organizações são guardadas através de `IOrganizationStore`. É necessária uma implementação durável antes de se poder criar qualquer uma. A predefinição integrada é vazia e só de leitura, e recusa escritas com uma mensagem que identifica o registo em falta. Isso é deliberado: os registos de organização decidem a emissão de tokens, e um dicionário local ao processo continuaria a emitir tokens em todos os nós que não tivessem visto uma revogação.

```csharp
await organizationStore.UpsertAsync(new Organization
{
    Id = "org_7f3a",              // opaque, immutable, emitted as org_id
    Slug = "international-sos",   // tenant-unique, immutable, emitted as org_slug
    DisplayName = "International SOS",
    CreatedAt = DateTimeOffset.UtcNow,
});
```

`Slug` tem de corresponder a `^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$`: de 1 a 64 caracteres de letras minúsculas, algarismos e hífenes interiores, sem hífen inicial ou final. Minúsculas porque o parâmetro `organization` é convertido em minúsculas antes da pesquisa pelo slug, pelo que um slug com maiúsculas seria um valor que nenhum pedido conseguiria alguma vez resolver.

`Slug` tem de ser único no inquilino, e **os ids e os slugs partilham um único espaço de nomes**: um armazenamento rejeita uma inserção/atualização cujo slug já pertença a outra organização e, da mesma forma, uma cujo slug seja igual ao id de outra organização, ou cujo id seja igual ao slug de outra. Dois registos a responder ao mesmo valor fariam com que o parâmetro `organization` significasse uma organização enquanto todos os ids guardados significariam outra.

`Id` tem de corresponder a `^[A-Za-z0-9._~-]{1,200}$`: a mesma forma que o parâmetro `organization`, para que todos os ids possam sempre ser enviados como tal. Um id fora dessa forma é um id que nenhum pedido consegue selecionar, e um cliente restrito a ele recusaria todos os pedidos.

`Id` **deve** também conter pelo menos um carácter que um slug não pode conter: uma letra maiúscula, `.`, `_` ou `~`. Os ids e os slugs partilham um único espaço de nomes de pesquisa, e um valor todo em minúsculas é resolvido primeiro como slug, pelo que um id que tenha ele próprio a forma de um slug poderia mais tarde ser recusado na criação porque alguém ficou com esse slug, ao passo que um id que contenha um carácter não permitido em slugs nunca o pode ser. A forma recomendada para novos ids é um valor opaco com o prefixo `org_` (`org_7f3a9c`): `_` não é válido num slug, pelo que o prefixo, por si só, o garante. Não é imposta nenhuma convenção, e os valores que já estão no campo são arbitrários: provêm da resposta `/try` do TCC de uma aplicação a jusante (`TccProvisioningOrchestrator`) ou da vinculação de um token SCIM feita por um operador (`ScimToken.OrganizationId`, gravado nos novos utilizadores por `ScimUserEndpoints`).

Tanto `Id` como `Slug` são, na prática, imutáveis. As relying parties comparam-nos com a instância que estão a servir e vão codificá-los de forma fixa, pelo que alterar qualquer um deles provoca uma interrupção de serviço sem nenhuma mensagem de erro. `DisplayName` pode ser alterado livremente e é o que um ecrã apresenta.

## Conceder a associação {#granting-membership}

```csharp
await membershipStore.UpsertAsync(new OrganizationMembership
{
    OrganizationId = "org_7f3a",
    UserId = user.Id,
    Status = MembershipStatus.Active,
    JoinedAt = DateTimeOffset.UtcNow,
    CreatedAt = DateTimeOffset.UtcNow,
});
```

`Status` é `invited`, `active` ou `suspended`. **Só `active` autoriza a emissão de tokens.** Suspender em vez de eliminar mantém o registo de quem convidou quem.

### Convites {#invitations}

Uma linha `invited` contém `InvitedByUserId`, `InvitedAt` e as funções que foram oferecidas ao convidado. A biblioteca nunca envia o convite; promove a linha a `active` (mantendo as funções, quem convidou e a hora do convite, e preenchendo `JoinedAt`) em dois casos:

- **Início de sessão através de uma ligação SAML ou OIDC com âmbito de organização.** O facto de o próprio IdP da organização garantir a pessoa aceita o convite (`FederatedOrganizationBinding`, 0.30.2). Sem isto, um convidado que só alguma vez inicie sessão através de SSO teria um token recusado pela verificação de associação.
- **Associação automática** (abaixo), quando o utilizador cumpre os requisitos.

Uma linha `suspended` nunca é promovida por nenhuma das vias e nunca é modificada por um início de sessão.

### Domínios verificados e associação automática {#verified-domains-and-automatic-membership}

`Organization.Domains` contém os domínios de email que a organização reivindicou, cada um uma `OrganizationDomain { Domain, VerificationToken, CreatedAt, VerifiedAt }`. `Domain` é guardado em minúsculas, sem espaços nas extremidades e sem ponto final. A biblioteca guarda a reivindicação e lê `VerifiedAt`; comprovar o controlo (normalmente um registo DNS TXT que contenha `VerificationToken`) é tarefa do anfitrião, e o anfitrião preenche `VerifiedAt` quando isso é bem-sucedido.

`Organization.AllowAutoMembership` (desativado por predefinição) permite que um utilizador adira sem convite. Quando a organização é selecionada **explicitamente** (uma concessão de atualização transportada, o parâmetro `organization`, uma ligação com âmbito de organização ou um cliente restrito exatamente a essa organização) e o utilizador não tem nenhuma associação ativa, este torna-se membro se **todas** as condições seguintes se verificarem:

- a organização está ativada e `AllowAutoMembership` está ativo,
- `AuthUser.EmailConfirmed` é true,
- a parte do email depois do último `@`, em minúsculas, é **exatamente** igual a um domínio com `VerifiedAt` definido. Um `acme.com` verificado não admite `user@eu.acme.com`.

Uma linha em falta é criada como `active` sem funções; uma linha `invited` é promovida; uma linha `suspended` nunca é alterada. Aplica-se na autorização e em cada atualização, pelo que, enquanto o indicador estiver ativo, eliminar a linha de um membro que cumpra os requisitos não o mantém de fora (volta a aderir no seu próximo token); suspenda-o em vez disso. Uma organização herdada apenas de `AuthUser.OrganizationId` nunca é objeto de adesão automática. Cada adesão automática é registada ao nível Information.

### Associação a partir de um token SCIM {#membership-from-a-scim-token}

Um token SCIM emitido com `organizationId` grava esse valor como `org_id` em todos os utilizadores que cria. Quando o id identifica uma organização existente, a criação também escreve uma associação `active` sem funções e audita `scim.organization_member_added`. Um id que não identifique nenhuma organização fica apenas como etiqueta. Apenas na criação: uma sincronização posterior não volta a etiquetar nem adiciona membros. Consulte [SCIM](scim#tagging-a-connectors-users-with-an-organization).

### Listar e eliminar {#listing-and-deleting}

`IOrganizationStore.ListPageAsync(cursor, limit)` e `IOrganizationMembershipStore.ListByOrganizationPageAsync(organizationId, cursor, limit)` devolvem uma página (`Items` e um `NextCursor` opaco, null na última página). `limit` é ajustado ao intervalo 1..200 e um cursor malformado lança `ArgumentException`. O cursor é baseado em conjunto de chaves, pelo que uma linha adicionada ou removida entre leituras nunca desloca nem repete uma página. Ambos têm implementações predefinidas sobre as listagens não paginadas, pelo que um armazenamento personalizado continua a compilar; os armazenamentos Azure Table substituem-nas por uma consulta de intervalo do lado do servidor.

Eliminar um utilizador através do `DELETE /api/v1/profile/{userId}` de administração, do `DELETE /scim/v2/Users/{id}` do SCIM ou do caminho de recuperação do SCIM também elimina todas as associações que ele detém (`AccountArtefactPurge.PurgeAsync` com um `IOrganizationMembershipStore`; a sobrecarga com três armazenamentos não elimina nenhuma). Um anfitrião com o seu próprio caminho de eliminação tem de passar também o armazenamento de associações, ou as organizações continuam a listar o membro eliminado.

## Funções com âmbito de organização {#organization-scoped-roles}

`OrganizationMembership.Roles` contém as funções que um utilizador tem **dentro** dessa organização. Os nomes provêm do catálogo de funções já existente do inquilino: um ISV declara "Auditor" uma vez, e cada cliente concede-a às suas próprias pessoas.

```csharp
membership.Roles = ["Auditor", "Site Manager"];
```

São unidas na claim `roles` juntamente com as funções atribuídas diretamente ao utilizador e as concedidas pela associação a grupos SCIM, sob a mesma verificação do âmbito `roles`. Um servidor de recursos não precisa de saber se uma função foi concedida ao nível de todo o inquilino ou por organização, mas **tem** de ler `org_id` juntamente com `roles`, porque o mesmo nome de função passa a significar "nesta organização".

Quatro regras delimitam isto:

- **Só uma organização selecionada explicitamente contribui com funções**: uma indicada pelo parâmetro `organization` ou por uma restrição de cliente com uma única entrada. Uma organização herdada de `AuthUser.OrganizationId` não contribui com nenhuma, a mesma assimetria que a verificação de associação tem.
- **Só uma associação `active` contribui.** Um membro convidado mas que não aceitou, ou suspenso, não concede nada, tal como não autoriza nada.
- **As funções nunca atravessam organizações.** São lidas da linha de associação identificada pela organização selecionada, pelo que uma função detida numa não consegue chegar a um token emitido para outra.
- **Os prefixos reservados são removidos.** Uma função que comece por `tenant:` ou `platform:` é descartada na união e registada ao nível Warning. Uma linha de associação são dados com âmbito de cliente, pelo que uma associação capaz de conceder `tenant:admin` transformaria "pode gerir a minha própria organização" em "pode administrar o inquilino". As funções atribuídas diretamente e os mapeamentos de grupo SCIM→função não são afetados: esses são escritos por um operador através de uma superfície de administração autenticada, que é a autoridade que uma linha de associação não tem.

As funções são relidas da linha de associação em cada rotação de atualização, pelo que alterá-las chega a uma sessão ativa na sua atualização seguinte.

As funções de todo o inquilino são **unidas com** as da organização, e não substituídas por elas: `tenant:admin` é autoridade do portal e mantém-se quando se seleciona uma organização.

## Selecionar uma organização num pedido de autorização {#selecting-an-organization-on-an-authorization-request}

Envie `organization` com o slug ou o id de uma organização:

```http
GET /connect/authorize
  ?client_id=mobiom-web
  &response_type=code
  &redirect_uri=https://audit.example.com/callback
  &scope=openid%20profile
  &organization=international-sos
  &code_challenge=...&code_challenge_method=S256
```

O valor tem de corresponder a `^[A-Za-z0-9._~-]{1,200}$` (o conjunto unreserved da RFC 3986); qualquer outra coisa é `invalid_request`. A forma como é resolvido depende das maiúsculas/minúsculas:

- **Qualquer carácter maiúsculo → resolvido apenas como id, exatamente.** Os slugs são apenas em minúsculas, pelo que um valor assim não pode ser um. Convertê-lo em minúsculas e consultar mesmo assim o índice de slugs equivaleria a perguntar "o slug de alguma organização é a forma em minúsculas deste id?", e, se fosse, a quem chama indicando um id seria entregue um cliente diferente.
- **Tudo em minúsculas → primeiro slug, depois id.** Pode ser qualquer um dos dois, e um slug é o que uma relying party normalmente envia. Não há ambiguidade porque um armazenamento recusa que um id e um slug partilhem um valor.

`org_slug` e `org_id` são aceites como alternativas: ambos são utilizados noutros fornecedores, e ignorar sem aviso aquele que este servidor não escolheu é pior do que aceitar ambos. Enviar dois que indiquem organizações *diferentes* é recusado com `invalid_request`: o pedido significa duas coisas e, fosse qual fosse a que o servidor escolhesse, à relying party teria sido indicada a outra. Repetir qualquer um dos três é recusado pela mesma razão que `redirect_uri`.

O parâmetro sobrevive à ida e volta pela interface de início de sessão, porque todo o URL de autorização viaja como `returnUrl`. Também funciona através de [Pushed Authorization Requests](par) sem trabalho adicional: o endpoint PAR guarda todos os campos que recebe, e `/connect/authorize` lê o payload enviado em vez da query.

### Precedência {#precedence}

A organização é resolvida por esta ordem:

1. **Numa atualização, a organização para a qual a concessão foi emitida.**
2. **A organização para a qual uma [ligação SSO com âmbito de organização](self-service-sso#organisation-scoped-connections) autenticou esta sessão.** A única origem aqui que foi *comprovada* em vez de afirmada por quem chama: o utilizador iniciou sessão num IdP que pertence a exatamente uma organização. Um pedido que indique uma organização diferente é recusado com `access_denied`, em vez de lhe ser emitida, sem aviso, a outra.
3. **O parâmetro `organization`.**
4. **`OAuthClient.RestrictedToOrganizationIds`, quando contém exatamente uma entrada.** Uma aplicação por cliente indica a sua organização uma vez, no registo, e a sua relying party nunca envia parâmetro nenhum. É este o formato que a maioria dos produtos com uma instância por cliente pretende.
5. **`AuthUser.OrganizationId`**: a organização guardada na própria conta.

As regras 1 a 4 são seleções *explícitas* e têm de satisfazer a associação. A regra 5 não é: o próprio registo da conta é a afirmação de associação, e exigir uma segunda bloquearia todos os utilizadores pré-existentes no momento em que a organização correspondente fosse criada.

Antes de alguém se ter autenticado (descoberta do domínio de origem, a lista de fornecedores da página de início de sessão, `/sso-check`), não existe utilizador nem concessão, pelo que as regras 3, 4 e depois `ITenantContext.OrganizationId` são resolvidas por si. Consulte [Ligações com âmbito de organização](self-service-sso#organisation-scoped-connections).

## Restringir um cliente a uma organização {#restricting-a-client-to-an-organization}

```csharp
client.RestrictedToOrganizationIds = ["org_7f3a"];
```

Cada entrada tem de corresponder à forma de id de organização `^[A-Za-z0-9._~-]{1,200}$`; a API de administração responde `400 invalid_request` a uma entrada vazia ou malformada, porque uma restrição que liste um id que nenhum parâmetro `organization` consegue enviar não corresponde a nada, e uma restrição que não corresponde a nada recusa todos os pedidos. Uma lista `null` é normalizada para vazia.

Vazia (o que todos os clientes existentes têm) significa sem restrições. Um pedido cuja organização não esteja na lista é recusado com `access_denied`. Uma lista com uma entrada também seleciona, conforme a regra 4 acima. Uma lista com várias restringe, mas não seleciona: o pedido continua a ter de indicar uma, ou é recusado com `account_selection_required`.

## As claims {#the-claims}

Tanto no ID token como no token de acesso:

| Claim | Valor | Âmbito |
|---|---|---|
| `org_id` | `Organization.Id` | nenhum; sempre presente quando o titular tem uma organização |
| `org_slug` | `Organization.Slug` | nenhum; sempre presente quando a organização é um registo real |
| `org_name` | `Organization.DisplayName` | `profile` |

**`org_id` e `org_slug` deliberadamente não estão condicionados a um âmbito.** São contexto de autorização, não dados de perfil: indicam por que cliente o token pode agir, que é a primeira coisa que um servidor de recursos com vários clientes verifica, antes de decidir se lhe interessa um nome, e muitas vezes num token que não pediu perfil nenhum. Com uma condição `profile`, um cliente só de API que pedisse apenas `openid` recebia um token sem organização, o que se lê como "não pertence a ninguém": o servidor de recursos ou recusa quem chama legitimamente, ou trata o token como sem âmbito e serve a partir dele os dados de todos os clientes. A segunda falha é silenciosa, e é a que importa.

Libertá-las sem condição não revela nada que o cliente não tivesse já estabelecido: escolheu a organização, ou está restrito a uma. `org_name` mantém a condição `profile` porque é apresentação, e nada deve basear autorizações nela.

Uma conta sem organização não emite nenhuma das três, pelo que um token que antes não tinha claims de organização continua a não ter nenhuma.

As três são reservadas: nenhuma lista `UserClaims` de um âmbito e nenhum atributo de utilizador personalizado as consegue produzir ou substituir. Isso é especialmente importante para `org_slug`, que é a chave estável que uma relying party compara com a instância do cliente que está a servir. Uma que fosse autodeclarada seria a resposta a essa comparação.

Uma conta com um id de organização que não corresponda a nenhum registo emite apenas `org_id`. A ausência de `org_slug` significa "não existe slug", nunca "retido".

**Valide `org_id` na sua aplicação:**

```csharp
var orgId = User.FindFirst("org_id")?.Value;
if (!string.Equals(orgId, ThisInstanceOrganizationId, StringComparison.Ordinal))
    return Results.Forbid();
```

## Userinfo, introspeção e troca de tokens {#userinfo-introspection-and-token-exchange}

**`/connect/userinfo`** responde com `org_id`, `org_slug`, `org_name` e `roles` a partir do **token apresentado**, não do registo do utilizador. `org_id` e `org_slug` são devolvidos sempre que o token os contém, sem condição de âmbito, pela mesma razão pela qual não estão condicionados no próprio token; `org_name` precisa de `profile`. É a única origem que pode estar correta a partir do momento em que um utilizador pode pertencer a várias organizações: a conta tem uma predefinição, enquanto o token indica a organização para a qual a concessão foi efetivamente emitida. Os campos de perfil (`email`, `name`, `phone_number`) continuam a ser lidos em tempo real: são os dados atuais do titular, que é para o que serve o userinfo.

Assim, voltar a etiquetar uma conta não altera o que o userinfo diz sobre um token já emitido, e a um utilizador com sessão iniciada na organização B nunca é indicado o `org_id` A pelo mesmo servidor que colocou B no seu ID token.

**`/connect/introspect`** inclui `org_id` e `org_slug` quando o token os contém. Um servidor de recursos que valide ele próprio o JWT lê-os do token; um que, em vez disso, faça introspeção obtém agora a mesma resposta.

**A troca de tokens RFC 8693** transporta `org_id`, `org_slug` e `org_name` do token do titular para o token trocado, e aplica-lhes o `RestrictedToOrganizationIds` do cliente que **faz a troca**: um cliente registado para servir um cliente final não consegue trocar o token de outro cliente final, nem trocar um token que não contenha organização nenhuma. Como `org_id` não está condicionado, essa verificação também funciona para um token de servidor de recursos emitido sem o âmbito `profile`: com o condicionamento antigo, um token assim parecia não atribuído, e a um cliente restrito era recusado o seu próprio tráfego. Uma recusa é `invalid_target`, em linha com as outras recusas de política de destino nesse caminho. Uma troca é uma projeção de uma sessão existente, e uma projeção que descartasse a organização em nome da qual estava a agir ficaria sem atribuição em vez de mais restrita. O `ITokenExchangeSubjectTransformer` de um anfitrião pode ainda assim vincular deliberadamente a troca a outra organização (é para isso que servem as trocas vinculadas a um contexto), mas tem de o declarar.

## Atualização {#refresh}

A organização para a qual uma concessão foi emitida é transportada através de cada rotação de atualização, e verificada de novo em cada uma. Por isso, três coisas produzem efeito na rotação seguinte, em vez de esperarem que termine o tempo de vida da atualização:

- revogar ou suspender uma associação,
- desativar uma organização (`Enabled = false`),
- restringir o `RestrictedToOrganizationIds` de um cliente.

**Cada uma recusa a atualização; nenhuma revoga a concessão.** O token de atualização apresentado fica por consumir e a família fica intacta, pelo que a cadeia continua a poder ser recusada enquanto a condição se mantiver e retoma no momento em que deixar de se verificar: repor uma associação, ou voltar a ativar uma organização, recupera a sessão sem novo início de sessão. A concessão continua a expirar ao fim do seu próprio tempo de vida absoluto. É o mesmo formato de um utilizador desativado, cujas atualizações são recusadas enquanto `IsActive` for false.

Para terminar efetivamente uma sessão, revogue a concessão: `POST /connect/revocation` com o token de atualização, ou `GrantRevocation` do lado do anfitrião. Desativar uma organização é uma barreira, não uma revogação.

Uma concessão que apenas herdou a organização da conta é, em vez disso, derivada de novo em cada rotação, pelo que voltar a etiquetar uma conta continua a produzir efeito.

**Mudar de organização é um novo pedido de autorização**, não uma atualização. Envie novamente `/connect/authorize` com outro `organization`; a sessão existente é reutilizada, pelo que não há segundo início de sessão, e começa uma nova concessão. Não espere que o endpoint de atualização mude de organização: não tem user agent nem consentimento, e a concessão regista os âmbitos aprovados para a organização para a qual foi emitida.

## Desativar a verificação de associação {#turning-the-membership-gate-off}

```csharp
organization.RequireMembershipForTokens = false;
```

Ativa por predefinição. Desative-a numa implementação que utilize organizações para personalização visual e encaminhamento, e não para controlo de acesso: qualquer pessoa que consiga indicar a organização recebe então um token para ela. Uma organização cuja associação é meramente indicativa não é uma fronteira; faça essa escolha deliberadamente.

## Recusar uma emissão a partir de um hook do anfitrião {#refusing-an-issuance-from-a-host-hook}

`IAuthHook.OnTokenIssuingAsync` é acionado imediatamente antes de as concessões `authorization_code`, `refresh_token` e `device_code` emitirem o que quer que seja, com o titular resolvido:

```csharp
public Task OnTokenIssuingAsync(TokenIssuanceContext context, CancellationToken ct = default)
{
    if (IsOffboarded(context.SubjectId, context.ClientId))
        throw new InvalidOperationException("This account is being offboarded.");
    return Task.CompletedTask;
}
```

Lançar uma exceção recusa a emissão com `access_denied` e a mensagem da exceção como `error_description`; lançar antes uma `ProtocolTokenException` indica o seu próprio erro OAuth. No caminho de atualização, a verificação é executada **antes** da rotação, pelo que uma recusa deixa o token de atualização apresentado por consumir e a família intacta: "agora não" não é "termine esta sessão".

É um membro de interface predefinido, pelo que um `IAuthHook` existente que não o substitua não é afetado. As duas emissões agênticas (`client_credentials` e troca de tokens, cada uma com um perfil de agente) acionam-no exatamente como antes.

## Recusas {#refusals}

| Condição | Erro |
|---|---|
| Dois seletores que indicam organizações diferentes | `invalid_request` |
| Qualquer seletor repetido | `invalid_request` (entregue diretamente, não refletido para `redirect_uri`) |
| A organização indicada não existe | `access_denied` |
| A organização está desativada | `access_denied` |
| O cliente não tem permissão para esta organização | `access_denied` |
| O utilizador não é um membro ativo (seleção explícita) | `access_denied` |
| O cliente serve várias organizações e o pedido não indicou nenhuma | `account_selection_required` |

## Fluxo de dispositivo {#device-flow}

A concessão de dispositivo não tem um pedido de autorização que transporte um parâmetro, pelo que recorre à restrição do cliente e depois à predefinição da conta. Um cliente de dispositivo que tenha de estar fixado a uma organização deve ser registado com um `RestrictedToOrganizationIds` de uma única entrada.

## Atualizar uma implementação existente {#upgrading-an-existing-deployment}

Nada muda até existir uma organização. Sem registos:

- nenhum pedido consegue selecionar uma organização,
- nenhuma verificação de associação entra em ação,
- uma conta com um `OrganizationId` antigo continua a emitir `org_id` a partir do registo do utilizador, exatamente como antes,
- um token de um utilizador sem organização não contém nenhuma das três claims.

Depois de criar organizações, conceda as associações **antes** de apontar um cliente ou uma relying party para uma delas: uma seleção explícita exige uma associação ativa, e um cliente cujos utilizadores tenham registos mas não tenham associações será recusado.
