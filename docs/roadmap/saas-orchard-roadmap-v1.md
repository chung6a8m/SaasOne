# SaaS Orchard Core — Roadmap triển khai V1

Ngày: 2026-11-06
Revision: 1.1 — xử lý findings R01–R08 theo yêu cầu chủ dự án.
Trạng thái: Đã bổ sung hợp đồng thiết kế và cổng nghiệm thu; chưa triển khai hoặc nghiệm thu M0. Các kiểm chứng runtime vẫn chưa thực hiện.

[Review baseline](../reviews/2026-11-06-roadmap-v1-review.md) · [Đối chiếu xử lý findings](../reviews/2026-11-06-roadmap-v1-remediation.md). Review baseline giữ nguyên và tham chiếu commit cũ; bản roadmap này là nguồn yêu cầu hiện hành.

**Mục tiêu:** Tạo code base modular monolith có thể tái sử dụng cho nhiều sản phẩm, chứng minh bằng Orders và OrderApprovals trên tối thiểu bốn tenant.

**Kiến trúc:** Một ASP.NET Core host; Default shell dành cho identity và quản trị nền tảng; mỗi tenant khách hàng có database riêng và đường dẫn `/{tenant}/...`. Orchard quản lý dữ liệu CMS; EF Core quản lý các bảng ứng dụng. Một bảng chỉ có một công cụ sở hữu migration.

**Tech stack đã chốt:** .NET 10, Orchard Core CMS, YesSql, EF Core 10, SQL Server trước; VS Code, dotnet CLI, PowerShell, Git và Codex có subagent.

**Căn cứ phạm vi:** Giữ các yêu cầu sản phẩm đã xác nhận. Yêu cầu “fix findings và commit” cho phép áp dụng các điều chỉnh R01–R08 vào roadmap; không đồng nghĩa phê duyệt mọi chi tiết implementation, hoàn thành ADR hay đạt kiểm thử. Các mặc định ở mục 2 vẫn là ứng viên cho ADR M0, trừ ràng buộc xử lý findings được ghi rõ bên dưới. Không thay đổi thứ tự M0–M7 hoặc tự mở rộng phạm vi production.

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

<a id="r01-cms-trust"></a>
### 3.3. Trust model CMS trên cùng origin — R01

Các đường dẫn Default, `/alpha` và `/beta` trên cùng scheme/host/port không tạo origin độc lập. Prefix, database riêng và cookie Path không phải ranh giới cách ly mã chạy trong trình duyệt. [12]

V1 giữ routing theo path với các ràng buộc bắt buộc: theme, script, template (kể cả Liquid), recipe, module và cấu hình feature chỉ do đội nền tảng kiểm soát. Tenant không được xuất bản JavaScript, HTML thực thi tùy ý, thay template hoặc tự cài/bật module qua UI hay API. Không cấp Orchard Administrator cho role khách hàng. Recipe dùng allowlist feature tối thiểu, không bật toàn bộ CMS mặc định.

Nội dung khách hàng mặc định là text/dữ liệu có cấu trúc và được encode khi hiển thị. Rich text hoặc upload chỉ được mở sau khi có allowlist, xử lý phía server và negative tests ở M1–M3; trước đó tính năng phải đóng. Không phục vụ HTML/SVG/JavaScript hoặc nội dung upload có khả năng thực thi inline trên origin ứng dụng. M0 ghi chính sách loại tệp, kiểu nội dung, nơi phục vụ và quyền publish vào ADR; CSP chỉ là lớp bổ sung. Nhu cầu cho khách hàng chạy mã tùy ý đòi hỏi review lại ranh giới origin, không được lách bằng quyền CMS.

Nghiệm thu: tenant gọi trực tiếp API chỉnh template/script/feature vẫn bị từ chối; nội dung không được phép bị chặn hoặc encode theo policy; kiểm tra cả recipe và quyền sau provision/nâng cấp. M1 kiểm tra cấu hình ban đầu; M3 kiểm tra role/override không mở lại bề mặt này.

### 3.4. Các điểm mở rộng, không phải microservice

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

<a id="r02-identity-gate"></a>
**Cổng identity R02:** ADR M0 khóa yêu cầu và phương án ứng viên, ghi trạng thái `Conditional — pending M2 PoC`, không tuyên bố validation liên shell đã được chứng minh. Task đầu tiên của M2 là PoC Default + Alpha/Beta trên package đã khóa. Chưa mở rộng identity trước khi PoC đạt. Phải chứng minh validation truy cập đúng token/authorization store của issuer hoặc introspection được cấu hình tương đương; không suy ra store dùng chung chỉ vì cùng process. [1][4]

