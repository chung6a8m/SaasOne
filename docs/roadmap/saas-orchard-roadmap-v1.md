# SaaS Orchard Core — Roadmap triển khai V1

Ngày: 2026-11-06
Revision: 1.3 — xử lý findings vòng 3 R3-01–R3-03 theo yêu cầu chủ dự án; giữ các ràng buộc R01–R08 và R2-01–R2-03.
Trạng thái: Đã bổ sung hợp đồng thiết kế và cổng nghiệm thu; chưa triển khai hoặc nghiệm thu M0. Các kiểm chứng runtime vẫn chưa thực hiện.

[Review baseline vòng 1](../reviews/2026-11-06-roadmap-v1-review.md) · [Xử lý vòng 1](../reviews/2026-11-06-roadmap-v1-remediation.md) · [Xử lý vòng 2](../reviews/2026-11-06-roadmap-v1-round2-remediation.md) · [Xử lý vòng 3](../reviews/2026-11-06-roadmap-v1-round3-remediation.md). Các báo cáo vòng trước giữ nguyên và tham chiếu baseline tương ứng; bản roadmap này là nguồn yêu cầu hiện hành.

**Mục tiêu:** Tạo code base modular monolith có thể tái sử dụng cho nhiều sản phẩm, chứng minh bằng Orders và OrderApprovals trên tối thiểu bốn tenant.

**Kiến trúc:** Một ASP.NET Core host; Default shell dành cho identity và quản trị nền tảng; mỗi tenant khách hàng có database riêng và đường dẫn `/{tenant}/...`. Orchard quản lý dữ liệu CMS; EF Core quản lý các bảng ứng dụng. Một bảng chỉ có một công cụ sở hữu migration.

**Tech stack đã chốt:** .NET 10, Orchard Core CMS, YesSql, EF Core 10, SQL Server trước; VS Code, dotnet CLI, PowerShell, Git và Codex có subagent.

**Căn cứ phạm vi:** Giữ các yêu cầu sản phẩm đã xác nhận. Yêu cầu “fix findings và commit” cho phép áp dụng các điều chỉnh R01–R08, R2-01–R2-03 và R3-01–R3-03 vào roadmap; không đồng nghĩa phê duyệt mọi chi tiết implementation, hoàn thành ADR hay đạt kiểm thử. Các mặc định ở mục 2 vẫn là ứng viên cho ADR M0, trừ ràng buộc xử lý findings được ghi rõ bên dưới. Không thay đổi thứ tự M0–M7 hoặc tự mở rộng phạm vi production. Revision 1.2 chọn xác lập lại quyền sau restore, giữ nghĩa vụ duyệt của đơn cũ và tách replay khỏi thực thi mới. Revision 1.3 bổ sung danh tính dữ liệu backup/restore, phối hợp lifecycle với operation và nhánh khôi phục gói có phê duyệt khi dữ liệu phục hồi còn nghĩa vụ duyệt. Đây là các lựa chọn thiết kế để xử lý findings, không gán thành yêu cầu người dùng đã chốt trước review.

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

Tư cách thành viên là dữ liệu nền tảng. Role, role permission, user-role và user permission override của khách hàng nằm trong database của tenant. Việc xóa membership làm mất quyền truy cập ngay cả khi local profile hoặc role assignment chưa được dọn. Sau restore tenant, role/override và audit trong bản restore không được coi là quyền hiện hành; áp dụng cổng xác lập lại quyền ở mục 5.3 trước khi mở nghiệp vụ.

Truy cập platform data đi qua dịch vụ chuyên trách với kết nối cố định của platform. DbContext nghiệp vụ chỉ dùng kết nối của shell đã được Orchard định tuyến. Không chọn database từ tenantId trong body, header hoặc token.

Các bảng nghiệp vụ không cần cột TenantId chỉ để cô lập database. Bảng dùng chung ở platform, token, log, audit và khóa cache vẫn phải mang TenantId khi cần phân biệt tenant. Mỗi customer DB phải có một bản ghi định danh TenantId bất biến do migration stream Tenancy sở hữu từ M1; readiness đối chiếu với registry/shell trước khi mở nghiệp vụ. Đây là định danh cả DB, không phải yêu cầu thêm TenantId vào mọi bảng; backup/restore dùng nó theo mục 5.4, không suy ra danh tính dữ liệu từ tên DB hoặc connection string.

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

`TenantOperational` kiểm tra subscription state, ExpiresAt, operation gate và readiness ở server theo mục 5.5; hoàn tất operation không tự biến tenant Suspended/PendingDeletion thành Active. Mọi điều kiện bắt buộc đều phải đạt; quyền thuộc role chỉ là một phần quyết định. `ResourceRuleSatisfied` là policy bảo mật trên tài nguyên; không đồng nhất với precondition chuyển trạng thái/revision của command mới. Replay dùng policy riêng theo mục 6.3, không bỏ kiểm tra quyền hiện hành.

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
| Restore tenant | Chặn nghiệp vụ, chặn cấp/refresh grant của tenant, tăng security generation tại platform; token cũ bị vô hiệu và đăng nhập mới không được dùng quyền đã restore. Chỉ mở lại sau xác lập quyền theo mục 5.3. |
| Logout tenant | Thu hồi grant/session của user hiện tại ở tenant được chọn; không logout các tenant khác. |
| Logout toàn cục hoặc khóa account | Thu hồi toàn bộ grant/session của user trên các tenant; kiểm tra account phía server, không chỉ xóa cookie issuer. |
| Lỗi platform, issuer store hoặc nguồn quyền tenant bắt buộc | Fail closed: không đọc/ghi nghiệp vụ và không cấp/refresh token bằng cache Allow; trả lỗi dependency unavailable có correlation id, không giả thành thiếu quyền. |

M0 khóa định danh và generation/security version của account, tenant, membership, session/grant; M2 hiện thực và M3 tích hợp quyền động. Version hoặc bản ghi thu hồi bền vững phải khiến grant đã mất hiệu lực không sống lại sau unlock, remove/re-add, gia hạn hoặc restart. Với expiry tự nhiên, phát/refresh token và lập/gia hạn session phải ràng buộc hạn hiệu lực vào ExpiresAt tại lúc cấp; gia hạn tenant không làm token/cookie/session đã hết hiệu lực trở lại hợp lệ. Hợp đồng cũng phải ghi TTL cụ thể của access token, refresh token (idle/absolute) và cookie/session trước khi triển khai M2; không tự lấy default package làm quyết định sản phẩm.

API nghiệp vụ phân biệt 401 (credential/grant không hợp lệ), 403 (đã xác thực nhưng bị policy chặn), 503 (nguồn bắt buộc không sẵn sàng); không tiết lộ tài nguyên tenant khác. UI áp dụng cùng quyết định, không redirect vòng lặp khi dependency lỗi. Endpoint OAuth tuân thủ lỗi giao thức của nó; refresh đã dùng/thu hồi trả `invalid_grant` thay vì HTTP 403 của API nghiệp vụ.

