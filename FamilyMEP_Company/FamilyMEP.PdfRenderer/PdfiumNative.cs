using System.Runtime.InteropServices;

internal static class PdfiumNative
{
    internal const int ObjectPath = 2;
    internal const int ObjectForm = 5;
    internal const int SegmentLineTo = 0;
    internal const int SegmentBezierTo = 1;
    internal const int SegmentMoveTo = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Matrix
    {
        internal float A;
        internal float B;
        internal float C;
        internal float D;
        internal float E;
        internal float F;

        internal static Matrix Identity => new() { A = 1, D = 1 };

        internal readonly PdfPoint Transform(float x, float y) =>
            new(A * x + C * y + E, B * x + D * y + F);

        internal static Matrix Compose(Matrix parent, Matrix child) => new()
        {
            A = parent.A * child.A + parent.C * child.B,
            B = parent.B * child.A + parent.D * child.B,
            C = parent.A * child.C + parent.C * child.D,
            D = parent.B * child.C + parent.D * child.D,
            E = parent.A * child.E + parent.C * child.F + parent.E,
            F = parent.B * child.E + parent.D * child.F + parent.F
        };
    }

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDF_InitLibrary();

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDF_DestroyLibrary();

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDF_LoadDocument(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath,
        [MarshalAs(UnmanagedType.LPStr)] string? password);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDF_GetPageCount(IntPtr document);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDF_ClosePage(IntPtr page);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern float FPDF_GetPageWidthF(IntPtr page);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern float FPDF_GetPageHeightF(IntPtr page);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFPage_GetRotation(IntPtr page);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFPage_CountObjects(IntPtr page);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFPageObj_GetType(IntPtr pageObject);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFPageObj_GetMatrix(IntPtr pageObject, out Matrix matrix);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFPageObj_GetStrokeColor(
        IntPtr pageObject,
        out uint red,
        out uint green,
        out uint blue,
        out uint alpha);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFPageObj_GetStrokeWidth(IntPtr pageObject, out float width);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFPath_CountSegments(IntPtr pathObject);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFPath_GetPathSegment(IntPtr pathObject, int index);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFPathSegment_GetPoint(IntPtr segment, out float x, out float y);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFPathSegment_GetType(IntPtr segment);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFPathSegment_GetClose(IntPtr segment);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFFormObj_CountObjects(IntPtr formObject);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFFormObj_GetObject(IntPtr formObject, nuint index);
}
