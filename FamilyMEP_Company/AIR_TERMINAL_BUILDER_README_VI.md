# FamilyMEP Air Terminal Builder

Quy tắc lookup table, nested family, formula và constraint dùng chung được lưu tại
`FAMILYMEP_PARAMETRIC_AUTHORING_WORKFLOW_VI.md`.

Menu mới nằm tại `FamilyMEP > Air Terminal` và gồm 5 công cụ:

1. `1100 Perforated Diffuser`
2. `PLQ Plaque Diffuser`
3. `1900 Linear Slot Diffuser`
4. `5810 / 5815 Grille`
5. `S80 / S85 Return Grille`

## Workflow

- Mở một dự án RVT rồi chọn công cụ trong menu `Air Terminal`.
- Tool đọc trực tiếp toàn bộ Family Type bên trong RFA chính hãng.
- Chọn Type cần mở mặc định; mọi Type nguồn vẫn được giữ trong family đầu ra.
- File nguồn không bị sửa.
- File đầu ra dùng category `Air Terminals`, giữ geometry và duct connector của RFA nguồn.
- Đơn vị hiển thị được chuyển sang millimetres.
- Material được chuẩn hóa thành màu xám trung tính.
- Manufacturer, model, URL và description được để trống.
- Custom parameter có thể đổi tên được sẽ dùng tiền tố `FT_*`.
- Lookup table nhúng, nếu có, được copy/rename theo tên family đầu ra và formula được cập nhật.
- Family đầu ra được load vào project và mở tự động nếu tùy chọn này được bật.

## Catalog chính hãng

- 1100: https://www.krueger-hvac.com/file/4069/11001190_Series_MiniCatalog.pdf
- PLQ: https://www.krueger-hvac.com/file/4047/PLQ_5PLQ_MiniCatalog.pdf
- 1900: https://www.krueger-hvac.com/file/4083/1900_MiniCatalog.pdf
- 5810: https://www.krueger-hvac.com/file/3371/5810_DimensionalData.pdf
- S80: https://www.krueger-hvac.com/file/4100/S80_MiniCatalog.pdf

## Cập nhật ribbon

DLL plugin được hot-load sau mỗi lần build. Riêng thay đổi nút trên ribbon cần đóng toàn bộ
Revit rồi mở lại một lần vì Autodesk chỉ tạo ribbon khi khởi động add-in.
