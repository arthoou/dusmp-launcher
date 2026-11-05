using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using DusmpLauncher.Updates;

internal static class TransactionChecks
{
    // The checks executable calls this before normal tests, so the lock proof
    // uses a real second process and then proves recovery after its death.
    public static int? TryRunProbe(string[] args)
    {
        if (args.Length != 2 || args[0] != "--transaction-lock-probe") return null;
        using var held = UpdateProcessLock.TryAcquire(args[1]);
        Console.WriteLine(held is null ? "busy" : "locked");
        Console.Out.Flush();
        Console.In.ReadLine();
        return held is null ? 2 : 0;
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dusmp-transaction-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "launcher with spaces & ç");
            var incoming = Path.Combine(root, "incoming");
            var backup = Path.Combine(root, "backup");
            var personal = Path.Combine(root, "config.json");
            await File.WriteAllTextAsync(target, "old trusted executable");
            await File.WriteAllTextAsync(incoming, "new verified executable");
            await File.WriteAllTextAsync(personal, "personal data");

            UpdateTransaction.ReplaceWithBackup(incoming, target, backup);
            Check(await File.ReadAllTextAsync(target) == "new verified executable", "Atomic installation publishes the new bytes.");
            Check(await File.ReadAllTextAsync(backup) == "old trusted executable", "The previous executable remains recoverable after installation.");
            Check(!File.Exists(incoming), "The sibling staging file is consumed by the atomic replacement.");
            UpdateTransaction.RestoreBackup(backup, target);
            Check(await File.ReadAllTextAsync(target) == "old trusted executable", "A failed or interrupted start can restore the previous bytes.");
            Check(await File.ReadAllTextAsync(personal) == "personal data", "Rollback preserves personal files.");

            await File.WriteAllTextAsync(incoming, "retry");
            await File.WriteAllTextAsync(backup, "existing backup");
            Expect<IOException>(() => UpdateTransaction.ReplaceWithBackup(incoming, target, backup));
            Check(await File.ReadAllTextAsync(target) == "old trusted executable", "A backup collision must leave the installation unchanged.");
            File.Delete(backup);

            var lockPath = Path.Combine(root, "locks", "instance.lock");
            using (var first = UpdateProcessLock.TryAcquire(lockPath))
            {
                Check(first is not null, "The first instance acquires the lock.");
                using var second = UpdateProcessLock.TryAcquire(lockPath);
                Check(second is null, "A second instance cannot update or launch concurrently.");
            }
            using (var again = UpdateProcessLock.TryAcquire(lockPath)) Check(again is not null, "Closing the owner releases the lock.");

            var entry = Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Missing test entry point.");
            var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test executable.");
            var start = new ProcessStartInfo(processPath) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
            if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(entry);
            start.ArgumentList.Add("--transaction-lock-probe");
            start.ArgumentList.Add(lockPath);
            using (var child = Process.Start(start) ?? throw new IOException("Cannot start the lock test process."))
            {
                try
                {
                    var line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Check(line == "locked", "An independent process acquires the shared OS lock.");
                    using var whileAlive = UpdateProcessLock.TryAcquire(lockPath);
                    Check(whileAlive is null, "The OS lock excludes a different process.");
                }
                finally
                {
                    if (!child.HasExited) child.Kill();
                    await child.WaitForExitAsync();
                }
            }
            using (var afterCrash = UpdateProcessLock.TryAcquire(lockPath)) Check(afterCrash is not null, "A crashed owner does not leave a stale lock.");

            await File.WriteAllTextAsync(Path.Combine(root, "failed-version.json"), "{\"version\":\"2.0.0\"}");
            Check(UpdateTransaction.IsFailedVersion(root, "2.0.0"), "A failed version is remembered to prevent restart loops.");
            Check(!UpdateTransaction.IsFailedVersion(root, "2.0.1"), "A later fixed release can still update normally.");

