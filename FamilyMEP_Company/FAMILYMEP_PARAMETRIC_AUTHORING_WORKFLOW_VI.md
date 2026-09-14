# FamilyMEP — Parametric Family Authoring Workflow

Đây là quy tắc bắt buộc khi tạo mới hoặc sửa family trong FamilyMEP.

## 1. Cấu trúc điều khiển

- Parent family chỉ truyền selector chính như `FT_LE_ZZ_NS`, `DN`, `Width` hoặc `TypeKey`.
- Geometry không được điều khiển bằng Visibility để thay đổi kích thước.
- Geometry phải bám Reference Plane/Reference Line bằng Align, Lock, Dimension và EQ.
- Family phức tạp được phép dùng nested family, nhưng mỗi bộ phận chỉ có một Type parametric.
- Parameter của nested phải được Associate lên parameter điều khiển của parent.

## 2. Kích thước thay đổi theo catalog

- Mọi kích thước thay đổi giữa các size phải lấy từ lookup table bằng `size_lookup(...)`.
- Parent và từng nested family có sử dụng dữ liệu catalog phải nhúng lookup table riêng.
- Parent chỉ truyền selector; nested tự đọc kích thước của nó từ lookup table.
- Không nhập thủ công giá trị catalog trực tiếp vào từng Type nếu lookup table có dữ liệu đó.

Ví dụ:

```text
FT_LE_ZZ_BladeD =
size_lookup("AirTerminal_TROX_RFD_Q_D_A_LTN", "BladeD", 1 mm, FT_LE_ZZ_NS)
```

## 3. Kích thước cố định

- Kích thước không thay đổi phải có formula dạng literal.
- Không để Value tự do cho người dùng sửa.

Ví dụ:

```text
FT_LE_ZZ_FaceT = 2 mm
FT_LE_ZZ_H1 = 8 mm
```

## 4. Kích thước dẫn xuất

- Kích thước tính từ parameter khác phải dùng formula.
- Không lặp dữ liệu dẫn xuất trong lookup table.

Ví dụ:

```text
FT_LE_ZZ_BladeR = FT_LE_ZZ_BladeD / 2
FT_LE_ZZ_CollarID = FT_LE_ZZ_CollarD - 3 mm
FT_LE_ZZ_SpigotX1 = FT_LE_ZZ_SpigotX0 + FT_LE_ZZ_SpigotL
```

## 5. Parameter được phép nhập

- Chỉ selector chính được phép không có formula.
- Các parameter kích thước `FT_LE_ZZ_*` khác bắt buộc có lookup formula, literal formula hoặc derived formula.
- Tool phải dừng tạo family nếu phát hiện parameter kích thước tùy chỉnh còn editable.

## 6. Kiểm tra trước khi xuất RFA

Tool phải flex qua tất cả size và kiểm tra:

- Lookup table tồn tại trong parent và các nested cần dữ liệu catalog.
- Selector parent đã Associate đúng vào nested.
- Geometry thay đổi đúng theo lookup table.
- Connector đúng kích thước, vị trí và hướng.
- Nested chỉ có một Type parametric, không dùng nhiều Type + Visibility.
- Không có parameter kích thước tùy chỉnh editable ngoài selector.
- Không có lỗi constraint, line quá ngắn, extrusion quá mỏng hoặc geometry tách rời.

## 7. Áp dụng hiện tại cho TROX RFD

- Parent, parametric core và parametric blade đều nhúng cùng bảng catalog.
- Parent truyền `FT_LE_ZZ_NS` vào core.
- Core truyền `FT_LE_ZZ_NS` vào blade.
- `FT_LE_ZZ_BladeD` trong core và blade tự dùng `size_lookup`.
- Các kích thước cố định/dẫn xuất đều bị khóa bằng formula.