PoC bao gồm SSO hai tenant, callback/logout URL có prefix và allowlist, cookie độc lập, mapping issuer + subject không theo email, JWT format/encryption, audience/scope, token Alpha bị Beta từ chối, thu hồi tại Default có hiệu lực tại API và session web mục tiêu. Ghi configuration, phiên bản, lệnh và kết quả; đạt mới chuyển ADR sang Accepted. Nếu thất bại, giữ gate đóng và sửa/review ADR trước implementation phụ thuộc. Không chuyển toàn bộ M2 vào M0; điều kiện còn mở này phải xuất hiện trong báo cáo nghiệm thu M0.

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

<a id="r03-revocation-contract"></a>
### 4.4. Hiệu lực thu hồi, session và lỗi nguồn — R03

Mốc có hiệu lực là lúc thay đổi có thẩm quyền đã commit. Request UI/API bắt đầu bước authorization sau mốc đó phải thấy trạng thái mới và bị từ chối khi không còn quyền; không chờ JWT/cookie hết hạn. V1 không cache kết quả Allow hoặc dữ liệu quyền động qua nhiều request: đọc account/session/grant, tenant/ExpiresAt/entitlement, membership và role/override cần thiết từ nguồn có thẩm quyền. Không đọc snapshot quyền cũ đã mở trước mốc thu hồi. Cache nội bộ cùng request không được dùng cho request khác; cache quyền liên request chỉ được bổ sung qua ADR và test vô hiệu hóa riêng.

Request đã qua authorization trước mốc thu hồi có thể hoàn tất theo transaction/concurrency của nghiệp vụ; V1 không hứa hủy hồi tố thao tác đang chạy. Tác vụ dài hoặc deferred phải authorization lại khi bắt đầu thực thi. Protocol đổi gói ở mục 5.2 là ngoại lệ chặt hơn: phải chặn và giải quyết các lệnh ghi đang chạy trước khi thay feature. Bất kỳ lần retry nào tạo lần thực thi mới đều kiểm tra quyền lại.

| Sự kiện | Hợp đồng bắt buộc |
|---|---|
| Gỡ membership | Chặn mọi grant/session của user tại tenant đó; thêm lại membership tạo generation mới, không hồi sinh grant cũ. |
| Suspend, hết hạn hoặc xóa tenant | Chặn nghiệp vụ và refresh của tenant; mở lại/gia hạn yêu cầu grant/session hợp lệ thuộc thế hệ mới, không khôi phục quyền của token cũ. |
| Đổi role hoặc Deny | Quyền mới áp dụng ở lần authorization tiếp theo; claim role cũ không là nguồn quyết định. |
| Logout tenant | Thu hồi grant/session của user hiện tại ở tenant được chọn; không logout các tenant khác. |
| Logout toàn cục hoặc khóa account | Thu hồi toàn bộ grant/session của user trên các tenant; kiểm tra account phía server, không chỉ xóa cookie issuer. |
| Lỗi platform, issuer store hoặc nguồn quyền tenant bắt buộc | Fail closed: không đọc/ghi nghiệp vụ và không cấp/refresh token bằng cache Allow; trả lỗi dependency unavailable có correlation id, không giả thành thiếu quyền. |

M0 khóa định danh và generation/security version của account, tenant, membership, session/grant; M2 hiện thực và M3 tích hợp quyền động. Version hoặc bản ghi thu hồi bền vững phải khiến grant đã mất hiệu lực không sống lại sau unlock, remove/re-add, gia hạn hoặc restart. Với expiry tự nhiên, phát/refresh token và lập/gia hạn session phải ràng buộc hạn hiệu lực vào ExpiresAt tại lúc cấp; gia hạn tenant không làm token/cookie/session đã hết hiệu lực trở lại hợp lệ. Hợp đồng cũng phải ghi TTL cụ thể của access token, refresh token (idle/absolute) và cookie/session trước khi triển khai M2; không tự lấy default package làm quyết định sản phẩm.

API nghiệp vụ phân biệt 401 (credential/grant không hợp lệ), 403 (đã xác thực nhưng bị policy chặn), 503 (nguồn bắt buộc không sẵn sàng); không tiết lộ tài nguyên tenant khác. UI áp dụng cùng quyết định, không redirect vòng lặp khi dependency lỗi. Endpoint OAuth tuân thủ lỗi giao thức của nó; refresh đã dùng/thu hồi trả `invalid_grant` thay vì HTTP 403 của API nghiệp vụ.

Refresh được serialize theo grant family và chỉ tiêu thụ mỗi token một lần. V1 chọn rotation nghiêm ngặt, không có cửa sổ chấp nhận lại token cũ: reuse thu hồi family và session liên quan tại tenant đó, không thu hồi family độc lập hoặc tenant khác. Hai refresh đồng thời có thể có một response thành công trước khi reuse thu hồi family; không được còn hai chuỗi token có hiệu lực. Client chỉ chạy một refresh tại một thời điểm. Response refresh bị mất không được xử lý bằng replay vô hạn: dùng token cũ có thể khiến family bị thu hồi và phải đăng nhập lại. M2 phải chứng minh cấu hình thực tế đáp ứng hợp đồng này, không mặc định framework đã bật đúng. [3]

