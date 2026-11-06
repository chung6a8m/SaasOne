# Review roadmap SaaS Orchard Core V1

Ngày review: 2026-11-06 (+07:00).
Trạng thái: REVIEWED — có đề xuất cần chủ dự án quyết định; chưa phê duyệt thay chủ dự án, chưa triển khai M0.

## 1. Baseline và giới hạn bằng chứng

Tài liệu được review: [saas-orchard-roadmap-v1.md](../roadmap/saas-orchard-roadmap-v1.md), giữ nguyên bản người dùng cung cấp.

| Hạng mục | Bằng chứng |
|---|---|
| Repository | `chung6a8m/SaasOne` |
| Commit nhập roadmap | `612449184edb7fa845c9fee7eb11c56ca5fa1ff3` |
| Đường dẫn | `docs/roadmap/saas-orchard-roadmap-v1.md` |
| Git blob SHA-1 | `677e4adee8b2b2e1ae483642d448f2054f5482e9` |
| SHA-256 của tệp gốc | `4b1a82479552025115dbdadb7fbe8e062763b38f6e925e83088a557ecacf098b` |
| Kích thước | 25.529 byte; UTF-8 không BOM; LF |
| Kiểm tra nhập tệp | GitHub trả về blob SHA và kích thước khớp tệp được cung cấp |

Các số dòng bên dưới trỏ vào baseline trên. Đây là review tài liệu, không phải review source code hoặc kiểm thử khả năng chạy. Những rủi ro có điều kiện được ghi rõ, không được hiểu là lỗ hổng đã tái hiện.

Tách ba loại thông tin: **Tài liệu gốc** là yêu cầu/đề xuất hiện có; **Đối chiếu kỹ thuật** là thông tin từ nguồn chính thức ở mục 7; **Đề xuất reviewer** chưa phải quyết định được chấp thuận. Các mặc định của mục 2 trong roadmap vẫn là đề xuất, đúng trạng thái của tài liệu gốc.

## 2. Kết luận

**Đề nghị giữ hướng kiến trúc và dùng roadmap làm baseline để lập task plan M0. Chưa coi thiết kế đã khóa hoặc M0 đã hoàn thành.**

Không có căn cứ từ review này để đổi framework, bỏ database-per-tenant, chuyển sang microservices hoặc hạ .NET 10. Những phần còn mở cần có owner, milestone và bằng chứng đóng việc; không phải tất cả đều phải triển khai ngay trong M0.

Các điểm nên giữ: TenantId ổn định; database nghiệp vụ được chọn từ shell đã định tuyến; mapping identity bằng issuer + subject; Deny thắng Allow; kiểm tra quyền tại UI/API; mỗi bảng một migration owner; audit cùng transaction nghiệp vụ; không giả định JWT tự bị thu hồi; không dùng distributed transaction cho provision; ma trận bốn tenant và giới hạn production rõ ràng. Căn cứ: roadmap mục 3–8, dòng 45–244.

**Đối chiếu phiên bản:** NuGet công bố `OrchardCore.Application.Cms.Targets 3.0.1` target `net10.0`; release notes 3.0.0 xác nhận yêu cầu .NET 10 [E1][E2]. Đây là ứng viên để kiểm chứng ở M0, không phải lockfile đã được tạo hoặc bằng chứng toàn bộ dependency graph tương thích. Chưa khóa SDK patch, EF patch, YesSql/OpenIddict transitive, SQL Server, hệ điều hành hay CI.

## 3. Findings và thời điểm phải xử lý

P1 = rủi ro kiến trúc/bảo mật/tính đúng đắn cần đóng trước cổng được chỉ định. P2 = hợp đồng hoặc tiêu chí nghiệm thu cần bổ sung ở milestone sở hữu. Không gắn nhãn P0 cho một lỗi chạy thực tế khi chưa có source để kiểm thử.

