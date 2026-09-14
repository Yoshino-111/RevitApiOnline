# Drain Connection — Case 01

## Vị trí

`FamilyMEP` → `Drainage Tools` → `Drain Connection`

Tool được viết bằng C# cho Revit 2025 và chạy modeless thông qua `ExternalEvent`.

## Case 01

- Chọn thiết bị/phễu có connector ống tròn đang mở.
- Chọn một ống main không thẳng đứng.
- Với main ngang, chọn preference `Left` hoặc `Right`.
- Tool tự thử cả hai hướng Y trên mặt bằng; điều kiện bắt buộc là cao độ phải tăng liên tục từ main lên thiết bị.
- Tool luôn tự xét cả hai phía, không có nút Left/Right. Với main level, code ưu tiên vị trí Y có khoảng hở an toàn hơn tới hai đầu main.
- Nhập độ dốc nhánh và chọn `Branch Pipe Type`.
- Không nhập góc Y.

Case 01 chỉ dùng Y 45°. “Tự động” là tự xoay/roll Y về đúng điểm main, không thay Y bằng góc 30° hoặc 60°. Góc 45° của fitting là góc thật trong không gian 3D; code tự bù góc mặt bằng theo độ dốc của main và độ dốc nhánh trước khi gọi `NewTeeFitting`.

## Quy tắc dòng chảy chung

Mọi case đều được dựng từ thiết bị về ống chính:

- cao độ giảm liên tục từ connector thiết bị đến điểm nối main;
- nhìn từ Y về thiết bị, nhánh Y phải hướng về đầu cao của main;
- phương án quay Y về đầu thấp của main bị loại bỏ trước khi tạo element;
- `Left/Right` chỉ là preference khi main không có chênh cao độ.

Tại phễu, tool tạo:

1. vertical stub;
2. hai elbow 45° compact/back-to-back;
3. sloped branch tới main;
4. junction lấy từ Routing Preferences của Target Pipe Type.

Elbow lấy từ Routing Preferences của Branch Pipe Type.

Nếu Revit không đặt được fitting, từng phương án được rollback. Sau khi tất cả phương án thất bại, toàn bộ `TransactionGroup` được rollback; tool không giữ lại các ống fallback giao vào main nhưng chưa có Y.

Tool thử hai hướng connector run. Case 01 dùng junction 45° được Revit giải từ Routing Preferences của Target Pipe Type; code không kiểm tra tên family và không chứa tên type riêng của một dự án.

Điểm kết nối được tính trên tim main, chiếu lại chính xác lên `LocationCurve`, rồi `PlumbingUtils.BreakCurve` ngay tại điểm đó. Hai connector mới của main và connector branch tại cùng điểm được đưa vào `NewTeeFitting`. ElementId, system, level, đường kính, Pipe Type và fitting đều được lấy từ các phần tử người dùng chọn trong document hiện hành.

Nếu Routing Preferences chứa rule bị mất family (`InvalidElementId`), tool bỏ qua rule hỏng và lấy family junction hợp lệ đầu tiên. Một Pipe Type tạm chỉ được tạo một lần trong `TransactionGroup`, dùng cho toàn bộ lần thử, sau đó main được trả về type gốc và type tạm bị xóa. Nếu lệnh thất bại, cả type tạm và hình học đều rollback.

## Preview

`VALIDATE & FOCUS` kiểm tra hình học, chọn/focus hai element trong Revit và không sửa model.

## Nạp bản mới

Sau khi chạy `build-all.ps1`, cần khởi động lại Revit để loader tạo nút ribbon mới. Sau đó có thể dùng `Reload Release` cho các lần build plugin tiếp theo.