Refresh được serialize theo grant family và chỉ tiêu thụ mỗi token một lần. V1 chọn rotation nghiêm ngặt, không có cửa sổ chấp nhận lại token cũ: reuse thu hồi family và session liên quan tại tenant đó, không thu hồi family độc lập hoặc tenant khác. Hai refresh đồng thời có thể có một response thành công trước khi reuse thu hồi family; không được còn hai chuỗi token có hiệu lực. Client chỉ chạy một refresh tại một thời điểm. Response refresh bị mất không được xử lý bằng replay vô hạn: dùng token cũ có thể khiến family bị thu hồi và phải đăng nhập lại. M2 phải chứng minh cấu hình thực tế đáp ứng hợp đồng này, không mặc định framework đã bật đúng. [3]

Nghiệm thu M2/M3: kiểm tra trước/sau commit thu hồi ở cả UI/API, request đang chạy theo giới hạn trên, role/Deny mới, lỗi từng nguồn quyền, logout hai phạm vi, refresh đồng thời/reuse/mất response, remove/re-add, suspend/unsuspend, hết hạn/gia hạn và restart; Alpha còn hoạt động khi chỉ Beta bị khóa. Restore phải qua cổng xác lập lại quyền ở mục 5.3; M6 kiểm tra cả credential cũ và đăng nhập mới, không coi đổi generation là đủ.

## 5. Vòng đời tenant và gói tính năng

Không dùng trực tiếp Orchard ShellState làm toàn bộ subscription state. Tách quyết định subscription (Active/Suspended/PendingDeletion cùng ExpiresAt) khỏi trạng thái operation (Provisioning/PlanChanging/Restoring/Migrating và các trạng thái recovery/lỗi). Cả hai cùng tham gia gate ở mục 5.5; trạng thái bảo trì không đồng nghĩa disable shell làm cản trở warm-up/migration. Các câu “chuyển Active/mở registry” bên dưới chỉ cho phép phục vụ khi subscription hiện hành và mọi gate đều cho phép, không ghi đè quyết định lifecycle mới hơn.

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

Không cho hạ gói Approval xuống Standard khi còn nghĩa vụ duyệt chưa hoàn tất: PendingApproval, Rejected hoặc Draft làm lại có `ApprovalRequired = true` theo mục 6.2; phải xử lý các đơn này trước. Không để thao tác tắt feature trở thành cách bỏ qua quy trình duyệt.

<a id="r04-bootstrap-readiness"></a>
### 5.1. Bootstrap, quyền vận hành và schema readiness — R04

M0 phải ghi bootstrap mode và bảng quyền SQL của runtime, migrator và provisioner trong ADR; liệt kê owner cho từng schema/table/migration history của Orchard/YesSql và EF. Provisioner quản lý DB/principal đích đã xác minh; migrator áp dụng schema; runtime chỉ có quyền cần thiết cho hoạt động đã chứng minh. Secret nằm ngoài Git, không truyền qua log hoặc command line dễ lộ. Không dùng tài khoản tạo/drop DB làm tài khoản runtime chung cho các tenant.

Từ M1, bootstrap Default chạy trong chế độ vận hành đóng traffic công khai, qua CLI/kênh riêng được xác thực hoặc credential bootstrap dùng một lần từ secret store; không để trang setup mặc định công khai cho người truy cập đầu tiên chiếm quyền. Kết thúc phải vô hiệu bootstrap credential/đường setup. Provision, bật feature, migrate và purge đều qua công cụ có authorization, kiểm tra TenantId/DB đích, operation id, lock và checkpoint; retry không tạo admin, database hoặc tenant trùng. Purge từ chối platform DB hoặc DB không khớp registry, không nhận connection string tùy ý từ người gọi.

M1 có audit vận hành tối thiểu cho bước thành công/thất bại, actor, TenantId và correlation/operation id, không đợi UI M3. EF owner audit platform được chỉ định ngay ở M1; M3 mở rộng qua cùng migration stream, không tạo owner cạnh tranh. Audit/checkpoint không ghi được thì thao tác không được báo thành công hoặc mở traffic.

Traffic nghiệp vụ chỉ mở khi registry cho phép theo mục 5.5, định danh DB khớp mục 5.4 và schema Orchard + EF, seed, feature/profile cùng health check cần thiết đều đạt đúng phiên bản kỳ vọng. Provisioning, migration lỗi hoặc trạng thái chưa biết phải giữ đóng; không dùng HTTP 200 của trang chủ làm readiness. Đường vận hành và warm-up được bảo vệ riêng để sửa lỗi mà không mở nghiệp vụ.

Orchard mô tả data migrations chạy tự động khi khởi động. [9] M1 phải kiểm chứng startup/warm-up trên package đã khóa: chạy phần cần quyền DDL trong chế độ bảo trì/identity được kiểm soát, rồi xác minh chế độ phục vụ bình thường. Không tuyên bố runtime không cần DDL khi chưa chứng minh; mọi ngoại lệ quyền phải được ghi, giới hạn vào tenant/schema cần thiết và review trước khi mở traffic. Release runner đầy đủ vẫn ở M6.

Nghiệm thu M1: anonymous không gọi được setup/provision; retry và chạy cạnh tranh không tạo trùng; secret không lọt Git/log; thiếu quyền hoặc lỗi schema/seed giữ tenant đóng và tiếp tục đúng bước; runtime tenant không truy cập DB khác; direct feature toggle không vượt orchestration.

<a id="r05-plan-transition"></a>
### 5.2. Đổi gói/tắt feature không tạo race — R05

M0 khóa protocol dưới đây; M3 hiện thực và kiểm thử hạ tầng, M5 chứng minh bằng Submit/Approve thật. Chỉ một operation đổi gói theo TenantId/version được sở hữu tại một thời điểm; operation id, gói cũ/mới, version và checkpoint lưu bền vững ở platform. Đây là workflow nhiều bước, không dùng distributed transaction. Operation đổi gói phải tham gia khóa điều phối chung và RegistryVersion ở mục 5.5; version riêng của đổi gói không đủ bảo vệ quyết định suspend/delete.