| ID | Mức | Nội dung | Cổng xử lý đề xuất |
|---|---|---|---|
| R01 | P1, có điều kiện | Chưa nêu trust model cho CMS và nội dung chạy trên cùng origin | ADR M0; kiểm tra cấu hình/role ở M1–M3 trước khi mở chức năng CMS cho tenant |
| R02 | P1 | Quyết định identity ở M0 nhưng chứng minh validation/revocation liên shell tại M2 | Ghi rõ ADR có điều kiện ở M0; PoC là việc đầu tiên của M2, trước mở rộng identity |
| R03 | P1 | “Thu hồi ngay” chưa có hợp đồng về cache, session, request đang chạy và lỗi platform | Hợp đồng M0; token/session ở M2, quyền/lifecycle ở M3 |
| R04 | P1 | Bootstrap, quyền vận hành và schema gate cần có trước các thao tác của M1 | Thiết kế M0; bảo vệ tối thiểu M1; hoàn thiện UI ở M3, release runner ở M6 |
| R05 | P1 | Hạ gói/tắt feature có kiểm tra PendingApproval nhưng chưa xử lý race xuyên database | Protocol M0/M3; test giao tranh với Submit/Approve ở M5 |
| R06 | P2 | Audit transaction và hợp đồng Orders/Approvals cần rõ trước khi M4 phụ thuộc | Audit contract M3; state/idempotency/extension contract M4; đa context nếu cần M5 |
| R07 | P2 | M0 chưa có ma trận môi trường, lệnh nghiệm thu và danh sách test cụ thể | Task plan và evidence M0 |
| R08 | P2 | Mục tiêu tái sử dụng chỉ được chứng minh đầy đủ ở M7 | Dependency rule M0; kiểm tra cấu trúc từ M1; nghiệm thu product thứ hai M7 |

### R01 — Prefix URL không phải ranh giới bảo mật trình duyệt

**Tài liệu gốc:** Một host, Default cho identity/operations, tenant ở `/{tenant}/...`; dùng CMS và theme riêng; cookie phân shell (dòng 8, 18, 33–34, 84–88).

**Đối chiếu kỹ thuật:** Origin được xác định bởi scheme, host và port, không bởi đường dẫn [E3]. Suy ra `/alpha`, `/beta` và Default trên cùng origin không có cách ly trình duyệt chỉ nhờ prefix.

**Rủi ro có điều kiện:** Nếu khách hàng được xuất bản JavaScript/HTML thực thi tùy ý trên origin này, mã đó có thể hành động bằng phiên của người đang mở trang đối với các đường dẫn cùng origin. Database riêng, HttpOnly và cookie Path không biến các path thành các origin độc lập. Roadmap chưa nói khách hàng được quyền này, nên đây là thiếu trust model, không phải kết luận đang có đường khai thác.

**Đề xuất reviewer:** V1 giữ routing theo path, nhưng theme/script/module do đội nền tảng kiểm soát; không cấp tenant khả năng xuất bản nội dung thực thi tùy ý. Chốt quyền sửa template, nội dung HTML, uploads và chính sách xử lý nội dung. Nếu sau này cần nội dung không đáng tin cậy có khả năng thực thi, phải thiết kế lại ranh giới origin; không coi CSP là sự thay thế hoàn toàn cho quyết định đó.

**Bằng chứng đóng:** ADR ghi trust model và bề mặt CMS được phép; test cấu hình/role không cho tenant sửa template/script ngoài phạm vi đã duyệt. Không tự bật toàn bộ feature CMS trong recipe.

### R02 — Khóa identity phải đi kèm điều kiện kiểm chứng liên shell

**Tài liệu gốc:** M0 tạo ADR identity/token; M2 mới chứng minh central login, JWT, refresh và revocation liên shell (dòng 84–88, 117–126, 165–187).

**Đối chiếu kỹ thuật:** Orchard có cấu hình token validation dùng authorization server của tenant khác [E4]. OpenIddict token-entry validation cần truy cập đúng database lưu token của issuer; mặc định không kiểm tra trạng thái token ở mỗi API request [E5]. Cùng process không đủ để suy ra mọi shell đang dùng đúng store.

**Nhận xét:** Hướng này có cơ sở framework, nhưng chưa có bằng chứng để coi phần tích hợp ứng dụng là đã hoàn tất. Không nên để agent khóa implementation theo suy đoán về DI/store chỉ vì đã chọn Default làm issuer.

