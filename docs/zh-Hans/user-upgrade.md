---
layout: default
title: 升级用户
locale: zh-Hans
---

# 升级用户（无密码账户认领）

有些账户在创建之初就没有密码：

- 打开共享链接、由联合登录即时创建的**访客**，
- 通过 SSO 登录或组织邀请 **JIT 预配**的用户，
- 通过 **SCIM** 推送进来的目录用户。

它们都是真正的 Authagonal 用户（拥有稳定的 ID，通常已经预配好下游访问权限），只是没有本地凭据。它们进入系统的途径*曾经*是联合、链接或邀请。

**升级**这样的用户，可以让其设置第一方密码，并且通常会同时提升其与你产品之间的关系（访客 → 标准成员、试用 → 付费、“创建你的组织”）。Authagonal 将其作为一项一等的、需显式启用的流程提供：用户使用相同的电子邮件重新注册，证明自己控制该收件箱，然后其**现有**账户会被*原地*认领（用户 ID 不变，因此其先前的所有访问权限都会保留），同时你的应用运行它所需的任何升级逻辑。

> 这有意与“重置密码”不同。密码重置假定账户已有凭据，并通过电子邮件发送重置链接。认领则是把一个*没有凭据*的账户变成有凭据的账户，并重新运行预配，以便你的下游能够对这次提升作出响应。

## 何时使用 {#when-to-use-it}

当下游产品把“有人用某个联合身份的电子邮件进行注册”视为合法的升级路径时，请启用认领流程，典型场景是共享链接访客决定创建一个正式账户。如果你的部署没有这样的路径，请保持关闭（默认）：此时每个已存在的电子邮件都会被视为重复，注册会返回常规的、不泄露账户是否存在的响应。

## 1. 启用 {#1-enable-it}

认领由 `Auth` 配置节（绑定到 `AuthOptions`）中的一个需显式启用的选项控制：

```json
{
  "Auth": {
    "AllowPasswordlessAccountClaim": true,
    "ClaimAllowedAttributeKeys": ["org_name", "plan"]
  }
}
```