Nghiệm thu M2/M3: kiểm tra trước/sau commit thu hồi ở cả UI/API, request đang chạy theo giới hạn trên, role/Deny mới, lỗi từng nguồn quyền, logout hai phạm vi, refresh đồng thời/reuse/mất response, remove/re-add, suspend/unsuspend, hết hạn/gia hạn và restart; Alpha còn hoạt động khi chỉ Beta bị khóa. Restore không được bỏ qua đối soát quyền tại M6.

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

<a id="r04-bootstrap-readiness"></a>
### 5.1. Bootstrap, quyền vận hành và schema readiness — R04

M0 phải ghi bootstrap mode và bảng quyền SQL của runtime, migrator và provisioner trong ADR; liệt kê owner cho từng schema/table/migration history của Orchard/YesSql và EF. Provisioner quản lý DB/principal đích đã xác minh; migrator áp dụng schema; runtime chỉ có quyền cần thiết cho hoạt động đã chứng minh. Secret nằm ngoài Git, không truyền qua log hoặc command line dễ lộ. Không dùng tài khoản tạo/drop DB làm tài khoản runtime chung cho các tenant.

Từ M1, bootstrap Default chạy trong chế độ vận hành đóng traffic công khai, qua CLI/kênh riêng được xác thực hoặc credential bootstrap dùng một lần từ secret store; không để trang setup mặc định công khai cho người truy cập đầu tiên chiếm quyền. Kết thúc phải vô hiệu bootstrap credential/đường setup. Provision, bật feature, migrate và purge đều qua công cụ có authorization, kiểm tra TenantId/DB đích, operation id, lock và checkpoint; retry không tạo admin, database hoặc tenant trùng. Purge từ chối platform DB hoặc DB không khớp registry, không nhận connection string tùy ý từ người gọi.

M1 có audit vận hành tối thiểu cho bước thành công/thất bại, actor, TenantId và correlation/operation id, không đợi UI M3. EF owner audit platform được chỉ định ngay ở M1; M3 mở rộng qua cùng migration stream, không tạo owner cạnh tranh. Audit/checkpoint không ghi được thì thao tác không được báo thành công hoặc mở traffic.

Traffic nghiệp vụ chỉ mở khi registry cho phép và schema Orchard + EF, seed, feature/profile cùng health check cần thiết đều đạt đúng phiên bản kỳ vọng. Provisioning, migration lỗi hoặc trạng thái chưa biết phải giữ đóng; không dùng HTTP 200 của trang chủ làm readiness. Đường vận hành và warm-up được bảo vệ riêng để sửa lỗi mà không mở nghiệp vụ.

Orchard mô tả data migrations chạy tự động khi khởi động. [9] M1 phải kiểm chứng startup/warm-up trên package đã khóa: chạy phần cần quyền DDL trong chế độ bảo trì/identity được kiểm soát, rồi xác minh chế độ phục vụ bình thường. Không tuyên bố runtime không cần DDL khi chưa chứng minh; mọi ngoại lệ quyền phải được ghi, giới hạn vào tenant/schema cần thiết và review trước khi mở traffic. Release runner đầy đủ vẫn ở M6.

Nghiệm thu M1: anonymous không gọi được setup/provision; retry và chạy cạnh tranh không tạo trùng; secret không lọt Git/log; thiếu quyền hoặc lỗi schema/seed giữ tenant đóng và tiếp tục đúng bước; runtime tenant không truy cập DB khác; direct feature toggle không vượt orchestration.

<a id="r05-plan-transition"></a>
### 5.2. Đổi gói/tắt feature không tạo race — R05

M0 khóa protocol dưới đây; M3 hiện thực và kiểm thử hạ tầng, M5 chứng minh bằng Submit/Approve thật. Chỉ một operation đổi gói theo TenantId/version được sở hữu tại một thời điểm; operation id, gói cũ/mới, version và checkpoint lưu bền vững ở platform. Đây là workflow nhiều bước, không dùng distributed transaction.