            if (!OperatingSystem.IsWindows())
            {
                var symlink = Path.Combine(root, "linked-incoming");
                File.CreateSymbolicLink(symlink, incoming);
                Expect<IOException>(() => UpdateTransaction.ReplaceWithBackup(symlink, target, backup));
                Check(await File.ReadAllTextAsync(target) == "old trusted executable", "A symbolic link cannot redirect an update.");
                if (Environment.UserName != "root")
                {
                    var readOnly = Path.Combine(root, "readonly");
                    Directory.CreateDirectory(readOnly);
                    var protectedTarget = Path.Combine(readOnly, "launcher");
                    var protectedNew = Path.Combine(readOnly, "incoming");
                    await File.WriteAllTextAsync(protectedTarget, "old");
                    await File.WriteAllTextAsync(protectedNew, "new");
                    File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    try
                    {
                        Expect<UnauthorizedAccessException>(() => UpdateTransaction.ReplaceWithBackup(protectedNew, protectedTarget, Path.Combine(readOnly, "backup")));
                        Check(await File.ReadAllTextAsync(protectedTarget) == "old", "Insufficient write permission preserves the old executable.");
                    }
                    finally { File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                }
            }
            Console.WriteLine("Transaction checks passed: atomic swap, rollback, personal data, collision, process locks, crash release, failed-release loop guard, links and permissions.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    /// <summary>Publishes isolated, signed test executables; no production key or trust bypass is involved.</summary>
    public static async Task RunNativeProcessChecksAsync([CallerFilePath] string sourceFile = "")
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("Native transaction harness currently requires Linux; Windows native validation remains separate.");
            return;
        }
        var repository = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        var root = Path.Combine(Path.GetTempPath(), "dusmp-native-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var owned = new List<int>();
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await File.WriteAllTextAsync(Path.Combine(root, "public.pem"), key.ExportSubjectPublicKeyInfoPem());
            var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup", new XElement("TargetFramework", "net8.0"), new XElement("OutputType", "Exe"),
                    new XElement("ImplicitUsings", "enable"), new XElement("Nullable", "enable"),
                    new XElement("AssemblyName", "UpdateHarness"), new XElement("PublishSingleFile", "true"),
                    new XElement("SelfContained", "true"), new XElement("RuntimeIdentifier", "linux-x64")),
                new XElement("ItemGroup", new[] { "ReleaseManifest.cs", "UpdateProcessLock.cs", "UpdateTransaction.cs" }
                    .Select(name => new XElement("Compile", new XAttribute("Include", Path.Combine(repository, "src", "DusmpLauncher", "Updates", name)), new XAttribute("Link", name))),
                    new XElement("EmbeddedResource", new XAttribute("Include", "public.pem"), new XAttribute("LogicalName", "DusmpLauncher.release-public-key.pem"))));
            await File.WriteAllTextAsync(Path.Combine(root, "Harness.csproj"), project.ToString());
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), NativeHarnessProgram);
            var dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"));
            foreach (var version in new[] { "1.0.0", "2.0.0" })
            {
                var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "publish", "Harness.csproj", "-c", "Release", "-p:Version=" + version, "-o", version, "--ignore-failed-sources" }) start.ArgumentList.Add(arg);
                using var build = Process.Start(start) ?? throw new IOException("Cannot build native transaction harness.");
                var output = build.StandardOutput.ReadToEndAsync(); var errors = build.StandardError.ReadToEndAsync();
                try { await build.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); }
                catch { if (!build.HasExited) build.Kill(entireProcessTree: true); throw; }
                Check(build.ExitCode == 0, $"Harness publish failed: {await output}\n{await errors}");
            }
            var old = Path.Combine(root, "1.0.0", "UpdateHarness");
            var updated = Path.Combine(root, "2.0.0", "UpdateHarness");
            var oldHash = await DigestAsync(old); var newHash = await DigestAsync(updated);
            var release = new ReleaseManifest
            {
                SchemaVersion = 1, Channel = "stable", Version = "2.0.0", MinimumVersion = "1.0.0", PublishedAt = DateTimeOffset.UtcNow,
                Artifacts = new()
                {
                    new() { Os = "linux", Arch = "x64", FileName = "DusmpLauncher", Url = "https://dusmp.online/launcher/releases/2.0.0/linux-x64/DusmpLauncher", Size = new FileInfo(updated).Length, Sha256 = newHash },
                    // This platform entry is metadata in an isolated test envelope, never a published release.
                    new() { Os = "windows", Arch = "x64", FileName = "DusmpLauncher.exe", Url = "https://dusmp.online/launcher/releases/2.0.0/windows-x64/DusmpLauncher.exe", Size = 128, Sha256 = new string('a', 64) },
                },
            };
            var payload = JsonSerializer.SerializeToUtf8Bytes(release, ReleaseManifest.Json);
            var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var envelope = Path.Combine(root, "release.json");
            await File.WriteAllTextAsync(envelope, JsonSerializer.Serialize(new { schemaVersion = 1, keyId = ReleaseManifest.KeyId, payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(signature) }));
            foreach (var mode in new[] { "success", "fail", "interrupted" })
            {
                var scenario = Path.Combine(root, mode); Directory.CreateDirectory(scenario);
                var target = Path.Combine(scenario, "launcher with spaces & ç"); File.Copy(old, target);
                var updates = Path.Combine(scenario, "updates");
                var personal = Path.Combine(scenario, "config.json"); await File.WriteAllTextAsync(personal, "preserve this user data");
                var marker = Path.Combine(scenario, "recovered.txt");
                ProcessStartInfo Start(params string[] args)
                {
                    var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = scenario };
                    foreach (var arg in args) start.ArgumentList.Add(arg);
                    start.Environment["DUSMP_HARNESS_MODE"] = mode == "interrupted" ? "hang" : mode;
                    start.Environment["DUSMP_HARNESS_ROOT"] = updates;
                    start.Environment["DUSMP_HARNESS_RECOVERED"] = marker;
                    return start;
                }
                using (var prepare = Process.Start(Start("--prepare", updated, envelope, updates)) ?? throw new IOException("Cannot start transaction parent."))
                {
                    owned.Add(prepare.Id);
                    await prepare.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    Check(prepare.ExitCode == 0, "Preparing the signed native transaction succeeds.");
                }
                var planPath = Directory.GetFiles(updates, "plan.json", SearchOption.AllDirectories).Single();
                if (mode == "interrupted")
                {
                    var installed = await WaitJournalAsync(planPath, p => p.GetProperty("phase").GetString() == "installed" && p.GetProperty("childId").GetInt32() > 0);
                    var helperId = installed.GetProperty("helperId").GetInt32(); var childId = installed.GetProperty("childId").GetInt32();
                    owned.Add(helperId); owned.Add(childId);
                    await StopTestProcessAsync(helperId); await StopTestProcessAsync(childId);
                    using var recovering = Process.Start(Start("--recover", updates)) ?? throw new IOException("Cannot restart interrupted update.");
                    owned.Add(recovering.Id);
                    await recovering.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    Check(recovering.ExitCode == 0, "An interrupted update schedules recovery outside the running executable.");
                }
                var final = await WaitJournalAsync(planPath, p => p.GetProperty("phase").GetString() == (mode == "success" ? "confirmed" : "rolledBack"));
                owned.Add(final.GetProperty("helperId").GetInt32()); owned.Add(final.GetProperty("childId").GetInt32());
                Check(await DigestAsync(target) == (mode == "success" ? newHash : oldHash), $"Native {mode} installs or restores the exact expected executable.");
                Check(await File.ReadAllTextAsync(personal) == "preserve this user data", "Native update and rollback preserve user data.");
                if (mode == "success")
                {
                    var backup = Directory.GetFiles(scenario, "*.dusmp-backup-*").Single();
                    Check(await DigestAsync(backup) == oldHash, "A confirmed release retains the previous executable backup.");
                }
                else
                {
                    var timer = Stopwatch.StartNew(); while (!File.Exists(marker) && timer.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(100);
                    Check(File.Exists(marker), "The recovered launcher actually restarts and reacquires the instance lock.");
                    Check(UpdateTransaction.IsFailedVersion(updates, "2.0.0"), "Native rollback persists the failed-release restart loop guard.");
                }
                Console.WriteLine($"Native Linux signed transaction passed: {mode} (real helper, replacement, child process and restart).");
            }
        }
        finally
        {
            foreach (var id in owned.Distinct().Where(x => x > 0)) await StopTestProcessAsync(id);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> DigestAsync(string path)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant();
    }
    private static async Task<JsonElement> WaitJournalAsync(string path, Func<JsonElement, bool> complete)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(45))
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            if (complete(doc.RootElement)) return doc.RootElement.Clone();
            await Task.Delay(100);
        }
        throw new TimeoutException("Native update journal did not reach the expected phase: " + await File.ReadAllTextAsync(path));
    }
    private static async Task StopTestProcessAsync(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            // Only children generated by this harness may be terminated during test cleanup.
            if (!process.HasExited && (process.ProcessName.Contains("launcher", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Contains("update-helper", StringComparison.OrdinalIgnoreCase)))
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
    }

    private const string NativeHarnessProgram = """
using DusmpLauncher.Updates;
if (args[0] == "--apply-launcher-update") return await UpdateTransaction.ApplyAsync(args[1]);
if (args[0] == "--confirm-launcher-update")
{
    var root = Path.GetDirectoryName(Path.GetDirectoryName(args[1]))!;
    if (await UpdateTransaction.RecoverAsync(Environment.ProcessPath!, root)) return 0;
    using var held = UpdateProcessLock.TryAcquire(Path.Combine(root, "instance.lock"));
    if (held is null) return 7;
    var mode = Environment.GetEnvironmentVariable("DUSMP_HARNESS_MODE");
    if (mode == "fail") return 12;
    if (mode == "hang") { await Task.Delay(120000); return 13; }
    await UpdateTransaction.ConfirmAsync(args[1], args[2]); return 0;
}
if (args[0] == "--update-recovered")
{
    var root = Environment.GetEnvironmentVariable("DUSMP_HARNESS_ROOT")!;
    if (await UpdateTransaction.RecoverAsync(Environment.ProcessPath!, root)) return 55;
    using var held = UpdateProcessLock.TryAcquire(Path.Combine(root, "instance.lock"));
    if (held is null) return 56;
    await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("DUSMP_HARNESS_RECOVERED")!, args[1]); return 0;
}
if (args[0] == "--recover") { await UpdateTransaction.RecoverAsync(Environment.ProcessPath!, args[1]); return 0; }
if (args[0] == "--prepare")
{
    Directory.CreateDirectory(args[3]);
    using var held = UpdateProcessLock.TryAcquire(Path.Combine(args[3], "instance.lock"));
    if (held is null) return 6;
    var plan = await UpdateTransaction.PrepareAsync(Environment.ProcessPath!, args[1], await File.ReadAllTextAsync(args[2]), args[3]);
    UpdateTransaction.StartHelper(plan); return 0;
}
return 9;
""";
}