**Đề xuất reviewer:** M0 khóa yêu cầu và phương án ứng viên, ghi ADR là có điều kiện. PoC hai tenant là task đầu tiên của M2, với quyền sửa ADR nếu bằng chứng bác bỏ phương án. Nếu cần chấp thuận identity ở trạng thái cuối ngay cuối M0, phải bổ sung một spike nhỏ sau skeleton; đây là thay đổi phạm vi đề xuất, chưa được thực hiện.

**Bằng chứng đóng:** User vào Alpha/Beta; token Alpha bị Beta từ chối; thu hồi token ở Default làm API mục tiêu từ chối; kiểm tra đọc đúng issuer store; cookie của hai shell không đổi context chéo; cùng email nhưng khác issuer/subject không tự link. Chốt JWT format/encryption, audience/scope, callback/logout URL và lựa chọn validation thực tế trên phiên bản đã khóa.

### R03 — Định nghĩa chính xác “thu hồi ngay” và phạm vi session

**Tài liệu gốc:** Xóa membership làm mất truy cập ngay; kiểm tra tenant/membership/session ở server; khóa Beta không khóa Alpha; có refresh rotation/reuse và logout (dòng 63, 119–128, 187, 195, 244).

**Nhận xét:** Yêu cầu đã đúng hướng, nhưng chưa phân biệt request mới với request đã qua authorization, cache permission với trạng thái nguồn, logout một tenant với logout toàn cục, và không có quyền với không đọc được dữ liệu quyền. Không thể lấy việc JWT hết hạn sau vài phút làm đáp ứng “ngay”.

**Đề xuất reviewer:** M0 chốt thời điểm có hiệu lực. Phương án V1 đơn giản: request mới thực hiện authorization sau khi lệnh thu hồi đã commit phải bị từ chối; hành vi với thao tác đang chạy phải được ghi riêng. Đọc trạng thái quyền có thẩm quyền cho từng request, hoặc dùng cache có cơ chế vô hiệu được chứng minh; không cho phép truy cập bằng cache Allow khi nguồn bắt buộc lỗi. Quyền động không chỉ dựa vào role claim cũ trong cookie/JWT. Ghi mã lỗi riêng cho dependency unavailable, không đánh đồng với 403.

Chốt định danh session/grant, TTL, quyền logout tenant/toàn cục, xử lý account lock, membership remove/re-add, refresh đồng thời, response refresh bị mất và phạm vi revoke khi phát hiện reuse. RFC 9700 mô tả rotation, giữ liên hệ giữa refresh token và thu hồi token đang hoạt động khi phát hiện replay [E6]; không được suy ra bật refresh là đã đáp ứng mọi tiêu chí ứng dụng.

**Bằng chứng đóng:** UI lẫn API đều bị chặn sau revoke; Alpha vẫn dùng được khi chỉ Beta bị khóa; đổi role/Deny có hiệu lực theo hợp đồng; lỗi platform không trở thành Allow; old grant không tự sống lại khi thêm lại membership; có test đồng thời và refresh replay. Restore phải được kiểm tra theo tiêu chí đã có ở M6, không hạ yêu cầu hiện tại.

### R04 — Bootstrap và schema gate phải tồn tại từ M1

**Tài liệu gốc:** M1 tạo registry, DB, shell, setup, seed, migrations; UI vận hành/audit ở M3; maintenance gate/lock/report/warm-up hoàn chỉnh ở M6 (dòng 136–143, 169–179, 193–195, 215–221). M6 đã nói kiểm thử migration được phát triển dần; nhận xét này không coi roadmap bỏ qua điều đó.

**Nhận xét:** Cần chỉ ra phần tối thiểu thuộc M1: ai được bootstrap Default, ai có thể tạo/drop DB, ai được bật feature hoặc chạy setup, và điều kiện để nhận traffic. Không chờ UI vận hành M3 mới bảo vệ công cụ provision.

**Đối chiếu kỹ thuật:** Orchard mô tả data migrations tự chạy lúc ứng dụng khởi động [E7]. Vì vậy, việc chuẩn bị schema và mở shell không thể chỉ là một yêu cầu trên giấy ở M6; cần kiểm tra startup/warm-up thực tế của package đã chọn. EF cũng yêu cầu review/test migration trước khi áp dụng production [E8].

