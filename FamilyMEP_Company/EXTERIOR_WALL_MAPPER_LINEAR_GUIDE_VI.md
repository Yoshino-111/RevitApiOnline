# Exterior Wall Mapper 2025 — LINEAR workflow

## 1. Ý nghĩa của Target LINEAR

`Target LINEAR` là mã short name của một Composite Component trong LINEAR Material Table, ví dụ `B13`, `B533` hoặc mã tương đương trong dự án.

Tool không tính U-value và không ghi trực tiếp vào database nội bộ của LINEAR. Tool tạo phạm vi lọc, danh sách Space và kế hoạch Find & Replace để người dùng áp dụng có kiểm soát trong LINEAR Building 25.

## 2. Chuẩn bị component trong LINEAR

1. Mở LINEAR Material Table.
2. Mở nhóm phù hợp, ví dụ `Walls`, `Sandwich Walls` hoặc nhóm component theo tiêu chuẩn dự án.
3. Tạo mới hoặc chọn Composite Component đã có.
4. Ghi lại mã short name của component, ví dụ `B533`.
5. Kiểm tra component có đúng loại sử dụng: exterior wall, exterior window hoặc interior wall.

## 3. Lọc theo Space Number / Space Name trong Exterior Wall Mapper

1. Trong Revit 2025, mở file MEP chứa Spaces và load link kiến trúc/IFC.
2. Chạy `SCAN LINEAR COMPONENTS`.
3. Trong `LEVEL / SPACE`, mở Level cần xử lý.
4. Chọn đúng dòng có dạng `Space Number — Space Name`.
5. Nếu cần, lọc thêm `Orientation` hoặc `Status`.
6. Mở tab `LINEAR BATCH PLAN`.
7. Chọn loại:
   - `EWA` — Exterior Walls.
   - `EWI` — Exterior Windows.
   - `IWA` — Interior Walls.
8. Nhập mã `Bxxx` vào cột `TARGET LINEAR U-VALUE` cho từng batch.
9. Chọn các batch cần xử lý. Nếu không chọn dòng nào, nút copy sẽ lấy toàn bộ batch thuộc Space đang filter.
10. Bấm `COPY FIND & REPLACE PLAN` hoặc `EXPORT EXCEL` để lưu danh sách kiểm tra.

Khi tất cả tường ngoài dùng cùng một component, bật `ONE COMPONENT FOR ALL EWA`, nhập một mã `Bxxx`, rồi copy plan. Chế độ này áp dụng phạm vi EWA toàn dự án và không giới hạn theo orientation.

## 4. Associate và replace hàng loạt trong LINEAR

1. Mở project trong LINEAR Building 25.
2. Trong Building Structure, chọn đúng Structural Level chứa các room trong plan. LINEAR chạy Find & Replace trên structure level đang active.
3. Trên symbol bar, mở `Search & Replace` và chọn category `Room components`.
4. Tạo các điều kiện tìm:

   - EWA: `Component Type = Exterior Wall` và `Adjoining = Exterior`.
   - EWI: `Component Type = Exterior Window` và `Adjoining = Exterior`.
   - IWA: `Component Type = Interior Wall` và `Adjoining = Adjacent Room`.
   - Thêm `Orientation` theo plan nếu không dùng chế độ global EWA.

5. Trong hit list, dùng cột `Name` để đối chiếu storey, room number và room name với danh sách `Space Number — Space Name` từ tool. Tắt dấu `X` của mọi room không thuộc phạm vi cần replace.
6. Tại phần `Replace`, chọn data type `U-value`, bấm nút chọn value và chọn component `Bxxx` trong Master Tables.
7. Không replace `Component Type`, trừ khi phân loại Interior/Exterior trong LINEAR đang sai.
8. So sánh số dòng còn bật `X` với `ROOM FACES` trong LINEAR Batch Plan.
9. Chỉ bấm `Replace values` khi số hit và danh sách Space khớp. LINEAR sẽ thông báo số component đã sửa.
10. Mở một vài room đại diện, kiểm tra bảng Transmission đã nhận đúng component `Bxxx`, orientation và adjoining condition.

## 5. Quy tắc an toàn khi replace

- Thực hiện từng Level hoặc từng nhóm Space trước.
- Luôn Preview trước Apply.
- Không dùng thickness làm điều kiện duy nhất vì một mặt phòng có thể gồm nhiều IFC Parts/layers.
- Nếu số hit trong LINEAR lớn hơn `ROOM FACES`, thu hẹp bằng Space Number/Name và Orientation.
- Nếu số hit nhỏ hơn, kiểm tra room enclosure, Room Bounding, adjoining condition và các opening/curtain panel.
- Lưu một bản project LINEAR trước khi replace hàng loạt.

## 6. Tài liệu LINEAR chính thức

- [Search and Replace Component Data](https://www.linear.eu/en/services/knowledge-base-revit/LS/V26/EN/RV/en/topics/geAn_t_btdSuchErsetz_ahl.html)
- [Details on Select Component](https://www.linear.eu/de/services/knowledge-base-revit/LS/V25/EN/RV/en/topics/erSc_r_selectComponent_BU_erSc.html)
- [Details on Composed Component](https://www.linear.eu/pl/services/knowledge-base-revit/LS/V25/EN/RV/en/topics/erSc_r_statbBauteilSchichtBU_erSc.html)
