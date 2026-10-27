using System.Runtime.InteropServices;

namespace DusmpLauncher.Authentication;

/// <summary>
/// Microsoft sign-in hosted in a GTK/WebKit window the launcher owns, so the
/// authorization code is read off the redirect instead of copied by hand.
///
/// Why this exists: Minecraft's built-in client id accepts exactly one redirect,
/// <c>https://login.live.com/oauth20_desktop.srf</c>. Verified against the live
/// endpoint — <c>http://localhost</c> and any DUSMP-owned https callback are
/// refused, and the device-code token is rejected by Xbox Live with HTTP 400. A
/// redirect that cannot be listened on can only be watched, and Photino exposes
/// no navigation event, so the launcher brings its own window.
///
/// Photino already owns the GTK main loop on the process's main thread. Nothing
/// here starts a loop or calls gtk_init: the window is created through
/// <c>g_idle_add</c> so it lands on that thread, and the existing loop delivers
/// its signals.
/// </summary>
public sealed class LinuxWebKitLoginWindow : IEmbeddedLoginWindow
{
    private const string Gtk = "libgtk-3.so.0";
    private const string WebKit = "libwebkit2gtk-4.1.so.0";
    private const string GObject = "libgobject-2.0.so.0";
    private const string GLib = "libglib-2.0.so.0";

    private const int GtkWindowToplevel = 0;

    private readonly Action<Action> _invokeOnUiThread;

    /// <summary>
    /// Native code keeps raw pointers to these delegates. They are fields
    /// because a collected delegate is a hard crash the moment GTK calls back,
    /// and that is the single easiest mistake to make in this file.
    /// </summary>
    private GCallback? _uriChanged;
    private GCallback? _destroyed;

    public LinuxWebKitLoginWindow(Action<Action> invokeOnUiThread) => _invokeOnUiThread = invokeOnUiThread;

    public bool IsSupported => OperatingSystem.IsLinux() && LibrariesPresent();

    public Task<string?> CaptureCodeAsync(Uri authorizeUri, string redirectUri, CancellationToken ct)
    {
        if (!IsSupported)
            return Task.FromResult<string?>(null);

        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        _invokeOnUiThread(() =>
        {
            try
            {
                Open(authorizeUri, redirectUri, completion);
            }
            catch (Exception ex)
            {
                // Falling back to the manual flow beats taking the process down.
                Console.Error.WriteLine($"GTK sign-in window failed to open: {ex.Message}");
                completion.TrySetResult(null);
            }
        });

        // Cancelling must release the waiter; the window closes with it.
        var registration = ct.Register(() => completion.TrySetResult(null));
        return completion.Task.ContinueWith(task =>
        {
            registration.Dispose();
            return task.IsCompletedSuccessfully ? task.Result : null;
        }, TaskScheduler.Default);
    }

    private void Open(Uri authorizeUri, string redirectUri, TaskCompletionSource<string?> completion)
    {
        var window = gtk_window_new(GtkWindowToplevel);
        if (window == IntPtr.Zero)
            throw new InvalidOperationException("gtk_window_new returned null.");

        gtk_window_set_title(window, "Microsoft");
        gtk_window_set_default_size(window, 520, 680);

        var webView = webkit_web_view_new();
        if (webView == IntPtr.Zero)
        {
            gtk_widget_destroy(window);
            throw new InvalidOperationException("webkit_web_view_new returned null.");
        }

        gtk_container_add(window, webView);

        var finished = 0;

        void Finish(string? code)
        {
            // GTK can deliver both the URI change and destroy; only the first wins.
            if (Interlocked.Exchange(ref finished, 1) != 0) return;
            completion.TrySetResult(code);
            try { gtk_widget_destroy(window); } catch { /* already gone */ }
        }

        _uriChanged = (_, _, _) =>
        {
            try
            {
                var pointer = webkit_web_view_get_uri(webView);
                if (pointer == IntPtr.Zero) return;
                var uri = Marshal.PtrToStringUTF8(pointer);
                if (string.IsNullOrEmpty(uri) ||
                    !uri.StartsWith(redirectUri, StringComparison.OrdinalIgnoreCase))
                    return;

                // Deliberately not logged: this URL carries the authorization code.
                Finish(ReadQueryValue(uri, "code"));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"sign-in window navigation handler failed: {ex.GetType().Name}");
                Finish(null);
            }
        };

        _destroyed = (_, _, _) =>
        {
            // The player closed the window: a normal "no code", not a failure.
            if (Interlocked.Exchange(ref finished, 1) != 0) return;
            completion.TrySetResult(null);
        };

        Connect(webView, "notify::uri", _uriChanged);
        Connect(window, "destroy", _destroyed);

        webkit_web_view_load_uri(webView, authorizeUri.AbsoluteUri);
        gtk_widget_show_all(window);
    }

    /// <summary>Returns a query parameter, or null when the callback carried an error instead.</summary>
    internal static string? ReadQueryValue(string uri, string key)
    {
        var start = uri.IndexOf('?');
        if (start < 0) return null;

        var query = uri[(start + 1)..];
        var hash = query.IndexOf('#');
        if (hash >= 0) query = query[..hash];

        string? found = null;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.Split('=', 2);
            if (split.Length != 2) continue;
            var name = Uri.UnescapeDataString(split[0]);

            // An explicit error means there will never be a code; stop.
            if (name.Equals("error", StringComparison.OrdinalIgnoreCase)) return null;
            if (name.Equals(key, StringComparison.OrdinalIgnoreCase))
                found = Uri.UnescapeDataString(split[1]);
        }
        return string.IsNullOrWhiteSpace(found) ? null : found;
    }

    private static void Connect(IntPtr instance, string signal, GCallback callback) =>
        g_signal_connect_data(instance, signal, callback, IntPtr.Zero, IntPtr.Zero, 0);

    private static bool LibrariesPresent()
    {
        foreach (var library in new[] { Gtk, WebKit, GObject, GLib })
        {
            if (!NativeLibrary.TryLoad(library, out _))
                return false;
        }
        return true;
    }

    // Three arguments covers both signals used here: "notify::uri" passes the
    // GParamSpec and "destroy" passes nothing, and extra parameters are harmless
    // under the C calling convention.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GCallback(IntPtr instance, IntPtr argument, IntPtr userData);

    [DllImport(Gtk)] private static extern IntPtr gtk_window_new(int type);
    [DllImport(Gtk)] private static extern void gtk_window_set_title(IntPtr window, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
    [DllImport(Gtk)] private static extern void gtk_window_set_default_size(IntPtr window, int width, int height);
    [DllImport(Gtk)] private static extern void gtk_container_add(IntPtr container, IntPtr widget);
    [DllImport(Gtk)] private static extern void gtk_widget_show_all(IntPtr widget);
    [DllImport(Gtk)] private static extern void gtk_widget_destroy(IntPtr widget);

    [DllImport(WebKit)] private static extern IntPtr webkit_web_view_new();
    [DllImport(WebKit)] private static extern void webkit_web_view_load_uri(IntPtr webView, [MarshalAs(UnmanagedType.LPUTF8Str)] string uri);
    [DllImport(WebKit)] private static extern IntPtr webkit_web_view_get_uri(IntPtr webView);

    [DllImport(GObject)] private static extern ulong g_signal_connect_data(
        IntPtr instance,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string detailedSignal,
        GCallback handler,
        IntPtr data,
        IntPtr destroyData,
        int connectFlags);
}