**Đề xuất reviewer:** M0 định nghĩa bootstrap mode, nguồn secret, bảng quyền runtime/migrator/provisioner và ownership schema/table/migration history. M1 dùng CLI hoặc đường quản trị có bảo vệ, không để setup/provision công khai; ghi trạng thái bước và audit vận hành tối thiểu; chỉ mở truy cập khi schema/seed/health đạt. Cấm database đích là platform/tenant khác trong thao tác purge. Tách quyền SQL theo tác vụ và chứng minh quyền Orchard cần lúc warm-up; không tuyên bố runtime không cần DDL trước khi kiểm tra.

**Bằng chứng đóng:** Bootstrap chạy lại không tạo admin/tenant trùng; secret không đi vào Git/log; anonymous không gọi được setup/provision; tenant chưa sẵn sàng không nhận nghiệp vụ; failure để trạng thái đóng có thể tiếp tục; tài khoản SQL tenant không đọc database khác. Release runner đầy đủ vẫn thuộc M6.

### R05 — Hạ gói cần protocol chống race, không chỉ kiểm tra trước

**Tài liệu gốc:** Không hạ Approval xuống Standard khi còn PendingApproval; feature chỉ mở sau schema; platform và tenant khác DB; không dùng distributed transaction (dòng 39–41, 141, 147–149, 159–161, 211).

**Tình huống suy luận:** Tác vụ đổi gói kiểm tra chưa có pending; request Submit tạo PendingApproval ngay sau đó; tác vụ đổi gói tắt Approvals. Nếu chỉ kiểm tra một lần, điều cấm trong roadmap vẫn có thể bị vi phạm.

**Đề xuất reviewer:** Có trạng thái chuyển đổi và một cơ chế đồng bộ được xác định: chặn nghiệp vụ liên quan mới, đợi/giải quyết thao tác đang chạy, đọc lại điều kiện, cập nhật schema/feature/entitlement theo protocol có checkpoint, rồi mới mở lại. Khi thất bại phải giữ trạng thái đóng và retry có kiểm tra; không thêm distributed transaction chỉ để giải quyết bài toán này. Các tên trạng thái và primitive cụ thể thuộc task plan, chưa chốt trong review.

**Bằng chứng đóng:** Race downgrade với Submit/Approve không sinh đơn chờ không thể xử lý; lỗi giữa cập nhật platform và shell có trạng thái khôi phục rõ; bật/tắt feature trực tiếp qua đường quản trị không vượt qua guard. Tắt feature không xóa bảng, không làm mất lịch sử.

### R06 — Đưa hợp đồng audit/Orders đến trước nơi sử dụng

**Tài liệu gốc:** Audit service ở M3; Orders và audit phải commit cùng nhau ở M4; transaction coordinator được liệt kê ở M5; không bắt buộc mỗi module có DbContext riêng (dòng 155–161, 193, 201–211, 282).

**Nhận xét có điều kiện:** Nếu audit dùng context riêng từ M3, M4 đã cần phối hợp transaction; không thể đợi M5. Nếu chung context, chưa có lý do xây coordinator tổng quát ngay M0. EF yêu cầu chia sẻ cả connection và transaction khi dùng nhiều context; retry phải được thiết kế cùng transaction [E9].

**Đề xuất reviewer:** M3 chốt audit owner và transaction contract đủ cho M4. M4 định nghĩa state machine Orders, hành vi Submit của Standard/Approval, sửa đơn đang chờ, từ chối/gửi lại, người tạo không tự duyệt, concurrency và idempotency. Phạm vi idempotency nên phân biệt tenant, actor/client, operation và payload; ghi kết quả xử lý cùng transaction khi cần chống request lặp sau lỗi mạng. Đây là đề xuất hợp đồng, không phải bổ sung trạng thái nghiệp vụ đã được người dùng duyệt.