1. Chuyển registry từ trạng thái cho phép sang `PlanChanging` bằng kiểm tra version; chặn nghiệp vụ mới. DB tenant phải có durable write gate đã tạo bởi migration M3 (owner Tenancy, không để Orders/Approvals tự sở hữu). Mỗi lệnh ghi nghiệp vụ phải kiểm tra gate/version trong chính transaction của nó và giữ khóa tương thích đến commit/rollback. Không đủ nếu chỉ kiểm tra middleware trước transaction.
2. Operation đóng write gate bằng khóa xung đột với các transaction nghiệp vụ đang giữ gate; đợi chúng kết thúc; timeout giữ registry đóng, không báo write gate đã đóng khi transaction đóng gate chưa commit. Chỉ sau khi đóng gate bền vững mới kiểm tra lại PendingApproval và các điều kiện hạ gói. Request đã authorization trước bước 1 nhưng chưa ghi không được vượt gate hoặc dùng version cũ.
3. Nếu còn PendingApproval, từ chối hạ gói; không đổi feature/entitlement. Chỉ mở lại gói cũ sau khi đối soát trạng thái cũ và version. Nhờ vậy người dùng có thể xử lý đơn chờ rồi thử lại; không giữ khóa nghiệp vụ vô hạn để chờ duyệt.
4. Nếu hợp lệ, giữ gate đóng trong khi migrate schema cần thiết, cập nhật feature/profile và entitlement theo checkpoint. Sau mỗi bước ghi trạng thái để resume đối soát thực tế, không lặp mù. Lỗi/crash giữa platform và tenant giữ `PlanChanging` hoặc trạng thái lỗi có thể khôi phục; không tự mở trong `finally`.
5. Đối soát gói/feature/schema và target version; chuẩn bị mở gate tenant ở version mới khi platform vẫn đóng; chuyển registry sang trạng thái phục vụ cuối cùng. Request cũ không khớp version phải bị từ chối. Chỉ mở lại khi cả hai phía thống nhất; recovery dùng lại operation id, tăng fencing version khi đổi worker sở hữu và kiểm tra quyền sở hữu tại mọi bước, không cho worker cũ ghi đè operation mới. Task plan phải bảo đảm độc quyền cho cả bước đổi feature không có API conditional-write.

Primitive SQL/lock timeout và đường recovery được khóa trong task plan M3. Bất biến bắt buộc: không có command commit thay đổi liên quan ở giữa lần kiểm tra cuối và lúc đổi gói; mất process/connection không được làm durable gate tự mở. Mọi đường tắt feature (CLI, API, Orchard Admin/recipe) phải đi qua protocol hoặc bị cấm; PlatformOperator không dùng direct toggle để bỏ guard. Tắt feature không xóa bảng/lịch sử.

Nghiệm thu: đua downgrade với Submit/Approve; request đã qua authorization nhưng đang đợi ghi; timeout và crash ở từng checkpoint; restart/retry; worker cũ; toggle trực tiếp. Kết quả không có PendingApproval bị kẹt sau khi tắt Approvals, không mở entitlement trước schema, không mất lịch sử. M3 dùng fixture kiểm chứng protocol; M5 thay bằng nghiệp vụ thật.

## 6. Audit và transaction

Orchard Audit Trail phục vụ sự kiện Orchard được hỗ trợ hoặc được đăng ký mở rộng; không mặc định nó theo dõi mọi thay đổi qua EF. [6]

Audit nghiệp vụ EF ghi action, actor GlobalUserId, TenantId, resource, timestamp UTC, correlation id và thay đổi cần thiết. Ghi audit cùng transaction với nghiệp vụ thành công; log thao tác bị từ chối là luồng riêng để không mất khi rollback.

Audit platform lưu create/suspend/renew/delete/plan-change/membership-change; audit tenant lưu Orders/Approvals/role/permission-change. Không lưu mật khẩu, raw token hoặc connection string. Không tuyên bố log chống sửa tuyệt đối chỉ vì UI không có nút xóa.

Với Orders và Approvals dùng context riêng, approve phải cập nhật trạng thái đơn, quyết định duyệt và audit như một đơn vị atomic. Cùng database không tự bảo đảm cùng transaction. Nếu dùng nhiều EF context, chia sẻ DbConnection và DbTransaction qua coordinator; thử cả rollback và execution strategy/retry. [7] Coordinator phải được triển khai trước consumer đa context đầu tiên (M3/M4 nếu audit dùng context riêng), không mặc định chờ M5. Nếu một context đáp ứng đủ ranh giới hiện tại thì chưa xây coordinator tổng quát.

Mỗi bảng audit chỉ có một migration owner; module khác không được sinh migration sửa lại bảng đó. Không đòi hỏi distributed transaction giữa database platform và database tenant: cấp phát nhiều bước có status/retry, và quyền truy cập mặc định đóng khi chưa hoàn tất.

<a id="r06-audit-orders-contract"></a>
### 6.1. Hợp đồng audit và Orders trước consumer — R06

M3 khóa owner/context của audit và API tham gia transaction để M4 sử dụng: audit không tự commit độc lập, không âm thầm ghi sang platform DB thay cho audit tenant. Lỗi audit phải rollback nghiệp vụ; ghi log thao tác bị từ chối là luồng riêng. Nếu phải dùng nhiều context, khóa connection/transaction lifetime và execution strategy cho cả đơn vị công việc trước khi implement consumer; không retry từng SaveChanges riêng rồi tạo audit/đơn trùng.

M4 khóa state machine và extension contract trước khi phân việc Orders/Approvals. Baseline để triển khai:

