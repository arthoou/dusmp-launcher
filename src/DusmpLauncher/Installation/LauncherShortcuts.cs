using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DusmpLauncher.Installation;

/// <summary>Creates the current user's shortcuts after launcher setup completes.</summary>
public static class LauncherShortcuts
{
    private const string ShortcutName = "DUSMP Launcher.lnk";

    public static void EnsureWindows(string executablePath)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("The launcher executable does not exist.", executablePath);

        EnsureWindows(executablePath,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            WriteWindowsShortcut);
    }

    internal sealed record WindowsShortcut(string Path, string Target, string WorkingDirectory,
        string IconLocation, string Description);

    internal static IReadOnlyList<WindowsShortcut> PlanWindows(string executablePath,
        string desktopDirectory, string programsDirectory)
    {
        var target = AbsolutePath(executablePath, nameof(executablePath));
        // The updater also runs a copy of this program. Never leave a shortcut
        // pointing to its temporary helper, a framework .dll, or another tool.
        if (!string.Equals(System.IO.Path.GetFileName(target), "DusmpLauncher.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A shortcut must point to the installed DusmpLauncher.exe.", nameof(executablePath));

        var desktop = AbsolutePath(desktopDirectory, nameof(desktopDirectory));
        var programs = AbsolutePath(programsDirectory, nameof(programsDirectory));
        var workingDirectory = System.IO.Path.GetDirectoryName(target)!;
        WindowsShortcut Shortcut(string path) => new(path, target, workingDirectory, target + ",0", "DUSMP Launcher");
        return new[]
        {
            Shortcut(System.IO.Path.Combine(desktop, ShortcutName)),
            Shortcut(System.IO.Path.Combine(programs, "DUSMP Launcher", ShortcutName)),
        };
    }

    internal static void EnsureWindows(string executablePath, string desktopDirectory,
        string programsDirectory, Action<WindowsShortcut> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        foreach (var shortcut in PlanWindows(executablePath, desktopDirectory, programsDirectory))
            writer(shortcut);
    }

    private static string AbsolutePath(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (!System.IO.Path.IsPathFullyQualified(value))
            throw new ArgumentException("Shortcut paths must be absolute.", parameter);
        return System.IO.Path.GetFullPath(value);
    }

    [SupportedOSPlatform("windows")]
    private static void WriteWindowsShortcut(WindowsShortcut shortcut)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(shortcut.Path)!);
        object? shell = null;
        object? link = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("Windows shortcut support is unavailable.");
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Windows shortcut support could not be started.");
            link = ((dynamic)shell).CreateShortcut(shortcut.Path);
            dynamic windowsShortcut = link;
            windowsShortcut.TargetPath = shortcut.Target;
            windowsShortcut.WorkingDirectory = shortcut.WorkingDirectory;
            windowsShortcut.IconLocation = shortcut.IconLocation;
            windowsShortcut.Description = shortcut.Description;
            windowsShortcut.Arguments = string.Empty;
            windowsShortcut.Save();
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
