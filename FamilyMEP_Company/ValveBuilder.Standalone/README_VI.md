# FamilyMEP Valve Builder - Standalone

Project này chỉ chứa tool tạo threaded ball-valve family để tiếp tục phát triển độc lập.

## Build cho Revit 2025

```powershell
dotnet build .\ValveBuilder.Standalone.csproj -c Debug -p:RevitVersion=2025
```

DLL sau khi build:

```text
bin\Debug\net8.0-windows\FamilyMEP.ValveBuilder.Plugin.dll
```

Giữ nguyên thư mục `Data` bên cạnh DLL. Master RFA và external Type Catalog nằm tại:

```text
Data\catalogs\BallValve
```

Entry point hot-reload:

```text
FamilyMEP.Plugin.ValveBuilderPlugin
```

Các file chính:

- `ValveBuilderPlugin.cs`: entry point.
- `Ui\ValveBuilderWindow.cs`: giao diện WPF.
- `Services\ValveBuilderService.cs`: Revit API và quy trình tạo family.
- `Models\ValveBuilderModels.cs`: request, result và catalog model.
- `Infrastructure\AppPaths.cs`: đường dẫn dữ liệu và output.

Catalog và family đầu ra dùng tên trung tính, không hiển thị tên hãng. Khi tạo family,
tool thử đưa hai reference plane `Defines Origin` về trung điểm của hai connector.
Nếu constraint hiện hữu không cho phép dịch origin, transaction này tự rollback để
không làm hỏng geometry.
