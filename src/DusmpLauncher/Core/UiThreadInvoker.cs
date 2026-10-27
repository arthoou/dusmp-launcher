namespace DusmpLauncher.Core;

/// <summary>
/// Runs work on the thread that owns the window toolkit loop.
///
/// The composition root builds the object graph before the window exists, but
/// the embedded sign-in window can only be created on that thread — GTK and
/// Win32 both reject windows built on a foreign thread. So dependents take this
/// indirection at construction time and the host fills in the real dispatcher
/// once Photino has a window.
///
/// Before that happens the work runs inline, which is correct for the only thing
/// that can reach it that early: a probe that touches no window at all.
/// </summary>
public sealed class UiThreadInvoker
{
    private Action<Action>? _dispatch;

    /// <summary>Called by the host once the window can accept work.</summary>
    public void Bind(Action<Action> dispatch) => _dispatch = dispatch;

    /// <summary>True once a real dispatcher is available.</summary>
    public bool IsBound => _dispatch is not null;

    /// <summary>
    /// Queues <paramref name="work"/> onto the UI thread. Exceptions are swallowed
    /// on purpose: this runs inside the toolkit's callback, where an escaping
    /// exception takes the whole process down. Callers that need a result signal
    /// it through their own TaskCompletionSource.
    /// </summary>
    public void Invoke(Action work)
    {
        var dispatch = _dispatch;
        try
        {
            if (dispatch is null) work();
            else dispatch(work);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"UI thread work failed: {ex.Message}");
        }
    }
}