1. Đặt operation state của registry thành `PlanChanging` bằng kiểm tra RegistryVersion và quyền sở hữu; giữ nguyên subscription state, chặn nghiệp vụ mới. DB tenant phải có durable write gate đã tạo bởi migration M3 (owner Tenancy, không để Orders/Approvals tự sở hữu). Mỗi lệnh ghi nghiệp vụ phải kiểm tra gate/version trong chính transaction của nó và giữ khóa tương thích đến commit/rollback. Không đủ nếu chỉ kiểm tra middleware trước transaction.
2. Operation đóng write gate bằng khóa xung đột với các transaction nghiệp vụ đang giữ gate; đợi chúng kết thúc; timeout giữ registry đóng, không báo write gate đã đóng khi transaction đóng gate chưa commit. Chỉ sau khi đóng gate bền vững mới kiểm tra lại mọi nghĩa vụ duyệt chưa hoàn tất theo mục 6.2 và các điều kiện hạ gói. Request đã authorization trước bước 1 nhưng chưa ghi không được vượt gate hoặc dùng version cũ.
3. Nếu còn nghĩa vụ duyệt chưa hoàn tất (kể cả Rejected/Draft làm lại), từ chối hạ gói; không đổi feature/entitlement. Chỉ kết thúc operation và mở lại gói cũ sau khi đối soát trạng thái cũ/version cùng điều kiện lifecycle hiện hành ở mục 5.5; nếu đã bị suspend/delete thì tiếp tục chặn. Nhờ vậy người dùng có thể xử lý đơn chờ rồi thử lại; không giữ khóa nghiệp vụ vô hạn để chờ duyệt.
4. Nếu hợp lệ, giữ gate đóng trong khi migrate schema cần thiết, cập nhật feature/profile và entitlement theo checkpoint. Sau mỗi bước ghi trạng thái để resume đối soát thực tế, không lặp mù. Lỗi/crash giữa platform và tenant giữ `PlanChanging` hoặc trạng thái lỗi có thể khôi phục; không tự mở trong `finally`.
5. Đối soát gói/feature/schema và target version; chuẩn bị mở gate tenant ở version mới khi platform vẫn đóng; chỉ kết thúc operation bằng cập nhật có điều kiện ở mục 5.5, không ghi đè subscription state để mở phục vụ. Request cũ không khớp version phải bị từ chối. Chỉ mở lại khi cả hai phía thống nhất; recovery dùng lại operation id, tăng fencing version khi đổi worker sở hữu và kiểm tra quyền sở hữu tại mọi bước, không cho worker cũ ghi đè operation mới. Task plan phải bảo đảm độc quyền cho cả bước đổi feature không có API conditional-write.

Primitive SQL/lock timeout và đường recovery được khóa trong task plan M3. Bất biến bắt buộc: không có command commit thay đổi liên quan ở giữa lần kiểm tra cuối và lúc đổi gói; mất process/connection không được làm durable gate tự mở. Mọi đường tắt feature (CLI, API, Orchard Admin/recipe) phải đi qua protocol hoặc bị cấm; PlatformOperator không dùng direct toggle để bỏ guard. Tắt feature không xóa bảng/lịch sử.

Nghiệm thu: đua downgrade với Submit/Approve; request đã qua authorization nhưng đang đợi ghi; timeout và crash ở từng checkpoint; restart/retry; worker cũ; toggle trực tiếp. Kết quả không có nghĩa vụ duyệt chưa hoàn tất bị kẹt hoặc biến thành Confirmed trực tiếp sau khi tắt Approvals, không mở entitlement trước schema, không mất lịch sử. M3 dùng fixture kiểm chứng protocol; M5 thay bằng nghiệp vụ thật.

<a id="r2-01-restore-security"></a>
### 5.3. Xác lập lại quyền sau restore tenant — R2-01

Khôi phục SQL về một mốc cũ chỉ khôi phục các thay đổi tới mốc đó. [13] Theo ranh giới mục 3.2 và 6, quyền cùng audit quyền trong DB tenant có thể cùng bị đưa về quá khứ; không thể dùng riêng chúng để suy ra các lần thu hồi sau backup. V1 chọn **đóng tenant và xác lập lại toàn bộ phân quyền**, không xây thêm bản sao quyền đồng bộ ngoài tenant chỉ để tự động đối soát. Tăng generation chỉ vô hiệu credential cũ, không chứng minh quyền dùng cho lần đăng nhập mới là đúng.

1. Trước restore, operation có authorization và lock điều phối chung theo TenantId ở mục 5.5 phải ghi operation state `Restoring`/`SecurityRevalidationRequired`, operation id và security generation mới ở platform; giữ nguyên subscription state. Chặn UI/API nghiệp vụ, cấp/refresh grant và session tenant; drain công việc đang chạy trước khi thay DB. Không tin write gate hoặc trạng thái Active trong DB vừa restore. Chỉ đường recovery được bảo vệ riêng được hoạt động. Phải xác minh nguồn backup/DB đích trước thay DB và danh tính dữ liệu sau restore theo mục 5.4, trước migrate/seed, áp dụng quyền mới hoặc warm-up ứng dụng.
2. Platform DB, registry, membership, generation, quyền recovery và audit operation phải nằm ngoài phạm vi restore tenant này. Nếu platform cũng bị phục hồi/lùi dữ liệu hoặc không xác minh được nguồn hiện hành, giữ đóng và chuyển sang quy trình phục hồi nền tảng được review riêng; không coi runbook restore một tenant đủ cho trường hợp đó.
3. Toàn bộ role definition/permission, user-role và user override (cả Allow/Deny) đã restore là dữ liệu lịch sử chưa đáng tin cho authorization. Không chỉ xóa một vài Allow hoặc bắt login lại. Người có quyền phê duyệt recovery hiện hành, được đăng ký tại platform trước diễn tập, phải xác lập một bộ quyền mới đầy đủ theo quyết định hiện tại; thiếu quyết định thì tiếp tục từ chối. Không lấy quyền phê duyệt từ ACL đã restore, không tự phong recovery authority, không tự gán lại role mặc định từ local profile. Quyền recovery không tự cấp quyền đọc Orders cho PlatformOperator.
4. Áp dụng bộ quyền mới bằng transaction tenant cùng audit và dấu xác nhận generation/operation; không merge âm thầm quyền cũ vào bộ mới. Giữ lịch sử được phục hồi nhưng không dùng nó làm nguồn cấp quyền. Audit/checkpoint platform ghi actor, người phê duyệt, căn cứ quyết định, định danh/hash bộ quyền và generation; đây là quyết định hiện hành có chủ thể chịu trách nhiệm, không phải tuyên bố đã tự tái tạo được mọi thay đổi sau backup.
5. Chỉ kết thúc recovery để mở registry sau khi kiểm tra danh tính dữ liệu mục 5.4, bộ quyền đã áp dụng, generation/checkpoint hai phía, membership hiện hành, gói/feature/schema/readiness, nghĩa vụ nghiệp vụ so với entitlement theo mục 5.6 và negative tests quyền. Bước cuối phải thỏa điều kiện cập nhật/lifecycle mục 5.5; hoàn tất restore không xóa Suspended/PendingDeletion hoặc gia hạn ExpiresAt. Mất nguồn, thiếu audit, lỗi hoặc crash giữ đóng; resume phải kiểm tra operation ownership/version, không mở gate trong `finally`. Chỉ grant/session mới thuộc generation hiện hành được dùng sau khi mở; tenant khác không bị thu hồi theo.

M0 khóa ADR nguồn tin cậy, quyền phê duyệt recovery và hợp đồng trên. M3 hiện thực cổng/quyền/apply contract bằng fixture; M6 ghép restore SQL thật và runbook có checkpoint. Không kéo thao tác restore SQL về M0 hoặc coi fixture M3 là diễn tập restore. Muốn tự giữ lại quyền hiện hành sau restore thay cho xác lập lại cần ADR riêng chứng minh nguồn ngoài restore đầy đủ và nhất quán.