**Bằng chứng đóng:** Lỗi audit rollback đơn; retry không tạo trùng đơn/quyết định/audit; cùng idempotency key khác payload có kết quả xác định; revision của đơn được duyệt không thể âm thầm thay đổi. Orders phụ thuộc abstraction/extension point, không phụ thuộc implementation Approvals. M5 chứng minh atomic Order/Approval/Audit nếu có nhiều context.

### R07 — M0 cần nghiệm thu lặp lại được, không chỉ build xanh

**Tài liệu gốc:** Khóa phiên bản/môi trường ở M0; clone mới chạy bằng dotnet/pwsh; SQL Server thật cho integration; DoD có bằng chứng thực thi (dòng 43, 169–171, 288–292).

**Đề xuất reviewer:** Task plan ghi SDK exact + roll-forward policy, package graph/lock strategy, local dotnet tools, SQL Server edition/version và cách cấp instance, PowerShell/OS/runner được hỗ trợ. GitHub Actions là ứng viên hợp lý cho repo này nhưng chưa được chọn thay người dùng. Phân biệt CI cơ bản M0 với integration bắt buộc từ milestone có data change. Có lệnh fail rõ khi thiếu SQL Server thay vì skip rồi báo pass.

**Bằng chứng đóng:** Clone sạch, restore theo lock strategy, build, test với số test thực sự lớn hơn 0, script smoke và log command/exit code. Kiểm thử C# không được chỉ là một assertion vô nghĩa. Nếu SQL Server smoke được đưa vào M0, báo cáo riêng instance/version/kết quả; không dùng EF InMemory hoặc SQLite làm bằng chứng SQL Server. Đây là tiêu chí cho M0 tương lai, không phải kết quả đã chạy trong review này.

### R08 — Chứng minh dependency tái sử dụng sớm hơn nghiệm thu M7

**Tài liệu gốc:** Mục tiêu codebase nhiều sản phẩm; product thứ hai không phụ thuộc Orders ở M7 (dòng 6, 227–229, 249–261, 282).

**Đề xuất reviewer:** M0 ghi dependency rules: platform/abstractions không tham chiếu Orders/Approvals; host là composition root của sản phẩm; không đồng nhất feature disabled với assembly dependency absent. Kiểm tra cấu trúc từ M1 hoặc từ project references đầu tiên; giữ demo product thứ hai và nghiệm thu hoạt động đầy đủ ở M7. Không cần tạo ngay một hệ thống plugin/nạp assembly động.

**Bằng chứng đóng:** Architecture check phát hiện dependency sai; product mẫu cuối kỳ có graph không kéo Orders/Approvals vào và khởi động được platform + theme riêng, đúng yêu cầu gốc.

## 4. Điều chỉnh roadmap đề xuất, chưa áp dụng

Giữ thứ tự M0–M7. Bổ sung trạng thái quyết định có điều kiện, trách nhiệm và cổng kiểm chứng; không biến M0 thành toàn bộ MVP.

| Milestone | Phần bổ sung cần thể hiện trong task plan |
|---|---|
| M0 | Trust model; decision register; lựa chọn validation/revocation có điều kiện; bootstrap/schema ownership; kế hoạch SQL/CI; dependency rules |
| M1 | Bootstrap được bảo vệ; audit/state tối thiểu cho provision; readiness gate; routing và DB isolation bốn tenant |
| M2 | PoC liên shell trước tiên; token/session store thật; refresh/revoke/cookie negative tests; chốt lại ADR theo evidence |
| M3 | Hợp đồng quyền có hiệu lực, cache/lỗi nguồn; lifecycle/plan transition; audit transaction đủ dùng cho Orders |
| M4 | State machine, extension point, concurrency/idempotency; transaction đơn + audit |
| M5 | Race approval/downgrade và atomic Order/Approval/Audit; tính tùy chọn thật |
| M6 | Hoàn thiện release runner và restore đã định nghĩa; không dời các guard tối thiểu từ M1 tới đây |
| M7 | Tải, báo cáo trên máy cụ thể và product thứ hai; giữ ranh giới không cam kết năng lực production |

## 5. Gói quyết định cho M0

Đây là nhóm việc đề xuất để lập task plan, chưa phải task đã giao hoặc đã làm.