| Trạng thái/thao tác | Kết quả và ràng buộc |
|---|---|
| Tạo/sửa Draft | Chỉ người có resource permission; mỗi sửa đổi cập nhật revision/concurrency token. |
| Submit Draft, gói Standard | Chuyển Confirmed theo policy; không sinh approval giả. |
| Submit Draft, gói Approval | Chuyển PendingApproval và tạo yêu cầu duyệt gắn đúng revision, cùng transaction. Nếu handler/schema Approvals chưa sẵn sàng thì từ chối, không fallback thành Standard. |
| PendingApproval | Không sửa nội dung đơn; Approve/Reject phải khớp revision và concurrency token. Người tạo không được tự duyệt. |
| Approve/Reject | Approve chuyển Confirmed; Reject chuyển Rejected và bắt buộc lý do. Chỉ một quyết định có hiệu lực cho mỗi request/revision. |
| Sửa đơn Rejected/gửi lại | Đưa về Draft với revision mới, giữ lịch sử quyết định/revision cũ; Submit lại tạo yêu cầu duyệt mới. |
| Confirmed | Không sửa/gửi lại trong V1; hủy hoặc sửa sau xác nhận là phạm vi bổ sung cần review. |

Các hành vi trên là lựa chọn xử lý R06 trong revision này, không phải default Orchard. M4 có thể chứng minh extension bằng fixture; không mở luồng Approval thật trước M5. Orders chỉ phụ thuộc contract của extension, không tham chiếu implementation OrderApprovals.

Idempotency cho command được phân vùng theo TenantId + actor/client + operation + key; fingerprint bao gồm resource, payload chuẩn hóa và revision kỳ vọng. Ghi kết quả/dedup record cùng transaction nghiệp vụ/audit. Cùng key và fingerprint trả lại kết quả đã commit, không tạo side effect lần hai; cùng key khác fingerprint trả conflict (409 ở API nghiệp vụ). Hai request cùng key được serialize bằng ràng buộc/transaction; lỗi giữa chừng không để completed record giả. Retry sau mất response phải trả được kết quả đã commit. Luôn authorization lại trước khi trả replay; key không cấp quyền đọc kết quả cũ. TTL/retention và quy tắc chuẩn hóa được ghi trong task plan M4.

Nghiệm thu M4/M5: lỗi audit rollback; cùng key khác payload; request trùng/đồng thời/mất response; xung đột revision; sửa đơn chờ bị chặn; duyệt revision cũ bị chặn; reject/sửa/gửi lại giữ lịch sử; lỗi giữa Order/Approval/Audit rollback toàn bộ, retry không nhân đôi dữ liệu.

## 7. Roadmap theo milestone

### M0 — Khóa thiết kế, môi trường và hợp đồng

**Phụ thuộc:** Không.

**Đầu ra:** ADR cho identity/membership, deny policy, database ownership, token/refresh, lifecycle; trust model CMS; bootstrap/quyền SQL/readiness; protocol đổi gói; decision register có owner/gate; solution skeleton và dependency rules; khóa SDK/packages/provider/tools; AGENTS.md và conventions; scripts thiết lập/check; CI build/test ban đầu. Identity ghi Conditional đến PoC đầu M2, không đánh dấu Accepted chỉ vì restore thành công.

**Nghiệm thu:** Clone repository mới, chạy theo README bằng dotnet CLI và pwsh; build/test không cần Visual Studio đầy đủ; không có secret trong Git. Hoàn thành ma trận và lệnh tại mục 7.1, SQL Server smoke thật, architecture checks và báo cáo command/exit code/test counts. Ghi rõ mọi quyết định bổ sung được review trước khi agent triển khai; không bỏ các điều kiện R01–R08 hoặc biến NOT RUN thành PASS.

<a id="r07-m0-acceptance"></a>
#### 7.1. Ma trận và bằng chứng nghiệm thu M0 — R07

M0 phải tạo compatibility matrix có giá trị cụ thể cho: .NET SDK exact và roll-forward policy; Orchard/EF/provider cùng dependency graph YesSql/OpenIddict đã resolve; local dotnet tools; PowerShell; OS/architecture phát triển; CI provider/runner; SQL Server edition/version, OS và cách cấp instance test. Ghi nguồn package, lock strategy, quyền DB và secret reference không chứa giá trị secret. Không để ô TBD ở cuối M0; không tự coi GitHub Actions, OS hoặc patch version đã được chọn chỉ vì repo đặt trên GitHub. Production host/RTO/RPO vẫn ngoài gate này.

Hợp đồng đầu ra M0 là `SaasOne.slnx`, tool manifest, `global.json`, central package versions và NuGet lock files được commit. Các lệnh dưới đây là yêu cầu phải làm chạy được trong M0, **chưa phải script/file hiện có hoặc kết quả đã chạy**:

