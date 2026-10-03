---
layout: default
title: SAML
locale: zh-Hans
---

# SAML 2.0 SP

Authagonal 自带一个自研的 SAML 2.0 服务提供方（SP）实现。它不依赖任何第三方 SAML 库，而是基于 `System.Security.Cryptography.Xml.SignedXml`（.NET 的一部分）构建。

## 范围 {#scope}

- **SP 发起的 SSO**（用户从 Authagonal 开始，被重定向到 IdP）
- AuthnRequest 使用 **HTTP-Redirect 绑定**（可选签名，见下文）
- Response（ACS）使用 **HTTP-POST 绑定**
- **加密断言**（`EncryptedAssertion`），使用每个连接独立的 SP 密钥对解密
- **单点注销**（SP 发起和 IdP 发起，支持 Redirect 和 POST 绑定）
- 主要目标是 Azure AD / Entra ID，但任何符合规范的 IdP 都可以使用（可以处理 Okta、OneLogin、Ping、Google Workspace、ADFS、Shibboleth 的属性名）

### 不支持 {#not-supported}

- Artifact 绑定
- AES-GCM 断言加密（受限于 .NET `EncryptedXml`；请在 IdP 上配置 AES-CBC，见下文）

**IdP 发起的登录可以正常工作，而且应用磁贴无需重新配置**，但让用户登录的并不是那个主动推送的断言。没有 `InResponseTo` 的 Response 会被丢弃，ACS 会把浏览器重定向到 `/saml/{connectionId}/login`，由它签发一个绑定到该浏览器的全新 AuthnRequest。用户在 IdP 上已经认证过，因此 IdP 会立即响应，这次往返对用户是不可见的；IdP 的 `RelayState` 会作为返回 URL 一路传递，因此用户仍然会落在磁贴所配置的深层链接上。

之所以必须丢弃这个断言，一是因为接受主动推送的断言会让任何在该 IdP 拥有账户的人都能把一个会话登录到任意用户代理中（攻击者为自己的账户合法获得的断言能满足 §4.1.4.3 的每一条规则）；二是因为只要同一个断言去掉 `InResponseTo` 后仍能重放，在 SP 发起路径上要求请求 Cookie 就毫无意义。重新开始流程既能让磁贴继续可用，又不必接受上述任何风险：最终登录的是 IdP 在*新*一轮交换中指明的那个人。

每个浏览器只会重新开始一次。如果 IdP 对 AuthnRequest 的回应又是一个主动推送的 Response，它会以 `error=saml_unsolicited` 被拒绝，而不会再次跳转，因此配置错误的 IdP 不会造成重定向循环。

如果你希望直接接受主动推送的断言，请在连接上设置 `allowUnsolicitedResponses: true`（**默认关闭**）。开启后，对于主动推送的响应会跳过请求 ID 检查，但断言 ID 的一次性使用仍然会被强制执行（参见“安全性”）。

## Azure AD 设置 {#azure-ad-setup}

### 1. 创建 SAML 提供方 {#1-create-a-saml-provider}

**方案 A：配置（推荐用于静态设置）**

添加到 `appsettings.json`：

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

提供方会在启动时预置。对于新连接，`ConnectionId`、`EntityId` 和 `MetadataLocation` 是必填的（缺少它们会导致启动失败）。SSO 域名映射会根据 `AllowedDomains` 自动注册，但组织范围的连接除外，其域名只在所属组织内部匹配。新预置的提供方不会获得 SP 密钥对（因此没有签名的 AuthnRequest、加密断言或签名的注销消息）；如需这些功能，请使用管理 API。

预置配置还可以设置 `OrganizationId`、`JitProvisioningEnabled`（默认 `false`）、`ChallengeMfaAfterLogin`（默认 `true`）、`ProvisioningAttributeParams`、`AllowUninvitedJit` 和 `AllowUnsolicitedResponses`。预置时会读取已存储的连接并进行合并，因此已有连接会保留其 SP 密钥对、粘贴的元数据、NameID 格式、`signAuthnRequests` 和图标，这些在预置配置中没有对应字段。上述行为标志在每次启动时都会从预置配置写入，因此你省略的标志会恢复为默认值。

`EntityId` 是**你的 SP 实体 ID**（你在 IdP 上注册的标识符），而不是 IdP 的实体 ID。

