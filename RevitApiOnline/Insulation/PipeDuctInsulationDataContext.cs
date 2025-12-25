using RevitApiOnline.Insulation;
using System.Collections.ObjectModel;

namespace RevitApiOnline.Insulation
{
    /// <summary>
    /// Dữ liệu để bind với WPF cho tool insulation.
    /// </summary>
    public class PipeDuctInsulationDataContext
    {
        /// <summary>
        /// 0 = áp dụng cho toàn bộ model, 1 = chỉ Active View.
        /// </summary>
        public int ScopeOption { get; set; }

        // ===== INSULATION TYPE =====
        public ObservableCollection<InsulationTypeVM> PipeInsulationTypes { get; set; }
        public ObservableCollection<InsulationTypeVM> DuctInsulationTypes { get; set; }

        public InsulationTypeVM SelectedPipeInsulationType { get; set; }
        public InsulationTypeVM SelectedDuctInsulationType { get; set; }

        // ===== PIPE =====
        public bool EnablePipe { get; set; }
        /// <summary>Size ống tròn cần bọc (mm). Nếu > 0 thì chỉ bọc đúng size này.</summary>
        public double PipeMinSizeMm { get; set; }
        public string PipeSystemNameFilter { get; set; }
        public double PipeInsulationThicknessMm { get; set; }

        // ===== PIPE FITTINGS =====
        public bool EnablePipeFittings { get; set; }
        public string PipeFittingSystemNameFilter { get; set; }
        public double PipeFittingInsulationThicknessMm { get; set; }

        // ===== DUCT TRÒN =====
        public bool EnableDuctRound { get; set; }
        /// <summary>Đường kính duct tròn cần bọc (mm). Nếu > 0 thì chỉ bọc đúng size này.</summary>
        public double DuctRoundMinDiameterMm { get; set; }

        // ===== DUCT CHỮ NHẬT =====
        public bool EnableDuctRect { get; set; }
        /// <summary>Width cần bọc (mm).</summary>
        public double DuctRectMinWidthMm { get; set; }
        /// <summary>Height cần bọc (mm).</summary>
        public double DuctRectMinHeightMm { get; set; }

        // system + thickness dùng chung cho tất cả duct
        public string DuctSystemNameFilter { get; set; }
        public double DuctInsulationThicknessMm { get; set; }

        // ===== DUCT FITTINGS =====
        public bool EnableDuctFittings { get; set; }
        public string DuctFittingSystemNameFilter { get; set; }
        public double DuctFittingInsulationThicknessMm { get; set; }

        public PipeDuctInsulationDataContext()
        {
            // Phạm vi mặc định: toàn bộ model
            ScopeOption = 0;

            PipeInsulationTypes = new ObservableCollection<InsulationTypeVM>();
            DuctInsulationTypes = new ObservableCollection<InsulationTypeVM>();

            // Pipe
            EnablePipe = true;
            PipeMinSizeMm = 25;
            PipeSystemNameFilter = string.Empty;
            PipeInsulationThicknessMm = 25;

            // Pipe fittings
            EnablePipeFittings = true;
            PipeFittingSystemNameFilter = string.Empty;
            PipeFittingInsulationThicknessMm = 25;

            // Duct tròn
            EnableDuctRound = true;
            DuctRoundMinDiameterMm = 250;

            // Duct chữ nhật
            EnableDuctRect = true;
            DuctRectMinWidthMm = 250;
            DuctRectMinHeightMm = 100;

            DuctSystemNameFilter = string.Empty;
            DuctInsulationThicknessMm = 25;

            // Duct fittings
            EnableDuctFittings = true;
            DuctFittingSystemNameFilter = string.Empty;
            DuctFittingInsulationThicknessMm = 25;
        }
    }
}