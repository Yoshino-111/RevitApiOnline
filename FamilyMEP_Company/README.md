# FamilyMEP_Company

Source C# cua tab FamilyMEP trong Revit. Ban sao tu du an
`RevitDevPortable/projects/RevitHotReload2025` ngay 2026-09-11.

## Source

- `FamilyMEP.Plugin`: cac tool Family Manager, Valve Builder, Air Terminal,
  Drain Connection, Sprinkler Modeler, Exterior Wall Mapper, Smart Tag va cac thanh phan lien quan.
- `RevitHotLoader2025`: tao tab FamilyMEP va nap plugin khi phat trien.
- `FamilyMEP.Release`: entry point cho ban phat hanh.
- `RevitHotReload.Abstractions`, `Shared`, `Assets`: interface, ribbon va tai nguyen.
- `FamilyMEP.PdfRenderer`, `FamilyMEP.Packager`: tien ich ho tro.
- `tests`: source cac kiem tra san co.

Khong bao gom tool Python/pyRevit, SDK, Revit API DLL, output build,
shadow cache hay thu vien family ca nhan. Source C# duoc giu nguyen.

## Tiep tuc code tren may nha

Cai Revit 2025 va .NET SDK 8 tren Windows. Mo folder nay trong IDE.
Build tu PowerShell tai folder nay:

```powershell
dotnet build .\RevitHotLoader2025\RevitHotLoader2025.csproj -c Release -o .\deploy
dotnet build .\FamilyMEP.Plugin\FamilyMEP.Plugin.csproj -c Debug -o .\plugin-output
```

Revit API mac dinh nam tai `C:\Program Files\Autodesk\Revit 2025`.
NuGet restore can ket noi Internet. Cac script build goc can bo SDK portable
va `activate.ps1` o thu muc cha, nen khong chay truc tiep trong ban source nay.

Truoc khi nap vao Revit, sua cac duong dan trong `hotloader.json` va manifest
`.addin.template` theo vi tri clone tren may nha. Xem script dang ky va tai lieu
kem theo; can kiem tra cac duong dan co dinh truoc khi chay.
Build Revit 2026/2027 can doi duong dan reference API trong project va dung SDK
phu hop; cau hinh goc dang tro vao o F cua may cong ty.

Ban sao da doi chieu SHA-256 voi source goc. Chua build hoac chay thu trong Revit
tu folder tach rieng nay.
