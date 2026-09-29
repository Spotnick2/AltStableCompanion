using System.Runtime.InteropServices;
using Avalonia.Threading;
using static AltStableCompanion.App.Platform.NativeMethods;

namespace AltStableCompanion.App.Platform;

/// <summary>One row of the tray menu; a null <see cref="Text"/> is a separator.</summary>
internal sealed record TrayMenuItem(string? Text, Action? Chosen = null, bool Checked = false, bool Enabled = true);

/// <summary>
/// The notification-area icon, owned directly through Shell_NotifyIcon. Avalonia's TrayIcon
/// wraps the same call but exposes no NIF_INFO, so it cannot show a balloon.
///
/// What is easy to get wrong, all of it learned by somebody the hard way:
/// <list type="bullet">
/// <item>The window behind the icon is a hidden TOP-LEVEL window, not a message-only one
///   (HWND_MESSAGE). Message-only windows do not receive broadcasts, so "TaskbarCreated" would
///   never arrive and the icon would be gone for good after Explorer restarts.</item>
/// <item>The WndProc delegate is kept in a field. Windows holds a raw pointer to it; collected,
///   it is a crash some minutes later.</item>
/// <item>The icon is identified by window + id, not by a GUID: the shell ties a GUID to the
///   path of the exe, and an unsigned exe that is moved loses its icon.</item>
/// <item>The menu needs SetForegroundWindow before and a WM_NULL after, or it does not close
///   when the player clicks elsewhere. It runs a nested message loop, so what was chosen is
///   POSTED and runs after the menu is gone - Quit must not run inside it.</item>
/// <item>Created and destroyed on the UI thread: that is whose message loop pumps the window,
///   and DestroyWindow only works from the thread that created it.</item>
/// <item>NIM_ADD can fail - at logon the shell may not be up yet - and nothing announces when
///   it would succeed. It is tried again until it does.</item>
/// </list>
/// </summary>
internal sealed class Win32TrayIcon : IDisposable
{
    private const string ClassName = "AltStableCompanion.Tray";
    private const uint CallbackMessage = WM_APP + 1;
    private const uint IconId = 1;

    private readonly WndProc _wndProc;                         // kept alive: see above
    private readonly byte[] _ico;
    private readonly Func<IReadOnlyList<TrayMenuItem>> _menu;
    private readonly uint _taskbarCreated;
    private readonly nint _instance;
    private readonly DispatcherTimer _retry;
    private nint _hwnd;
    private nint _small;
    private nint _large;
    private string _tip;
    private bool _v4;
    private bool _menuOpen;
    private bool _disposed;

    /// <summary>The icon was clicked, or its balloon was.</summary>
    public event Action? Activated;

    /// <summary>The icon is in the tray, or could not be put there (yet).</summary>
    public event Action<bool>? PresenceChanged;

    public bool Present { get; private set; }