Nghiệm thu bắt buộc: backup khi user có quyền → thu hồi quyền nhưng membership vẫn Active → restore → thử credential cũ và đăng nhập mới → quyền đã thu hồi không tự trở lại. Chạy riêng cho user-role, role permission và user override/Deny; bộ quyền mới giữ quyết định thu hồi. Có ca người chỉ có quyền recovery trong ACL cũ không được phê duyệt, thiếu bộ quyền/nguồn platform giữ đóng, lỗi/crash từng checkpoint và tenant khác vẫn hoạt động.

<a id="r3-01-restore-identity"></a>
### 5.4. Danh tính dữ liệu backup/restore — R3-01

SQL Server cho phép restore backup thành database có tên/vị trí mới. [14] Vì vậy, DB đích khớp tên trong registry chưa chứng minh dữ liệu nguồn thuộc tenant đó. V1 bắt buộc đối chiếu **TenantId trong registry hiện hành, manifest backup đáng tin cậy và bản ghi định danh trong DB phục hồi**; không dùng slug, tên file, tên DB hoặc việc cùng schema làm bằng chứng thay thế.

M1 tạo bản ghi định danh duy nhất trong mỗi customer DB, lưu TenantId bất biến đã cấp ở platform, do migration stream Tenancy sở hữu; runtime không được sửa/xóa định danh. Provision retry chỉ xác nhận định danh trùng, không ghi đè DB đã thuộc tenant khác. Readiness từ M1 từ chối định danh thiếu/sai; migration/seed không tự “sửa” định danh để hợp thức hóa DB có sẵn. Không thêm chức năng clone dữ liệu sang TenantId mới trong V1.

M6 tạo manifest backup từ nguồn đã xác minh, lưu ngoài customer DB trong kho vận hành có kiểm soát truy cập. Manifest gắn TenantId với backup set/chuỗi backup được chọn, định danh nguồn, mốc phục hồi và digest của các artifact cần dùng. Digest chỉ ràng buộc nội dung, không tự xác thực người tạo; phải xác minh nguồn manifest/quyền ghi của kênh vận hành. Task plan M6 khóa định dạng, owner và cách bảo đảm artifact không bị thay giữa kiểm tra và restore; không nhận manifest do người gọi tùy ý khai TenantId.

Protocol mục 5.3 phải thực hiện hai cổng:

1. Trước thay DB: xác minh manifest/backup set và mọi phần của chuỗi phục hồi thuộc TenantId đích; xác minh artifact theo manifest và DB đích qua registry hiện hành. Từ chối platform DB, DB thuộc tenant khác, nguồn không rõ hoặc chuỗi không khớp. Đổi tên backup Alpha thành Delta không làm nó hợp lệ; sai ở cổng này không được chạy bước ghi đè DB.
2. Sau restore, khi ứng dụng vẫn cách ly: đọc định danh trong DB bằng đường kiểm tra vận hành tối thiểu, đối chiếu với registry và manifest trước migrate/seed, apply quyền hoặc warm-up ứng dụng. Thiếu/sai định danh thì giữ đóng, ghi audit và dừng recovery; không tự thay TenantId hoặc báo thành công. Restore đúng tenant sang vị trí DB mới chỉ được phép khi đích đã được xác minh/ủy quyền trong operation; tên vật lý không là danh tính tenant.

Bản ghi định danh và manifest bảo vệ lỗi chọn nhầm/đánh tráo trong phạm vi trust model vận hành; không tuyên bố chống DBA có toàn quyền sửa mọi nguồn tin cậy. Backup thiếu định danh không tự được hỗ trợ bằng cách seed lại; giữ đóng để review quy trình riêng. M0 khóa hợp đồng/owner; M1 kiểm tra định danh và readiness; M6 kiểm chứng backup/restore SQL thật.

Nghiệm thu: backup Alpha chọn cho Delta dù cùng schema phải bị từ chối; manifest sai/không đáng tin, artifact bị đổi và chuỗi backup lẫn tenant bị chặn trước ghi đè. Dùng fixture lỗi để kiểm tra cổng sau restore khi định danh thiếu/sai vẫn không apply quyền/migrate/seed hoặc mở traffic. Có ca đúng tenant thành công và tenant khác không bị ảnh hưởng; không coi chỉ kiểm tra tên DB là đủ.

<a id="r3-02-lifecycle-coordination"></a>
### 5.5. Lifecycle không bị workflow vận hành ghi đè — R3-02

V1 tách subscription state/ExpiresAt khỏi operation state. `TenantOperational` chỉ đúng khi subscription là Active, chưa hết hạn, không còn operation/recovery gate chặn, và readiness đạt. Hoàn tất operation chỉ kết thúc gate do chính operation sở hữu; không tự unsuspend, hủy PendingDeletion hoặc gia hạn. Tenant đã purge giữ dấu vết định danh tại platform và không được worker cũ tạo/mở lại.

Platform sở hữu **RegistryVersion chung theo TenantId**, bao phủ các quyết định subscription, ExpiresAt, entitlement/plan, security generation và operation owner/state/fencing liên quan đến mở phục vụ. Mọi đường thay đổi, kể cả CLI/recovery/release runner, phải kiểm tra và tăng version trong transaction platform cùng audit; nếu dữ liệu trải nhiều bảng thì vẫn cập nhật version của cùng aggregate trong transaction đó. Version này khác revision của Order; version gate tenant phải được đối soát theo operation, không dùng riêng nó thay cho lifecycle version. EF concurrency token chỉ so sánh version khi cấu hình/thực hiện update phù hợp; ứng dụng vẫn phải xử lý xung đột theo hợp đồng này. [15]

Provision, đổi gói, migration/release, restore và purge dùng cùng cơ chế sở hữu/khóa điều phối theo TenantId cho các bước thay đổi tài nguyên; không được mỗi workflow giữ một lock riêng rồi chạy phá nhau. Checkpoint và fencing chống worker cũ; bước không hỗ trợ conditional-write phải thực sự được bảo đảm độc quyền như mục 5.2. Đặc biệt purge chỉ phá hủy sau khi worker đang thay DB/feature đã dừng hoặc được xác minh không còn khả năng ghi; hết lease hay tăng version không tự chứng minh điều đó. M1 đặt owner/contract và dùng cho provision; M3 mở rộng cùng cơ chế cho lifecycle/plan/recovery fixture; M6 tích hợp release/restore, không tạo owner cạnh tranh.

Suspend/PendingDeletion là quyết định chặn độc lập, được commit bằng transaction platform ngắn có kiểm tra version và audit dù operation dài đang chạy; không đợi toàn bộ restore/đổi gói mới khóa truy cập. Chúng không tự giải phóng lock tài nguyên của worker. Worker có bước không thể ngắt an toàn phải kết thúc/cách ly bước đó theo runbook nhưng không chuyển tiếp hoặc mở traffic bằng trạng thái cũ. Quy tắc request đã authorization trước thu hồi ở mục 4.4 vẫn giữ; đây không phải lời hứa hủy hồi tố mọi transaction.

