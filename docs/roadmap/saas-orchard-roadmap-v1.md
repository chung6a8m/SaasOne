# SaaS Orchard Core — Roadmap triển khai V1

Ngày: 2026-11-06  
Trạng thái: Đề xuất thiết kế và roadmap để review trước khi triển khai. Chưa tạo hoặc kiểm thử source code.

**Mục tiêu:** Tạo code base modular monolith có thể tái sử dụng cho nhiều sản phẩm, chứng minh bằng Orders và OrderApprovals trên tối thiểu bốn tenant.

**Kiến trúc:** Một ASP.NET Core host; Default shell dành cho identity và quản trị nền tảng; mỗi tenant khách hàng có database riêng và đường dẫn `/{tenant}/...`. Orchard quản lý dữ liệu CMS; EF Core quản lý các bảng ứng dụng. Một bảng chỉ có một công cụ sở hữu migration.

**Tech stack đã chốt:** .NET 10, Orchard Core CMS, YesSql, EF Core 10, SQL Server trước; VS Code, dotnet CLI, PowerShell, Git và Codex có subagent.

**Căn cứ phạm vi:** Các yêu cầu người dùng đã xác nhận trong hội thoại; các quyết định bổ sung bên dưới là đề xuất, không được coi là đã được người dùng phê duyệt riêng.

## 1. Phạm vi đã xác nhận

- Xây mới, không nhập dữ liệu cũ; code base dùng lại cho nhiều sản phẩm.
- Modular monolith; một bản build cho các tenant trong cùng một sản phẩm.
- URL `app.example.com/{tenant}/...`; database riêng cho từng tenant khách hàng.
- Một tài khoản tham gia một hoặc nhiều tenant, có vai trò và quyền được cấp/từ chối.
- Giao diện riêng, không lấy Orchard Admin làm giao diện nghiệp vụ chính.
- MVP có Orders và OrderApprovals.
- Đội vận hành tạo tenant; chọn gói tính năng, hạn sử dụng, tạm khóa và xóa tenant.
- Có lịch sử thao tác; có Web API với JWT và refresh token.
- Kiểm thử tối thiểu bốn tenant và nhiều người dùng đồng thời.
- Chấp nhận cửa sổ bảo trì khi nâng cấp.
- Chưa chốt RTO/RPO; chưa xây theo dõi dung lượng/file/tác vụ nền hoặc yêu cầu lưu trú dữ liệu riêng.
- Có Git nhưng chưa chọn nhà cung cấp CI/CD hoặc môi trường production.

## 2. Các mặc định đề xuất để lập roadmap

| Hạng mục | Mặc định đề xuất cho V1 |
|---|---|
| Frontend | ASP.NET Core MVC + Razor và theme riêng; chưa xây SPA độc lập. |
| Identity | Default shell vận hành Orchard Users và OpenID Server; tenant khách hàng dùng SSO, không có bộ mật khẩu nghiệp vụ riêng. |
| Authorization | Role theo tenant, user override; Deny ưu tiên hơn Allow; không cấu hình thì từ chối. |
| API grant | Một grant access/refresh token gắn với một TenantId; muốn chuyển tenant cần authorization cho tenant mới. |
| Approvals | Một cấp duyệt; không tự duyệt đơn mình tạo; từ chối phải có lý do. |
| Expiration | Đến ExpiresAt UTC thì chặn nghiệp vụ; không xóa dữ liệu, không khóa tài khoản toàn cục. |
| Deletion | Xóa mềm trước; purge vật lý là thao tác vận hành riêng, có xác nhận và audit. |
| Deployment | Ban đầu một instance và bảo trì có kiểm soát; chưa yêu cầu zero-downtime hay Kubernetes. |
| Optional feature | Chỉ bật sau khi schema đã sẵn sàng; tắt feature không đồng nghĩa xóa bảng. |

Các mặc định này là lựa chọn thu hẹp phạm vi, không phải giới hạn lâu dài. Phiên bản patch Orchard/.NET/EF, phiên bản SQL Server, hệ điều hành triển khai và CI được khóa ở M0 sau khi kiểm tra tính tương thích.

## 3. Ranh giới dữ liệu và tài khoản

### 3.1. Database nền tảng

`saas_platform` thuộc Default shell, chứa:

- Tài khoản gốc và dữ liệu OpenID do các module Orchard tương ứng quản lý.
- Bảng EF cho TenantRegistry, gói tính năng, TenantMemberships, tiến trình provision/migration và audit vận hành.
- TenantId ổn định, ShellName, UrlPrefix, trạng thái, ExpiresAt và tham chiếu secret.

