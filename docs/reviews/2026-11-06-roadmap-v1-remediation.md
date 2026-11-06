# Xử lý findings roadmap V1 — R01–R08

Ngày: 2026-11-06 (+07:00).
Phạm vi: cập nhật tài liệu theo yêu cầu chủ dự án “fix findings trên và commit vào repo”. Không triển khai source code M0.
Trạng thái: **DOCUMENTED — 8/8 findings đã có hợp đồng/cổng nghiệm thu trong roadmap; IMPLEMENTATION/VERIFICATION PENDING.** Không coi sửa tài liệu là bằng chứng runtime hoặc đóng milestone.

## 1. Baseline và nguồn yêu cầu

Repository: `chung6a8m/SaasOne`, nhánh `main` tại `7d6dc2389bcf71a462c85d72dff4d9b80432f040` trước lượt sửa này.

| Tệp baseline | Git blob SHA-1 |
|---|---|
| `docs/roadmap/saas-orchard-roadmap-v1.md` | `677e4adee8b2b2e1ae483642d448f2054f5482e9` |
| `docs/reviews/2026-11-06-roadmap-v1-review.md` | `017a4157dd6543ca32b7ca784c06e4f2fee6399b` |

[Review gốc](2026-11-06-roadmap-v1-review.md) được giữ nguyên để bảo toàn nhận xét, số dòng và mức độ chắc chắn ở baseline. Các câu “chưa áp dụng” trong review mô tả thời điểm review, không phải trạng thái hiện hành. [Roadmap revision 1.1](../roadmap/saas-orchard-roadmap-v1.md) là nguồn yêu cầu hiện hành; bảng bên dưới là đối chiếu xử lý, không thay thế roadmap.

Giữ hướng kiến trúc, stack, yêu cầu sản phẩm, thứ tự M0–M7 và ranh giới trước production. Không tự khóa patch version, CI provider, OS, production host hoặc RTO/RPO. Các lựa chọn chi tiết được đưa ra trong lần sửa này là quyết định thiết kế để xử lý findings, không gán thành hành vi mặc định của Orchard/EF/OpenIddict hoặc yêu cầu người dùng đã nói từ trước.

## 2. Đối chiếu từng finding

Mỗi dòng có trạng thái **Documented / runtime pending**. Chủ dự án review quyết định và evidence; implementer milestone sở hữu thực hiện code/tests. Chưa chỉ định danh tính reviewer độc lập hay tuyên bố có chữ ký nghiệm thu.

