using System;
using System.Runtime.InteropServices;
using Godot;

namespace FootballSim.Game;

/// <summary>
/// macOS only: hides the app (so focus returns to the user's app) and opts out of App Nap while hidden.
/// Used by --background for automated runs. Every call is a no-op on other platforms.
/// </summary>
public static class MacBackground
{
    const string ObjC = "/usr/lib/libobjc.A.dylib";
    const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";
    // NSActivityUserInitiated (0x00FFFFFF | IdleSystemSleepDisabled) minus IdleSystemSleepDisabled = allow idle sleep.
    const ulong NSActivityUserInitiatedAllowingIdleSystemSleep = 0x00FFFFFFUL & ~(1UL << 20);

    [DllImport(ObjC)] static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Msg(IntPtr self, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr MsgPtr(IntPtr self, IntPtr sel, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr MsgActivity(IntPtr self, IntPtr sel, ulong options, IntPtr reason);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr MsgStr(IntPtr self, IntPtr sel, [MarshalAs(UnmanagedType.LPUTF8Str)] string s);

    static IntPtr _activity; // keeps the activity token alive for the process lifetime

    // Runs when the assembly loads, which is before the main scene exists.
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Early()
    {
        foreach (var a in System.Environment.GetCommandLineArgs()) if (a == "--background") { Apply(); return; }
    }

    public static void Apply()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            // Load Foundation so NSProcessInfo resolves (Godot already links AppKit).
            NativeLibrary.TryLoad(Foundation, out _);
            var nsString = objc_getClass("NSString");
            var reason = MsgStr(nsString, sel_registerName("stringWithUTF8String:"), "FootballSim background run");
            var info = Msg(objc_getClass("NSProcessInfo"), sel_registerName("processInfo"));
            _activity = MsgActivity(info, sel_registerName("beginActivityWithOptions:reason:"), NSActivityUserInitiatedAllowingIdleSystemSleep, reason);
            Msg(_activity, sel_registerName("retain"));

            var app = Msg(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            MsgPtr(app, sel_registerName("hide:"), IntPtr.Zero);
        }
        catch (Exception e) { GD.PushWarning($"--background: {e.Message}"); }

        if (DisplayServer.GetName() != "headless")
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, true);
    }
}
