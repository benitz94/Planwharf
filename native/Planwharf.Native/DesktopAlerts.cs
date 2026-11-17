using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Planwharf.Native.Core;

namespace Planwharf.Native;

// Best effort: the desktop's quiet mode and notification permissions remain in charge.
sealed class DesktopAlerts(Window window) : IDisposable
{
    bool trayAdded;
    nint attention;
    nint lastHandle;
    nint Handle
    {
        get
        {
            if (window.TryGetPlatformHandle()?.Handle is { } handle && handle != 0) lastHandle = handle;
            return lastHandle;
        }
    }

    public void Show(WorkAlert alert) => Deliver(alert.Options, window.IsActive,
        () => Notify(alert.Message), () => Attention(true), PlaySound);

    internal static void Deliver(AlertOptions options, bool active, Action notify, Action flash, Action sound)
    {
        if (options.Notification) Try(notify);
        if (options.Flash && !active) Try(flash);
        if (options.Sound) Try(sound);
    }

    public void ClearAttention() => Try(() => Attention(false));

    public void Dispose()
    {
        ClearAttention();
        if (trayAdded) Try(() => { var data = TrayData(""); Shell_NotifyIcon(2, ref data); });
    }

    static void Try(Action action)
    {
        try { action(); }
        catch (Exception error) { Trace.WriteLine("Desktop alert unavailable: " + error.Message); }
    }

    void Notify(string message)
    {
        if (OperatingSystem.IsWindows())
        {
            var data = TrayData(message);
            if (!trayAdded) trayAdded = Shell_NotifyIcon(0, ref data);
            else Shell_NotifyIcon(1, ref data);
        }
        else if (OperatingSystem.IsMacOS())
            Run("/usr/bin/osascript", "-e", "display notification " + AppleString(message) + " with title \"Planwharf\"");
        else if (OperatingSystem.IsLinux())
            Run("notify-send", "--app-name=Planwharf", "--hint=boolean:suppress-sound:true", "--", "Planwharf", message);
    }

    internal static string AppleString(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"")
        .Replace("\r", " ").Replace("\n", " ") + "\"";

    static void Run(string program, params string[] args)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        // Never block the UI on a desktop helper; reap it and bound its lifetime.
        _ = RunAsync(start);
    }

    static async Task RunAsync(ProcessStartInfo start)
    {
        try
        {
            using var process = Process.Start(start);
            if (process == null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); }
        }
        catch (Exception error) { Trace.WriteLine("Desktop alert helper unavailable: " + error.Message); }
    }

    static void PlaySound()
    {
        if (OperatingSystem.IsWindows()) MessageBeep(0x40);
        else if (OperatingSystem.IsMacOS()) NSBeep();
        else if (OperatingSystem.IsLinux())
        {
            // The X server's configured short bell needs no sound file or extra package.
            var display = XOpenDisplay(0);
            if (display == 0) return;
            try { XBell(display, 0); XFlush(display); }
            finally { XCloseDisplay(display); }
        }
    }

    void Attention(bool on)
    {
        if (OperatingSystem.IsWindows())
        {
            var flash = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = Handle, Flags = on ? 14u : 0u };
            FlashWindowEx(ref flash); // FLASHW_TRAY | FLASHW_TIMERNOFG: until foreground.
        }
        else if (OperatingSystem.IsMacOS())
        {
            var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (attention != 0) SendArg(app, sel_registerName("cancelUserAttentionRequest:"), attention);
            attention = on ? SendArg(app, sel_registerName("requestUserAttention:"), 0) : 0; // critical: bounce until active
        }
        else if (OperatingSystem.IsLinux() && window.TryGetPlatformHandle()?.HandleDescriptor == "XID")
        {
            var display = XOpenDisplay(0);
            if (display == 0) return;
            nint hints = 0;
            try
            {
                hints = XGetWMHints(display, Handle);
                if (hints == 0) hints = XAllocWMHints();
                if (hints == 0) return;
                var flags = Marshal.ReadIntPtr(hints).ToInt64();
                Marshal.WriteIntPtr(hints, (nint)(on ? flags | 256 : flags & ~256)); // XUrgencyHint
                XSetWMHints(display, Handle, hints);
                XFlush(display);
            }
            finally { if (hints != 0) XFree(hints); XCloseDisplay(display); }
        }
    }

    TrayInfo TrayData(string message) => new()
    {
        Size = (uint)Marshal.SizeOf<TrayInfo>(), Window = Handle, Id = 11, Flags = 2 | 4 | 16,
        Icon = LoadIcon(0, (nint)32512), Tip = "Planwharf", Info = message[..Math.Min(255, message.Length)],
        Title = "Planwharf", InfoFlags = 1 | 16 // NIIF_INFO | NIIF_NOSOUND: sound is independently switched.
    };

    [StructLayout(LayoutKind.Sequential)]
    struct FlashInfo { public uint Size; public nint Window; public uint Flags, Count, Timeout; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TrayInfo
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    static extern bool Shell_NotifyIcon(uint action, ref TrayInfo data);
    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FlashInfo data);
    [DllImport("user32.dll")] static extern bool MessageBeep(uint type);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] static extern nint LoadIcon(nint instance, nint name);
    [DllImport("/System/Library/Frameworks/AppKit.framework/AppKit")] static extern void NSBeep();
    [DllImport("/usr/lib/libobjc.A.dylib")] static extern nint objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib")] static extern nint sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] static extern nint Send(nint obj, nint selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] static extern nint SendArg(nint obj, nint selector, nint arg);
    [DllImport("libX11.so.6")] static extern nint XOpenDisplay(nint name);
    [DllImport("libX11.so.6")] static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] static extern int XBell(nint display, int percent);
    [DllImport("libX11.so.6")] static extern int XFlush(nint display);
    [DllImport("libX11.so.6")] static extern nint XGetWMHints(nint display, nint window);
    [DllImport("libX11.so.6")] static extern nint XAllocWMHints();
    [DllImport("libX11.so.6")] static extern int XSetWMHints(nint display, nint window, nint hints);
    [DllImport("libX11.so.6")] static extern int XFree(nint data);
}