```powershell
dotnet --info
pwsh --version
dotnet tool restore
dotnet restore SaasOne.slnx --locked-mode
dotnet build SaasOne.slnx --configuration Release --no-restore
dotnet test SaasOne.slnx --configuration Release --no-build --logger trx --results-directory artifacts/test-results/m0
pwsh -NoProfile -File ./scripts/check.ps1 -Scope M0
pwsh -NoProfile -File ./scripts/check.ps1 -Scope SqlSmoke
```

`check.ps1 -Scope M0` kiểm tra prerequisite/pin, locked restore/build, unit/architecture tests, script smoke và secrets/config hygiene; phải phát hiện test discovery bằng 0 hoặc suite bắt buộc bị skip. `SqlSmoke` kết nối SQL Server thật được cấp cho test, xác minh engine/version/database đích và truy vấn thành công; thiếu instance/secret/quyền thì exit khác 0 và ghi BLOCKED/FAIL, không tự skip, không dùng EF InMemory/SQLite thay bằng chứng. Smoke M0 không được ghi thành đã kiểm chứng isolation/migration; các test đó bắt đầu ở M1 và bắt buộc từ thay đổi dữ liệu đầu tiên.

Unit/architecture tests tối thiểu kiểm tra dependency graph thật của skeleton và fixture hợp lệ/vi phạm; script smoke kiểm tra prerequisite hợp lệ lẫn thiếu prerequisite trả lỗi. Không chấp nhận test chỉ assert hằng số. CI cơ bản chạy restore/build/test/check; SQL smoke là gate riêng trên runner được cấp SQL và phải có evidence trước khi đóng M0. Báo cáo ghi SHA, môi trường, command/exit code, số discovered/executed/passed/failed/skipped, log đã bỏ secret và điều kiện chưa chứng minh. Gate M0 chỉ PASS khi các suite bắt buộc thực chạy, executed > 0, failed = 0, không skip bắt buộc và tất cả lệnh bắt buộc exit 0.

### M1 — Host, tenant routing và provisioning

**Phụ thuộc:** M0.

**Đầu ra:** Một host, Default/platform DB và bốn customer DB; registry; TenantId bất biến; shell prefix; recipe allowlist và migration baseline; bootstrap/provision được bảo vệ, lock/checkpoint/audit vận hành tối thiểu và readiness gate theo mục 5.1; công cụ provision chạy lại được. Không đợi UI M3 để bảo vệ thao tác vận hành.

**Nghiệm thu:** Bốn prefix vào đúng bốn DB; prefix không tồn tại trả lỗi có kiểm soát, không rơi vào nghiệp vụ Default; trùng slug bị chặn; prefix hệ thống như connect/admin/api được dành riêng. Tài khoản DB tenant không đọc được DB tenant khác. Test cache key và link có prefix. Đạt negative tests CMS/bootstrap/readiness ở mục 3.3 và 5.1; kiểm tra dependency graph ngay từ project references đầu tiên, không chờ M7.

### M2 — Identity dùng chung và API token thực tế

**Phụ thuộc:** M1.

**Đầu ra:** PoC liên shell ở mục 4.1 là task đầu tiên và gate trước khi mở rộng identity; cập nhật ADR theo kết quả. Sau đó central login, SSO giữa tenant, mapping subject, memberships, chọn tenant; JWT access token và refresh token; API thử nghiệm được bảo vệ; session/grant generation và validation/revocation liên shell theo mục 4.4.

**Nghiệm thu:** Một user vào Alpha/Beta nhưng không vào Gamma; hai tab hai tenant không bị đổi context chéo; token Alpha không gọi API Beta dù user có membership cả hai; refresh không đổi tenant; token sai issuer/audience/expired bị từ chối; refresh reuse và logout có test. Account không được link chỉ vì trùng email. PoC có bằng chứng đọc đúng issuer store; đạt ca thu hồi UI/API, refresh đồng thời/replay/mất response, lỗi nguồn và remove/re-add ở mục 4.4; không dùng token expiry thay cho kiểm tra trạng thái server.

### M3 — Authorization, lifecycle và audit nền tảng

**Phụ thuộc:** M2.

**Đầu ra:** Roles theo tenant; allow/deny/unspecified; per-user override; resource policy; hạn dùng/khóa/gói; UI vận hành tối thiểu; protocol và durable write gate đổi gói ở mục 5.2; audit service/owner/transaction contract đủ dùng trước M4. Hiện thực coordinator tại đây nếu consumer đầu tiên đã cần nhiều context; không dời bắt buộc sang M5.

**Nghiệm thu:** Deny thắng mọi Allow; không thể nâng quyền qua API quản lý role; hết hạn, suspended hoặc membership revoked bị chặn dù JWT chưa hết hạn; tenant khác của cùng user vẫn dùng được. Xóa mềm/purge trên dữ liệu giả không xóa global account hoặc DB khác. Hoàn thiện kiểm tra ở cả UI và API, lỗi nguồn quyền/cache và phạm vi revoke theo mục 4.4. Test protocol đổi gói với fixture, crash/retry/gate/version; không cho direct toggle bỏ guard. Audit platform M1 giữ cùng migration owner.

