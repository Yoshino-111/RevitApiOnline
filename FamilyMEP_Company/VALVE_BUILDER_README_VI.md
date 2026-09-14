# FamilyMEP — Create Valve Family

Tool tạo mới một Ball Valve RFA từ `Metric Generic Model.rft`.

## Chức năng hiện tại

- đổi Family Category thành `Pipe Accessories`;
- đặt Part Type thành `Valve - Breaks Into`;
- dựng một bộ native Family Extrusions gồm barrel, retaining ring, union nut,
  socket, bonnet, stem, retaining plate, bolt và lever handle;
- tạo đúng hai pipe connector đồng trục;
- tạo các Family Parameter `DN`, `L`, `Body OD`, `Port ID`, `H`, `Handle L`, `LTN`, `Connection`;
- tạo 9 Family Types: DN15, DN20, DN25, DN32, DN40, DN50, DN65, DN80 và DN100;
- xuất và import lookup CSV gồm 9 size từ DN15 đến DN100;
- gán công thức `size_lookup` cho `L`, `Body OD`, `Port ID`, `H` và `Handle L`;
- associate `Extrusion Start/End` với các parameter vị trí tính từ `L`;
- gắn diameter/linear dimensions của sketch bằng `FamilyLabel` vào các parameter
  `Body OD`, `Port ID`, `Handle L` và các kích thước dẫn xuất;
- dùng một void extrusion chạy theo `Port ID` để tạo bore xuyên thân;
- chỉ tạo một cặp connector; vị trí chạy theo `L` và đường kính chạy theo `DN`.
- lưu RFA, load vào project và tự mở Family kết quả nếu người dùng chọn.

Family không dùng các parameter `Show_DNxx` và không nhân bản geometry theo từng Type.
Mọi Type dùng chung một bộ native forms; lookup table thay đổi parameter và chính geometry đó flex.

## Debug khi Revit đang mở

1. Build plugin:

   ```powershell
   .\build-plugin.cmd
   ```

2. Trong project Revit, giữ `Shift` và bấm:

   ```text
   FamilyMEP > Family Tools > Family Manager
   ```

3. Chọn DN, kiểm tra preview, bấm `Run Validation`, sau đó bấm `Create RFA`.

DLL debug được xuất tại:

```text
plugin-output\FamilyMEP.Plugin.dll
```

Không cần đóng Revit khi dùng hot reload. Chỉ cần đóng cửa sổ Valve Builder cũ trước khi mở lại.