> **位于你自己私有网络中的 IdP。** `MetadataLocation` 必须是 https，并且默认必须解析到可公开路由的地址：元数据文档携带着用于验证每个断言的证书，而 Authagonal 对它获取的每个 URL 都会拒绝内部目标。如需与本地部署的 IdP 联合，请在 [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard) 中列出它。如果该 IdP 根本没有提供 https 元数据端点，请改为通过管理 API 将文档粘贴到 `MetadataXml` 中。

**方案 B：管理 API（用于运行时管理）**

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

API 会生成 `connectionId`（一个 GUID），并在 `Location` 响应头和响应正文中返回。其他可选字段：`metadataXml`（粘贴的元数据，见下文）、`nameIdFormat`（见下文）、`signAuthnRequests`（强制签名 AuthnRequest）、`iconUrl`（登录按钮图标）、`jitProvisioningEnabled`（首次登录时自动创建未知用户；**默认关闭**，因此在你设置它之前未知用户会被拒绝）、`challengeMfaAfterLogin`（默认 `true`；`false` 表示信任 IdP 自身的 MFA）、`provisioningAttributeParams` 和 `allowUninvitedJit`（参见[自助 SSO](self-service-sso)）、`organizationId`（将连接限定到某一个组织，参见[自助 SSO](self-service-sso#organisation-scoped-connections)）、`allowUnsolicitedResponses`（按原样接受 IdP 发起的断言，而不是重新开始流程；默认关闭，见上文）。通过 API 创建的连接还会获得一个自动生成的 SP 密钥对（参见下文的“SP 密钥对”）。

连接通过对 `/api/v1/saml/connections[/{connectionId}]` 发起 `POST` / `GET` / `PUT` / `DELETE` 进行管理。`PUT` 是部分更新：只有请求中提供的字段会被修改。

### 2. 配置 Azure AD {#2-configure-azure-ad}

1. 在 Azure AD → Enterprise Applications → New Application → Create your own
2. 设置 Single Sign-On → SAML
3. **Identifier (Entity ID)：**`https://auth.example.com/saml/acme-azure`
4. **Reply URL (ACS)：**`https://auth.example.com/saml/acme-azure/acs`
5. **Sign on URL：**`https://auth.example.com/saml/acme-azure/login`

### 3. SSO 域名路由 {#3-sso-domain-routing}

指定了 `AllowedDomains`（在配置中或通过创建 API）时，SSO 域名映射会被自动注册。当用户在登录页面输入 `user@acme.com` 时，SPA 会检测到需要 SSO，并显示“使用 SSO 继续”。一个域名只能映射到一个连接；如果某个域名已被其他连接占用，API 会拒绝该请求。

你也可以在运行时通过管理 API 管理域名；参见[管理 API](admin-api)。

## 粘贴的元数据 XML {#pasted-metadata-xml}

有些 IdP 不提供元数据 URL（Google Workspace），或者它们的元数据端点无法从 SP 访问（私有网络中的 ADFS）。对于这些情况，请改为粘贴元数据文档：在创建/更新时提供 `metadataXml`。`metadataLocation` 和 `metadataXml` 必须且只能提供其中一个；在更新时提供其中一个会清除另一个。

粘贴的元数据会在保存时进行校验，并被**精简**（`SamlMetadataParser.Condense`）为一个规范的最小 `EntityDescriptor`，其中恰好包含 SP 所需的内容：entityID、签名证书、SSO 端点、SLO 端点（如果存在）以及 `WantAuthnRequestsSigned` 标志。厂商提供的文档可能超过 100KB（ADFS 的 `FederationMetadata.xml`），超出了 Azure Table 64KB 的属性上限，而 SP 用到的部分只有几 KB。无法解析的粘贴内容会以 400 被拒绝；文档必须包含带有签名证书和 `SingleSignOnService` 的 `IDPSSODescriptor`。

## NameID 格式 {#nameid-format}

`nameIdFormat` 字段控制 AuthnRequest 中请求的 `NameIDPolicy` Format：

| 值 | 行为 |
|---|---|
| 省略 / null | `urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress`（历来的默认值） |
| `"none"` | 完全省略 `NameIDPolicy` 元素。这是对 ADFS 安全的设置：当 ADFS 的声明规则没有输出所请求的格式时，ADFS 会让整个登录失败（MSIS7070）。 |
| 其他任意值 | 按原样作为 Format URN 发送（必须以 `urn:` 开头） |

更新时，`""` 会重置为 emailAddress 默认值。SP 元数据会公布该连接所请求的格式（设置为 `"none"` 时会省略 `NameIDFormat`）。

## 端点 {#endpoints}

| 端点 | 说明 |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...&loginHint=...` | 发起 SP 发起的 SSO。构建 AuthnRequest（适用时进行签名）并重定向到 IdP。对于支持的 IdP（Entra、Google），`loginHint` 会作为 `login_hint` 传递。 |
| `POST /saml/{connectionId}/acs` | 断言消费服务。接收 SAML Response，对其进行校验，然后创建用户或让用户登录。 |
| `GET /saml/{connectionId}/metadata` | 用于配置 IdP 的 SP 元数据 XML。 |
| `GET /saml/{connectionId}/logout?returnUrl=...` | SP 发起的单点注销。先结束本地会话，若 IdP 支持 SLO，再向其发送 LogoutRequest。 |
| `GET/POST /saml/{connectionId}/slo` | 单点注销端点。接收 IdP 发起的 LogoutRequest（Redirect 或 POST 绑定），以及 SP 发起的 SLO 中的 LogoutResponse 一环。 |

登录后的返回 URL 保存在服务器端已存储的 AuthnRequest 上（以请求 ID 为键），而不是放在 RelayState 中：SAML 规范将 RelayState 限制为 80 字节，而且有些 IdP 会截断它。只有 IdP 发起的流程才会参考 RelayState。

## SP 密钥对与加密断言 {#sp-keypair--encrypted-assertions}

每个通过 API 创建的连接都会获得一个自动生成的 SP 密钥对：一个自签名的 2048 位 RSA 证书（有效期 10 年），以 PKCS#12 格式存储，并由宿主的密钥提供程序进行静态保护。它只存在于服务器端，API 永远不会返回它。该密钥对支持：

- **签名的 AuthnRequest**（Redirect 绑定的 `SigAlg`/`Signature` 查询参数签名）。当 IdP 元数据声明了 `WantAuthnRequestsSigned` 时会自动开启签名；当连接设置了 `signAuthnRequests: true` 时则始终签名。
- **加密断言的解密。** 当 SP 元数据公布了加密证书时，ADFS 默认就会开始加密断言；ACS 使用 SP 私钥解密，并让解密后的断言经过与明文断言相同的签名/条件校验管道。支持：RSA-OAEP（SHA-1/SHA-256）密钥传输；AES-128/192/256-CBC 和 3DES 数据加密。**RSA-1.5 密钥传输会被拒绝**（PKCS#1 v1.5 解包是一个 Bleichenbacher/ROBOT 预言机），**不支持 AES-GCM**（受限于 .NET `EncryptedXml`）。请将 IdP 配置为使用 RSA-OAEP 和 AES-CBC。这两种失败返回的都是同一条固定消息（“Could not decrypt the assertion.”），这是有意为之：说出失败的算法或阶段正是构成预言机的原因，因此请根据 IdP 的配置而不是错误信息进行诊断。
- **签名的注销消息**（Redirect 绑定上的 LogoutRequest/LogoutResponse）。

SP 元数据会把该证书同时发布为 `signing` 和 `encryption` 两个 `KeyDescriptor`，并在连接强制签名时设置 `AuthnRequestsSigned="true"`。

## 单点注销 {#single-logout}

ACS 会在认证 Cookie 上记录 SAML 会话（`saml_connection`、`saml_name_id`、`saml_name_id_format`、`saml_session_index` 声明），以便将注销关联回 IdP 会话。

- **SP 发起：**`GET /saml/{connectionId}/logout` 总是先结束本地 Cookie 会话（用户已要求注销；IdP SLO 是尽力而为的）。如果浏览器的会话来自这个连接，并且 IdP 元数据公布了 `SingleLogoutService`，就会通过 Redirect 绑定发送一个 LogoutRequest（NameID + SessionIndex，SP 有密钥时进行签名）；IdP 的 LogoutResponse 会回到 `/slo`，再把用户带到已存储的 `returnUrl`。没有 SLO 端点的 IdP（Google）只会执行本地注销。
- **IdP 发起：**IdP 向 `/saml/{connectionId}/slo` 发送 LogoutRequest（Redirect GET 或 POST 绑定）。签名的请求会根据 IdP 元数据中的证书进行验证。**未签名或无法验证的 LogoutRequest 会以 400 被拒绝**，这发生在查询任何会话之前。不存在限定于会话的回退机制：把*受害者*的浏览器导航到这里的第三方页面提供的是受害者的会话，而不是攻击者的，因此把回退限定于当前会话也无法限制谁会被注销。Profiles §4.4.3.1 本来就要求 IdP 在 Redirect 或 POST 绑定上对 LogoutRequest 签名，而连接的元数据已经提供了证书，因此拒绝未签名的请求不会给任何符合规范的 IdP 带来损失。当 IdP 有 SLO 端点时，会返回一个签名的 LogoutResponse。仅限前端通道：消息到达的是用户的浏览器，因此结束 Cookie 会话注销的恰好就是那个浏览器。

## 元数据缓存与证书轮换 {#metadata-caching--cert-rollover}

- 从 `MetadataLocation` 获取的 IdP 元数据会在内存中缓存 60 分钟（可通过 `Cache:SamlMetadataCacheMinutes` 配置），以元数据 URL 为键（而不是连接 ID，因此不可能出现跨租户的缓存混淆）。
- 粘贴的元数据按内容寻址（XML 的哈希值）进行缓存，永远不会重新获取。
- **签名失败时重新获取：**IdP 证书轮换后紧接着出现的签名校验失败意味着缓存的元数据已经过时。恰好在这种失败发生时，缓存条目会被逐出，元数据会被重新获取一次，然后重试校验；每个元数据位置有 5 分钟的冷却时间，因此无法利用垃圾断言去冲击 IdP 的元数据端点。如果没有这一机制，证书轮换后的登录会一直失败，直到缓存 TTL 过期。（仅适用于通过 URL 获取的元数据；粘贴的元数据没有可重新获取的来源。）

## Azure AD 兼容性 {#azure-ad-compatibility}

| Azure AD 行为 | 处理方式 |
|---|---|
| 仅对断言签名（默认） | 校验 Assertion 元素上的签名 |
| 仅对响应签名 | 校验 Response 元素上的签名 |
| 两者都签名 | 校验两个签名 |
| SHA-256（默认） | 支持 SHA-256 和 SHA-1 |
| NameID：emailAddress | 直接提取电子邮件 |
| NameID：persistent（不透明） | 回退到属性中的电子邮件声明 |
| NameID：unspecified | 回退到属性中的电子邮件声明 |
| NameID：transient | 每次登录都会变化，因此永远不会用作联合键。改用 IdP 稳定的对象 ID 属性；如果没有断言该属性，登录会被拒绝，并给出可操作的错误提示（配置 persistent 或 emailAddress NameID，或者断言一个对象 ID 属性）。 |

## 属性映射 {#attribute-mapping}

属性会以不区分大小写的方式同时按其 `Name` 和 `FriendlyName` 建立索引（Okta 和 Shibboleth 输出的 Name 是 OID，FriendlyName 才是人类可读的名称；两者任一匹配即可，这正是厂商映射得以生效的原因）。每个字段按顺序尝试一个别名列表；第一个别名是 Microsoft 的声明 URI，因此 Entra/ADFS 的行为保持不变，其余别名涵盖 Okta、OneLogin、Ping、Google 和 Shibboleth 默认输出的友好名称和 OID 名称：

| 字段 | 接受的属性名 |
|---|---|
| email | `.../claims/emailaddress`, `email`, `mail`, `emailaddress`, `urn:oid:0.9.2342.19200300.100.1.3` |
| firstName | `.../claims/givenname`, `givenName`, `given_name`, `firstName`, `first_name`, `urn:oid:2.5.4.42` |
| lastName | `.../claims/surname`, `sn`, `surname`, `lastName`, `last_name`, `familyName`, `family_name`, `urn:oid:2.5.4.4` |
| displayName | `http://schemas.microsoft.com/identity/claims/displayname`, `displayName`, `urn:oid:2.16.840.1.113730.3.1.241`, `cn`, `urn:oid:2.5.4.3` |
| objectId | `http://schemas.microsoft.com/identity/claims/objectidentifier`, `objectGUID`, `user.objectid` |
| groups | `.../claims/groups`, `groups`, `memberOf`, `.../claims/role`, `urn:oid:1.3.6.1.4.1.5923.1.5.1.1` |

（`.../claims/...` 是完整 URI `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/...` 或 `http://schemas.microsoft.com/ws/2008/06/identity/claims/...` 的缩写。）

电子邮件解析优先级：显式的电子邮件属性（任一别名）→ 格式为 emailAddress 时的 NameID → 包含 `@` 的 `name` 声明 → 拒绝（电子邮件是必需的）。

**组是多值的：**会捕获每一个 `AttributeValue` 元素（每个组成员资格一个），而不仅仅是第一个。

## JIT 预配 {#jit-provisioning}

JIT 预配**默认关闭**。设置了 `jitProvisioningEnabled: true` 的连接会在首次登录时自动创建未知用户（电子邮件、名字和姓氏取自断言，电子邮件标记为已确认），并通过其稳定的联合身份（`saml:{connectionId}` + NameID，或者对于 transient NameID 使用对象 ID）将用户关联到该连接。未开启时，未知用户会被拒绝。声明了 `provisioningAttributeParams` 的连接还会要求登录时携带该邀请上下文，除非设置了 `allowUninvitedJit`；参见[自助 SSO](self-service-sso)。回访用户首先通过联合关联进行匹配，绝不只凭电子邮件匹配；只有当连接的 `AllowedDomains` 覆盖该电子邮件的域名时（即管理员明确声明此 IdP 拥有该域名），才会按电子邮件附加到已有的本地账户，从而防止通过恶意 IdP 接管账户。

## 会话生命周期 {#session-lifetime}

如果断言的 `AuthnStatement` 携带了 `SessionNotOnOrAfter`，那就是 IdP 对其刚刚建立的会话所设的上限，Authagonal 会遵循它。登录 Cookie 的过期时间不会晚于该时刻（当该时刻在 30 天以内时），同样的上限还会作为 `session_max_exp` 随会话传递，用于限制由该会话签发的每一个访问令牌、ID 令牌和刷新令牌。没有 `SessionNotOnOrAfter` 的断言不会施加额外的上限。SAML 没有上游刷新令牌，因此这是 IdP 在登录后限制会话的唯一方式；OIDC 连接请参见[联合会话](federated-sessions)。

## 安全性 {#security}

- **防止重放：**对于 SP 发起的流程，`InResponseTo` 会与已存储的请求 ID 进行校验（一次性使用）。此外，每个被接受的断言的 ID 都会被存储并强制一次性使用，这同样覆盖了 IdP 发起的响应以及 `InResponseTo` 被剥离的响应（断言 ID 位于签名的断言内部，因此一旦修改就会破坏签名）。
- **时钟偏差：**NotBefore/NotOnOrAfter 有 5 分钟的容差
- **断言时效上限：**在其自身 `IssueInstant` 之后超过一小时（加上时钟偏差）才出示的断言会被拒绝，无论其 `NotOnOrAfter` 如何；`IssueInstant` 位于未来的断言也会被拒绝
- **签发者、目标与受众：**Response 和 Assertion 的 `Issuer` 必须等于该连接的 IdP 实体 ID，签名的 Response 必须携带与本 ACS URL 一致的 `Destination`，受众必须是本连接的 SP 实体 ID
- **IdP 证书有效期：**超出自身 `NotBefore`/`NotAfter` 时间窗口（5 分钟偏差）的固定 IdP 签名证书会被跳过，断言和 Redirect 绑定的注销签名都是如此，因此证书轮换后请刷新元数据
- **防止包装攻击：**签名的 Reference URI 必须与被签名元素的 ID 一致
- **防止开放重定向：**登录后的返回 URL 必须是根相对路径（以 `/` 开头，不能是 `//`，不能包含反斜杠，因为浏览器会把 `\` 当作 `/`）
- **域名担保：**配置了 `AllowedDomains` 时，针对这些域名之外电子邮件的断言会被拒绝，因此一个连接无法冒充另一个连接的域名或本地用户的电子邮件
- **MFA：**联合认证只能证明第一个因素。如果用户的实效策略要求 MFA，登录会转入本地的 MFA 质询/设置，而不是直接签发完全认证的会话，除非连接设置了 `challengeMfaAfterLogin: false`。
