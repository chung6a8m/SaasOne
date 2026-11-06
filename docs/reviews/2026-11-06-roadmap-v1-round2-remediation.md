# Xử lý findings roadmap V1 — vòng 2

Ngày: 2026-11-06 (+07:00).
Phạm vi: sửa tài liệu theo yêu cầu “fix findings và commit vào repo” sau review vòng 2 trong hội thoại.
Trạng thái: **DOCUMENTED — R2-01–R2-03 đã có hợp đồng và cổng nghiệm thu; IMPLEMENTATION/VERIFICATION PENDING.** Không coi sửa Markdown là hoàn thành M0 hoặc đã sửa/kiểm chứng runtime.

## 1. Baseline và phạm vi thay đổi

Repository: `chung6a8m/SaasOne`, nhánh `main`, commit baseline `8247b79bdad82839170e9a21b710d0e9e6dcf759`.

| Tệp baseline | Git blob SHA-1 |
|---|---|
| `docs/roadmap/saas-orchard-roadmap-v1.md` (revision 1.1, 52.167 byte) | `7ad0979ec97354bea394c459c6084e75d3a43582` |
| `docs/reviews/2026-11-06-roadmap-v1-review.md` | `017a4157dd6543ca32b7ca784c06e4f2fee6399b` |
| `docs/reviews/2026-11-06-roadmap-v1-remediation.md` | `57c4fccd9d87465e3244f4a2cb8e8fe3c0fffc1f` |

Review vòng 2 trong hội thoại nêu 1 P1 và 2 P2, chưa sửa/commit tại thời điểm review. Bảng dưới lưu lại vấn đề và cách xử lý; không giả định đã có một file review vòng 2 trước commit này. [Review vòng 1](2026-11-06-roadmap-v1-review.md) và [báo cáo xử lý vòng 1](2026-11-06-roadmap-v1-remediation.md) giữ nguyên byte-for-byte để bảo toàn baseline lịch sử. [Roadmap revision 1.2](../roadmap/saas-orchard-roadmap-v1.md) là nguồn yêu cầu hiện hành.

Commit này chỉ sửa roadmap và thêm báo cáo này. Giữ stack, database-per-tenant, M0–M7, giới hạn production và các cổng R01–R08; không tạo source, ADR triển khai, solution, workflow CI hay schema. Các lựa chọn dưới đây là quyết định thiết kế của lượt xử lý được giao, không gán thành hành vi mặc định của Orchard/SQL Server hoặc yêu cầu người dùng đã nói trước review.

## 2. Đối chiếu findings và cổng đóng việc

