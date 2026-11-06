# Xử lý findings roadmap V1 — vòng 3

Ngày: 2026-11-06 (+07:00).
Phạm vi: sửa tài liệu theo yêu cầu “fix findings và commit vào repo” sau review vòng 3 trong hội thoại.
Trạng thái: **DOCUMENTED — R3-01–R3-03 đã có hợp đồng và cổng nghiệm thu; IMPLEMENTATION/VERIFICATION PENDING.** Không coi sửa Markdown là hoàn thành M0 hoặc sửa/kiểm chứng runtime.

## 1. Baseline và phạm vi thay đổi

Repository: `chung6a8m/SaasOne`, nhánh `main`, commit baseline `b30d3c37332041fd7a7510c52e85a54f812182b4`; tree `56e821e9393bc1b6960bb48bd24a78bfdd097d3f`.

| Tệp baseline | Git blob SHA-1 |
|---|---|
| `docs/roadmap/saas-orchard-roadmap-v1.md` (revision 1.2, 66.557 byte) | `b046db1e272e3b0bac65b9b5fd50f67f977a33c6` |
| `docs/reviews/2026-11-06-roadmap-v1-review.md` | `017a4157dd6543ca32b7ca784c06e4f2fee6399b` |
| `docs/reviews/2026-11-06-roadmap-v1-remediation.md` | `57c4fccd9d87465e3244f4a2cb8e8fe3c0fffc1f` |
| `docs/reviews/2026-11-06-roadmap-v1-round2-remediation.md` | `2e805bb1a723bc8d43d002258f9f26e055326230` |

Review vòng 3 trong hội thoại nêu 2 P1 và 1 P2; tại lúc review chưa sửa/commit và chưa có file review vòng 3 riêng. Báo cáo này lưu findings, lựa chọn xử lý và evidence còn thiếu. [Review vòng 1](2026-11-06-roadmap-v1-review.md), [xử lý vòng 1](2026-11-06-roadmap-v1-remediation.md) và [xử lý vòng 2](2026-11-06-roadmap-v1-round2-remediation.md) giữ nguyên; [roadmap revision 1.3](../roadmap/saas-orchard-roadmap-v1.md) là nguồn yêu cầu hiện hành.

Commit chỉ sửa roadmap và thêm báo cáo này. Không thêm source, schema, ADR đã Accepted, solution hoặc CI; giữ stack, database-per-tenant, M0–M7, phạm vi production và các ràng buộc R01–R08/R2-01–R2-03. Đây là lựa chọn thiết kế của lượt xử lý được giao, không gán thành hành vi mặc định framework hoặc yêu cầu người dùng đã chốt trước review.

## 2. Đối chiếu findings và cổng đóng việc

