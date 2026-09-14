# FamilyMEP Unified Family Creator

Quy tắc tạo family parametric dùng chung được lưu tại
`FAMILYMEP_PARAMETRIC_AUTHORING_WORKFLOW_VI.md`.

Ribbon chỉ dùng một nút:

`FamilyMEP > Family Creator`

## Thư viện tích hợp

### Pipe Accessories

1. Ball Valve
2. Gate Valve
3. Globe Valve
4. Angle Valve
5. Swing Check Valve
6. Ring Check Valve
7. Butterfly Valve

### Air Terminals

1. 1100 Perforated Diffuser
2. PLQ Plaque Diffuser
3. 1900 Linear Slot Diffuser
4. 5810 / 5815 Multi-Deflection Grille
5. S80 / S85 Return Grille

## Workflow

- Tìm kiếm và lọc Family ở cột trái.
- Chọn Type chính hãng ở vùng preview.
- Kiểm tra category, connector, lookup table, parameter và output ở cột phải.
- `Validate` kiểm tra source RFA, Type và đường dẫn output.
- `Create / Update Family` gọi lại service Valve hoặc Air Terminal tương ứng.
- Geometry, connector, catalog và constraint vẫn dùng workflow đã kiểm chứng trước đó.
- Family đầu ra chuyển sang millimetres, neutral gray, metadata hãng để trống.
- Custom parameter chỉnh sửa được sẽ đổi sang tên `FT_*`.
- Family được load vào project và có thể mở tự động sau khi tạo.

## Cập nhật ribbon

Việc đổi từ nhiều nút thành một nút cần đóng toàn bộ Revit và mở lại một lần.
Sau đó các lần sửa plugin tiếp theo tiếp tục dùng hot reload.