Đây không phải database dùng chung cho Orders của khách hàng. TenantRegistry không chứa dữ liệu đơn hàng.

### 3.2. Database khách hàng

`saas_alpha`, `saas_beta`, `saas_gamma`, `saas_delta` chứa dữ liệu CMS riêng cùng bảng nghiệp vụ và quyền riêng của từng tenant.

Tài khoản gốc có thể xuất hiện trong nhiều tenant nhưng chỉ có một danh tính đăng nhập. Khi tích hợp Orchard Users tại tenant cần local user liên kết, ánh xạ bằng issuer + subject vào GlobalUserId ổn định; không liên kết tài khoản chỉ vì trùng email. Không đồng bộ mật khẩu qua các database.

Tư cách thành viên là dữ liệu nền tảng. Role, role permission, user-role và user permission override của khách hàng nằm trong database của tenant. Việc xóa membership làm mất quyền truy cập ngay cả khi local profile hoặc role assignment chưa được dọn.

Truy cập platform data đi qua dịch vụ chuyên trách với kết nối cố định của platform. DbContext nghiệp vụ chỉ dùng kết nối của shell đã được Orchard định tuyến. Không chọn database từ tenantId trong body, header hoặc token.

Các bảng nghiệp vụ không cần cột TenantId chỉ để cô lập database. Bảng dùng chung ở platform, token, log, audit và khóa cache vẫn phải mang TenantId khi cần phân biệt tenant.

### 3.3. Các điểm mở rộng, không phải microservice

- Tenant context: TenantId, ShellName, UrlPrefix, provider và cấu hình kết nối đã xác thực.
- Platform access reader: đọc tenant status, hạn sử dụng, membership và gói tính năng.
- Tenant permission evaluator: quyết định một hành động cụ thể trên tài nguyên cụ thể.
- Tenant transaction coordinator: phối hợp các EF context khi một nghiệp vụ cần atomic commit.
- Audit writer: ghi đúng database, không lộ secret.
- Tenant provisioner/release runner: thao tác vận hành có trạng thái, log và khả năng tiếp tục.

Tên interface và signature cụ thể được khóa trong task plan của milestone sở hữu chúng, trước khi giao agent khác triển khai phần phụ thuộc.

## 4. Authentication, authorization và API

### 4.1. Authentication dùng chung

Ưu tiên Orchard OpenID Server dựa trên OpenIddict tại Default. Tenant web dùng OIDC client; API xác thực token từ issuer chung. [1]

Chứng minh sớm luồng đăng nhập qua hai tenant, ánh xạ subject, callback có prefix và validation liên shell. Membership nhiều tenant và quyền Deny là logic ứng dụng phải xây thêm, không phải kết quả tự có khi bật module OpenID.

Web dùng cookie Secure/HttpOnly và chống CSRF cho thao tác thay đổi dữ liệu. Chuyển tenant dựa trên URL của request; không có biến CurrentTenant dùng chung khiến hai tab ảnh hưởng nhau. Cookie của từng shell và cookie của issuer phải được kiểm thử riêng; không tự chia sẻ cookie toàn bộ shell để thay SSO.

### 4.2. Quyền hiệu lực

```text
Allow = TenantOperational
     AND MembershipActive
     AND FeatureEntitledAndEnabled
     AND (RoleAllow OR UserAllow)
     AND NOT (RoleDeny OR UserDeny)
     AND ResourceRuleSatisfied
```

`TenantOperational` kiểm tra trạng thái và ExpiresAt ở server. Mọi điều kiện bắt buộc đều phải đạt; quyền thuộc role chỉ là một phần quyết định.

- Một người có thể là Manager ở Alpha, Sales ở Beta và không là thành viên Gamma.
- User deny hoặc role deny thắng allow từ role khác.
- Không cấu hình một quyền không đồng nghĩa explicit deny; nhưng thiếu mọi nguồn allow thì kết quả vẫn là từ chối.
- Role nghiệp vụ Manager/Accountant/Sales không được gán Orchard Administrator để tiện triển khai.
- PlatformOperator quản trị vòng đời tenant, không tự động có quyền đọc mọi đơn hàng.
- Endpoint UI và API dùng chung application service và policy, không chỉ ẩn menu.
- Các quyền quản lý membership, role và override phải chống tự nâng quyền; thao tác đặc quyền có audit.