Bước cuối hoặc resume muốn mở phục vụ phải cập nhật có điều kiện theo TenantId + operation id + owner/fencing + RegistryVersion đã đối soát, đồng thời kiểm tra subscription hiện hành, ExpiresAt và toàn bộ readiness/recovery gates. Xung đột hoặc có quyết định chặn mới thì giữ tenant không phục vụ, lưu trạng thái cần đối soát; không chỉ nạp version mới rồi thử ghi Active lại. Unsuspend/gia hạn phải là command riêng có quyền, audit và kiểm tra gate còn lại; hoàn tất command đó cũng không bỏ gate của restore đang dở. PendingDeletion không được operation tự hoàn tác. Recovery sau xung đột phải được authorization lại và đối soát checkpoint/thực trạng trước quyết định tiếp tục.

M0 khóa các bất biến và phạm vi version/lock; primitive SQL, timeout và đường xử lý bước không thể ngắt được khóa ở task plan milestone sở hữu trước triển khai. M3 phải test: dừng đổi gói ngay trước mở registry → commit Suspend hoặc PendingDeletion → chạy tiếp/restart/retry → UI/API/grant vẫn bị chặn, quyết định lifecycle và audit không mất. Kiểm tra cả nhánh từ chối downgrade mở lại gói cũ, hết hạn trong operation, unsuspend khi recovery chưa xong và worker cũ sau purge. M6 chạy lại race mở registry với restore/release thật và kiểm tra hai workflow thay tài nguyên không chạy chồng.

<a id="r3-03-restore-obligations"></a>
### 5.6. Khôi phục nghĩa vụ duyệt khi platform đang Standard — R3-03

Ca bắt buộc: `backup lúc PendingApproval → Approve → downgrade Standard → restore backup`. Platform hiện hành vẫn Standard nhưng dữ liệu phục hồi có `ApprovalRequired = true AND Status != Confirmed`. Khớp schema/feature với Standard không đủ để mở tenant; cũng không được lấy subscription trong backup làm nguồn hiện hành.

Sau khi đạt cổng danh tính mục 5.4 và chuẩn bị schema đọc được, recovery phải gọi contract đọc nghĩa vụ do Orders cung cấp, dùng cùng định nghĩa ở mục 6.2, khi cả registry/write gate còn đóng. Contract/adapter recovery phải dùng được khi feature Approvals đang tắt, không cần mở endpoint nghiệp vụ; thiếu reader/schema/marker hoặc lỗi nguồn trả kết quả không xác định và giữ đóng, không coi là không có nghĩa vụ. Platform chỉ gọi abstraction được host ghép, không tham chiếu implementation Orders/Approvals. M3 có contract/fixture; M4 cung cấp reader/model; M5 kiểm chứng với dữ liệu/feature thật; M6 mới ghép SQL restore.

V1 chọn nhánh recovery chính: **khôi phục gói Approval bằng quyết định hiện hành có phê duyệt trong khi tenant vẫn đóng**, không cấp ngoại lệ âm thầm cho endpoint Approvals ở Standard. Người phê duyệt recovery và người có quyền đổi gói tại platform phải cho phép target plan/entitlement mới; có thể cùng một người nếu có đủ quyền, nhưng quyền restore riêng không mặc nhiên có quyền đổi gói. Ghi actor, phê duyệt, lý do, plan/version cũ-mới và operation id vào audit/checkpoint platform.

Đổi gói recovery là bước con của operation restore đang sở hữu cùng lock/fencing, không khởi chạy operation đổi gói độc lập tranh lock hoặc yêu cầu tenant phải Active. Tái dùng các bước migrate → feature/profile → entitlement → đối soát của mục 5.2 khi gate đóng; bước con **không** kết thúc gate restore hoặc tự mở registry. Lỗi/crash resume theo checkpoint và mục 5.5, không direct-toggle để bỏ orchestration. Chỉ sau khi capability Approval sẵn sàng, quyền đã xác lập lại theo mục 5.3 và mọi gate đạt mới được kết thúc recovery có điều kiện; nếu Suspended/PendingDeletion hoặc hết hạn thì vẫn không phục vụ.

Sau mở hợp lệ, người dùng có quyền dùng grant/session thế hệ mới xử lý các đơn qua Approve/Reject bình thường; recovery không tự duyệt, xóa marker, xóa đơn hoặc sửa lịch sử để hết nghĩa vụ. Khi hết nghĩa vụ mới được downgrade theo mục 5.2/6.2. Nếu không được phê duyệt gói Approval, giữ đóng; operator chỉ được chọn một điểm phục hồi khác cùng tenant đã xác minh, có phê duyệt phạm vi mất dữ liệu và thực hiện lại các cổng. Không tự chọn backup khác hoặc mở Standard với đơn mắc kẹt. Chưa có quyết định recovery thì trạng thái là BLOCKED, không phải restore thành công.

M0 ghi lựa chọn/quyền phê duyệt; M3–M5 kiểm tra contract và bước con bằng fixture/dữ liệu nghiệp vụ, không nhận là đã restore SQL. Nghiệm thu M6 chạy đầy đủ chuỗi trên: trước phê duyệt không mở; sau phê duyệt phục hồi Approval đúng quyền, đơn vẫn chờ duyệt và xử lý được; hoàn tất nghĩa vụ rồi mới downgrade. Bao gồm Rejected/Draft làm lại, thiếu quyền phê duyệt/reader, lỗi audit, crash từng bước con và Suspend/PendingDeletion xen giữa; không mở gate sớm, không bỏ duyệt hoặc ảnh hưởng tenant khác.

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
| Submit Draft, gói Standard | Chỉ chuyển Confirmed theo policy khi `ApprovalRequired = false`; không sinh approval giả. Còn nghĩa vụ duyệt mà gói/handler không hỗ trợ thì từ chối theo mục 6.2. |
| Submit Draft, gói Approval | Ghi `ApprovalRequired = true`, chuyển PendingApproval và tạo yêu cầu duyệt gắn đúng revision, cùng transaction. Nếu handler/schema Approvals chưa sẵn sàng thì từ chối, không fallback thành Standard. |
| PendingApproval | Không sửa nội dung đơn; Approve/Reject phải khớp revision và concurrency token. Người tạo không được tự duyệt. |
| Approve/Reject | Approve chuyển Confirmed; Reject chuyển Rejected và bắt buộc lý do. Chỉ một quyết định có hiệu lực cho mỗi request/revision. |
| Sửa đơn Rejected/gửi lại | Đưa về Draft với revision mới, giữ `ApprovalRequired = true` và lịch sử quyết định/revision cũ; Submit lại phải tạo yêu cầu duyệt mới, không xác nhận trực tiếp do đổi gói. |
| Confirmed | Không sửa/gửi lại trong V1; hủy hoặc sửa sau xác nhận là phạm vi bổ sung cần review. |

Các hành vi trên là lựa chọn xử lý R06 trong revision này, không phải default Orchard. M4 có thể chứng minh extension bằng fixture; không mở luồng Approval thật trước M5. Orders chỉ phụ thuộc contract của extension, không tham chiếu implementation OrderApprovals.

