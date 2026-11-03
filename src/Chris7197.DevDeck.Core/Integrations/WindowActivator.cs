using System.Diagnostics;
using System.Runtime.InteropServices;

namespace chris7197.DevDeck.Core;

public static class WindowActivator
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);

    public static bool Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        ShowWindow(hwnd, 9); // SW_RESTORE
        if (!IsWindow(hwnd)) return false; // can close between check and restore
        SetForegroundWindow(hwnd);
        return IsWindow(hwnd) && GetForegroundWindow() == hwnd;
    }

    public static bool IsForegroundProcess(string processName)
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        try { using var p = Process.GetProcessById(checked((int)pid));
            return p.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    public static void FocusChatShortcut()
    {
        // VS Code default: Ctrl+Alt+I (workbench.action.chat.open).
        foreach (byte key in new byte[] { 0x11, 0x12, 0x49 }) keybd_event(key, 0, 0, UIntPtr.Zero);
        foreach (byte key in new byte[] { 0x49, 0x12, 0x11 }) keybd_event(key, 0, 2, UIntPtr.Zero);
    }

    public static void FocusChatInputShortcut()
    {
        // Ctrl+Down is bound to workbench.action.chat.focusInput while chat has focus.
        keybd_event(0x11, 0, 0, UIntPtr.Zero);
        keybd_event(0x28, 0, 0, UIntPtr.Zero);
        keybd_event(0x28, 0, 2, UIntPtr.Zero);
        keybd_event(0x11, 0, 2, UIntPtr.Zero);
    }
}