Dùng policy/requirement riêng cho nghiệp vụ. Với ASP.NET Core, explicit deny phải trở thành thất bại bắt buộc, chẳng hạn context.Fail(), thay vì chỉ không gọi Succeed(). [2]

### 4.3. JWT và refresh token

MVP phải thực sự có một API được bảo vệ và kiểm thử refresh, không chỉ để sẵn folder.

- Luồng user: Authorization Code + PKCE; không tự viết endpoint nhận mật khẩu rồi ký JWT.
- Cấu hình access token dạng JWT rõ ràng; không mặc định mọi cấu hình Orchard/OpenID đều trả JWT không mã hóa. [1]
- Kiểm tra signature, issuer, audience, expiry; token phải có subject, TenantId đã được issuer kiểm tra và phạm vi API phù hợp.
- `tenant_id` trong token phải khớp TenantId mà URL/shell đã phân giải. Token không dùng để chọn database.
- Authorization request chọn tenant phải được server kiểm tra membership trước khi phát token. Refresh grant bị ràng buộc cùng tenant; không dùng refresh token Alpha để chuyển sang Beta.
- Refresh rotation và phát hiện reuse; thu hồi grant/session bị ảnh hưởng. Kiểm thử logout, khóa tài khoản, khóa tenant, hết hạn và gỡ membership. [3]
- Không đặt refresh token trong localStorage của giao diện web server-rendered; không ghi token vào log.
- Duy trì signing/encryption keys và Data Protection keys bền vững, có quy trình thay khóa.

Không giả định JWT tự vô hiệu ngay khi sửa database. OpenIddict mặc định không truy vấn trạng thái token mỗi API request. Thiết kế V1 phải kiểm tra tenant/membership/session phía server, và cấu hình kiểm tra token entry hoặc introspection phù hợp với store của issuer. Phần validation liên shell là tiêu chí PoC bắt buộc ở M2. [4]

Khóa Beta chỉ làm mất quyền với Beta. Không thu hồi tài khoản gốc hoặc quyền Alpha của cùng người dùng.

## 5. Vòng đời tenant và gói tính năng

Không dùng trực tiếp Orchard ShellState làm toàn bộ subscription state. Có trạng thái ứng dụng và hạn sử dụng riêng; trạng thái bảo trì không đồng nghĩa disable shell làm cản trở warm-up/migration.

| Thao tác | Hành vi đề xuất |
|---|---|
| Tạo | Ghi registry ở Provisioning; tạo DB/user; cấu hình shell; setup Orchard; EF migrations; seed; kiểm tra; chuyển Active. |
| Lỗi provision | Ghi bước lỗi; retry có kiểm tra bước đã hoàn thành; không tạo trùng database/tenant. |
| Hết hạn | Kiểm tra ExpiresAt UTC ở request; không cần đợi cron để chặn; giữ dữ liệu. |
| Tạm khóa | Chặn UI/API nghiệp vụ và quyền grant của tenant; đường vận hành được bảo vệ riêng để mở lại. |
| Gia hạn/mở lại | Kiểm tra điều kiện, cập nhật trạng thái/hạn và audit; không hồi sinh session đã bị thu hồi trái thiết kế. |
| Đổi gói | So khớp quyền sử dụng với feature thực tế; migrate trước khi bật; tắt không xóa bảng. |
| Xóa mềm | Chuyển PendingDeletion, chặn truy cập, thu hồi grant của tenant, ghi audit tại platform. |
| Purge | Job/CLI riêng với TenantId ổn định, xác nhận và kiểm tra DB đích; dọn DB, cấu hình shell, secret; giữ dấu vết vận hành tối thiểu. |

Purge không xóa tài khoản gốc vẫn thuộc tenant khác. Chính sách lưu backup/audit và thời điểm purge thật phải được xác nhận trước khi dùng cho dữ liệu production; MVP có thể diễn tập trên dữ liệu giả mà chưa đặt RTO/RPO.

Chọn hai gói thử nghiệm: Standard = Orders; Approval = Orders + OrderApprovals. Feature Profile là rào giới hạn feature, không thay thế kiểm tra membership/quyền API. [5]

Không cho hạ gói Approval xuống Standard khi còn đơn PendingApproval; phải xử lý các đơn chờ trước. Không để thao tác tắt feature trở thành cách bỏ qua quy trình duyệt.

## 6. Audit và transaction

