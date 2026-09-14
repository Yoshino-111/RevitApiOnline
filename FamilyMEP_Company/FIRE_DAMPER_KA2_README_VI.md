# FamilyMEP – TROX KA2-EU Fire Damper

## Nguồn dữ liệu

- Catalog chính thức: `KA2-EU Product data sheet`, phiên bản `PD-05/2025 - DE/en`.
- B: 250–1200 mm, bước 25 mm.
- H: 250–500 mm, bước 25 mm.
- L: 580 mm cho H ≤ 400; 680 mm cho nhóm H > 400.
- L1/L2:
  - Nhóm L580: 275 / 305 mm.
  - Nhóm L680: 285 / 395 mm.
- Tổng số tổ hợp bên trong Family: 39 × 11 = 429 Types.

## Cấu trúc Family

- Template đầu vào: `Metric Generic Model 2020.rft`, không host.
- Category đầu ra: `Duct Accessories`.
- Part Type: `Damper`.
- Parent Family:
  - Casing rỗng, hai end flange, stiffener giữa.
  - Cánh damper mở theo trục ống.
  - Hai rectangular duct connectors, B × H.
  - Connector host là geometry kỹ thuật ẩn cố định, không dùng Visibility để đổi size.
- Nested Family:
  - Một Type duy nhất cho actuator + shaft.
  - Parent truyền `FT_LE_ZZ_KA2Sel`.
  - Nested có cùng lookup table và tự lấy H để dịch actuator theo size.

## Quy tắc parameter

- Selector duy nhất người dùng/type được phép thay đổi:
  - `FT_LE_ZZ_KA2Sel`
- Kích thước catalog:
  - `FT_LE_ZZ_B`, `FT_LE_ZZ_H`, `FT_LE_ZZ_L`, `FT_LE_ZZ_L1`, `FT_LE_ZZ_L2`
  - Luôn lấy từ `size_lookup`.
- Kích thước cố định:
  - Có formula literal, ví dụ `6 mm`, `20 mm`, `195 mm`.
- Kích thước dẫn xuất:
  - Có formula từ B/H/L và kích thước cố định.
- Không dùng nhiều nested Types hoặc Visibility để chuyển size.

## Kiểm tra trước khi xuất

- Kiểm tra đủ 429 khóa Type duy nhất.
- Kiểm tra range và bước 25 mm.
- Flex các mốc:
  - B250×H250.
  - B500×H400.
  - B500×H425 (mốc chuyển sang L680).
  - B1200×H500.
- Kiểm tra B/H/L và hai connector theo từng mốc.
- Kiểm tra parent selector truyền xuống nested actuator.
- Build plugin phải đạt 0 error, 0 warning.
