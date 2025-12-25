using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace RevitApiOnline.Insulation
{
    /// <summary>
    /// App tạo tab "Luc Nguyen" + panel "Insulation" (cũ)
    /// + panel "Super Tool" (mới) cho Filter / Export / Rename.
    /// </summary>
    public class LucNguyenInsulationApp : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            const string tabName = "Luc Nguyen";
            const string insulationPanelName = "Insulation";
            const string superPanelName = "Super Tool";

            // 1. Tạo tab "Luc Nguyen" nếu chưa có
            try
            {
                application.CreateRibbonTab(tabName);
            }
            catch
            {
                // Tab đã tồn tại thì bỏ qua
            }

            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            // ============================================
            // PANEL 1: INSULATION  (GIỮ NGUYÊN CÁC NÚT CŨ)
            // ============================================
            RibbonPanel insulationPanel;
            try
            {
                insulationPanel = application.CreateRibbonPanel(tabName, insulationPanelName);
            }
            catch
            {
                // nếu panel đã có, lấy lại
                insulationPanel = FindPanel(application, tabName, insulationPanelName)
                                  ?? application.CreateRibbonPanel(insulationPanelName);
            }

            // --- 1. Nút Insulation ---
            AddButton(
                insulationPanel,
                assemblyPath,
                internalName: "LucNguyenInsulationButton",
                text: "Insulation",
                commandClass: "RevitApiOnline.Insulation.PipeDuctInsulationBinding",
                tooltip: "Luc Nguyen – Pipe & Duct Insulation.",
                largeImageFile: "Insulation32.png",
                smallImageFile: "Insulation16.png");

            // --- 2. Nút Schedule CSV ---
            AddButton(
                insulationPanel,
                assemblyPath,
                internalName: "LucNguyenScheduleCsvButton",
                text: "Schedule CSV",
                commandClass: "RevitApiOnline.Schedule.ScheduleExportImportBinding",
                tooltip: "Luc Nguyen – Schedule CSV export / import.",
                largeImageFile: "ScheduleCSV32.png",
                smallImageFile: "ScheduleCSV16.png");

            // --- 3. Nút Align 3D ---
            AddButton(
                insulationPanel,
                assemblyPath,
                internalName: "LucNguyenAlign3DButton",
                text: "Align 3D",
                commandClass: "RevitApiOnline.Align3D.CmdAlign3D",
                tooltip: "Luc Nguyen – Align 3D elements.",
                largeImageFile: "Align3D32.png",
                smallImageFile: "Align3D16.png");

            // --- 4. Nút Elbow 45° ---
            AddButton(
                insulationPanel,
                assemblyPath,
                internalName: "LucNguyenElbow45Button",
                text: "Elbow 45°",
                commandClass: "RevitApiOnline.Align3D.CmdElbow45_500",
                tooltip: "Luc Nguyen – Elbow 45° cho Pipe/Duct + đoạn ống 500mm.",
                largeImageFile: "Elbow45_32.png",
                smallImageFile: "Elbow45_16.png");

            // --- 5. Nút Align Branch ---
            AddButton(
                insulationPanel,
                assemblyPath,
                internalName: "LucNguyenAlignBranch3DButton",
                text: "Align Branch",
                commandClass: "RevitApiOnline.Align3D.CmdAlignBranch3D",
                tooltip: "Luc Nguyen – Align ống nhánh theo mặt ống trục trong 3D.",
                largeImageFile: "AlignBranch3D32.png",
                smallImageFile: "AlignBranch3D16.png");
            // nut branch Y
            AddButton(
                insulationPanel,
                assemblyPath,
                internalName: "LucNguyenAlignBranchButton",
                text: "Create Branch",
                commandClass: "RevitApiOnline.Align3D.CmdWyeBranch500",
                tooltip: "Luc Nguyen – Tạo Branch cho ống.",
                largeImageFile: "AlignBranch32.png",
                smallImageFile: "AlignBranch16.png");

            // --- 6. Nút Elbow 90° ---
            AddButton(
                insulationPanel,
                assemblyPath,
                internalName: "LucNguyenElbow90Button",
                text: "Elbow 90°",
                commandClass: "RevitApiOnline.Align3D.CmdElbow90_500",
                tooltip: "Luc Nguyen – Elbow 90° cho Pipe/Duct + đoạn ống 500mm.",
                largeImageFile: "Elbow90_32.png",
                smallImageFile: "Elbow90_16.png");

            // --- 7. Nút Space Mapping ---
            AddButton(
                insulationPanel,
                assemblyPath,
                internalName: "SpaceMapping",
                text: "Space Mapping",
                commandClass: "RevitApiOnline.SpaceMapping.SpaceMappingAppBinding",
                tooltip: "Luc Nguyen – Space Mapping.",
                largeImageFile: "Elbow90_32.png",
                smallImageFile: "Elbow90_16.png");

            return Result.Succeeded;
        }

        /// <summary>
        /// Helper: tìm panel theo tên trên một tab.
        /// </summary>
        private static RibbonPanel? FindPanel(UIControlledApplication app, string tabName, string panelName)
        {
            try
            {
                foreach (var panel in app.GetRibbonPanels(tabName))
                {
                    if (panel.Name == panelName)
                        return panel;
                }
            }
            catch
            {
                // ignore
            }
            return null;
        }

        private static void AddButton(
            RibbonPanel panel,
            string assemblyPath,
            string internalName,
            string text,
            string commandClass,
            string tooltip,
            string largeImageFile,
            string smallImageFile)
        {
            var data = new PushButtonData(
                internalName,
                text,
                assemblyPath,
                commandClass);

            var btn = panel.AddItem(data) as PushButton;
            if (btn == null) return;

            string folder = System.IO.Path.GetDirectoryName(assemblyPath);
            string imageFolder = System.IO.Path.Combine(folder, "Images");

            if (!string.IsNullOrEmpty(largeImageFile))
            {
                string largeImgPath = System.IO.Path.Combine(imageFolder, largeImageFile);
                if (System.IO.File.Exists(largeImgPath))
                {
                    btn.LargeImage = new System.Windows.Media.Imaging.BitmapImage(new Uri(largeImgPath));
                }
            }

            if (!string.IsNullOrEmpty(smallImageFile))
            {
                string smallImgPath = System.IO.Path.Combine(imageFolder, smallImageFile);
                if (System.IO.File.Exists(smallImgPath))
                {
                    btn.Image = new System.Windows.Media.Imaging.BitmapImage(new Uri(smallImgPath));
                }
            }

            btn.ToolTip = tooltip;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