| ID | Thay đổi đã áp dụng | Cổng implementation/evidence còn bắt buộc |
|---|---|---|
| R01 | [Mục 3.3](../roadmap/saas-orchard-roadmap-v1.md#r01-cms-trust): trust model cùng origin; tenant không sửa script/template/feature; recipe allowlist; text encode; rich text/upload đóng cho đến khi có policy và tests. | M0 ADR quyền CMS/nội dung/upload; M1 negative test recipe/setup; M3 chứng minh role/override và API không mở khả năng thực thi tùy ý. |
| R02 | [Mục 4.1](../roadmap/saas-orchard-roadmap-v1.md#r02-identity-gate): ADR identity Conditional; PoC Default + hai tenant là task đầu M2, chặn mở rộng trước khi đạt; yêu cầu đúng issuer store/introspection và cấu hình token/callback. | M0 ghi conditional decision; M2 evidence SSO, token sai tenant, revoke UI/API, cookie, mapping subject; chỉ chuyển ADR Accepted sau PoC đạt. |
| R03 | [Mục 4.4](../roadmap/saas-orchard-roadmap-v1.md#r03-revocation-contract): mốc commit; giới hạn in-flight; không cache Allow liên request; phân biệt lỗi nguồn; phạm vi logout/revoke; generation bền vững; strict refresh rotation và xử lý mất response. | M0 khóa TTL/định danh/security version và contract; M2 session/grant/replay tests; M3 quyền động/lifecycle; M6 restore đối soát, không hồi sinh grant/quyền cũ. |
| R04 | [Mục 5.1](../roadmap/saas-orchard-roadmap-v1.md#r04-bootstrap-readiness): bootstrap kín, quyền SQL theo tác vụ, checkpoint/audit tối thiểu ở M1; schema/seed/health gate; kiểm chứng Orchard warm-up và quyền DDL. | M0 ADR owner/privilege/secret; M1 retry/anonymous/SQL isolation/readiness tests; M3 mở rộng audit/UI cùng owner; M6 release runner đầy đủ. |
| R05 | [Mục 5.2](../roadmap/saas-orchard-roadmap-v1.md#r05-plan-transition): durable PlanChanging/write gate/version, drain transaction đang chạy, kiểm tra lại pending, checkpoint/resume và mở registry cuối; không DTC hoặc direct toggle bỏ guard. | M0 protocol; M3 chọn primitive và test fixture/crash/worker cũ; M5 race Submit/Approve/downgrade thật và recovery từng checkpoint. |
| R06 | [Mục 6.1](../roadmap/saas-orchard-roadmap-v1.md#r06-audit-orders-contract): audit contract M3 trước M4; coordinator tại consumer đa context đầu tiên; state machine/revision, idempotency và extension boundary. | M3 contract/owner; M4 audit rollback, replay/conflict, state/extension tests; M5 atomic Order/Approval/Audit, revision và reject/resubmit thật. |
| R07 | [Mục 7.1](../roadmap/saas-orchard-roadmap-v1.md#r07-m0-acceptance): compatibility matrix không TBD cuối M0; solution/tool/package lock contract; lệnh kiểm chứng; test discovery/executed; SQL smoke thật, thiếu prerequisite phải lỗi. | M0 tạo các artifact/script nêu trong hợp đồng, clone sạch và CI evidence với command/exit code/count; không dùng smoke làm bằng chứng isolation/migration M1. |
| R08 | [Mục 9.1](../roadmap/saas-orchard-roadmap-v1.md#r08-dependency-rules): cấm dependency trực tiếp/bắc cầu từ platform/shared theme tới business modules; host là composition root; architecture check từ references đầu tiên. | M0 fixture tốt/xấu và graph skeleton; M1+ graph ProjectReference/package/feature thật; M7 product thứ hai không có Orders/Approvals trong publish graph và khởi động được. |

## 3. Các lựa chọn được làm rõ trong lần sửa

**Thu hồi và refresh:** request authorization sau commit phải thấy quyền mới; request đã authorization trước đó có thể hoàn tất, trừ hàng rào chặt hơn cho đổi gói. Logout tenant thu hồi các session/grant của user ở tenant chọn; logout toàn cục/khóa account tác động toàn user. Refresh nghiêm ngặt không chấp nhận lại token đã dùng; response bị mất có thể yêu cầu login lại. Chọn hành vi bảo thủ, chấp nhận đánh đổi trải nghiệm thay vì để agent tự chọn grace period không có test. Cơ chế thực tế vẫn phải được PoC chứng minh.

**Đổi gói:** kiểm tra PendingApproval một lần trước thao tác là không đủ. Đóng gate trong DB tenant phải đồng bộ với transaction ghi và lưu bền vững; gate ở middleware hoặc lock chỉ trong bộ nhớ không đáp ứng crash/restart. Nếu phát hiện pending, không đổi gói và chỉ mở lại gói cũ sau đối soát. Không khóa cả hệ thống vô hạn để đợi người dùng xử lý pending.

**Orders:** bổ sung baseline Draft/PendingApproval/Confirmed/Rejected, chặn sửa đơn chờ, duyệt đúng revision, giữ lịch sử khi gửi lại. Không thêm hủy đơn hoặc sửa sau xác nhận vào V1. Gói Approval thiếu handler không được âm thầm thực hiện hành vi Standard. M4 khóa contract trước implementation; M5 ghép module thực.

**M0:** SQL smoke tối thiểu trở thành gate M0 để chứng minh môi trường SQL thực đã có, nhưng chưa yêu cầu hoàn tất provisioning/isolation/migrations của M1. Lệnh và tên solution/script trong roadmap là hợp đồng đầu ra tương lai, không mô tả file đã tồn tại. CI/OS/version cụ thể vẫn phải được chọn, ghi và kiểm chứng ở M0.

## 4. Giới hạn đóng findings và bàn giao

Không có finding bị bỏ qua, đổi mức để che thiếu evidence, hoặc gộp tất cả implementation vào M0. Cả R01–R08 được xử lý ở mức thiết kế bằng yêu cầu có thể kiểm tra, owner theo milestone và cổng rõ ràng. **Không đánh dấu runtime verified hoặc security fixed trên sản phẩm chưa có code.**

Task plan/decision register M0 phải dẫn lại từng finding, trạng thái Conditional/Documented, owner và evidence cần thu. Những phần M0 có thể đóng bằng tài liệu vẫn cần review; conditional identity chỉ được giữ mở đến PoC đầu M2, không phải ngoại lệ bỏ PoC. TTL, SQL privileges, lock primitive và các signature là đầu ra của task plan/ADR tại milestone được chỉ định, không phải chỗ trống được phép lờ đi.

## 5. Kiểm chứng thay đổi tài liệu

Phạm vi commit chỉ gồm sửa roadmap và thêm báo cáo xử lý này; review gốc giữ nguyên byte-for-byte. Kiểm chứng cục bộ: đối chiếu blob baseline với GitHub; kiểm tra diff/whitespace, UTF-8 không BOM/LF, liên kết nội bộ/anchor, giữ M0–M7 và phạm vi sản phẩm, đối chiếu đủ R01–R08 và trạng thái evidence. Kiểm chứng sau ghi GitHub: đối chiếu tree/blob và branch head trước khi báo commit thành công.

Không tạo/chạy solution, dotnet build/test, workflow CI, SQL Server, migration, SSO, refresh/revocation, load hoặc restore. Runtime tests: **NOT RUN**. Review tài liệu do trợ lý tự đối chiếu; chưa có reviewer độc lập/subagent.

Nguồn kỹ thuật giữ theo roadmap/review gốc. Lần sửa này đối chiếu lại WHATWG Origins, Orchard OpenID/Data Migrations, OpenIddict Token Storage, RFC 9700 và EF Transactions ngày 2026-11-06; những nguồn đó hỗ trợ ràng buộc framework, không chứng minh implementation. M0/M2 vẫn phải đối chiếu package/tag đã khóa thay vì chỉ đọc main/latest.
