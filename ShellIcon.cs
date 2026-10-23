using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KnobControl;

/// <summary>Loads the same icon Explorer shows for a Start-menu app or an executable.</summary>
internal static class ShellIcon
{
    public static ImageSource? For(MenuItemConfig item, int px = 96)
    {
        string? name = !string.IsNullOrWhiteSpace(item.AppId) ? @"shell:AppsFolder\" + item.AppId : item.Path;
        return string.IsNullOrWhiteSpace(name) ? null : Load(name, px);
    }

    static ImageSource? Load(string parsingName, int px)
    {
        try
        {
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            if (factory.GetImage(new SIZE { cx = px, cy = px }, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out IntPtr hbm) != 0 || hbm == IntPtr.Zero)
                return null;
            try { return ToBitmapSource(hbm); }
            finally { DeleteObject(hbm); }
        }
        catch (COMException)
        {
            return null;
        }
    }

    static BitmapSource ToBitmapSource(IntPtr hbm)
    {
        GetObject(hbm, Marshal.SizeOf<BITMAP>(), out BITMAP bm);
        BitmapSource src;
        if (bm.bmBits != IntPtr.Zero && bm.bmBitsPixel == 32)
        {
            // Read the DIB directly: CreateBitmapSourceFromHBitmap drops the alpha channel.
            int height = Math.Abs(bm.bmHeight);
            src = BitmapSource.Create(bm.bmWidth, height, 96, 96, PixelFormats.Pbgra32, null,
                bm.bmBits, bm.bmWidthBytes * height, bm.bmWidthBytes);
            src = new TransformedBitmap(src, new ScaleTransform(1, -1)); // shell DIBs are bottom-up
        }
        else
        {
            src = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        src.Freeze();
        return src;
    }

    const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    static extern int GetObject(IntPtr h, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr h);
}
