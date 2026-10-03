---
layout: default
title: Xác thực cho agent (Agentic Auth)
locale: vi
---

# Xác thực cho agent (Agentic Auth)

Authagonal cung cấp sẵn các khối xây dựng để ủy quyền thẩm quyền của người dùng cho các AI agent (hoặc bất kỳ
workload nào không phải con người) một cách an toàn: agent đã đăng ký, các authority grant chi tiết, token ủy quyền
tổng hợp, chấp thuận thường trực của người dùng, phê duyệt tức thời (just-in-time), capability ticket, và một bề mặt
audit nhận biết được chuỗi ủy quyền. Thư viện sở hữu các primitive và bất biến; ứng dụng host lắp ráp chúng thành sản
phẩm (phần triển khai connector, UX phê duyệt, việc gửi thông báo và chính sách nghiệp vụ vẫn nằm ở phía host).

## Bất biến {#the-invariant}

Mọi token được ủy quyền đều tuân theo:

```
effective authority = admin ceiling ∩ user consent ∩ task request ∩ subject-token authority
```

Không thành phần nào ở phía sau có thể nới rộng nó; mỗi bước ủy quyền tiếp theo lại lấy giao một lần nữa, nên thẩm
quyền chỉ có thể thu hẹp. Phép giao được triển khai một lần duy nhất (`AuthoritySet.Intersect`) và được dùng ở mọi nơi.

## Các thực thể {#entities}

| Thực thể | Kiểu | Ghi chú |
|---|---|---|
| Agent | `AgentProfile` trên một `OAuthClient` confidential | Việc đăng ký profile chính là điều biến một client thành agent; xóa profile thì client trở lại là OAuth thông thường. |
| Thẩm quyền | `AuthoritySet` / `AuthorityGrant` | Theo dạng `authorization_details` của RFC 9396: `type` của connector, `actions`, `locations`, các ràng buộc, chính sách `auto`/`ask`/`deny` cho từng action. |
| Mức trần | `AgentProfile.Ceiling` | Thẩm quyền rộng nhất mà bất kỳ lần ủy quyền nào qua agent có thể mang. Do quản trị viên quản lý (`/api/v1/agents`). |
| Chấp thuận (mức sàn) | `PersistedGrant` loại `agent_consent` | Theo từng cặp (người dùng, agent), quản lý tại `/consent/agents`. Được lưu ở dạng đã giao sẵn với mức trần và được giao lại ở mỗi lần mint. |
| Ủy quyền | Token exchange theo RFC 8693 | Danh tính tổng hợp: `sub` = người dùng, `act` = agent (lồng nhau theo từng bước), `authorization_details` = phần giao hiệu lực. Thời hạn ngắn, không bao giờ refresh được. |
| Phê duyệt | `PersistedGrant` loại `approval` | Cổng JIT cho các action có chính sách `ask`; ngữ nghĩa polling như device flow; dùng một lần, gắn với hình dạng của request. |
| Capability ticket | `ICapabilityTicketService` | Handle mờ (opaque), dùng một lần, gắn với một token: ws-ticket của BFF được tổng quát hóa, nguyên tử trên grant store. |
| Audit | `IAuthHook` | `OnDelegationMintedAsync`, `OnApprovalRequested/ResolvedAsync`, `OnAgentConsentChangedAsync`, `OnCapabilityTicketRedeemedAsync`, cùng cổng `OnTokenIssuingAsync` chạy trước khi mint. |

## Đăng ký một agent {#registering-an-agent}

1. Tạo một client confidential cho phép `urn:ietf:params:oauth:grant-type:token-exchange`
   (chế độ ủy quyền) và/hoặc `client_credentials` (chế độ dịch vụ).
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

`mode` là `delegated`, `service` hoặc `both` (khi cập nhật mà bỏ qua `mode` thì giá trị hiện có được giữ nguyên). `maxDelegationDepth` phải từ 0 đến 8 (mặc định 0), `maxTokenLifetimeSeconds` từ 30 đến 86400 (mặc định 300), và `highRiskDefault` là `auto`, `ask` hoặc `deny`; mọi giá trị khác đều trả về 400.

