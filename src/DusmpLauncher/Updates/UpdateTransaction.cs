using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace DusmpLauncher.Updates;

/// <summary>
/// Replaces only the launcher executable. The old, trusted executable runs the
/// helper, validates the signed release again, and retains the previous version
/// until the new process acknowledges its first working window.
/// </summary>
public static class UpdateTransaction
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly TimeSpan ParentTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(30);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static async Task<string> PrepareAsync(string executablePath, string stagedPath, string envelopeJson,
        string updateRoot, CancellationToken ct = default)
    {
        executablePath = ValidateTarget(executablePath);
        if (!SamePath(executablePath, Environment.ProcessPath ?? ""))
            throw new InvalidDataException("Only the currently running launcher can prepare its own update.");
        updateRoot = SecureDirectory(updateRoot);
        var manifest = ReleaseManifest.Verify(envelopeJson);
        var artifact = manifest.Select(ReleaseManifest.Os, ReleaseManifest.Arch);
        if (IsFailedVersion(updateRoot, manifest.Version))
            throw new IOException("This release previously failed to start; a different release is required.");
        await ReleaseManifest.VerifyFileAsync(stagedPath, artifact, ct).ConfigureAwait(false);
        ReleaseManifest.ValidateExecutable(stagedPath, artifact.Os);
        var directory = SecureDirectory(Path.Combine(updateRoot, Guid.NewGuid().ToString("N")));
        var plan = new Plan
        {
            Target = executablePath,
            UpdateRoot = updateRoot,
            Envelope = envelopeJson,
            Version = manifest.Version,
            Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            ParentId = Environment.ProcessId,
            ParentStart = ProcessStarted(Process.GetCurrentProcess()),
            OldSha256 = await HashAsync(executablePath, ct).ConfigureAwait(false),
            OldSize = new FileInfo(executablePath).Length,
        };
        var planPath = Path.Combine(directory, "plan.json");
        await CopyDurableAsync(stagedPath, StagePath(planPath), ct).ConfigureAwait(false);
        await ReleaseManifest.VerifyFileAsync(StagePath(planPath), artifact, ct).ConfigureAwait(false);
        await WritePlanAsync(planPath, plan, ct).ConfigureAwait(false);
        return planPath;
    }

    public static void StartHelper(string planPath)
    {
        var plan = ReadPlan(planPath);
        if (plan.Phase != "prepared" || !SamePath(plan.Target, Environment.ProcessPath ?? "") || plan.ParentId != Environment.ProcessId)
            throw new InvalidDataException("This update does not belong to the running launcher.");
        var helper = HelperPath(planPath);
        CopyDurableAsync(plan.Target, helper, CancellationToken.None).GetAwaiter().GetResult();
        if (new FileInfo(helper).Length != plan.OldSize || HashAsync(helper, CancellationToken.None).GetAwaiter().GetResult() != plan.OldSha256)
            throw new InvalidDataException("The update helper copy could not be verified.");
        MakeExecutable(helper, plan.Target);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(plan.Target)! };
        start.ArgumentList.Add("--apply-launcher-update");
        start.ArgumentList.Add(Path.GetFullPath(planPath));
        using var process = Process.Start(start) ?? throw new IOException("The update helper could not be started.");
    }

    public static async Task<int> ApplyAsync(string planPath, CancellationToken ct = default)
    {
        var plan = ReadPlan(planPath);
        if (!SamePath(Environment.ProcessPath ?? "", HelperPath(planPath)))
            throw new InvalidDataException("An update must run from its isolated trusted helper copy.");
        if (await HashAsync(HelperPath(planPath), ct).ConfigureAwait(false) != plan.OldSha256)
            throw new InvalidDataException("The update helper has changed.");
        using var parent = FindProcess(plan.ParentId, plan.ParentStart);
        if (parent is not null)
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waiting.CancelAfter(ParentTimeout);
            try { await parent.WaitForExitAsync(waiting.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return 3; }
        }
        using var updateLock = UpdateProcessLock.TryAcquire(Path.Combine(plan.UpdateRoot, "update.lock"));
        if (updateLock is null) return 2;
        using var instanceLock = UpdateProcessLock.TryAcquire(Path.Combine(plan.UpdateRoot, "instance.lock"));
        if (instanceLock is null) return 2;
        plan = ReadPlan(planPath);
        if (plan.Phase == "recovering")
        {
            await RollbackAsync(planPath, plan, "interrupted", ct).ConfigureAwait(false);
            instanceLock.Dispose();
            updateLock.Dispose();
            using var recovered = StartLauncher(plan.Target, "--update-recovered", plan.Version);
            return 0;
        }
        if (plan.Phase != "prepared") return 4;
        var manifest = ReleaseManifest.Verify(plan.Envelope);
        var artifact = manifest.Select(ReleaseManifest.Os, ReleaseManifest.Arch);
        Process? child = null;
        var instanceReleased = false;
        try
        {
            ValidateTarget(plan.Target);
            await VerifyOldAsync(plan.Target, plan, ct).ConfigureAwait(false);
            await ReleaseManifest.VerifyFileAsync(StagePath(planPath), artifact, ct).ConfigureAwait(false);
            ReleaseManifest.ValidateExecutable(StagePath(planPath), artifact.Os);
            var incoming = IncomingPath(planPath, plan);
            await CopyDurableAsync(StagePath(planPath), incoming, ct).ConfigureAwait(false);
            MakeExecutable(incoming, plan.Target);
            await ReleaseManifest.VerifyFileAsync(incoming, artifact, ct).ConfigureAwait(false);
            plan.Phase = "swapping";
            plan.HelperId = Environment.ProcessId;
            plan.HelperStart = ProcessStarted(Process.GetCurrentProcess());
            await WritePlanAsync(planPath, plan, ct).ConfigureAwait(false);
            ReplaceWithBackup(incoming, plan.Target, BackupPath(planPath, plan));
            plan.Phase = "installed";
            await WritePlanAsync(planPath, plan, ct).ConfigureAwait(false);
            await ReleaseManifest.VerifyFileAsync(plan.Target, artifact, ct).ConfigureAwait(false);
            // The new process must acquire the same instance lock at its entry point.
            instanceLock.Dispose();
            instanceReleased = true;
            child = StartLauncher(plan.Target, "--confirm-launcher-update", Path.GetFullPath(planPath), plan.Nonce);
            plan.ChildId = child.Id;
            plan.ChildStart = ProcessStarted(child);
            await WritePlanAsync(planPath, plan, ct).ConfigureAwait(false);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < HealthTimeout)
            {
                if (await IsHealthyAsync(planPath, plan, ct).ConfigureAwait(false))
                {
                    plan.Phase = "confirmed";
                    await WritePlanAsync(planPath, plan, ct).ConfigureAwait(false);
                    return 0;
                }
                if (child.HasExited) break;
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            // A user can close the window immediately after it acknowledged;
            // process exit must not race the final durable acknowledgement.
            if (await IsHealthyAsync(planPath, plan, ct).ConfigureAwait(false))
            {
                plan.Phase = "confirmed";
                await WritePlanAsync(planPath, plan, ct).ConfigureAwait(false);
                return 0;
            }
            await StopOwnedChildAsync(child).ConfigureAwait(false);
            child.Dispose(); child = null;
            throw new IOException("The updated launcher did not confirm a working window in time.");
        }
        catch (Exception ex)
        {
            if (child is not null) await StopOwnedChildAsync(child).ConfigureAwait(false);
            using var rollbackLock = UpdateProcessLock.TryAcquire(Path.Combine(plan.UpdateRoot, "instance.lock"));
            // If the swap has not happened, this process still owns instanceLock.
            if (plan.Phase is "swapping" or "installed")
            {
                if (rollbackLock is null && instanceReleased)
                    throw new IOException("Cannot restore while another launcher owns the installation.", ex);
                await RollbackAsync(planPath, plan, ex.GetType().Name, CancellationToken.None).ConfigureAwait(false);
                rollbackLock?.Dispose(); instanceLock.Dispose();
                updateLock.Dispose();
                using var recovered = StartLauncher(plan.Target, "--update-recovered", plan.Version);
            }
            else
            {
                await VerifyOldAsync(plan.Target, plan, CancellationToken.None).ConfigureAwait(false);
                plan.Phase = "failed";
                await WritePlanAsync(planPath, plan, CancellationToken.None).ConfigureAwait(false);
                await WriteAtomicAsync(Path.Combine(plan.UpdateRoot, "failed-version.json"),
                    new { version = plan.Version, at = DateTimeOffset.UtcNow, reason = ex.GetType().Name }, CancellationToken.None).ConfigureAwait(false);
                rollbackLock?.Dispose(); instanceLock.Dispose(); updateLock.Dispose();
                using var recovered = StartLauncher(plan.Target, "--update-recovered", plan.Version);
            }
            return 1;
        }
        finally { child?.Dispose(); }
    }

    public static async Task ConfirmAsync(string planPath, string nonce)
    {
        var plan = ReadPlan(planPath);
        if (plan.Phase != "installed" || !FixedEqual(plan.Nonce, nonce) || !SamePath(plan.Target, Environment.ProcessPath ?? ""))
            throw new InvalidDataException("Invalid update confirmation.");
        var manifest = ReleaseManifest.Verify(plan.Envelope);
        var version = typeof(UpdateTransaction).Assembly.GetName().Version?.ToString(3);
        if (!string.Equals(version, manifest.Version, StringComparison.Ordinal))
            throw new InvalidDataException("The running launcher version does not match the signed release.");
        await ReleaseManifest.VerifyFileAsync(plan.Target, manifest.Select(ReleaseManifest.Os, ReleaseManifest.Arch)).ConfigureAwait(false);
        var health = new Health { Nonce = nonce, ProcessId = Environment.ProcessId, ProcessStart = ProcessStarted(Process.GetCurrentProcess()) };
        await WriteAtomicAsync(HealthPath(planPath), health, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Call before the normal instance lock. True means exit: recovery is running or has been started.</summary>
    public static async Task<bool> RecoverAsync(string executablePath, string updateRoot)
    {
        executablePath = Path.GetFullPath(executablePath);
        if (!Directory.Exists(updateRoot)) return false;
        UpdateProcessLock.RejectLinks(updateRoot);
        using var updateLock = UpdateProcessLock.TryAcquire(Path.Combine(updateRoot, "update.lock"));
        if (updateLock is null) return !IsConfirmationInvocation(executablePath);
        foreach (var directory in Directory.EnumerateDirectories(updateRoot))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            var planPath = Path.Combine(directory, "plan.json");
            if (!File.Exists(planPath)) continue;
            Plan plan;
            try { plan = ReadPlan(planPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or ArgumentException)
            {
                Console.Error.WriteLine($"Ignoring an invalid launcher update journal in {Path.GetFileName(directory)}: {ex.GetType().Name}");
                continue;
            }
            if (!SamePath(plan.Target, executablePath) || plan.Phase is not ("swapping" or "installed" or "recovering")) continue;
            using var helper = FindProcess(plan.HelperId, plan.HelperStart);
            using var child = FindProcess(plan.ChildId, plan.ChildStart);
            if (helper is not null || child is not null) return !IsConfirmationInvocation(executablePath);
            if (await IsHealthyAsync(planPath, plan, CancellationToken.None).ConfigureAwait(false))
            {
                plan.Phase = "confirmed";
                await WritePlanAsync(planPath, plan, CancellationToken.None).ConfigureAwait(false);
                continue;
            }
            // The executable calling recovery is itself mapped by Windows. The
            // isolated helper must wait for this process to exit before restoring.
            var helperPath = HelperPath(planPath);
            await VerifyOldAsync(helperPath, plan, CancellationToken.None).ConfigureAwait(false);
            plan.ParentId = Environment.ProcessId;
            plan.ParentStart = ProcessStarted(Process.GetCurrentProcess());
            plan.Phase = "recovering";
            await WritePlanAsync(planPath, plan, CancellationToken.None).ConfigureAwait(false);
            var start = new ProcessStartInfo(helperPath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(plan.Target)! };
            start.ArgumentList.Add("--apply-launcher-update");
            start.ArgumentList.Add(Path.GetFullPath(planPath));
            using var recovery = Process.Start(start) ?? throw new IOException("The recovery helper could not be started.");
            return true;
        }
        return false;
    }

    public static bool IsFailedVersion(string updateRoot, string version)
    {
        var file = Path.Combine(updateRoot, "failed-version.json");
        if (!File.Exists(file)) return false;
        UpdateProcessLock.RejectLinks(file);
        if (new FileInfo(file).Length > 4096) throw new InvalidDataException("Invalid failed-release record.");
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        return document.RootElement.TryGetProperty("version", out var failed) && failed.GetString() == version;
    }

    private static async Task RollbackAsync(string planPath, Plan plan, string reason, CancellationToken ct)
    {
        var backup = BackupPath(planPath, plan);
        if (File.Exists(backup))
        {
            await VerifyOldAsync(backup, plan, ct).ConfigureAwait(false);
            UpdateProcessLock.RejectLinks(plan.Target);
            RestoreBackup(backup, plan.Target);
        }
        else await VerifyOldAsync(plan.Target, plan, ct).ConfigureAwait(false);
        await WriteAtomicAsync(Path.Combine(plan.UpdateRoot, "failed-version.json"),
            new { version = plan.Version, at = DateTimeOffset.UtcNow, reason }, ct).ConfigureAwait(false);
        plan.Phase = "rolledBack";
        await WritePlanAsync(planPath, plan, ct).ConfigureAwait(false);
    }

    private static async Task<bool> IsHealthyAsync(string planPath, Plan plan, CancellationToken ct)
    {
        try
        {
        var path = HealthPath(planPath);
        if (!File.Exists(path)) return false;
        UpdateProcessLock.RejectLinks(path);
        if (new FileInfo(path).Length > 4096) return false;
        var health = JsonSerializer.Deserialize<Health>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), Json);
        if (health is null || !FixedEqual(health.Nonce, plan.Nonce)) return false;
        if (plan.ChildId > 0 && (health.ProcessId != plan.ChildId || health.ProcessStart != plan.ChildStart)) return false;
        var release = ReleaseManifest.Verify(plan.Envelope);
        await ReleaseManifest.VerifyFileAsync(plan.Target, release.Select(ReleaseManifest.Os, ReleaseManifest.Arch), ct).ConfigureAwait(false);
        return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return false;
        }
    }

    internal static void ReplaceWithBackup(string incoming, string target, string backup)
    {
        UpdateProcessLock.RejectLinks(incoming); UpdateProcessLock.RejectLinks(target); UpdateProcessLock.RejectLinks(backup);
        if (File.Exists(backup)) throw new IOException("An update backup already exists.");
        File.Replace(incoming, target, backup, ignoreMetadataErrors: false);
    }

    internal static void RestoreBackup(string backup, string target)
    {
        UpdateProcessLock.RejectLinks(backup); UpdateProcessLock.RejectLinks(target);
        File.Move(backup, target, overwrite: true);
    }

    private static Plan ReadPlan(string planPath)
    {
        planPath = Path.GetFullPath(planPath);
        UpdateProcessLock.RejectLinks(planPath);
        if (Path.GetFileName(planPath) != "plan.json" || !Guid.TryParseExact(Path.GetFileName(Path.GetDirectoryName(planPath)), "N", out _)
            || !File.Exists(planPath) || new FileInfo(planPath).Length > 512 * 1024)
            throw new InvalidDataException("Invalid launcher update plan path.");
        var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(planPath), Json) ?? throw new InvalidDataException("Invalid update plan.");
        var root = Path.GetDirectoryName(Path.GetDirectoryName(planPath))!;
        if (plan.Schema != 1 || !SamePath(root, plan.UpdateRoot) || plan.Nonce?.Length != 64 || plan.OldSha256?.Length != 64
            || plan.OldSize <= 0 || plan.ParentId <= 0 || plan.ParentStart <= 0)
            throw new InvalidDataException("Invalid launcher update plan fields.");
        ValidateTarget(plan.Target, requireFile: false);
        var manifest = ReleaseManifest.Verify(plan.Envelope);
        if (manifest.Version != plan.Version) throw new InvalidDataException("The update plan version has changed.");
        return plan;
    }

    internal static string ValidateTarget(string path, bool requireFile = true)
    {
        path = Path.GetFullPath(path);
        UpdateProcessLock.RejectLinks(path);
        if (OperatingSystem.IsLinux())
        {
            foreach (var prefix in new[] { "/usr", "/opt", "/bin", "/sbin", "/lib", "/lib64", "/etc", "/var", "/snap", "/app" })
                if (path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("This launcher is managed by the system; use its package manager to update it.");
        }
        if (requireFile && !File.Exists(path)) throw new FileNotFoundException("The launcher executable does not exist.", path);
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new DirectoryNotFoundException("The launcher installation directory does not exist.");
        return path;
    }

    private static string SecureDirectory(string path)
    {
        path = Path.GetFullPath(path);
        UpdateProcessLock.RejectLinks(path);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static async Task CopyDurableAsync(string source, string destination, CancellationToken ct)
    {
        UpdateProcessLock.RejectLinks(source); UpdateProcessLock.RejectLinks(destination);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken ct)
    {
        UpdateProcessLock.RejectLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await JsonSerializer.SerializeAsync(stream, value, Json, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static Task WritePlanAsync(string path, Plan plan, CancellationToken ct) => WriteAtomicAsync(path, plan, ct);
    private static string StagePath(string path) => Path.Combine(Path.GetDirectoryName(path)!, "launcher.staged");
    private static string HealthPath(string path) => Path.Combine(Path.GetDirectoryName(path)!, "health.json");
    private static string HelperPath(string path) => Path.Combine(Path.GetDirectoryName(path)!, OperatingSystem.IsWindows() ? "update-helper.exe" : "update-helper");
    private static string IncomingPath(string path, Plan plan) => plan.Target + ".dusmp-new-" + Path.GetFileName(Path.GetDirectoryName(path));
    private static string BackupPath(string path, Plan plan) => plan.Target + ".dusmp-backup-" + Path.GetFileName(Path.GetDirectoryName(path));
    private static bool SamePath(string a, string b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && Path.GetFullPath(a).Equals(Path.GetFullPath(b), PathComparison);
    private static bool IsConfirmationInvocation(string target)
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length != 4 || args[1] != "--confirm-launcher-update") return false;
        var plan = ReadPlan(args[2]);
        return plan.Phase == "installed" && SamePath(plan.Target, target) && FixedEqual(plan.Nonce, args[3]);
    }
    private static bool FixedEqual(string? a, string? b) => a is not null && b is not null && a.Length == b.Length && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
    private static async Task<string> HashAsync(string file, CancellationToken ct)
    {
        await using var input = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
    }
    private static async Task VerifyOldAsync(string file, Plan plan, CancellationToken ct)
    {
        if (!File.Exists(file) || new FileInfo(file).Length != plan.OldSize || await HashAsync(file, ct).ConfigureAwait(false) != plan.OldSha256)
            throw new InvalidDataException("The previous launcher could not be verified for recovery.");
    }
    private static void MakeExecutable(string path, string original)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, File.GetUnixFileMode(original) | UnixFileMode.UserExecute);
    }
    // .NET derives Linux StartTime from wall-clock/uptime snapshots and can
    // differ by milliseconds between processes. Kernel start ticks are stable.
    private static long ProcessStarted(Process process)
    {
        if (OperatingSystem.IsLinux())
        {
            var stat = File.ReadAllText($"/proc/{process.Id}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return long.Parse(fields[19], System.Globalization.CultureInfo.InvariantCulture);
        }
        return process.StartTime.ToUniversalTime().Ticks;
    }
    private static Process? FindProcess(int id, long started)
    {
        if (id <= 0 || started <= 0) return null;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(id);
            if (!process.HasExited && ProcessStarted(process) == started) return process;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException) { }
        process?.Dispose(); return null;
    }
    private static Process StartLauncher(string target, params string[] args)
    {
        var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("The launcher could not be restarted.");
    }
    private static async Task StopOwnedChildAsync(Process child)
    {
        if (child.HasExited) return;
        try
        {
            child.CloseMainWindow();
            using var waiting = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await child.WaitForExitAsync(waiting.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { if (!child.HasExited) child.Kill(entireProcessTree: false); }
            await child.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }
    }

    private sealed class Health
    {
        public string Nonce { get; set; } = "";
        public int ProcessId { get; set; }
        public long ProcessStart { get; set; }
    }
    private sealed class Plan
    {
        public int Schema { get; set; } = 1;
        public string Target { get; set; } = "";
        public string UpdateRoot { get; set; } = "";
        public string Envelope { get; set; } = "";
        public string Version { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string OldSha256 { get; set; } = "";
        public long OldSize { get; set; }
        public int ParentId { get; set; }
        public long ParentStart { get; set; }
        public int HelperId { get; set; }
        public long HelperStart { get; set; }
        public int ChildId { get; set; }
        public long ChildStart { get; set; }
        public string Phase { get; set; } = "prepared";
    }
}
