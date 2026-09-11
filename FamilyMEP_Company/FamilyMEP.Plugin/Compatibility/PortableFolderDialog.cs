using System.Windows;

namespace FamilyMEP.Plugin.Compatibility;

internal static class PortableFolderDialog
{
    public static bool TrySelect(Window owner, string title, string initialDirectory, out string folder)
    {
#if NET48
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = title,
            SelectedPath = initialDirectory,
            ShowNewFolderButton = true
        };
        var helper = new System.Windows.Interop.WindowInteropHelper(owner);
        System.Windows.Forms.DialogResult result = dialog.ShowDialog(new Win32Window(helper.Handle));
        folder = dialog.SelectedPath ?? string.Empty;
        return result == System.Windows.Forms.DialogResult.OK && folder.Length > 0;
#else
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title,
            InitialDirectory = initialDirectory
        };
        bool accepted = dialog.ShowDialog(owner) == true;
        folder = accepted ? dialog.FolderName : string.Empty;
        return accepted;
#endif
    }

#if NET48
    private sealed class Win32Window : System.Windows.Forms.IWin32Window
    {
        public Win32Window(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }
#endif
}