| ID / mức | Khoảng trống ở revision 1.1 | Hợp đồng bổ sung | Owner milestone / evidence còn thiếu |
|---|---|---|---|
| R2-01 / P1 | Quyền và audit quyền đều nằm trong DB tenant; restore có thể làm mất cả quyền hiện hành lẫn dấu vết thu hồi. Generation mới không ngăn session mới nhận quyền cũ. | [Mục 5.3](../roadmap/saas-orchard-roadmap-v1.md#r2-01-restore-security): platform nằm ngoài restore giữ gate/generation/recovery authority; toàn bộ ACL cũ không dùng để cấp quyền; bộ quyền mới được quyết định/phê duyệt hiện hành, áp dụng có audit/checkpoint rồi mới mở registry. | M0 ADR/decision; M3 cổng, quyền recovery và apply fixture; M6 restore SQL thật, kiểm tra cả login mới, từng loại quyền và crash/restart. |
| R2-02 / P2 | Chỉ PendingApproval chặn downgrade; Rejected → Draft có thể gặp quy tắc Standard Confirmed đối lập với quy tắc gửi lại phải duyệt. | [Mục 6.2](../roadmap/saas-orchard-roadmap-v1.md#r2-02-approval-obligation): giữ nghĩa vụ qua marker server-owned; guard bao gồm Rejected/Draft làm lại, chỉ hoàn tất khi Confirmed; cập nhật bảng state và protocol 5.2 cùng chính sách. | M0 ghi quyết định; M3 guard contract/fixture; M4 model/state/extension; M5 cả hai thứ tự sửa/downgrade, race và lịch sử thật. |
| R2-03 / P2 | “Authorization lại” chưa phân biệt quyền truy cập kết quả với trạng thái/revision để thực thi mới; replay sau commit có thể bị conflict giả. | [Mục 6.3](../roadmap/saas-orchard-roadmap-v1.md#r2-03-replay-policy): replay kiểm tra quyền hành động và truy cập kết quả hiện hành, không chạy lại transition/concurrency; chỉ nhánh thực thi mới áp dụng precondition. | M4 policy mapping và Submit/fixture tests; M5 Approve thật, response mất, state/revision đổi, quyền bị thu hồi, chỉ một side effect/audit thành công. |

Mỗi dòng chỉ đạt **Documented**. Chủ dự án review quyết định/evidence; implementer milestone chịu trách nhiệm hiện thực. Không ghi nhận chữ ký nghiệm thu hoặc reviewer độc lập chưa có.

## 3. Các lựa chọn và đánh đổi được ghi rõ

### R2-01 — Xác lập lại quyền thay vì giả định đối soát tự động

Chọn phương án đơn giản đã đề xuất ở review: sau restore giữ tenant đóng và xác lập lại toàn bộ bộ quyền hiện hành, không chỉ hủy token cũ. Nguồn quyết định là người có thẩm quyền recovery hiện hành ở platform và bộ quyền mới được phê duyệt, không phải audit/ACL vừa restore. Thiếu nguồn/quyết định thì không mở. Việc cấp lại quyền không tự gán cho operator khả năng đọc Orders.

Không xây thêm nguồn replica quyền độc lập trong V1. Đánh đổi là thời gian/thao tác phục hồi quyền; không hứa tự khôi phục nguyên trạng quyền ngay trước sự cố. Muốn giữ nguyên tự động phải thiết kế nguồn ngoài restore với bằng chứng đầy đủ/nhất quán. Restore đồng thời platform không thuộc bảo đảm của runbook restore riêng một tenant.

Ca nghiệm thu trọng tâm: giữ membership Active, backup trước khi thu hồi quyền, restore sau thu hồi, xác lập bộ quyền mới giữ quyết định thu hồi, đăng nhập mới vẫn bị từ chối. Chạy riêng role assignment, role permission và override/Deny; chỉ test token cũ không đủ.

### R2-02 — Giữ nghĩa vụ duyệt, không dùng downgrade làm đường thoát

Chọn giữ nghĩa vụ của đơn đã Submit cần duyệt. `ApprovalRequired` do Orders sở hữu; Reject/sửa/tăng revision không xóa marker. Guard hỏi qua contract, không kéo business implementation vào platform. Standard chỉ xác nhận trực tiếp đơn không có nghĩa vụ; entitlement hoặc handler thiếu không là lý do bỏ duyệt.

Đánh đổi: Rejected chưa được sửa/gửi lại và Approve có thể tiếp tục chặn downgrade. V1 chưa thêm hủy/đóng đơn không duyệt; không tự mở rộng state machine để làm biến mất nghĩa vụ. Draft chưa từng Submit cần duyệt không tự bị đánh dấu chỉ vì được tạo ở gói Approval. Khi hết nghĩa vụ, downgrade vẫn được phép; lịch sử còn nguyên.

Hai ca tuần tự đều phải đạt, ngoài ca race: `Rejected → downgrade → sửa → Submit` và `Rejected → sửa thành Draft → downgrade → Submit`. Downgrade phải bị chặn, gói cũ được mở lại để xử lý; sau Approve đúng revision mới có thể downgrade.

### R2-03 — Replay được authorization nhưng không thực thi lần hai

Policy replay yêu cầu quyền hành động tương ứng và quyền truy cập tài nguyên/kết quả hiện hành; vẫn kiểm tra Deny, membership, generation, feature và lỗi nguồn. Không dùng business precondition “đang PendingApproval” hay expected revision đã cũ sau thành công để chặn đọc lại kết quả. Fingerprint lấy từ request gốc chuẩn hóa, không viết lại bằng revision mới của DB.

Chỉ khi chưa có kết quả committed mới kiểm tra precondition của command mới và ghi transaction. Sau unique-key race phải quay về nhánh replay có kiểm tra quyền. Key mới với revision cũ vẫn bị conflict; cùng key khác payload vẫn conflict sau kiểm tra quyền, không lộ kết quả cho actor/tenant khác. Replay không thêm audit nghiệp vụ thành công lần hai; log truy cập riêng không bị cấm. Không nới rotation/reuse của OAuth refresh token: replay command nghiệp vụ là hợp đồng khác.

## 4. Bàn giao theo milestone

M0 decision register phải mang cả R01–R08 và R2-01–R2-03, cùng owner/gate/evidence. M0 khóa nguồn/quyền recovery và quyết định giữ nghĩa vụ, không phải triển khai SQL restore hay Orders trước hạn. M3 chuẩn bị infrastructure/contracts bằng fixture; M4 khóa model và replay policy trước consumer; M5 ghép Approvals thật; M6 mới có restore SQL thật. Các mục nghiệm thu và ma trận lỗi trong roadmap đã được đồng bộ, không chỉ thêm đoạn giải thích tách rời.

Phạm vi RTO/RPO, host production, PostgreSQL và năng lực tải production không đổi. Không đổi trạng thái Conditional của identity hoặc tự đóng bất kỳ milestone nào.

## 5. Kiểm chứng và giới hạn bằng chứng

Bản roadmap baseline dùng trong workspace kiểm tra đã được đối chiếu byte count và Git blob với GitHub trước khi sửa. Kiểm chứng tài liệu gồm diff/whitespace, UTF-8 không BOM/LF, link nội bộ/anchor, giữ các anchor R01–R08 và M0–M7, coverage ba findings, chỉ hai file Markdown thay đổi. Đích link tới tài liệu lịch sử được đối chiếu tree GitHub baseline; anchor của roadmap được kiểm tra trên nội dung đã sửa. Không gọi các kiểm tra văn bản này là unit/integration test sản phẩm.

Kiểm tra cục bộ: `git diff --cached --check` và helper kiểm tra tài liệu ngoài repository đã exit 0. Helper kiểm tra hai file, 11 liên kết nội bộ (3 liên kết có fragment), giữ 8 anchor cũ/thêm 3 anchor mới, đủ M0–M7 và 14 đoạn ngoài phạm vi sửa giữ nguyên. Diff nội dung được tự review lại trước commit; các kiểm tra được chạy lại sau lần sửa cuối. Kiểm chứng sau ghi GitHub: đối chiếu parent/tree/blob của commit và ref `main`, không force-push hoặc ghi đè thay đổi đồng thời. Các blob review/remediation vòng 1 phải giữ nguyên như mục 1.

Build/test, SQL Server, SSO, migration, refresh, replay nghiệp vụ, load và restore: **NOT RUN**. Không có bằng chứng runtime mới; review tài liệu là tự đối chiếu của trợ lý, chưa có reviewer độc lập/subagent.

Nguồn kỹ thuật: giữ các nguồn roadmap đã có; bổ sung Microsoft Learn về SQL Server point-in-time restore (nguồn [13] ở roadmap), đối chiếu ngày 2026-11-06. Nguồn này hỗ trợ giới hạn điểm phục hồi của DB, không chứng minh policy hoặc implementation ứng dụng. Các quy tắc recovery, nghĩa vụ duyệt và replay ở trên là hợp đồng thiết kế để kiểm chứng ở milestone sở hữu.
