# FamilyMEP 2025 (C#)

Đây là bản C# WPF độc lập cho Revit 2025, không cần pyRevit. Ribbon được tạo tại tab `FamilyMEP`, panel `Family Tools`.

## Vị trí trên ổ F

- Source C#: `FamilyMEP.Plugin`
- DLL được build: `plugin-output\FamilyMEP.Plugin.dll`
- Loader cố định: `deploy\RevitHotLoader2025.dll`
- State, preview, backup, set và log: `familymep-data`
- Shadow-copy dùng để hot reload: `shadow`

Tool không tự ghi cấu hình, cache, preview hoặc log vào ổ C. Revit và .NET vẫn được đọc từ vị trí cài sẵn của máy công ty.

## Build và hot reload

1. Sửa source trong `FamilyMEP.Plugin`.
2. Chạy `build-plugin.cmd`.
3. Trong Revit bấm lại `Family Manager` tại tab `FamilyMEP`.

Loader sẽ đóng phiên FamilyMEP trước, copy DLL mới sang một thư mục shadow trên ổ F và nạp phiên mới. Không cần đóng Revit.

Nếu muốn build cả loader và plugin, chạy `build-all.cmd`. Khi chỉ sửa code của FamilyMEP, chỉ cần `build-plugin.cmd`.

## Đăng ký với Revit

Revit chỉ khám phá add-in khi có một manifest nhỏ trong thư mục add-in của user hiện tại. Việc này không cần quyền Administrator.

- Cài manifest: chạy `register-current-user.ps1`
- Gỡ manifest: chạy `unregister-current-user.ps1`

Đây là file duy nhất của bộ tool cần nằm trong profile user trên ổ C. File này chỉ trỏ tới loader DLL trên ổ F. Source, DLL, dữ liệu và file tạm vẫn ở ổ F.

## Chức năng đã nối C# thật

- Quét nhiều thư mục RFA trên ổ F và tự phân loại MEP.
- Tìm kiếm, sắp xếp, dạng grid/compact/list và lọc category.
- Xem thông tin, mở thư mục, mở family, load một hoặc nhiều family vào project.
- Preview từ view 3D do Revit xuất ra PNG trên ổ F.
- Save/Load family set.
- Đọc family parameter, batch rename, backup theo phiên, restore và export log CSV.
- Favorites bằng chuột phải trên family card.
- Báo cáo thống kê từ dữ liệu scan thật.

Batch Edit bị giới hạn với file nguồn trên ổ F để tránh sửa nhầm dữ liệu trên ổ hệ thống.