| Nhóm | Đầu ra cần review | Điều kiện hoàn tất |
|---|---|---|
| M0-A | Decision register, ADR identity/membership/deny/token/lifecycle và threat model | Phân biệt yêu cầu đã chốt, lựa chọn đề xuất, trạng thái có điều kiện; owner và gate cho R01–R05 |
| M0-B | Compatibility matrix, SDK/packages/tools, SQL Server/OS/CI | Metadata và restore/build/smoke thực tế của phiên bản chọn; không dựa riêng vào docs main/latest |
| M0-C | Solution skeleton và dependency rules | Platform không kéo business modules; cấu trúc đủ nhỏ, không tạo mọi folder thành assembly mặc định |
| M0-D | Database ownership và contracts tối thiểu | Một owner cho từng bảng/history; audit contract có lịch trước M4; không khóa signature chưa có nhu cầu |
| M0-E | AGENTS.md, conventions, scripts, README và CI | Clone sạch chạy được bằng dotnet/pwsh; test có ý nghĩa; thiếu prerequisite báo lỗi rõ |
| M0-F | Báo cáo nghiệm thu M0 | SHA, môi trường, command/exit code, test counts, giới hạn, quyết định còn mở; không nhận lời báo agent làm bằng chứng |

Không cần chốt RTO/RPO, production host, billing, PostgreSQL migrations hay năng lực tải production để hoàn thành review này. Giữ các điều kiện trước production của roadmap mục 11.

## 6. Những gì chưa được thực hiện

Chưa tạo solution, host, project, package lock, ADR triển khai, database, tài khoản, secret hay workflow CI. Chưa chạy dotnet build/test, SQL Server integration, trình duyệt SSO, migration, load hoặc restore. Chưa có reviewer độc lập/subagent; đây là review của trợ lý trên tài liệu và nguồn chính thức.

Chỉ nhập nguyên bản roadmap và lưu báo cáo review riêng; không sửa nội dung roadmap, không đổi trạng thái của các mặc định thành Approved, không tuyên bố M0 hoàn thành.

## 7. Nguồn đối chiếu kỹ thuật

Tra cứu ngày 2026-11-06. Các trang main/latest có thể thay đổi; khi triển khai phải đối chiếu tag/package đã khóa. Các nguồn này hỗ trợ nhận xét framework, không thay thế yêu cầu sản phẩm hoặc bằng chứng chạy.

- [E1] [NuGet — OrchardCore.Application.Cms.Targets 3.0.1](https://www.nuget.org/packages/OrchardCore.Application.Cms.Targets/3.0.1): target framework net10.0.
- [E2] [Orchard Core 3.0.0 release notes](https://docs.orchardcore.net/en/latest/releases/3.0.0/): yêu cầu .NET 10.
- [E3] [WHATWG HTML — Origins](https://html.spec.whatwg.org/multipage/browsers.html#origin): scheme/host/port và mô hình tin cậy cùng origin.
- [E4] [Orchard Core OpenID](https://docs.orchardcore.net/en/main/reference/modules/OpenId/): token validation dùng authorization server tenant khác; JWT/configuration.
- [E5] [OpenIddict — Token storage](https://documentation.openiddict.com/configuration/token-storage.html): token-entry validation và quyền truy cập store issuer.
- [E6] [RFC 9700, section 4.14](https://datatracker.ietf.org/doc/html/rfc9700#section-4.14): refresh token protection, rotation và replay detection.
- [E7] [Orchard Core Data Migrations](https://docs.orchardcore.net/en/latest/reference/modules/Migrations/): migration chain và automatic startup execution.
- [E8] [EF Core — Applying Migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying): review/test và cách triển khai migrations.
- [E9] [EF Core — Transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions): cross-context transaction và lưu ý execution strategy.
- [E10] [ASP.NET Core — Policy authorization](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/policies?view=aspnetcore-10.0): context.Fail bảo đảm thất bại ngay cả khi handler khác Succeed, phù hợp nguyên tắc Deny trong roadmap.
- [E11] [Orchard Core Tenants](https://docs.orchardcore.net/en/latest/reference/modules/Tenants/): Feature Profiles giới hạn feature khả dụng, không thay thế business authorization.
