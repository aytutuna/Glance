using System.Runtime.InteropServices;

namespace Glance;

static class Native
{
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    public const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207, WM_MOUSEWHEEL = 0x20A, WM_XBUTTONDOWN = 0x20B;

    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, char[] text, int max);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out Rect rect, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    public static Rectangle? ReadableWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == GetShellWindow() || IsIconic(hwnd))
            return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId)
            return null;
        var name = new char[64];
        string cls = new string(name, 0, GetClassName(hwnd, name, name.Length));
        // the desktop, the taskbars and the hidden tray icons flyout (Windows 10 and 11)
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland")
            return null;
        // the extended bounds leave out the invisible resize borders
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out Rect r, Marshal.SizeOf<Rect>()) != 0 && !GetWindowRect(hwnd, out r))
            return null;
        var bounds = Rectangle.Intersect(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), SystemInformation.VirtualScreen);
        return bounds.IsEmpty ? null : bounds;
    }

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern nint GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern nint SetWindowLongPtr(IntPtr hWnd, int index, nint value);

    [DllImport("user32.dll")]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    // earlier builds treat the flag like WDA_MONITOR and draw the window black in captures
    public static readonly bool CanExcludeFromCapture = Environment.OSVersion.Version.Build >= 19041;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int hookId, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);

    public const int Unassigned = 0xE8;

    [StructLayout(LayoutKind.Sequential)]
    struct KeyboardInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public IntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput
    {
        public int Dx, Dy;
        public uint Data, Flags, Time;
        public IntPtr Extra;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Key;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, Input[] inputs, int size);

    // a key press the shell can't map to anything, so a held Win or Alt isn't read as a bare tap
    public static void TapUnassigned()
    {
        var inputs = new Input[2];
        inputs[0].Type = inputs[1].Type = 1;
        inputs[0].Data.Key.Vk = Unassigned;
        inputs[1].Data.Key.Vk = Unassigned;
        inputs[1].Data.Key.Flags = 2;
        SendInput(2, inputs, Marshal.SizeOf<Input>());
    }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(Point pt, uint flags);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    public static double ScaleFor(Screen screen)
    {
        var b = screen.Bounds;
        var center = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
        var monitor = MonitorFromPoint(center, 2);
        return GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
    }
}