- **`AllowPasswordlessAccountClaim`**（默认 `false`）：开启该流程。
- **`ClaimAllowedAttributeKeys`**（默认为空，即允许所有非保留键）：认领可以带到账户上的自定义属性键的白名单（参见[传递升级上下文](#4-pass-upgrade-context-safely)）。请列出你的预配程序所需的键，以免认领注入任意属性。尽管名字如此，同一个列表也会过滤普通自助注册的 `customAttributes`。

标志关闭时，已存在的电子邮件就是重复。标志开启时，已存在的**没有凭据**的账户（没有 `PasswordHash`）可以被认领；已经有密码的账户**永远不会**被改动：重新注册无法覆盖真正的凭据。

## 2. 认领的完整流程 {#2-the-claim-end-to-end}

用户使用想要认领的账户的电子邮件调用普通的注册端点：

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

响应为 `201`，正文是 `{ "success": true, "userId": "..." }`。在认领路径上，`userId` 是一个一次性的值，而不是真正的账户 ID，因此无法通过响应区分认领和全新注册。

此时还没有任何内容生效。服务器会**暂存**密码以及个人资料/属性，并通过电子邮件发送一个新的验证链接。用户打开它：

```
GET https://auth.example.com/api/auth/confirm-email?token=<from the email>
```

这个 `GET` 只会渲染一个一键确认页面（这样获取该 URL 的邮件扫描器和链接预取程序就不会消耗令牌）。**按下该页面上的按钮**会提交 `POST /api/auth/confirm-email`，正是这一步提升暂存的凭据并运行升级。同一个 `POST` 也接受以查询参数或 JSON 正文（`{ "token": "..." }`）传递的令牌；JSON 调用方会收到 `{ "message": "Email confirmed successfully.", "appLink": ... }`，而页面上的表单提交则会重定向到 `/login?email_confirmed=1`。之后，用户使用新密码正常登录。

### 服务器所做的工作 {#what-the-server-does}

1. **注册**：由于账户存在且没有密码，该请求被视为认领。所选密码会被哈希后写入 `PendingPasswordHash`（处于惰性状态，没有任何认证路径会读取它），名字、姓氏以及白名单中的 `customAttributes` 会暂存到 `PendingClaimJson` 中。与此同时，账户的安全戳会被轮换，这会使收件箱中已有的任何验证链接失效。除此之外，账户**不会**被修改。即使账户的电子邮件已经由其原始流程确认过，也仍然会发送验证邮件：先前的证明属于*另一个*行为方，而认领需要它自己的证明。该链接携带一个 `pc=` 摘要，对应为其暂存的凭据。
2. **确认**：确认就是所有权证明。服务器检查该链接是否绑定到*当前*暂存的凭据，应用暂存的个人资料/属性，运行 **`ReprovisionAsync`**（见下一节），然后将 `PendingPasswordHash` 提升为 `PasswordHash`，并再次轮换安全戳。如果预配拒绝了这次升级，暂存的凭据和个人资料会被丢弃，账户仍然保持无密码且可再次认领，因此不会留下任何不完整的状态。

如果在第一次认领确认之前又提交了第二次认领，第二次会替换暂存的凭据，第一个链接随即失效：确认它会得到 `claim_superseded`（JSON `400`，或者从确认页面重定向到 `/login?error=claim_superseded`）。没有 `pc=` 摘要的链接（例如来自管理员“发送验证邮件”操作的链接）在有凭据暂存时也会以同样的方式失败。在这两种情况下，用户都需要再次注册以获取新链接。

用户 ID 永远不会改变，因此访客的项目访问权限、SCIM 关联、组成员资格等等，都会在升级后保留。

## 3. 在下游执行升级 {#3-do-the-upgrade-downstream}

认领确认会调用 `ReprovisionAsync`，与普通预配不同，它会**即使对于用户已经被预配到的应用**也重新运行 [TCC Try/Confirm/Cancel](provisioning) 周期。这正是关键所在：你的应用已经把这个用户预配为*访客*，因此普通预配会跳过他；重新预配会给你第二次 Try，而这次携带着注册上下文，让你可以提升他。

你的预配 `Try` 处理程序根据是否已有该 `userId` 的记录来区分“首次预配”和“升级”，并对认领所携带的上下文（这里是 `org_name`）作出响应：

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

任何应用返回 `approved: false`（或回调失败）都会使确认以 `400 provisioning_rejected` 失败（对 API 调用方返回 JSON 正文，对确认页面则重定向到 `/login?error=provisioning_rejected&error_description=...`），并让账户保持未升级状态，仍然无密码且仍可认领。被预配应用拒绝的非认领注册返回 `422`；参见 [TCC 预配](provisioning)。批准响应中的 `organizationId`（或额外的 `customAttributes`）会合并到用户上，并随其令牌一起传递。

## 4. 安全地传递升级上下文 {#4-pass-upgrade-context-safely}

注册调用中的 `customAttributes` 是认领向你的预配程序传递注册上下文（组织名称、套餐、推荐来源）的方式。它们会被**暂存**，只在点击验证链接时应用，并由 `ClaimAllowedAttributeKeys` 过滤。请让这个白名单尽量严格：它是一道边界，阻止那些仅仅*知道*某个联合用户电子邮件的人注入会随真正所有者的令牌一起传递的属性。空白名单允许所有非保留键（对受信任的第一方流程很方便）；非空白名单会丢弃所有未列出的键。

无论白名单如何设置，过滤器始终会施加以下限制，并静默丢弃任何违反限制的内容（注册仍然会成功）：

- 最多 32 个属性，键最长 64 个字符，值最长 1024 个字符；
- 以下保留键永远不会被接受，即使列在 `ClaimAllowedAttributeKeys` 中也是如此：`federated_connection`、`org_id`、`roles`、`groups`、`sub`、`iss`、`aud`、`scope`、`client_id`、`sid`、`acr`、`amr`、`email`、`email_verified`。

同样的过滤器也会在普通（非认领）自助注册时运行。

## 安全特性 {#security-properties}

- **仅知道电子邮件是不够的。**只有当账户自己的收件箱收到并确认验证链接时，认领才会完成。知道该地址的攻击者永远收不到这封邮件。
- **同一时间只有一个暂存凭据。**链接绑定到为其暂存的凭据，因此较早的链接无法提升较晚的认领（`claim_superseded`）。
- **真正的凭据永远不会被覆盖。**只有没有 `PasswordHash` 的账户才能被认领；针对有凭据账户的认领会得到常规的、不泄露账户是否存在的重复响应。
- **确认之前一切都不会生效。**在确认之前，暂存的密码无法用于认证，暂存的个人资料/属性也不会被应用。被拒绝的升级会回滚所有内容。
- **属性注入有边界**，由 `ClaimAllowedAttributeKeys` 限定。

## 相关内容 {#related}

- [TCC 预配](provisioning)：你的处理程序所实现的 Try/Confirm/Cancel 约定。
- [自助 SSO](self-service-sso)：最初创建这些无密码账户的 JIT 流程。
