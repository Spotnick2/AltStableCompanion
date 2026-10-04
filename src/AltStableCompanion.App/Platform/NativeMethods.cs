using System.Runtime.InteropServices;

namespace AltStableCompanion.App.Platform;

/// <summary>The Win32 this app calls, and nothing it does not.</summary>
internal static class NativeMethods
{
    public const int WM_DESTROY = 0x0002;
    public const int WM_NULL = 0x0000;
    public const int WM_CONTEXTMENU = 0x007B;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_USER = 0x0400;
    public const int WM_APP = 0x8000;

    public const int NIN_SELECT = WM_USER + 0;
    public const int NIN_KEYSELECT = WM_USER + 1;
    public const int NIN_BALLOONUSERCLICK = WM_USER + 5;

    public const uint NIM_ADD = 0;
    public const uint NIM_MODIFY = 1;
    public const uint NIM_DELETE = 2;
    public const uint NIM_SETVERSION = 4;
    public const uint NOTIFYICON_VERSION_4 = 4;

    public const uint NIF_MESSAGE = 0x01;
    public const uint NIF_ICON = 0x02;
    public const uint NIF_TIP = 0x04;
    public const uint NIF_INFO = 0x10;
    public const uint NIF_SHOWTIP = 0x80;

    public const uint NIIF_USER = 0x04;
    public const uint NIIF_LARGE_ICON = 0x20;

    public const uint MF_STRING = 0x0000;
    public const uint MF_GRAYED = 0x0001;
    public const uint MF_CHECKED = 0x0008;
    public const uint MF_SEPARATOR = 0x0800;

    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_NONOTIFY = 0x0080;
    public const uint TPM_RETURNCMD = 0x0100;

    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;

    public const int SM_CXICON = 11;
    public const int SM_CXSMICON = 49;

    public const uint MB_ICONERROR = 0x10;
    public const uint MB_ICONINFORMATION = 0x40;

    public const uint ASFW_ANY = 0xFFFFFFFF;

    public delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public int cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    /// <summary>
    /// NOTIFYICONDATAW as Vista and later define it. Sequential, natural packing, the three
    /// strings INLINE (128, 256 and 64 UTF-16 units), the timeout/version union as one uint:
    /// 976 bytes on x64. The size is what tells the shell which layout it is being handed, so
    /// a wrong one is not a cosmetic mistake.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public nint lParam;
    }

    public const uint ABM_GETTASKBARPOS = 5;
    public const uint ABE_LEFT = 0;
    public const uint ABE_TOP = 1;

    // The resize border of a window, per side, at a DPI.
    public const int SM_CXFRAME = 32;
    public const int SM_CYFRAME = 33;
    public const int SM_CXPADDEDBORDER = 92;

    [DllImport("shell32.dll")]
    public static extern nuint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterClassW(string className, nint hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint hInstance, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern nint CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);

    // With TPM_RETURNCMD the "BOOL" it returns is the chosen command's id, or 0 for none.
    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hWnd, nint tpm);

    [DllImport("user32.dll")]
    public static extern nint CreateIconFromResourceEx(nint bits, uint size,
        [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandleW(string? name);

    // Downloads has no Environment.SpecialFolder: ask the shell, which knows where it was moved.
    private static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(nint pv);

    /// <summary>The player's Downloads folder; %USERPROFILE%\Downloads when the shell does not say.</summary>
    public static string DownloadsFolder()
    {
        if (SHGetKnownFolderPath(FOLDERID_Downloads, 0, 0, out var p) == 0 && p != 0)
        {
            try
            {
                if (Marshal.PtrToStringUni(p) is { Length: > 0 } path) return path;
            }
            finally
            {
                CoTaskMemFree(p);
            }
        }
        else if (p != 0)
        {
            CoTaskMemFree(p);
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