Idempotency cho command được phân vùng theo TenantId + actor/client + operation + key; fingerprint bao gồm resource, payload chuẩn hóa và revision kỳ vọng. Ghi kết quả/dedup record cùng transaction nghiệp vụ/audit. Cùng key và fingerprint trả lại kết quả đã commit, không tạo side effect lần hai; cùng key khác fingerprint trả conflict (409 ở API nghiệp vụ). Hai request cùng key được serialize bằng ràng buộc/transaction; lỗi giữa chừng không để completed record giả. Retry sau mất response phải trả được kết quả đã commit. Luôn authorization lại trước khi trả replay theo mục 6.3; key không cấp quyền đọc kết quả cũ. Không chạy lại precondition trạng thái/revision của command mới khi đã tìm thấy kết quả committed khớp key/fingerprint. TTL/retention và quy tắc chuẩn hóa được ghi trong task plan M4.

Nghiệm thu M4/M5: lỗi audit rollback; cùng key khác payload; request trùng/đồng thời/mất response; xung đột revision; sửa đơn chờ bị chặn; duyệt revision cũ bị chặn; reject/sửa/gửi lại giữ lịch sử; lỗi giữa Order/Approval/Audit rollback toàn bộ, retry không nhân đôi dữ liệu.

<a id="r2-02-approval-obligation"></a>
### 6.2. Nghĩa vụ duyệt của đơn cũ khi đổi gói — R2-02

V1 chọn **giữ nghĩa vụ duyệt**, không cho downgrade biến đơn từng bị Reject thành Confirmed không qua duyệt. Orders sở hữu marker bền vững `ApprovalRequired`: server đặt true trong transaction Submit đầu tiên cần duyệt; client không được ghi/xóa marker. Reject, sửa nội dung, tăng revision và gửi lại đều giữ true. Sau Approve, giữ marker làm lịch sử; trạng thái Confirmed cho biết nghĩa vụ đã hoàn tất. Draft chưa từng Submit cần duyệt không tự mang nghĩa vụ chỉ vì được tạo khi tenant ở gói Approval; lúc Submit vẫn kiểm tra gói hiện hành và marker.

Điều kiện Submit cần duyệt là **gói hiện hành yêu cầu duyệt OR marker true**. Chỉ Standard với marker false mới xác nhận trực tiếp. Nếu còn nghĩa vụ mà entitlement/feature/handler/schema không hỗ trợ, từ chối và yêu cầu khôi phục cấu hình hợp lệ; không bật lén endpoint Approvals ở Standard và không fallback Confirmed. Dữ liệu thiếu marker hoặc không xác định được nghĩa vụ phải được migration/recovery kiểm tra trước khi mở, không tự mặc định false cho lịch sử cũ.

Guard downgrade trong mục 5.2 kiểm tra `ApprovalRequired = true AND Status != Confirmed`, bao gồm PendingApproval, Rejected và Draft làm lại; trạng thái không nhận diện được giữ đóng để xử lý. Predicate nghiệp vụ đi qua extension contract, không tạo dependency từ platform sang implementation Orders/Approvals. Cùng định nghĩa được reader recovery dùng để đối soát entitlement sau restore theo mục 5.6, kể cả khi Approvals đang tắt; không giới hạn kiểm tra nghĩa vụ vào thao tác downgrade. Việc kiểm tra diễn ra sau drain/đóng durable gate như protocol hiện có; Reject hoặc sửa Rejected thành Draft không làm nghĩa vụ biến mất giữa các bước.

V1 chưa có thao tác hủy/đóng đơn chưa duyệt: một đơn Rejected không được sửa/gửi lại và duyệt xong có thể tiếp tục chặn downgrade. Đây là đánh đổi có chủ ý của lựa chọn bảo thủ này; thêm đường kết thúc khác cần review phạm vi/state machine, không dùng xóa đơn, sửa marker hoặc tắt module để né guard. Khi không còn nghĩa vụ, downgrade được phép; các đơn mới hợp lệ ở Standard vẫn Confirmed trực tiếp và lịch sử duyệt cũ được giữ.

M0 ghi lựa chọn vào decision register; M3 có guard contract/fixture; M4 khóa model/ownership/state machine trước implementation; M5 kiểm chứng module thật. Nghiệm thu gồm cả `Rejected → downgrade → sửa → Submit` (downgrade bị chặn, gói cũ được mở lại) và `Rejected → sửa thành Draft → downgrade → Submit` (vẫn bị chặn). Sau gửi lại/Approve đúng revision, downgrade mới được phép. Kiểm tra giả mạo marker, race downgrade với Reject/sửa/gửi lại, mất handler và không mất lịch sử.

<a id="r2-03-replay-policy"></a>
### 6.3. Authorization của replay và precondition thực thi mới — R2-03

Replay là đọc lại kết quả đã commit trong thời hạn lưu dedup, không phải thực thi lại command. Policy replay bắt buộc có quyền hành động tương ứng còn hiệu lực **và** quyền truy cập tài nguyên/kết quả hiện hành (own/others, giới hạn người tạo và Deny nếu áp dụng). Không dùng trạng thái PendingApproval/Draft hoặc expected revision của lần thực thi mới làm điều kiện bảo mật để đọc kết quả cũ. Task plan M4 khóa mapping policy cho từng command trước implementation, không dùng một policy bỏ qua mọi resource rule.

Thứ tự xử lý bắt buộc:

1. Xác thực và kiểm tra account/session/grant, tenant/generation, membership, feature/entitlement và nguồn quyền hiện hành như mục 4; lỗi nguồn vẫn fail closed. Tra cứu dedup chỉ trong partition TenantId + actor/client + operation + key của request đã xác thực.
2. Nếu có kết quả committed, dùng định danh tài nguyên đã lưu để kiểm tra policy replay hiện hành trước khi tiết lộ kết quả hoặc xung đột fingerprint. Cùng fingerprint thì trả kết quả đã commit, không chạy lại chuyển trạng thái, kiểm tra optimistic concurrency hay ghi audit thành công lần hai. Fingerprint được tính từ request gốc chuẩn hóa, bao gồm expected revision đã gửi; không thay bằng revision mới của tài nguyên.
3. Cùng key nhưng khác fingerprint chỉ trả conflict cho người đã qua policy tương ứng; key không tiết lộ tài nguyên/kết quả cho user/tenant khác. Quyền bị thu hồi, Deny mới hoặc tenant bị chặn phải từ chối replay, kể cả khi command trước đây đã thành công. Log truy cập/replay riêng không phải side effect nghiệp vụ lần hai.
4. Nếu chưa có kết quả committed, áp dụng authorization của command mới và toàn bộ precondition trạng thái, expected revision, business rule cùng durable write gate trong transaction. Ràng buộc unique/serialization vẫn chống hai lần thực thi. Khi đua cùng key làm xuất hiện kết quả committed, quay về nhánh replay và kiểm tra quyền lại; không biến xung đột revision hậu-commit thành lỗi giả của request đã thành công.