### M4 — Orders xuyên suốt

**Phụ thuộc:** M3.

**Đầu ra:** State machine, revision, Submit Standard/Approval, idempotency và extension contracts theo mục 6.1 được khóa trước implementation; Orders module, OrdersDbContext/migrations, application service, API version 1, màn hình riêng, audit và optimistic concurrency. Coordinator sẵn sàng trước consumer đa context đầu tiên nếu M3 chưa cần.

**Nghiệm thu:** Tạo/sửa nháp/xem/gửi đơn đúng quyền; phân biệt tài nguyên của mình/người khác; dữ liệu và audit commit cùng nhau; xung đột cập nhật không bị ghi đè âm thầm; UI/API cho cùng kết quả. Orders có extension point để Approvals tham gia mà không phụ thuộc implementation của Approvals. Đạt các ca audit rollback, idempotency khác payload/đồng thời/mất response và bất biến revision ở mục 6.1; không cho fallback Confirmed nếu gói yêu cầu Approval nhưng handler thiếu.

### M5 — OrderApprovals và tính tùy chọn

**Phụ thuộc:** M4.

**Đầu ra:** ApprovalRequests/decisions, migrations riêng nếu có context riêng, extension vào Orders, một cấp duyệt, kiểm tra người tạo, lý do từ chối; dùng/mở rộng coordinator từ consumer đầu tiên khi cần, không xây trùng. Ghép protocol đổi gói M3 với Orders/Approvals thật.

**Nghiệm thu:** Tenant Approval không xác nhận đơn khi chưa được duyệt; không tự duyệt; hai request đồng thời chỉ một quyết định có hiệu lực; lỗi giữa chừng rollback Order/Approval/Audit. Standard không có endpoint/menu duyệt hoạt động. Tắt feature không xóa dữ liệu và không bỏ qua đơn đang chờ. Race downgrade với Submit/Approve và request đang đợi gate, crash ở từng checkpoint, worker cũ và retry phải đạt mục 5.2; reject/sửa/gửi lại và duyệt đúng revision đạt mục 6.1.

### M6 — Release và migration có kiểm soát

**Phụ thuộc:** M5; CI và kiểm thử migration đã được phát triển dần từ các milestone trước.

**Đầu ra:** Build artifact/bundles, release manifest theo context/module, script pwsh chạy theo tenant, maintenance gate, lock/retry/report, warm-up Orchard, smoke tests, backup/restore runbook.

**Nghiệm thu:** Database trống và database từ bản trước đều nâng cấp được; áp dụng lại không tạo trùng schema; cố ý làm lỗi một tenant có trạng thái rõ ràng và tiếp tục đúng bước. Kiểm tra cả EF và Orchard, không chỉ HTTP 200. Feature chưa được mở trước khi schema sẵn sàng; release runner kế thừa bootstrap/readiness/đổi gói đã có từ M1/M3, không thay bằng đường tắt thiếu guard. Diễn tập restore một tenant không làm mất tenant khác; khi restore quyền cũ phải đối soát membership/quyền hiện tại, không mặc nhiên mở lại đặc quyền đã bị thu hồi.

EF migration bundles dùng cho job tự động; SQL scripts khi cần review. Orchard migration được chạy bằng shell thích hợp, không nằm trong EF bundle. [8][9]

### M7 — Kiểm thử tải, nghiệm thu và tái sử dụng

**Phụ thuộc:** M6.

**Đầu ra:** Bộ test 4 tenant, test tải nhiều user, báo cáo bottleneck, runbooks, module template, ví dụ product thứ hai không phụ thuộc Orders.

**Nghiệm thu:** Không truy cập chéo dữ liệu hoặc quyền; không lost update/duyệt trùng; không lỗi server bất ngờ trong kịch bản thử. Có báo cáo latency/throughput/error trên cấu hình máy cụ thể. Product mẫu thứ hai khởi động được phần platform + theme riêng mà không phải sửa lõi hoặc kéo Orders/OrderApprovals vào project/package/feature graph hay publish artifact; chỉ tắt feature không đủ.

Test tải ban đầu đề xuất 25 virtual users mỗi tenant, tổng 100, có ramp-up và workload đọc/ghi/duyệt. Đây là cấu hình thử nghiệm, không phải cam kết năng lực production. Khóa mục tiêu latency cụ thể sau baseline và trước nghiệm thu chạy thử.

## 8. Ma trận dữ liệu thử nghiệm

