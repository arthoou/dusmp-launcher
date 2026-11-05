namespace DusmpLauncher.Updates;

/// <summary>A kernel-owned lock; a crashed process cannot leave a stale PID lock behind.</summary>
public sealed class UpdateProcessLock : IDisposable
{
    private readonly FileStream _stream;
    private UpdateProcessLock(FileStream stream) => _stream = stream;

    public static UpdateProcessLock? TryAcquire(string lockPath)
    {
        lockPath = Path.GetFullPath(lockPath);
        var parent = Path.GetDirectoryName(lockPath)!;
        Directory.CreateDirectory(parent);
        RejectLinks(parent);
        if (File.Exists(lockPath)) RejectLinks(lockPath);
        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return new UpdateProcessLock(stream);
            }
            catch { stream.Dispose(); throw; }
        }
        catch (IOException) { return null; }
    }

    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Launcher updates cannot use symbolic links or reparse points.");
        }
    }

    public void Dispose() => _stream.Dispose();
}