Orchard Audit Trail phục vụ sự kiện Orchard được hỗ trợ hoặc được đăng ký mở rộng; không mặc định nó theo dõi mọi thay đổi qua EF. [6]

Audit nghiệp vụ EF ghi action, actor GlobalUserId, TenantId, resource, timestamp UTC, correlation id và thay đổi cần thiết. Ghi audit cùng transaction với nghiệp vụ thành công; log thao tác bị từ chối là luồng riêng để không mất khi rollback.

Audit platform lưu create/suspend/renew/delete/plan-change/membership-change; audit tenant lưu Orders/Approvals/role/permission-change. Không lưu mật khẩu, raw token hoặc connection string. Không tuyên bố log chống sửa tuyệt đối chỉ vì UI không có nút xóa.

Với Orders và Approvals dùng context riêng, approve phải cập nhật trạng thái đơn, quyết định duyệt và audit như một đơn vị atomic. Cùng database không tự bảo đảm cùng transaction. Nếu dùng nhiều EF context, chia sẻ DbConnection và DbTransaction qua coordinator; thử cả rollback và execution strategy/retry. [7]

Mỗi bảng audit chỉ có một migration owner; module khác không được sinh migration sửa lại bảng đó. Không đòi hỏi distributed transaction giữa database platform và database tenant: cấp phát nhiều bước có status/retry, và quyền truy cập mặc định đóng khi chưa hoàn tất.

## 7. Roadmap theo milestone

### M0 — Khóa thiết kế, môi trường và hợp đồng

**Phụ thuộc:** Không.

**Đầu ra:** ADR cho identity/membership, deny policy, database ownership, token/refresh, lifecycle; solution skeleton; khóa SDK/packages/provider; AGENTS.md và conventions; scripts thiết lập/check; CI build/test ban đầu.

**Nghiệm thu:** Clone repository mới, chạy theo README bằng dotnet CLI và pwsh; build/test không cần Visual Studio đầy đủ; không có secret trong Git. Chọn môi trường SQL Server thực cho integration test. Ghi rõ mọi quyết định bổ sung được review trước khi agent triển khai.

### M1 — Host, tenant routing và provisioning

**Phụ thuộc:** M0.

**Đầu ra:** Một host, Default/platform DB và bốn customer DB; registry; TenantId bất biến; shell prefix; recipes và migration baseline; công cụ provision chạy lại được.

**Nghiệm thu:** Bốn prefix vào đúng bốn DB; prefix không tồn tại trả lỗi có kiểm soát, không rơi vào nghiệp vụ Default; trùng slug bị chặn; prefix hệ thống như connect/admin/api được dành riêng. Tài khoản DB tenant không đọc được DB tenant khác. Test cache key và link có prefix.

### M2 — Identity dùng chung và API token thực tế

**Phụ thuộc:** M1.

**Đầu ra:** Central login, SSO giữa tenant, mapping subject, memberships, chọn tenant; JWT access token và refresh token; API thử nghiệm được bảo vệ; validation/revocation liên shell đã chứng minh.

**Nghiệm thu:** Một user vào Alpha/Beta nhưng không vào Gamma; hai tab hai tenant không bị đổi context chéo; token Alpha không gọi API Beta dù user có membership cả hai; refresh không đổi tenant; token sai issuer/audience/expired bị từ chối; refresh reuse và logout có test. Account không được link chỉ vì trùng email.

### M3 — Authorization, lifecycle và audit nền tảng

**Phụ thuộc:** M2.

**Đầu ra:** Roles theo tenant; allow/deny/unspecified; per-user override; resource policy; hạn dùng/khóa/gói; UI vận hành tối thiểu; audit service.

**Nghiệm thu:** Deny thắng mọi Allow; không thể nâng quyền qua API quản lý role; hết hạn, suspended hoặc membership revoked bị chặn dù JWT chưa hết hạn; tenant khác của cùng user vẫn dùng được. Xóa mềm/purge trên dữ liệu giả không xóa global account hoặc DB khác. Hoàn thiện kiểm tra ở cả UI và API.

### M4 — Orders xuyên suốt

**Phụ thuộc:** M3.

**Đầu ra:** Orders module, OrdersDbContext/migrations, application service, API version 1, màn hình riêng, audit, optimistic concurrency và xử lý request lặp.

