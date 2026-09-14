# Exterior Wall Mapper - Developer Update (Revit 2025)

Đây là gói cập nhật source nhẹ, không phải bộ cài độc lập và không phải MSI.

## Cách cập nhật trên máy khác

1. Máy đích phải có project nền `RevitHotReload2025` và môi trường `RevitDevPortable`.
2. Giải nén gói ZIP.
3. Chép toàn bộ nội dung thư mục đã giải nén vào thư mục gốc `RevitHotReload2025`, giữ nguyên cấu trúc và cho phép ghi đè.
4. Chạy `build-plugin.ps1`.
5. Khởi động lại Revit hoặc chạy lại DLL mới qua hot loader.

## Nội dung

- Source scanner, model, controller và Excel exporter của Exterior Wall Mapper.
- WPF XAML của cửa sổ tool.
- Điểm đăng ký plugin và project file.
- Smoke-test source (không tự chạy ngoài Revit).
- Script build và đóng gói developer update.

Các dependency, assets, WebView2 runtime và bộ cài không được lặp lại trong gói này vì đã có trong project nền.