Các thành phần ràng buộc được định kiểu theo hình dạng JSON: string/mảng string → allowlist (phép kết hợp là giao tập
hợp; các mục hỗ trợ khớp chính xác, wildcard `*.host` và khớp `@suffix`), số → giới hạn trên (phép kết hợp lấy giá trị
nhỏ nhất), bool → cổng (phép kết hợp là AND). Các thành phần không diễn giải được sẽ được giữ nguyên văn và bị từ chối
(fail closed) khi đánh giá. `GET /api/v1/agents/{clientId}/effective-grant?subjectId=…` cho xem trước
mức trần ∩ chấp thuận dành cho giao diện quản trị.

## Chấp thuận của người dùng (mức sàn) {#user-consent-the-floor}

- `GET /consent/agents/{clientId}/info`: mức trần được hiển thị đối chiếu với danh mục connector
  (đăng ký một `IConnectorCatalog` để có tên hiển thị, mô tả action, cờ rủi ro cao;
  các type của nó được quảng bá trong discovery dưới dạng `authorization_details_types_supported`).
- `POST /consent/agents` `{ "clientId": …, "authority": […] }`: cấp mức sàn (bỏ qua
  `authority` để chấp thuận toàn bộ mức trần). Người dùng có thể siết chặt một chính sách (`auto` → `ask`)
  nhưng không bao giờ nới lỏng hay mở rộng được, vì store luôn giao trước với mức trần hiện hành.
- `GET /consent/agents` / `DELETE /consent/agents/{clientId}`: liệt kê và thu hồi. Việc thu hồi
  chặn lần mint kế tiếp; các ủy quyền đang tồn tại không có refresh và sẽ hết hạn trong thời hạn (ngắn) của chúng.
  Không có chấp thuận → exchange thất bại với `invalid_grant` /
  `consent_required`; riêng mức trần không cấp gì cả.

## Mint một ủy quyền {#minting-a-delegation}

Agent xác thực với tư cách chính nó và exchange token của người dùng:

```
POST /connect/token
grant_type=urn:ietf:params:oauth:grant-type:token-exchange
client_id=agent&client_secret=…            (or private_key_jwt, below)
subject_token={user access token}
subject_token_type=urn:ietf:params:oauth:token-type:access_token
authorization_details=[{"type":"email","actions":["read"]}]   (the task slice; omit = everything grantable)
```

Lần mint thực thi lần lượt: chế độ của agent, chấp thuận thường trực, độ sâu ủy quyền lại (mọi actor đã có trong chuỗi
`act` đều cần ngân sách `maxDelegationDepth` cho thêm một bước), phép giao, các từ chối đối với yêu cầu tường minh
(`invalid_target`: agent không được tin rằng mình nắm giữ thẩm quyền mà thực ra nó không có), cổng ask, và các giới
hạn thời hạn (thời hạn của client ∩ thời gian còn lại của subject token ∩ `maxTokenLifetimeSeconds`). Token mang `act`
(RFC 8693; lồng nhau theo từng bước) và `authorization_details` (RFC 9396); response trả lại các details đã được cấp;
introspection phát ra cả hai. Việc exchange tiếp một token đã được ủy quyền sẽ tự động thu hẹp thêm, vì chính claim
của subject token tham gia vào phép giao.

Các client **không có** agent profile giữ nguyên chính xác hành vi exchange hiện tại, ngoại trừ việc tham số request
`authorization_details` giờ đây thu hẹp (không bao giờ mở rộng) token được exchange.

## Phê duyệt (cổng ask) {#approvals-ask-gate}

Khi phần hiệu lực có chứa một action `ask`, exchange sẽ tạm dừng:

```json
{ "error": "authorization_pending", "approval_id": "…", "interval": 5 }
```

Host được thông báo qua `IAuthHook.OnApprovalRequestedAsync` (việc gửi qua email/push/chat nằm ở phía host). Người
dùng xử lý yêu cầu qua `GET /approvals`, `POST /approvals/{id}`
`{ "decision": "approve" | "deny" }`, trong khi agent thử lại đúng request cũ kèm thêm
`approval_id`, dùng từ vựng của device flow xuyên suốt (`slow_down`, `access_denied`,
`expired_token`). Phê duyệt chỉ dùng một lần (tiêu thụ nguyên tử), hết hạn sau
`ApprovalLifetimeSeconds` (mặc định 300), và gắn với đúng hình dạng request *cùng trạng thái chính sách hiện tại*:
nếu quản trị viên sửa mức trần giữa lúc tạm dừng và lúc poll thì phê duyệt bị vô hiệu thay vì mint ra thẩm quyền đã
lỗi thời. Một phê duyệt đã tiêu thụ sẽ mint với các action `ask` được chuyển thành `auto` (đã hỏi và đã được trả lời).

