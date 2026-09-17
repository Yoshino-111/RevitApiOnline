# FamilyMEP — test Revit 2020–2027

## Phạm vi

Bản test gồm **Family Creator, Drain Connection, Sprinkler, Exterior Wall Mapper và Smart Tag**.
Mỗi phiên bản Revit có DLL riêng trong `Bin/<năm>`; không dùng DLL 2025 cho Revit 2020.

Đây là bộ test tại workspace hiện tại. Giữ nguyên thư mục package bên trong
`RevitDevPortable/projects/RevitHotReload2025/packages` để tool tìm được `familymep-data`
(catalog, template, thư viện). Chưa phải bộ cài độc lập để gửi sang máy khác.
Sprinkler dùng PDF renderer chạy bằng .NET 8 và PDFium, được đóng gói theo từng năm.
Máy hiện tại đã có .NET 8. Máy test khác cần runtime .NET 8 và dữ liệu FamilyMEP tương ứng.

## Bật bản test cho Revit 2020

1. Đóng Revit.
2. Trong thư mục `FamilyMEP-Revit2020-2027-Test`, có thể double-click `REGISTER-Revit2020.cmd`.
   Hoặc mở PowerShell trong thư mục này.
3. Chạy:

```powershell
powershell -ExecutionPolicy Bypass -File .\register-test-version.ps1 -Year 2020
```

4. Mở Revit 2020, mở một project RVT 2020 và kiểm tra tab **FamilyMEP**.
5. Test trên bản sao project; lưu kết quả mỗi phiên bản vào thư mục riêng.

Thay `2020` bằng `2021`…`2027` để đăng ký năm khác. Script chỉ đăng ký năm được chọn.
Nếu báo đã có FamilyMEP/HotLoader/ExteriorWallMapper, script sẽ in đúng đường dẫn manifest
cần tạm vô hiệu hóa để tránh hai add-in cùng tạo ribbon. Script không tự ghi đè bản đang dùng.

Gỡ bản test của một năm:

```powershell
powershell -ExecutionPolicy Bypass -File .\register-test-version.ps1 -Year 2020 -Unregister
```

Giữ nguyên vị trí package sau khi đăng ký. Chưa đăng ký hay thay thế add-in đang dùng khi tạo package.

## Checklist test trong Revit

| Tool | Thao tác | Cần xác nhận |
|---|---|---|
| Ribbon | Khởi động Revit và mở lần lượt 5 tool | Đủ nút, cửa sổ mở/đóng được, không lỗi nạp DLL/XAML |
| Family Creator | Tạo một family từ template 2020; đổi kích thước; load vào project | Family tạo được, tham số và connector đúng; kiểm tra thêm các loại family thường dùng |
| Drain Connection | Chọn floor drain và main pipe; chạy case đang dùng | Cao độ, độ dốc, hướng dòng chảy, nối connector và fitting; kiểm tra log khi routing preferences thiếu fitting; thử Undo |
| Sprinkler | Mở PDF và DWG, chọn layer, preview; căn chỉnh/đặt vào view | PDF render được, đơn vị và tỷ lệ đúng; kiểm tra kết quả tạo ống theo workflow hiện tại; thử Cancel/Undo |
| Exterior Wall Mapper | Scan host và một linked model; focus kết quả; export Excel | Đúng phần tử, chiều dài/diện tích, vị trí highlight, nội dung Excel |
| Smart Tag | Preview và apply với tag host/link, có/không leader | Đúng tag, elbow/end, không mất tag; kiểm tra va chạm; Undo khôi phục được |

Ghi kết quả cho từng năm, không suy ra rằng chạy được trên 2020 thì mọi workflow đều chạy trên 2027.
Khi có lỗi, lưu tên tool, phiên bản/build Revit, bước thao tác, ElementId và nội dung lỗi/log.

## Khác biệt cần biết khi test

- Revit 2020 dùng API đơn vị cũ; 2021+ dùng Forge unit IDs.
- Revit 2020–2023 dùng ElementId 32-bit; 2024+ giữ nguyên ID 64-bit. Chuyển sang ID cũ có kiểm tra tràn số.
- Revit 2020–2021 dùng một đối tượng được tag và API leader cũ.
- Revit 2020–2022: chọn reference trong link chuyển thành chọn link instance; vẫn cần kiểm tra highlight/focus.
- Revit 2020–2023: không có collector lọc phần tử link theo host view. Smart Tag đọc link rồi giới hạn bằng vùng bố trí; có thể tính thêm obstacle đang ẩn trong view. Kiểm tra kỹ link có nhiều level/phase.
- RFA/RVT/RFT từ phiên bản mới hơn không tự chuyển xuống phiên bản cũ. Với Revit 2020 hãy dùng template/family 2020 hoặc cũ hơn; family master từ 2025 vẫn cần nguồn tương thích 2020.
- PDF renderer là tiến trình riêng dùng .NET 8, kể cả khi add-in chính chạy trong Revit 2020.

## Kiểm tra đã thực hiện

Kết quả compile từng năm nằm tại `multi-release/build-<năm>-FamilyMEP.Plugin.log` và
`multi-release/build-<năm>-FamilyMEP.Release.log` trong project source.
Compile thành công chỉ xác nhận liên kết API và cú pháp; chưa xác nhận các workflow trên model thật trong Revit.

Đã chạy bài test bố trí Smart Tag và bài test compatibility trên .NET Framework 4.8.
PDF renderer đã render và trích xuất thành công một PDF mẫu 1 trang, 1 đường vector; chưa test toàn bộ PDF thực tế.
Đã compile thành công plugin và entry/ribbon cho cả 8 năm 2020–2027 (0 lỗi build).
Chưa test trong Revit; dùng `TEST_RESULTS.csv` ghi kết quả thực tế. Log compile được chép vào `Verification`.
`SHA256.csv` trong package ghi hash từng file để kiểm tra bộ test.

Build lại từ source:

```powershell
.\build-test-versions.ps1
# Hoặc một năm:
.\build-test-versions.ps1 -Years 2020
```

Build API dùng bản Revit 2020–2025 cài trên máy, reference packs 2026.4.10 và 2027.1.0.
Runtime trong project: .NET Framework 4.8 cho 2020–2024, .NET 8 cho 2025–2026,
.NET 10 cho 2027. Autodesk xác nhận thay đổi ở
[Revit 2025](https://help.autodesk.com/cloudhelp/2025/CHS/Revit-API/files/Revit_API_Developers_Guide/Introduction/Getting_Started/Using_the_Autodesk_Revit_API/Revit_API_Revit_API_Developers_Guide_Introduction_Getting_Started_Using_the_Autodesk_Revit_API_NET8_Update_html.html)
và [Revit 2027](https://help.autodesk.com/cloudhelp/2027/ENU/Revit-WhatsNew/files/GUID-8D7A4715-EAF8-4BD1-BE78-061F900D0BCE.htm).