**Nghiệm thu:** Tạo/sửa nháp/xem/gửi đơn đúng quyền; phân biệt tài nguyên của mình/người khác; dữ liệu và audit commit cùng nhau; xung đột cập nhật không bị ghi đè âm thầm; UI/API cho cùng kết quả. Orders có extension point để Approvals tham gia mà không phụ thuộc implementation của Approvals.

### M5 — OrderApprovals và tính tùy chọn

**Phụ thuộc:** M4.

**Đầu ra:** ApprovalRequests/decisions, migrations riêng nếu có context riêng, extension vào Orders, một cấp duyệt, kiểm tra người tạo, lý do từ chối, transaction coordinator.

**Nghiệm thu:** Tenant Approval không xác nhận đơn khi chưa được duyệt; không tự duyệt; hai request đồng thời chỉ một quyết định có hiệu lực; lỗi giữa chừng rollback Order/Approval/Audit. Standard không có endpoint/menu duyệt hoạt động. Tắt feature không xóa dữ liệu và không bỏ qua đơn đang chờ.

### M6 — Release và migration có kiểm soát

**Phụ thuộc:** M5; CI và kiểm thử migration đã được phát triển dần từ các milestone trước.

**Đầu ra:** Build artifact/bundles, release manifest theo context/module, script pwsh chạy theo tenant, maintenance gate, lock/retry/report, warm-up Orchard, smoke tests, backup/restore runbook.

**Nghiệm thu:** Database trống và database từ bản trước đều nâng cấp được; áp dụng lại không tạo trùng schema; cố ý làm lỗi một tenant có trạng thái rõ ràng và tiếp tục đúng bước. Kiểm tra cả EF và Orchard, không chỉ HTTP 200. Feature chưa được mở trước khi schema sẵn sàng. Diễn tập restore một tenant không làm mất tenant khác; khi restore quyền cũ phải đối soát membership/quyền hiện tại, không mặc nhiên mở lại đặc quyền đã bị thu hồi.

EF migration bundles dùng cho job tự động; SQL scripts khi cần review. Orchard migration được chạy bằng shell thích hợp, không nằm trong EF bundle. [8][9]

### M7 — Kiểm thử tải, nghiệm thu và tái sử dụng

**Phụ thuộc:** M6.

**Đầu ra:** Bộ test 4 tenant, test tải nhiều user, báo cáo bottleneck, runbooks, module template, ví dụ product thứ hai không phụ thuộc Orders.

**Nghiệm thu:** Không truy cập chéo dữ liệu hoặc quyền; không lost update/duyệt trùng; không lỗi server bất ngờ trong kịch bản thử. Có báo cáo latency/throughput/error trên cấu hình máy cụ thể. Product mẫu thứ hai khởi động được phần platform + theme riêng mà không phải sửa lõi hoặc kéo Orders vào.

Test tải ban đầu đề xuất 25 virtual users mỗi tenant, tổng 100, có ramp-up và workload đọc/ghi/duyệt. Đây là cấu hình thử nghiệm, không phải cam kết năng lực production. Khóa mục tiêu latency cụ thể sau baseline và trước nghiệm thu chạy thử.

## 8. Ma trận dữ liệu thử nghiệm

| Tenant | Gói | Mục tiêu |
|---|---|---|
| Alpha | Standard | Orders, role Sales/Manager, dữ liệu trùng mã với tenant khác. |
| Beta | Approval | Duyệt/từ chối, không tự duyệt, đồng thời nhiều người duyệt. |
| Gamma | Approval | Tài khoản dùng chung nhưng role khác; role Allow + user Deny. |
| Delta | Standard | Gia hạn/hết hạn/tạm khóa/xóa mềm/purge/restore. |

Baseline tải giữ cả bốn tenant Active; test lifecycle thay đổi Delta trong kịch bản riêng. Có tài khoản chỉ ở Alpha, tài khoản ở nhiều tenant với role khác nhau, tài khoản không có membership và tài khoản vận hành.

Các ca lỗi phải có: đổi URL/token/body TenantId; feature bị tắt nhưng gọi API trực tiếp; session/role/membership bị thu hồi; cache không phân tách tenant; audit lỗi; migration lỗi; retry refresh; hai tab khác tenant; restore làm xuất hiện dữ liệu quyền cũ; xóa tenant không xóa tài khoản dùng chung.

## 9. Tổ chức repository dự kiến