    public Win32TrayIcon(byte[] ico, string tip, Func<IReadOnlyList<TrayMenuItem>> menu)
    {
        Dispatcher.UIThread.VerifyAccess();
        System.Diagnostics.Debug.Assert(nint.Size != 8 || Marshal.SizeOf<NOTIFYICONDATAW>() == 976);

        _ico = ico;
        _tip = tip;
        _menu = menu;
        _wndProc = WindowProc;
        _instance = GetModuleHandleW(null);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        var wc = new WNDCLASSEXW
        {
            cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _instance,
            lpszClassName = ClassName,
        };
        if (RegisterClassExW(ref wc) == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastWin32Error()})");
        }
        _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, ClassName, "AltStable Companion", WS_POPUP,
            0, 0, 0, 0, 0, 0, _instance, 0);
        if (_hwnd == 0)
        {
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()})");
        }

        LoadIcons();
        _retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _retry.Tick += (_, _) => Add();
        Add();
    }

    public void SetTip(string tip)
    {
        if (_disposed || tip == _tip) return;
        _tip = tip;
        if (!Present) return;
        var data = Data(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>
    /// A balloon - a toast, on Windows 10 and 11. Best effort: Focus Assist holds them back
    /// while the game is full screen, which is when most portraits are written. The window's
    /// list is the record; this is the nudge.
    /// </summary>
    public void ShowBalloon(string title, string text)
    {
        if (_disposed || !Present) return;
        var data = Data(NIF_INFO);
        data.szInfoTitle = Fit(title, 63);
        data.szInfo = Fit(text, 255);
        data.dwInfoFlags = _large != 0 ? NIIF_USER | NIIF_LARGE_ICON : NIIF_USER;
        data.hBalloonIcon = _large;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private void Add()
    {
        if (_disposed) return;
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        var added = Shell_NotifyIconW(NIM_ADD, ref data);
        if (added)
        {
            data.uVersion = NOTIFYICON_VERSION_4;
            // Without version 4 the callback arrives in the old packing; remember which.
            _v4 = Shell_NotifyIconW(NIM_SETVERSION, ref data);
            _retry.Stop();
        }
        else
        {
            _retry.Start();
        }
        if (added != Present)
        {
            Present = added;
            PresenceChanged?.Invoke(added);
        }
    }

    private NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _small,
        szTip = Fit(_tip, 127),
        szInfo = "",
        szInfoTitle = "",
    };

    private static string Fit(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";

    private nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == CallbackMessage)
            {
                OnCallback(wParam, lParam);
                return 0;
            }
            if (msg == _taskbarCreated && _taskbarCreated != 0)
            {
                // Explorer started again and knows nothing of the icon it had.
                Present = false;
                LoadIcons();
                Add();
                return 0;
            }
            if (msg == WM_DISPLAYCHANGE)
            {
                // The small-icon size follows the display's scale.
                LoadIcons();
                if (Present)
                {
                    var data = Data(NIF_ICON);
                    Shell_NotifyIconW(NIM_MODIFY, ref data);
                }
            }
        }
        catch (Exception)
        {
            // An exception must not unwind into Windows' own code.
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void OnCallback(nint wParam, nint lParam)
    {
        // Version 4: the event is the low word of lParam and the pointer is in wParam.
        // Before it: the event is lParam, and the position has to be asked for.
        var what = _v4 ? (int)(lParam.ToInt64() & 0xFFFF) : (int)lParam.ToInt64();
        switch (what)
        {
            case NIN_SELECT or NIN_KEYSELECT when _v4:
            case WM_LBUTTONUP when !_v4:
            case NIN_BALLOONUSERCLICK:
                Post(Activated);
                break;
            case WM_CONTEXTMENU when _v4:
            case WM_RBUTTONUP when !_v4:
                ShowMenu(wParam);
                break;
        }
    }

    private void ShowMenu(nint wParam)
    {
        if (_menuOpen) return;
        // Signed 16-bit halves: a monitor left of or above the primary has negative coordinates.
        var x = (int)(short)(wParam.ToInt64() & 0xFFFF);
        var y = (int)(short)((wParam.ToInt64() >> 16) & 0xFFFF);
        if (!_v4 || (x == 0 && y == 0))
        {
            // Opened from the keyboard, or no position given: at the pointer.
            if (GetCursorPos(out var at)) (x, y) = (at.X, at.Y);
        }

        var items = _menu();
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        _menuOpen = true;
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Text is null)
                {
                    AppendMenuW(menu, MF_SEPARATOR, 0, null);
                    continue;
                }
                var flags = MF_STRING | (item.Checked ? MF_CHECKED : 0) | (item.Enabled ? 0 : MF_GRAYED);
                AppendMenuW(menu, flags, (nuint)(i + 1), item.Text);
            }

            SetForegroundWindow(_hwnd);
            var chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON, x, y, _hwnd, 0);
            PostMessageW(_hwnd, WM_NULL, 0, 0);
            if (chosen >= 1 && chosen <= items.Count) Post(items[chosen - 1].Chosen);
        }
        finally
        {
            DestroyMenu(menu);
            _menuOpen = false;
        }
    }

    private static void Post(Action? action)
    {
        if (action is not null) Dispatcher.UIThread.Post(action);
    }

    private void LoadIcons()
    {
        var small = IconFromIco(_ico, GetSystemMetrics(SM_CXSMICON));
        var large = IconFromIco(_ico, GetSystemMetrics(SM_CXICON));
        var (oldSmall, oldLarge) = (_small, _large);
        if (small != 0) _small = small;
        if (large != 0) _large = large;
        if (small != 0 && oldSmall != 0) DestroyIcon(oldSmall);
        if (large != 0 && oldLarge != 0) DestroyIcon(oldLarge);
    }

    /// <summary>
    /// An HICON from the bytes of an .ico file, at the size asked for. CreateIconFromResourceEx
    /// takes ONE image of the file, not the file: the directory is read here, and the image
    /// chosen is the smallest that is at least as large as wanted, else the largest there is.
    /// </summary>
    private static nint IconFromIco(byte[] ico, int want)
    {
        if (ico.Length < 6 || BitConverter.ToUInt16(ico, 2) != 1) return 0;
        var count = BitConverter.ToUInt16(ico, 4);
        var best = -1;
        var bestSize = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + i * 16;
            if (entry + 16 > ico.Length) break;
            var size = ico[entry] == 0 ? 256 : ico[entry];
            var better = best < 0
                || (bestSize < want && size > bestSize)
                || (size >= want && size < bestSize);
            if (better)
            {
                best = entry;
                bestSize = size;
            }
        }
        if (best < 0) return 0;

        var length = BitConverter.ToInt32(ico, best + 8);
        var offset = BitConverter.ToInt32(ico, best + 12);
        if (offset < 0 || length <= 0 || (long)offset + length > ico.Length) return 0;

        var bits = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.Copy(ico, offset, bits, length);
            return CreateIconFromResourceEx(bits, (uint)length, true, 0x00030000, want, want, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(bits);
        }
    }

    /// <summary>
    /// Take the icon out of the tray and nothing else. For when the process is ending on a
    /// thread that does not own the window: an icon left behind stays until the pointer passes
    /// over it.
    /// </summary>
    public void RemoveIcon()
    {
        if (!Present) return;
        Present = false;
        var data = Data(0);
        Shell_NotifyIconW(NIM_DELETE, ref data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Dispatcher.UIThread.VerifyAccess();
        _disposed = true;
        _retry.Stop();
        RemoveIcon();
        if (_hwnd != 0)
        {
            DestroyWindow(_hwnd);
            _hwnd = 0;
        }
        UnregisterClassW(ClassName, _instance);
        if (_small != 0) DestroyIcon(_small);
        if (_large != 0) DestroyIcon(_large);
        _small = _large = 0;
    }
}
