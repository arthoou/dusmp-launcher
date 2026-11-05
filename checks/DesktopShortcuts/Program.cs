using DusmpLauncher.Installation;

var checks = 0;
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

void Reject(Action action, string message)
{
    try { action(); }
    catch (ArgumentException) { checks++; return; }
    throw new InvalidOperationException(message);
}

var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DUSMP João's launcher & checks"));
var target = Path.Combine(root, "Aplicativos Árvore", "DusmpLauncher.exe");
var desktop = Path.Combine(root, "OneDrive Organização", "Área de Trabalho");
var programs = Path.Combine(root, "Menu Iniciar redirecionado", "Programas");
var plan = LauncherShortcuts.PlanWindows(target, desktop, programs);
Require(plan.Count == 2, "Both Desktop and Start menu must receive a launcher shortcut.");
Require(plan[0].Path == Path.Combine(desktop, "DUSMP Launcher.lnk"), "The Desktop shortcut must use the supplied redirected folder.");
Require(plan[1].Path == Path.Combine(programs, "DUSMP Launcher", "DUSMP Launcher.lnk"), "The Start menu shortcut must use the supplied Programs folder.");
Require(plan.All(shortcut => shortcut.Target == target), "The target must retain Unicode, spaces, apostrophes and ampersands without shell escaping.");
Require(plan.All(shortcut => shortcut.WorkingDirectory == Path.GetDirectoryName(target)), "The working directory must be the actual launcher directory.");
Require(plan.All(shortcut => shortcut.IconLocation == target + ",0" && shortcut.Description == "DUSMP Launcher"), "Both shortcuts must use the launcher icon and description.");

// A dictionary models the writer's overwrite behavior without creating a real
// shortcut, touching the user's Desktop, or requiring Windows COM on Linux.
var written = new Dictionary<string, LauncherShortcuts.WindowsShortcut>();
var writes = new List<string>();
void Write(LauncherShortcuts.WindowsShortcut shortcut)
{
    written[shortcut.Path] = shortcut;
    writes.Add(shortcut.Path);
}
LauncherShortcuts.EnsureWindows(target, desktop, programs, Write);
LauncherShortcuts.EnsureWindows(target, desktop, programs, Write);
Require(written.Count == 2 && writes.Count == 4, "Repeated setup must overwrite the same two shortcuts without duplicates.");
Require(writes.All(path => plan.Any(shortcut => shortcut.Path == path)), "Setup must only write its own two shortcut filenames.");
var updatedTarget = Path.Combine(root, "Outro local de instalação", "DusmpLauncher.exe");
LauncherShortcuts.EnsureWindows(updatedTarget, desktop, programs, Write);
Require(written.Count == 2 && written.Values.All(shortcut => shortcut.Target == updatedTarget), "Rerunning setup from a different installation must refresh both existing targets.");

foreach (var name in new[] { "update-helper.exe", "DusmpUpdater.exe", "DusmpLauncher.dll", "DusmpLauncher.exe.tmp" })
    Reject(() => LauncherShortcuts.EnsureWindows(Path.Combine(root, name), desktop, programs, Write), $"{name} must never become a shortcut target.");
Require(writes.Count == 6, "Invalid targets must be rejected before any shortcut is written.");
Reject(() => LauncherShortcuts.PlanWindows("DusmpLauncher.exe", desktop, programs), "A relative target must be rejected.");
Reject(() => LauncherShortcuts.PlanWindows(target, "", programs), "An unavailable Desktop Known Folder must not fall back to the working directory.");
Reject(() => LauncherShortcuts.PlanWindows(target, desktop, "relative-programs"), "A relative Start menu folder must be rejected.");

if (!OperatingSystem.IsWindows())
{
    LauncherShortcuts.EnsureWindows("not-a-windows-executable");
    Require(true, "Public shortcut registration must be a no-op on non-Windows systems.");
}

Console.WriteLine($"DesktopShortcuts: {checks} checks passed (native Windows COM not exercised).");