```text
src/
  SaaS.Web/
  Platform/
    SaaS.Abstractions/
    SaaS.Tenancy/
    SaaS.Identity/
    SaaS.AccessControl/
    SaaS.Auditing/
  Modules/
    SaaS.Orders/
    SaaS.OrderApprovals/
  Themes/
    SaaS.Theme.App/
tests/
  Unit/
  Integration/
  EndToEnd/
  Load/
scripts/
  dev.ps1
  check.ps1
  tenant-provision.ps1
  tenant-migrate.ps1
  tenant-purge.ps1
docs/
  adr/
  roadmap/
  runbooks/
AGENTS.md
global.json
Directory.Packages.props
```

Đây là cấu trúc dự kiến, không phải các file đã được tạo trong repository người dùng. Không bắt buộc mỗi folder là một assembly hoặc mỗi module một DbContext; chỉ tách theo ranh giới nghiệp vụ/vòng đời và khóa thiết kế ở M0–M5.

## 10. Workflow cho một người và Codex subagents

Người phát triển giữ quyết định kiến trúc, review bảo mật, migration và merge. Agent chính nhận một task có tiêu chí nghiệm thu; subagent làm việc độc lập theo phạm vi file/worktree; reviewer riêng kiểm tra quyền, isolation và transaction.

Mỗi task cần có: đầu vào/phụ thuộc, file được sửa, interface dùng/cung cấp, ca test chính và ca lỗi, lệnh verify, migration owner và điều kiện hoàn thành. AGENTS.md nêu quy tắc; mỗi milestone có task plan nhỏ hơn trước khi code. [10]

Không cho nhiều agent cùng sửa model snapshot, context model hoặc migration stream của một DbContext. Identity/membership/permission contracts phải được merge trước khi phân việc phụ thuộc. Tài liệu và test độc lập có thể làm song song; auth core không tách tùy tiện theo file để tránh mỗi agent hiểu một policy khác nhau.

Definition of Done: diff được review, dotnet build/test thành công, integration test SQL Server thành công cho thay đổi dữ liệu, negative authorization tests đạt, migration được review, không lộ secret và tài liệu được cập nhật. Không coi lời báo của agent là bằng chứng thay cho lệnh kiểm thử.

## 11. Sau V1 và điều kiện trước production

PostgreSQL cần provider + tập migration/snapshot riêng và integration test riêng. EF chỉ scaffold cho provider đang dùng; đổi connection string không chuyển được toàn bộ migration SQL Server sang PostgreSQL. Chuẩn bị ranh giới provider ngay nhưng không sinh migration PostgreSQL giả trong V1. [11]

Ngoài V1: billing tự động, khách hàng tự tạo tenant, workflow nhiều cấp, SPA riêng, theo dõi quota/file/job nâng cao, distributed infrastructure, zero-downtime và multi-region.

Không chốt RTO/RPO lúc này nhưng vẫn làm backup/restore smoke test. Trước production có dữ liệu thật, cần chốt owner vận hành, lịch backup, retention/purge, xử lý sự cố, secrets/keys, TLS, quyền DB và môi trường CI/CD. Không coi dữ liệu không có yêu cầu nhạy cảm đặc biệt là miễn kiểm soát truy cập.

## 12. Nguồn kỹ thuật chính

Các mục tiêu sản phẩm và tiêu chí nghiệm thu trong tài liệu là đề xuất thiết kế, không phải mặc định của framework. Tài liệu nhánh main/latest có thể đi trước package đã phát hành; M0 phải đối chiếu với phiên bản đã khóa.

1. Orchard Core OpenID: https://docs.orchardcore.net/en/main/reference/modules/OpenId/
2. ASP.NET Core policy authorization: https://learn.microsoft.com/en-us/aspnet/core/security/authorization/policies?view=aspnetcore-10.0
3. OAuth Security BCP, RFC 9700: https://datatracker.ietf.org/doc/html/rfc9700
4. OpenIddict token storage/revocation: https://documentation.openiddict.com/configuration/token-storage.html
5. Orchard Core Tenants/Feature Profiles: https://docs.orchardcore.net/en/latest/reference/modules/Tenants/
6. Orchard Core Audit Trail: https://docs.orchardcore.net/en/latest/reference/modules/AuditTrail/
7. EF Core transactions: https://learn.microsoft.com/en-us/ef/core/saving/transactions
8. EF Core applying migrations: https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying
9. Orchard Core Data Migrations: https://docs.orchardcore.net/en/latest/reference/modules/Migrations/
10. OpenAI Codex execution plans: https://developers.openai.com/cookbook/articles/codex_exec_plans
11. EF Core multiple database providers: https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/providers
