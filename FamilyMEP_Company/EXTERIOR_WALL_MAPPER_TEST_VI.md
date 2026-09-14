# Exterior Wall Mapper — hướng dẫn test Revit 2025

## Chuẩn bị model

1. File MEP phải có MEP Spaces đã đặt, có `Number`, `Name`, `Area` và boundary hợp lệ.
2. RVT kiến trúc link vào MEP phải bật `Room Bounding`.
3. Nếu RVT kiến trúc chứa IFC link lồng bên trong, IFC link phải đặt `Attachment` và bật `Room Bounding` trong file kiến trúc.
4. Load tất cả các link trước khi scan.

## Mở tool

Sau khi chạy `build-all.cmd` và restart Revit, mở:

`FamilyMEP > Building Analysis > Exterior Wall Mapper`

## Quy trình test

1. Bấm `SCAN LINEAR COMPONENTS`.
2. Kiểm tra Space Number, Space Name, Level, Orientation và LINEAR Type trong `ROOM / ORIENTATION`.
3. Chọn Level hoặc Space trong navigator để xác nhận bộ lọc theo `Space Number — Space Name`.
4. Mở `DETAIL` để kiểm tra structure, architectural wall type và thickness group.
5. Mở `LINEAR BATCH PLAN`, chọn EWA, EWI hoặc IWA.
6. Nhập short name LINEAR, ví dụ `B13`, vào `TARGET LINEAR U-VALUE`.
7. Bấm `COPY FIND & REPLACE PLAN` và xác nhận clipboard chứa đúng Space đang filter.
8. Bấm `EXPORT EXCEL`; workbook có năm sheet: Exterior Walls, Replacement Batches, LINEAR Plan, LINEAR Faces và Warnings.

Quy trình associate/replace trong LINEAR: xem `EXTERIOR_WALL_MAPPER_LINEAR_GUIDE_VI.md`.

## Phạm vi bản test đầu tiên

- Exterior được suy ra từ MEP Space boundary: phía đối diện không tìm thấy Space khác.
- Shaft hoặc vùng chưa đặt Space có thể xuất hiện với trạng thái `Review`.
- Native Revit Wall đọc được Compound Structure.
- IFC/DirectShape riêng lẻ được gom theo các element song song nằm trong vùng dày tối đa 1200 mm tính từ boundary.
- Diện tích hiện là gross boundary area; chưa trừ cửa và cửa sổ.
- Tool không ghi hoặc điều khiển dữ liệu nội bộ của LINEAR; cột Target LINEAR và Excel là replace plan để người dùng thao tác nhanh bằng Template Types hoặc Find & Replace.