Nghiệm thu M4/M5: Submit/Approve commit, response bị mất, trạng thái và revision đã đổi → gửi lại đúng key/payload/expected revision cũ vẫn nhận kết quả đã lưu, chỉ một side effect và một audit nghiệp vụ thành công. Cùng trường hợp nhưng quyền hành động/quyền xem bị thu hồi hoặc có Deny mới → từ chối. Dùng key mới với revision cũ vẫn phải conflict theo precondition bình thường. Bao gồm hai request đồng thời, khác actor/tenant, khác payload và lỗi nguồn quyền; M4 dùng fixture Approvals khi cần, M5 chạy Approve thật. TTL/retention không được ngầm hiểu là bảo đảm replay vô hạn.

## 7. Roadmap theo milestone

### M0 — Khóa thiết kế, môi trường và hợp đồng

**Phụ thuộc:** Không.

**Đầu ra:** ADR cho identity/membership, deny policy, database ownership, token/refresh, lifecycle; trust model CMS; bootstrap/quyền SQL/readiness; protocol đổi gói; hợp đồng restore/xác lập lại quyền mục 5.3; danh tính DB/manifest backup mục 5.4, lifecycle/operation và RegistryVersion chung mục 5.5, nhánh recovery gói có phê duyệt mục 5.6; lựa chọn giữ nghĩa vụ duyệt mục 6.2 và cổng replay mục 6.3; decision register có owner/gate; solution skeleton và dependency rules; khóa SDK/packages/provider/tools; AGENTS.md và conventions; scripts thiết lập/check; CI build/test ban đầu. Identity ghi Conditional đến PoC đầu M2, không đánh dấu Accepted chỉ vì restore thành công.

**Nghiệm thu:** Clone repository mới, chạy theo README bằng dotnet CLI và pwsh; build/test không cần Visual Studio đầy đủ; không có secret trong Git. Hoàn thành ma trận và lệnh tại mục 7.1, SQL Server smoke thật, architecture checks và báo cáo command/exit code/test counts. Ghi rõ mọi quyết định bổ sung được review trước khi agent triển khai; không bỏ các điều kiện R01–R08, R2-01–R2-03, R3-01–R3-03 hoặc biến NOT RUN thành PASS.

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

**Đầu ra:** Một host, Default/platform DB và bốn customer DB; registry với subscription/operation tách biệt và contract điều phối mục 5.5; TenantId bất biến và bản ghi định danh DB do Tenancy sở hữu mục 5.4; shell prefix; recipe allowlist và migration baseline; bootstrap/provision được bảo vệ, lock/checkpoint/audit vận hành tối thiểu và readiness gate theo mục 5.1; công cụ provision chạy lại được. Không đợi UI M3 để bảo vệ thao tác vận hành.

**Nghiệm thu:** Bốn prefix vào đúng bốn DB; prefix không tồn tại trả lỗi có kiểm soát, không rơi vào nghiệp vụ Default; trùng slug bị chặn; prefix hệ thống như connect/admin/api được dành riêng. Tài khoản DB tenant không đọc được DB tenant khác. Readiness từ chối DB thiếu/sai TenantId; provision retry không ghi đè định danh đã có, runtime không sửa/xóa được bản ghi định danh. Test cache key và link có prefix. Đạt negative tests CMS/bootstrap/readiness ở mục 3.3 và 5.1; kiểm tra dependency graph ngay từ project references đầu tiên, không chờ M7.

### M2 — Identity dùng chung và API token thực tế

**Phụ thuộc:** M1.

**Đầu ra:** PoC liên shell ở mục 4.1 là task đầu tiên và gate trước khi mở rộng identity; cập nhật ADR theo kết quả. Sau đó central login, SSO giữa tenant, mapping subject, memberships, chọn tenant; JWT access token và refresh token; API thử nghiệm được bảo vệ; session/grant generation và validation/revocation liên shell theo mục 4.4.

**Nghiệm thu:** Một user vào Alpha/Beta nhưng không vào Gamma; hai tab hai tenant không bị đổi context chéo; token Alpha không gọi API Beta dù user có membership cả hai; refresh không đổi tenant; token sai issuer/audience/expired bị từ chối; refresh reuse và logout có test. Account không được link chỉ vì trùng email. PoC có bằng chứng đọc đúng issuer store; đạt ca thu hồi UI/API, refresh đồng thời/replay/mất response, lỗi nguồn và remove/re-add ở mục 4.4; không dùng token expiry thay cho kiểm tra trạng thái server.

### M3 — Authorization, lifecycle và audit nền tảng

**Phụ thuộc:** M2.

**Đầu ra:** Roles theo tenant; allow/deny/unspecified; per-user override; resource policy; hạn dùng/khóa/gói; UI vận hành tối thiểu; protocol và durable write gate đổi gói ở mục 5.2; điều phối chung/lifecycle version mục 5.5; guard nghĩa vụ duyệt qua contract/fixture mục 6.2 và reader recovery độc lập entitlement mục 5.6; cổng security revalidation, recovery authority và apply contract mục 5.3 (chưa diễn tập SQL restore); audit service/owner/transaction contract đủ dùng trước M4. Hiện thực coordinator tại đây nếu consumer đầu tiên đã cần nhiều context; không dời bắt buộc sang M5.

**Nghiệm thu:** Deny thắng mọi Allow; không thể nâng quyền qua API quản lý role; hết hạn, suspended hoặc membership revoked bị chặn dù JWT chưa hết hạn; tenant khác của cùng user vẫn dùng được. Xóa mềm/purge trên dữ liệu giả không xóa global account hoặc DB khác. Hoàn thiện kiểm tra ở cả UI và API, lỗi nguồn quyền/cache và phạm vi revoke theo mục 4.4. Test protocol đổi gói với fixture, crash/retry/gate/version; không cho direct toggle bỏ guard. Audit platform M1 giữ cùng migration owner. Fixture restore/security revalidation kiểm tra đóng mặc định, credential mới không dùng ACL cũ, quyền recovery không lấy từ tenant đã restore và lỗi apply/audit giữ đóng. Đạt race Suspend/PendingDeletion với bước mở registry/resume, expiry trong operation và khóa dùng chung mục 5.5. Fixture mục 5.6 chứng minh nhánh con đổi gói có phê duyệt khi restore đang đóng, reader lỗi giữ đóng và bước con không tự mở tenant.

### M4 — Orders xuyên suốt

**Phụ thuộc:** M3.

**Đầu ra:** State machine, revision, Submit Standard/Approval, marker nghĩa vụ duyệt, policy replay và extension contracts theo mục 6.1–6.3 được khóa trước implementation; reader nghĩa vụ phục vụ recovery mục 5.6 ngay cả khi Approvals tắt; Orders module, OrdersDbContext/migrations, application service, API version 1, màn hình riêng, audit và optimistic concurrency. Coordinator sẵn sàng trước consumer đa context đầu tiên nếu M3 chưa cần.

