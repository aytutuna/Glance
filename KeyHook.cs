using System.Runtime.InteropServices;

namespace ScreenCopy;

class KeyHook : IDisposable
{
    readonly Native.HookProc proc;
    readonly SynchronizationContext ui = SynchronizationContext.Current!;
    readonly HashSet<Keys> held = new();
    readonly HashSet<Keys> swallowedDown = new();
    IntPtr hook;
    Keys lone;
    long loneStart;
    bool fakeCtrl;
    bool away;

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
        for (int vk = 7; vk < 255; vk++)
        {
            if (vk is 0x10 or 0x11 or 0x12)
                continue;
            if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0)
                held.Add((Keys)vk);
        }
        hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
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
        bool swallow;
        if (down)
        {
            bool first = !held.Contains(key);
            // a repeat of a key whose first down got through must get through too
            swallow = swallowedDown.Contains(key) || (inDialog && first);
            if (!swallow && first && key == Keys.PrintScreen && Bound != null && Bound(Compose(key)))
            {
                swallow = true;
                if (held.Any(k => k is Keys.LWin or Keys.RWin or Keys.LMenu or Keys.RMenu))
                    Native.TapUnassigned();
            }
            if (swallow)
                swallowedDown.Add(key);
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
            held.ExceptWith(swallowedDown);
        }
        else if (inDialog && away)
        {
            away = false;
            swallowedDown.Clear();
        }
    }

    void Prune(Keys except)
    {
        held.RemoveWhere(k => k != except && !swallowedDown.Contains(k) && (Native.GetAsyncKeyState((int)k) & 0x8000) == 0);
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
        if (held.Count == 1 && modifier)
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
        Combo = null;
        Lone = null;
        Released = null;
    }
}