Chế độ dịch vụ (`client_credentials`) không có người dùng tham gia: chỉ mức trần được áp dụng và
`ask` bị hạ thành `deny`.

## Thực thi ở phía resource {#resource-side-enforcement}

- `AuthorityEvaluator.Permits(user, type, action, context, location, strict)` trong bất kỳ resource server nào (các
  khóa context được khớp với tên ràng buộc; hãy truyền những gì bạn suy ra được, ví dụ `recipient_domains` khi gửi
  mail). Token không có claim này được đánh giá là không bị hạn chế (tương thích ngược); claim bị hỏng được đánh giá
  là từ chối tất cả.
  - `location` là giá trị `locations` theo RFC 9396 nơi bạn đang thực hiện hành động. Grant có nêu locations chỉ được
    chấp nhận tại các location đó; một location được cấp là một **gốc**, nên
    `https://api.example.com/orders` bao gồm `/orders/17` nhưng không bao gồm `/orders-admin`.
  - `strict: true` từ chối khi bên gọi không cung cấp context cho một ràng buộc, thay vì bỏ qua ràng buộc đó. Hãy
    dùng nó ở bất cứ đâu bạn liệt kê được mọi khóa mình hỗ trợ:
    `AuthoritySet.UncheckedConstraints(type, context)` cho biết những khóa bạn chưa kiểm tra.
- Điểm chặn BFF: `BffUpstream.RequiredAuthority = ["email:send"]` khiến proxy kiểm tra bearer gửi đi trước khi
  chuyển tiếp, trả 403 khi thất bại, không có đường đi ẩn danh. Location mà proxy đưa ra là upstream mà request thực
  sự sẽ tới (`AuthorityLocation` ghi đè gốc khi thẩm quyền được mint theo một định danh công khai thay vì địa chỉ nội
  bộ); `StrictAuthority` khiến proxy từ chối một ràng buộc mà nó không đánh giá được thay vì để upstream xử lý.

## Capability ticket {#capability-tickets}

`ICapabilityTicketService` (mặc định là `GrantStoreCapabilityTicketService`, được đăng ký bằng `TryAdd`
bởi `AddAuthagonalCore`, nên `AddAuthagonal` cũng có nó) mint các handle mờ, dùng một lần, gắn với một token, được
đổi (redeem) một cách nguyên tử qua thao tác xóa có điều kiện của grant store; bền vững và an toàn trước replay giữa
các pod, khác với kiểu lấy-rồi-xóa của một cache thông thường. ws-ticket của BFF giữ nguyên hợp đồng distributed-cache
hiện có (`WsTicketKey` / `TryRedeemWsTicketAsync`) vì bên redeem của nó thường là một host riêng chỉ dùng chung Redis;
các broker được host chung nên ưu tiên dịch vụ capability ticket.

## private_key_jwt {#private_key_jwt}

Agent là workload; shared secret là mắt xích yếu nhất trong chuỗi. Hãy đặt
`OAuthClient.JwksJson` (JWKS nội tuyến) hoặc `JwksUri` (được tải về, cache khoảng 10 phút) và xác thực bằng một client
assertion theo RFC 7523 (`client_assertion_type=…:jwt-bearer`). Các điều được thực thi:
chữ ký đối chiếu với JWKS đã đăng ký, `iss` = `sub` = `client_id`, audience = issuer hoặc
token endpoint, `exp` có giới hạn (≤ 10 phút), và `jti` dùng một lần (replay cache trên
`IRevokedTokenStore`). Khi đã có assertion thì không bao giờ quay về đường xác thực bằng secret.

## Tính tương thích {#compatibility}

- Không có agent profile → không thay đổi hành vi trên bất kỳ flow nào. Mọi bảng/cột mới đều
  nullable với mặc định và được tự động tạo trên cả hai storage provider (bảng `AgentProfiles`;
  `JwksJson`/`JwksUri` trên client; chấp thuận/phê duyệt/ticket dùng chung bảng grant hiện có).
- Các thành viên mới của `IAuthHook` là default-interface method; các hook hiện có vẫn biên dịch mà không cần thay đổi.
- `ITokenExchangeSubjectTransformer` vẫn chạy ở mọi lần exchange và có thể từ chối hoặc gắn các context claim; nó
  không bao giờ có thể mở rộng ủy quyền (đầu ra của nó được giao lại) hay chạm vào chuỗi `act` (claim dành riêng).