| Tenant | Gói | Mục tiêu |
|---|---|---|
| Alpha | Standard | Orders, role Sales/Manager, dữ liệu trùng mã với tenant khác. |
| Beta | Approval | Duyệt/từ chối, không tự duyệt, đồng thời nhiều người duyệt. |
| Gamma | Approval | Tài khoản dùng chung nhưng role khác; role Allow + user Deny. |
| Delta | Standard | Gia hạn/hết hạn/tạm khóa/xóa mềm/purge/restore. |

Baseline tải giữ cả bốn tenant Active; test lifecycle thay đổi Delta trong kịch bản riêng. Có tài khoản chỉ ở Alpha, tài khoản ở nhiều tenant với role khác nhau, tài khoản không có membership và tài khoản vận hành.

Các ca lỗi phải có: đổi URL/token/body TenantId; feature bị tắt nhưng gọi API trực tiếp; session/role/membership bị thu hồi; cache không phân tách tenant; audit lỗi; migration lỗi; retry refresh; hai tab khác tenant; restore làm xuất hiện dữ liệu quyền cũ; xóa tenant không xóa tài khoản dùng chung. Bổ sung ca CMS/template/upload bị cấm, bootstrap anonymous, readiness lỗi, mất nguồn quyền, refresh đồng thời/mất response, membership remove/re-add, token cũ sau gia hạn/mở lại, downgrade đua Submit/Approve, crash/retry/version cũ và replay idempotency khác payload theo các hợp đồng R01–R06.

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
  reviews/
  runbooks/
AGENTS.md
global.json
Directory.Packages.props
```

Đây là cấu trúc dự kiến, không phải các file đã được tạo trong repository người dùng. Không bắt buộc mỗi folder là một assembly hoặc mỗi module một DbContext; chỉ tách theo ranh giới nghiệp vụ/vòng đời và khóa thiết kế ở M0–M5.

<a id="r08-dependency-rules"></a>
### 9.1. Dependency rules để tái sử dụng — R08

Platform/Abstractions không tham chiếu trực tiếp hoặc gián tiếp Orders/OrderApprovals. Shared platform/theme không được kéo business module qua ProjectReference, package hoặc Orchard feature manifest. Host của từng sản phẩm là composition root chọn module/theme; Orders cung cấp extension contract và không tham chiếu implementation Approvals. Không tạo cơ chế nạp plugin động chỉ để đáp ứng quy tắc này.

M0 ghi rules trong conventions/AGENTS và tạo architecture check từ project references đầu tiên của skeleton; M1 tiếp tục kiểm tra graph thật khi thêm Orchard modules/feature dependencies. Check phải phát hiện cả cạnh cấm trực tiếp và dependency bắc cầu, có fixture vi phạm để chứng minh không pass rỗng khi chưa có Orders. Mỗi milestone thêm reference/package/feature phải chạy lại check. M7 mới làm demo product thứ hai và kiểm tra published dependency graph; feature disabled không đồng nghĩa module không là dependency.

## 10. Workflow cho một người và Codex subagents

Người phát triển giữ quyết định kiến trúc, review bảo mật, migration và merge. Agent chính nhận một task có tiêu chí nghiệm thu; subagent làm việc độc lập theo phạm vi file/worktree; reviewer riêng kiểm tra quyền, isolation và transaction.

Mỗi task cần có: đầu vào/phụ thuộc, file được sửa, interface dùng/cung cấp, ca test chính và ca lỗi, lệnh verify, migration owner và điều kiện hoàn thành. AGENTS.md nêu quy tắc; mỗi milestone có task plan nhỏ hơn trước khi code. [10]

Không cho nhiều agent cùng sửa model snapshot, context model hoặc migration stream của một DbContext. Identity/membership/permission contracts phải được merge trước khi phân việc phụ thuộc. Tài liệu và test độc lập có thể làm song song; auth core không tách tùy tiện theo file để tránh mỗi agent hiểu một policy khác nhau.

Definition of Done cho thay đổi code/runtime: diff được review, dotnet build/test thành công, integration test SQL Server thành công cho thay đổi dữ liệu, negative authorization tests đạt, migration được review, không lộ secret và tài liệu được cập nhật. Không coi lời báo của agent là bằng chứng thay cho lệnh kiểm thử. Thay đổi chỉ Markdown như revision này kiểm tra diff/phạm vi, UTF-8, liên kết/anchor và coverage findings; ghi build/test/runtime là NOT RUN, không nhận là hoàn thành milestone.

Task plan và decision register M0 phải mang theo owner, trạng thái, cổng nghiệm thu và bằng chứng còn thiếu của R01–R08 trong [bảng xử lý](../reviews/2026-11-06-roadmap-v1-remediation.md). `Documented` chỉ nghĩa là yêu cầu đã được bổ sung; chỉ evidence tại milestone sở hữu mới đóng verification. Chủ dự án giữ quyền review/merge; implementer không tự nâng Conditional thành Accepted hoặc bỏ negative test để có CI xanh.

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
12. WHATWG HTML — Origins: https://html.spec.whatwg.org/multipage/browsers.html#origin
