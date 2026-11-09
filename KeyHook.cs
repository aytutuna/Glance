using System.Runtime.InteropServices;

namespace Glance;

class KeyHook : IDisposable
{
    readonly Native.HookProc proc;
    readonly SynchronizationContext ui = SynchronizationContext.Current!;
    readonly HashSet<Keys> held = new();
    readonly Dictionary<Keys, long> swallowedDown = new();
    readonly Native.HookProc mouseProc;
    IntPtr hook;
    IntPtr mouseHook;
    Keys lone;
    long loneStart;
    bool fakeCtrl;
    bool away;

    // a repeat gap longer than this means the key went up unseen
    const long RepeatGap = 1500;

    public long LastSwallow { get; private set; }

    public Func<bool>? Swallow { get; init; }
    public Func<Keys, bool>? Bound { get; init; }
    public event Action<Keys>? Combo;
    public event Action<Keys, long>? Lone;
    public event Action? Released;

    public bool AnyHeld
    {
        get
        {
            Prune(Keys.None);
            return held.Count > 0;
        }
    }

    // Releases on the secure desktop are never seen. Swallowed keys never reach the key state,
    // so that state says nothing about them; instead they are forgotten when the dialog loses the
    // foreground (they stay in swallowedDown so repeats and releases are still eaten meanwhile).
    public void Poll()
    {
        Refocus(Swallow?.Invoke() == true);
    }

    public KeyHook()
    {
        proc = Callback;
        mouseProc = MouseCallback;
        for (int vk = 7; vk < 255; vk++)
        {
            if (vk is 0x10 or 0x11 or 0x12)
                continue;
            if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0)
                held.Add((Keys)vk);
        }
        hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
        mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, Native.GetModuleHandle(null), 0);
    }

    // a click or scroll with a modifier held is a chord, not a lone press
    IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (int)wParam is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN
            or Native.WM_XBUTTONDOWN or Native.WM_MOUSEWHEEL)
            lone = Keys.None;
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public static Keys ModifierFlag(Keys key)
    {
        return key switch
        {
            Keys.LControlKey or Keys.RControlKey => Keys.Control,
            Keys.LShiftKey or Keys.RShiftKey => Keys.Shift,
            Keys.LMenu or Keys.RMenu => Keys.Alt,
            Keys.LWin or Keys.RWin => KeyDialog.Win,
            _ => Keys.None,
        };
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
            return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        var key = (Keys)Marshal.ReadInt32(lParam);
        // our own masking key, which comes back through this hook
        if ((int)key == Native.Unassigned)
            return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        key = key switch
        {
            Keys.ControlKey => Keys.LControlKey,
            Keys.ShiftKey => Keys.LShiftKey,
            Keys.Menu => Keys.LMenu,
            _ => key,
        };
        int msg = (int)wParam;
        bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
        bool inDialog = Swallow?.Invoke() == true;
        Refocus(inDialog);
        long now = Environment.TickCount64;
        if (down && swallowedDown.TryGetValue(key, out long last) && now - last > RepeatGap)
        {
            swallowedDown.Remove(key);
            held.Remove(key);
        }
        Prune(Keys.None);
        bool swallow;
        if (down)
        {
            bool first = !held.Contains(key);
            // a repeat of a key whose first down got through must get through too
            swallow = swallowedDown.ContainsKey(key) || (inDialog && first);
            if (!swallow && first && key == Keys.PrintScreen && Bound != null && Bound(Compose(key)))
            {
                swallow = true;
                if (held.Any(k => k is Keys.LWin or Keys.RWin or Keys.LMenu or Keys.RMenu))
                    Native.TapUnassigned();
            }
            if (swallow)
            {
                swallowedDown[key] = now;
                LastSwallow = now;
            }
        }
        else
        {
            swallow = swallowedDown.Remove(key);
        }
        // AltGr sends a fake Left Ctrl with scan code 0x21D ahead of Right Alt
        if (key == Keys.LControlKey && Marshal.ReadInt32(lParam, 4) == 0x21D)
        {
            fakeCtrl = down;
        }
        else if (Track(key, down, inDialog))
        {
            // a lone Alt release would open the menu bar in the app underneath
            Native.TapUnassigned();
        }
        return swallow ? 1 : Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    void Refocus(bool inDialog)
    {
        if (Swallow == null)
            return;
        if (!inDialog && !away)
        {
            away = true;
            held.ExceptWith(swallowedDown.Keys);
        }
        else if (inDialog && away)
        {
            away = false;
            swallowedDown.Clear();
        }
    }

    void Prune(Keys except)
    {
        held.RemoveWhere(k => k != except && !swallowedDown.ContainsKey(k) && (Native.GetAsyncKeyState((int)k) & 0x8000) == 0);
    }

    Keys Compose(Keys key)
    {
        Prune(key);
        var keys = key;
        foreach (var k in held)
            keys |= ModifierFlag(k);
        // AltGr is Ctrl+Alt at runtime, but its fake Ctrl is kept out of held
        if (fakeCtrl && held.Contains(Keys.RMenu))
            keys |= Keys.Control;
        return keys;
    }

    // true when the release is a lone Alt that is the bound key
    bool Track(Keys key, bool down, bool swallowed)
    {
        bool active = Swallow == null || swallowed;
        if (!active)
            lone = Keys.None;

        if (!down)
        {
            held.Remove(key);
            bool mask = false;
            if (key == lone)
            {
                lone = Keys.None;
                long ms = Environment.TickCount64 - loneStart;
                if (Lone != null)
                    ui.Post(_ => Lone?.Invoke(key, ms), null);
                mask = Swallow == null && (key is Keys.LMenu or Keys.RMenu) && ms < 500 && Bound?.Invoke(key) == true;
            }
            if (held.Count == 0 && Released != null)
                ui.Post(_ => Released?.Invoke(), null);
            return mask;
        }

        if (!held.Add(key))
            return false;
        Prune(key);

        bool modifier = ModifierFlag(key) != Keys.None;
        if (active && held.Count == 1 && modifier)
        {
            lone = key;
            loneStart = Environment.TickCount64;
        }
        else
        {
            lone = Keys.None;
        }

        if (!modifier && active && Combo != null)
        {
            var keys = Compose(key);
            ui.Post(_ => Combo?.Invoke(keys), null);
        }
        return false;
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero)
            Native.UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
        if (mouseHook != IntPtr.Zero)
            Native.UnhookWindowsHookEx(mouseHook);
        mouseHook = IntPtr.Zero;
        Combo = null;
        Lone = null;
        Released = null;
    }
}
