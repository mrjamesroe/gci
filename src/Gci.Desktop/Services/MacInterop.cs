using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Gci.Desktop.Services;

/// <summary>
/// The thin slice of the Objective-C runtime and AppKit/WebKit we need to embed a native WKWebView from .NET.
/// Everything here is macOS-only and must be called behind <see cref="OperatingSystem.IsMacOS"/>.
///
/// FIRST CUT — written on Windows and not yet run on macOS. The msgSend signatures (especially passing CGRect by
/// value on arm64) are the most likely thing to need adjustment once tested on a real Mac.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacInterop
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    // WebKit lives in a framework that isn't auto-loaded; touching a WebKit class first requires the dylib be present.
    // dlopen keeps it resident so objc_getClass("WKWebView") resolves.
    [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);
    private static bool _webKitLoaded;

    public static void EnsureWebKit()
    {
        if (_webKitLoaded) return;
        dlopen("/System/Library/Frameworks/WebKit.framework/WebKit", 2 /* RTLD_NOW */);
        _webKitLoaded = true;
    }

    [DllImport(ObjC, EntryPoint = "objc_getClass")] public static extern IntPtr GetClass(string name);
    [DllImport(ObjC, EntryPoint = "sel_registerName")] public static extern IntPtr Sel(string name);

    // objc_msgSend overloads — one per argument shape we use. IntPtr receiver + selector, then the args.
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr recv, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr recv, IntPtr sel, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr recv, IntPtr sel, string a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr recv, IntPtr sel, IntPtr a, IntPtr b);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] public static extern IntPtr SendFrame(IntPtr recv, IntPtr sel, CGRect frame, IntPtr config);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr recv, IntPtr sel, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr recv, IntPtr sel, IntPtr a, IntPtr b);
    // WKUserScript initWithSource:injectionTime:forMainFrameOnly: — NSString, NSInteger, BOOL (marshal BOOL as I1).
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendUserScript(IntPtr recv, IntPtr sel, IntPtr source, nint injectionTime, [MarshalAs(UnmanagedType.I1)] bool mainFrameOnly);

    // Runtime class creation — for the one custom NSObject subclass that serves as nav delegate + script-message handler.
    [DllImport(ObjC)] public static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, IntPtr extraBytes);
    [DllImport(ObjC)] public static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct CGRect(double x, double y, double width, double height)
    {
        public readonly double X = x, Y = y, Width = width, Height = height;
        public static CGRect Zero => new(0, 0, 0, 0);
    }

    /// <summary>A new autoreleased NSString from a managed string.</summary>
    public static IntPtr NSString(string value)
    {
        var cls = GetClass("NSString");
        return Send(cls, Sel("stringWithUTF8String:"), value);
    }

    /// <summary>Managed string from an NSString pointer (via -UTF8String), or null.</summary>
    public static string? FromNSString(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero) return null;
        var utf8 = Send(nsString, Sel("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }

    /// <summary>alloc + init of a class by name.</summary>
    public static IntPtr New(string className)
    {
        var cls = GetClass(className);
        return Send(Send(cls, Sel("alloc")), Sel("init"));
    }
}