**Nghiệm thu:** Tạo/sửa nháp/xem/gửi đơn đúng quyền; phân biệt tài nguyên của mình/người khác; dữ liệu và audit commit cùng nhau; xung đột cập nhật không bị ghi đè âm thầm; UI/API cho cùng kết quả. Orders có extension point để Approvals tham gia mà không phụ thuộc implementation của Approvals. Đạt các ca audit rollback, idempotency khác payload/đồng thời/mất response và bất biến revision ở mục 6.1; không cho fallback Confirmed nếu gói hoặc đơn yêu cầu Approval nhưng handler thiếu. Kiểm tra marker được giữ qua Rejected/Draft; replay hậu-commit không chạy lại precondition, nhưng vẫn bị chặn sau thu hồi quyền theo mục 6.3.

### M5 — OrderApprovals và tính tùy chọn

**Phụ thuộc:** M4.

**Đầu ra:** ApprovalRequests/decisions, migrations riêng nếu có context riêng, extension vào Orders, một cấp duyệt, kiểm tra người tạo, lý do từ chối; dùng/mở rộng coordinator từ consumer đầu tiên khi cần, không xây trùng. Ghép protocol đổi gói M3 với Orders/Approvals thật; ghép reader nghĩa vụ và bước con recovery mục 5.6 bằng dữ liệu nghiệp vụ, chưa thực hiện SQL restore.

**Nghiệm thu:** Tenant Approval không xác nhận đơn khi chưa được duyệt; không tự duyệt; hai request đồng thời chỉ một quyết định có hiệu lực; lỗi giữa chừng rollback Order/Approval/Audit. Standard không có endpoint/menu duyệt hoạt động. Tắt feature không xóa dữ liệu và không bỏ qua đơn đang chờ. Race downgrade với Submit/Approve và request đang đợi gate, crash ở từng checkpoint, worker cũ và retry phải đạt mục 5.2; reject/sửa/gửi lại và duyệt đúng revision đạt mục 6.1–6.2. Hai thứ tự Rejected/downgrade/sửa và Rejected/sửa/downgrade đều không né nghĩa vụ duyệt; replay Approve thật hậu-commit/thu hồi quyền đạt mục 6.3. Dữ liệu fixture có nghĩa vụ khi Approvals tắt vẫn được reader nhận diện; recovery upgrade có phê duyệt không mở gate cha và sau mở hợp lệ vẫn phải duyệt đúng revision.

### M6 — Release và migration có kiểm soát

**Phụ thuộc:** M5; CI và kiểm thử migration đã được phát triển dần từ các milestone trước.

**Đầu ra:** Build artifact/bundles, release manifest theo context/module, script pwsh chạy theo tenant, maintenance gate, lock/retry/report, warm-up Orchard, smoke tests, manifest backup đáng tin cậy và backup/restore runbook tích hợp mục 5.3–5.6.

**Nghiệm thu:** Database trống và database từ bản trước đều nâng cấp được; áp dụng lại không tạo trùng schema; cố ý làm lỗi một tenant có trạng thái rõ ràng và tiếp tục đúng bước. Kiểm tra cả EF và Orchard, không chỉ HTTP 200. Feature chưa được mở trước khi schema sẵn sàng; release runner kế thừa bootstrap/readiness/đổi gói đã có từ M1/M3, không thay bằng đường tắt thiếu guard. Diễn tập restore một tenant không làm mất tenant khác; thực hiện protocol mục 5.3 với platform hiện hành ngoài phạm vi restore. Backup → thu hồi quyền nhưng giữ membership → restore → xác lập bộ quyền mới và đăng nhập mới vẫn không hồi sinh quyền cũ; kiểm tra riêng role assignment, role permission, override/Deny và crash/checkpoint. Không coi chỉ thu hồi token cũ hoặc bắt login lại là đã đạt.

Đạt cổng nguồn/đích trước restore và định danh sau restore mục 5.4: backup Alpha cho Delta cùng schema, manifest/artifact/chuỗi sai phải bị chặn; định danh thiếu/sai không được seed lại hoặc apply quyền. Đạt race Suspend/PendingDeletion với bước mở registry của restore/release và ngăn workflow thay tài nguyên chạy chồng theo mục 5.5. Chạy `backup PendingApproval → Approve → downgrade Standard → restore` và nhánh recovery gói có phê duyệt mục 5.6: trước phê duyệt đóng, sau mở hợp lệ đơn vẫn cần duyệt và xử lý được; lỗi/crash/thiếu nguồn hoặc quyết định lifecycle mới không mở gate sớm. Test cả đúng nguồn và sai nguồn, không chỉ happy path.

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

Các ca lỗi phải có: đổi URL/token/body TenantId; feature bị tắt nhưng gọi API trực tiếp; session/role/membership bị thu hồi; cache không phân tách tenant; audit lỗi; migration lỗi; retry refresh; hai tab khác tenant; restore làm xuất hiện dữ liệu quyền cũ; xóa tenant không xóa tài khoản dùng chung. Bổ sung ca CMS/template/upload bị cấm, bootstrap anonymous, readiness lỗi, mất nguồn quyền, refresh đồng thời/mất response, membership remove/re-add, token cũ sau gia hạn/mở lại, downgrade đua Submit/Approve, crash/retry/version cũ và replay idempotency khác payload theo các hợp đồng R01–R06. Vòng 2 bổ sung restore rồi đăng nhập mới sau thu hồi quyền với membership còn Active; recovery không tin ACL/audit đã restore; hai thứ tự Rejected/sửa/downgrade; replay sau commit đổi trạng thái/revision và replay sau thu hồi quyền theo mục 5.3, 6.2–6.3.

Vòng 3 bổ sung backup sai tenant dù cùng schema; manifest/artifact/chuỗi và định danh DB không khớp; Suspend/PendingDeletion xen trước bước mở registry, restart/worker cũ/purge; restore dữ liệu còn nghĩa vụ dưới gói Standard và recovery upgrade có phê duyệt khi gate cha vẫn đóng. Phân biệt fixture M1/M3–M5 với SQL restore/release thật M6; giữ các ca quyền và replay của vòng trước.

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

Task plan và decision register M0 phải mang theo owner, trạng thái, cổng nghiệm thu và bằng chứng còn thiếu của R01–R08 trong [bảng xử lý vòng 1](../reviews/2026-11-06-roadmap-v1-remediation.md), R2-01–R2-03 trong [bảng xử lý vòng 2](../reviews/2026-11-06-roadmap-v1-round2-remediation.md) và R3-01–R3-03 trong [bảng xử lý vòng 3](../reviews/2026-11-06-roadmap-v1-round3-remediation.md). `Documented` chỉ nghĩa là yêu cầu đã được bổ sung; chỉ evidence tại milestone sở hữu mới đóng verification. Chủ dự án giữ quyền review/merge; implementer không tự nâng Conditional thành Accepted hoặc bỏ negative test để có CI xanh.

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
13. SQL Server restore về thời điểm trước: https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/restore-a-sql-server-database-to-a-point-in-time-full-recovery-model
14. SQL Server restore sang tên/vị trí mới: https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/restore-a-database-to-a-new-location-sql-server
15. EF Core concurrency conflicts: https://learn.microsoft.com/en-us/ef/core/saving/concurrency