| ID / mức | Khoảng trống revision 1.2 | Hợp đồng bổ sung | Milestone / bằng chứng còn thiếu |
|---|---|---|---|
| R3-01 / P1 | Restore kiểm tra DB đích nhưng chưa chứng minh backup/dữ liệu thuộc đúng TenantId. | [Mục 5.4](../roadmap/saas-orchard-roadmap-v1.md#r3-01-restore-identity): định danh DB bất biến do Tenancy sở hữu; manifest tin cậy ngoài tenant gắn backup set/chuỗi/artifact; kiểm tra trước ghi đè và sau restore, không seed sửa danh tính. | M0 hợp đồng/owner; M1 định danh/readiness và quyền runtime; M6 manifest/runbook/SQL restore thật với backup Alpha cho Delta, nguồn sai và định danh thiếu/sai. |
| R3-02 / P1 | Ownership/version của từng workflow chưa khóa quy tắc ngăn bước cuối ghi đè Suspend/PendingDeletion mới hơn. | [Mục 5.5](../roadmap/saas-orchard-roadmap-v1.md#r3-02-lifecycle-coordination): subscription tách operation, RegistryVersion chung, khóa điều phối tài nguyên chung, cập nhật cuối có điều kiện; worker không tự mở lại/gia hạn hoặc hồi sinh tenant đã purge. | M0 phạm vi version/lock; M1 contract/provision; M3 race fixture và lifecycle thật; M6 restore/release, worker cũ và workflow cạnh tranh. |
| R3-03 / P2 | Restore đưa PendingApproval về trong khi platform đã Standard; chưa có nhánh recovery giải quyết nghĩa vụ. | [Mục 5.6](../roadmap/saas-orchard-roadmap-v1.md#r3-03-restore-obligations): reader nghĩa vụ hoạt động khi Approvals tắt; recovery upgrade Approval có phê duyệt là bước con của restore, giữ gate cha đóng; không tự duyệt/xóa marker. | M0 lựa chọn/quyền; M3 fixture; M4 reader/model; M5 dữ liệu/feature thật; M6 backup → Approve → downgrade → restore và khôi phục sử dụng hợp lệ. |

Cả ba chỉ đạt **Documented**; milestone sở hữu chịu trách nhiệm hiện thực và thu thập evidence. Không ghi nhận nghiệm thu runtime hoặc chữ ký reviewer độc lập chưa có.

## 3. Lựa chọn và đánh đổi

### R3-01 — Xác minh nguồn dữ liệu, không chỉ tên DB đích

Chọn bản ghi TenantId ở cấp DB và manifest vận hành được bảo vệ ngoài customer DB; không thêm TenantId vào mọi bảng Orders. Readiness M1 dùng cùng định danh; backup/restore M6 kiểm tra nguồn và nội dung phục hồi trước mở ứng dụng. Digest ràng buộc artifact, không thay thế nguồn manifest đáng tin cậy. Sai nguồn trước restore không được ghi đè; sai định danh sau restore không được migrate/seed/apply quyền để làm cho khớp.

Không hỗ trợ clone sang tenant mới hoặc tự nhận backup thiếu định danh trong V1. M0 phải chỉ định owner/quyền; M6 khóa format và cách giữ artifact không đổi trong khoảng kiểm tra–restore. Các kiểm tra này không được quảng cáo là chống DBA sửa mọi nguồn tin cậy.

### R3-02 — Tách quyết định chặn khỏi tiến trình công việc

Chọn subscription state/ExpiresAt độc lập với operation state và một RegistryVersion chung bảo vệ các quyết định liên quan đến mở phục vụ. Những workflow thay tài nguyên dùng cùng ownership/lock theo TenantId; Suspend/PendingDeletion vẫn được commit ngắn trong lúc operation dài chạy, không phải chờ restore kết thúc mới khóa.

Worker đang ở bước không thể ngắt an toàn chỉ được kết thúc/cách ly bước đó theo runbook, không dùng trạng thái cũ chuyển tiếp hoặc mở traffic. Version mismatch không được giải quyết bằng reload rồi ghi Active mù. Unsuspend/gia hạn là command riêng và cũng không xóa gate recovery chưa đạt. Purge phải chờ worker thay tài nguyên không còn khả năng ghi, không coi lease hết hạn là bằng chứng đã dừng.

Không xây distributed transaction hay hạ tầng nhiều instance trong lượt này. Primitive và timeout cụ thể thuộc task plan trước consumer; M1 đặt contract tối thiểu, M3/M6 mở rộng cùng owner.

### R3-03 — Nâng lại gói được phê duyệt trong recovery, không bypass entitlement

Chọn nhánh chính là khôi phục gói Approval có quyết định hiện hành của người có quyền recovery và đổi gói tại platform. Bước con chạy dưới operation id/lock/fencing của restore, tái dùng orchestration, không đòi tenant Active và không tự kết thúc gate cha. Sau khi tất cả cổng đạt, người dùng có quyền mới xử lý duyệt; operator recovery không tự được đọc Orders hoặc duyệt thay.

Reader do Orders cung cấp qua abstraction/host composition, hoạt động khi Approvals tắt; thiếu reader/schema/marker thì giữ đóng. Không để platform tham chiếu implementation business. Khi không được phê duyệt nâng gói, giữ BLOCKED hoặc chọn điểm phục hồi khác cùng tenant có phê duyệt phạm vi mất dữ liệu rồi chạy lại cổng; không âm thầm đổi backup hoặc rollback subscription. Không thêm hủy đơn/state mới để né nghĩa vụ.

## 4. Bàn giao và phạm vi giữ nguyên

Roadmap đồng bộ mục 3.2, TenantOperational, vòng đời/provision/readiness, protocol đổi gói 5.2, restore 5.3, ba hợp đồng mới 5.4–5.6, reader/guard 6.2, đầu ra/nghiệm thu M0/M1/M3–M6, ma trận lỗi và decision register. Identity vẫn Conditional đến PoC M2; RTO/RPO, host production, PostgreSQL, tải production và M7 không đổi. Không coi fixture là diễn tập SQL restore.

Ca chứng minh chủ đạo R3-01 là Alpha backup → Delta target; R3-02 là dừng worker trước mở registry → commit Suspend/PendingDeletion → tiếp tục/restart; R3-03 là backup PendingApproval → Approve → downgrade Standard → restore → phê duyệt nâng gói trong gate đóng → mở hợp lệ → duyệt bình thường → downgrade. Mỗi ca có nhánh thất bại và kiểm tra tenant khác không bị ảnh hưởng.

## 5. Kiểm chứng tài liệu và giới hạn

Bản roadmap baseline trong workspace kiểm tra khớp Git blob và 66.557 byte với tree GitHub. Workspace cục bộ chỉ phục vụ diff/check tài liệu, không phải checkout/build sản phẩm. Các tệp lịch sử không được viết lại; link tới chúng được đối chiếu tree baseline. Helper kiểm tra ngoài repository kiểm tra UTF-8 không BOM/LF, diff/whitespace, link nội bộ/anchor, đủ M0–M7, giữ anchor cũ, coverage R3 và các đoạn ngoài phạm vi sửa.

Kiểm tra cục bộ ngày 2026-11-06: `git diff --cached --check` và helper kiểm tra tài liệu ngoài repository đều exit 0. Helper xác nhận đúng hai file Markdown thay đổi; 14 liên kết nội bộ (3 có fragment), 11 anchor cũ giữ nguyên và 3 anchor mới, đủ M0–M7, 10 khối nội dung ngoài phạm vi sửa giữ nguyên cùng 13 nguồn cũ. Đã tự review diff và chạy lại kiểm tra sau lần sửa cuối; đây không phải unit/integration test sản phẩm. Sau ghi GitHub phải đối chiếu blob/tree/parent/ref và giữ nguyên blob lịch sử; cập nhật nhánh không force, không ghi đè thay đổi đồng thời.

Build/test sản phẩm, SQL Server, SSO, migration, refresh, replay, tải và restore: **NOT RUN**. Review nội dung là tự đối chiếu của trợ lý, chưa có reviewer độc lập/subagent. Kiểm tra văn bản không chứng minh bảo mật hoặc tính đúng đắn runtime.

Nguồn kỹ thuật: giữ các nguồn cũ; bổ sung Microsoft Learn về restore SQL Server sang tên/vị trí mới và EF Core concurrency (nguồn [14]–[15] trong roadmap), đối chiếu ngày 2026-11-06. Chúng chỉ hỗ trợ khả năng/giới hạn framework; các protocol, quyền phê duyệt và cổng ở đây là thiết kế ứng dụng cần kiểm chứng ở milestone sở hữu.
