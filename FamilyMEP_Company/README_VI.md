# Revit 2025 Hot Reload DLL

`RevitHotLoader2025.dll` là bootstrap ổn định được Revit nạp một lần. DLL nghiệp vụ trong `plugin-output` được shadow-copy và nạp lại mỗi lần bấm **Run Latest DLL**.

## Build lần đầu

```powershell
.\build-all.cmd
```

## Chu trình sửa code không tắt Revit

1. Sửa code trong `RevitHotPlugin.Sample\SamplePlugin.cs`.
2. Chạy `build-plugin.cmd`.
3. Trong Revit bấm **FamilyMEP > Family Tools > Family Manager**.

File build gốc không bị Revit khóa vì Revit chạy một bản sao trong thư mục `shadow`.

DLL plugin được build ở cấu hình Debug và có file PDB đi kèm. Khi gắn debugger vào `Revit.exe`, debugger có thể nạp symbol từ bản DLL/PDB trong thư mục shadow.

## Phần hot-reload được

- Logic lệnh trong project `RevitHotPlugin.Sample`.
- WPF view/view-model và service được đặt trong plugin DLL.
- Dependency riêng được copy vào `plugin-output`.

## Phần cần khởi động lại Revit

- Code của bootstrap `RevitHotLoader2025`.
- Interface trong `RevitHotReload.Abstractions`.
- Việc tạo hoặc đổi Ribbon button của bootstrap.

Hãy giữ loader và interface thật nhỏ, còn code thay đổi thường xuyên đặt trong plugin.

## Quy tắc để unload thành công

Trong `Shutdown()` của plugin phải đóng mọi cửa sổ modeless, bỏ đăng ký Revit events, dừng timer và background task. Nếu vẫn còn reference, .NET không thể hoàn tất unload nhưng loader vẫn có thể shadow-copy và chạy bản DLL mới.

## Đăng ký vào Revit

File `deploy\RevitHotLoader2025.addin` là manifest mẫu. Revit chỉ nhận loader khi manifest này được sao chép vào thư mục Addins của Revit 2025. Project hiện không tự ghi vào ổ C.

## Kiểm tra cơ chế loader ngoài Revit

```powershell
.\run-smoke-test.cmd
```

Test phải báo rằng shadow DLL được gọi, DLL nguồn vẫn ghi được và `AssemblyLoadContext` đã unload.
