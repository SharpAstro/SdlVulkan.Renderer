// Linux backend interop — WebKitGTK 4.1 + GTK3 + GDK-X11 + JavaScriptCore + GLib/GObject + Xlib.
// AOT-clean [LibraryImport] (source-generated marshalling), so no GetFunctionPointerForDelegate.
// These declarations compile on every TFM (they're metadata only); the libraries are probed
// lazily on first call, which only happens when GtkWebView is instantiated — i.e. on Linux.
//
// Pointer ownership: functions returning `const char*` (get_title, get_uri) return a string owned
// by the library — do NOT g_free it. Functions returning a freshly-allocated `char*` (jsc_value_to_*)
// must be released with g_free. WebKitWebView/UCM/JSCValue handles are owned by the view graph.
using System.Runtime.InteropServices;

namespace SdlVulkan.Renderer.WebView;

internal static partial class GtkInterop
{
    private const string Gtk = "libgtk-3.so.0";
    private const string Gdk = "libgdk-3.so.0";
    private const string Webkit = "libwebkit2gtk-4.1.so.0";
    private const string Jsc = "libjavascriptcoregtk-4.1.so.0";
    private const string GObject = "libgobject-2.0.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const string X11 = "libX11.so.6";

    // ---- GTK ----------------------------------------------------------------
    // gtk_init_check returns gboolean (4-byte int); 0 == failure (e.g. no X display).
    [LibraryImport(Gtk)] internal static partial int gtk_init_check(nint argc, nint argv);
    [LibraryImport(Gtk)] internal static partial nint gtk_window_new(int type);
    [LibraryImport(Gtk)] internal static partial void gtk_window_set_default_size(nint window, int width, int height);
    [LibraryImport(Gtk)] internal static partial void gtk_container_add(nint container, nint widget);
    [LibraryImport(Gtk)] internal static partial void gtk_widget_realize(nint widget);
    [LibraryImport(Gtk)] internal static partial void gtk_widget_show_all(nint widget);
    [LibraryImport(Gtk)] internal static partial void gtk_widget_hide(nint widget);
    [LibraryImport(Gtk)] internal static partial nint gtk_widget_get_window(nint widget);
    [LibraryImport(Gtk)] internal static partial void gtk_widget_grab_focus(nint widget);
    [LibraryImport(Gtk)] internal static partial void gtk_widget_destroy(nint widget);
    [LibraryImport(Gtk)] internal static partial void gtk_main();
    [LibraryImport(Gtk)] internal static partial void gtk_main_quit();

    // ---- GDK (X11 backend) --------------------------------------------------
    // Pin GTK to the X11 backend before gtk_init so gtk_widget_get_window yields an X11 GdkWindow
    // (under WSLg both WAYLAND_DISPLAY and DISPLAY are set, and GDK would otherwise pick Wayland).
    [LibraryImport(Gdk, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void gdk_set_allowed_backends(string backends);
    [LibraryImport(Gdk)] internal static partial ulong gdk_x11_window_get_xid(nint gdkWindow);

    // ---- WebKitGTK ----------------------------------------------------------
    [LibraryImport(Webkit)] internal static partial nint webkit_web_view_new();
    [LibraryImport(Webkit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void webkit_web_view_load_uri(nint webView, string uri);
    [LibraryImport(Webkit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void webkit_web_view_load_html(nint webView, string content, string? baseUri);
    [LibraryImport(Webkit)] internal static partial nint webkit_web_view_get_title(nint webView);     // const char* (don't free)
    [LibraryImport(Webkit)] internal static partial nint webkit_web_view_get_uri(nint webView);       // const char* (don't free)
    [LibraryImport(Webkit)] internal static partial nint webkit_web_view_get_user_content_manager(nint webView);
    [LibraryImport(Webkit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int webkit_user_content_manager_register_script_message_handler(nint ucm, string name);
    [LibraryImport(Webkit)] internal static partial void webkit_user_content_manager_add_script(nint ucm, nint userScript);
    [LibraryImport(Webkit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint webkit_user_script_new(string source, int injectedFrames, int injectionTime,
        nint allowList, nint blockList);
    [LibraryImport(Webkit)] internal static partial void webkit_user_script_unref(nint userScript);
    [LibraryImport(Webkit)] internal static partial nint webkit_javascript_result_get_js_value(nint jsResult);
    // length = gssize (-1 = NUL-terminated); callback = GAsyncReadyCallback fn ptr (nint.Zero = none).
    [LibraryImport(Webkit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void webkit_web_view_evaluate_javascript(nint webView, string script, nint length,
        string? worldName, string? sourceUri, nint cancellable, nint callback, nint userData);
    [LibraryImport(Webkit)] internal static partial nint webkit_web_view_evaluate_javascript_finish(
        nint webView, nint result, out nint error);

    // ---- JavaScriptCore -----------------------------------------------------
    [LibraryImport(Jsc)] internal static partial nint jsc_value_to_string(nint value);   // alloc -> g_free
    [LibraryImport(Jsc)] internal static partial nint jsc_value_to_json(nint value, uint indent); // alloc -> g_free

    // ---- GObject / GLib -----------------------------------------------------
    [LibraryImport(GObject, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial ulong g_signal_connect_data(nint instance, string detailedSignal, nint cHandler,
        nint data, nint destroyData, int connectFlags);
    [LibraryImport(GLib)] internal static partial uint g_idle_add(nint function, nint data); // GSourceFunc fn ptr
    [LibraryImport(GLib)] internal static partial void g_free(nint mem);
    [LibraryImport(GLib)] internal static partial void g_error_free(nint error);

    // ---- Xlib (reparent the WebKit GdkWindow into SDL's X11 window) ----------
    [LibraryImport(X11)] internal static partial nint XOpenDisplay(nint name);
    [LibraryImport(X11)] internal static partial int XCloseDisplay(nint display);
    [LibraryImport(X11)] internal static partial int XReparentWindow(nint display, ulong window, ulong parent, int x, int y);
    [LibraryImport(X11)] internal static partial int XMoveResizeWindow(nint display, ulong window, int x, int y, uint width, uint height);
    [LibraryImport(X11)] internal static partial int XMapWindow(nint display, ulong window);
    [LibraryImport(X11)] internal static partial int XUnmapWindow(nint display, ulong window);
    [LibraryImport(X11)] internal static partial int XSync(nint display, int discard);
}
