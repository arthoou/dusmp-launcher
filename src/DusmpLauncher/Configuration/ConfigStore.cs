using System.Runtime.InteropServices;
using System.Text.Json;
using DusmpLauncher.Core;

namespace DusmpLauncher.Configuration;

/// <summary>
/// Raised when the config file could not be written. Carries a translation key
/// because every failure the player can see has to be expressible in their own
/// language; <see cref="Detail"/> stays untranslated for the log pane.
/// </summary>
public sealed class ConfigStoreException : Exception
{
    public ConfigStoreException(string errorKey, string detail, Exception? inner = null)
        : base($"{errorKey}: {detail}", inner)
    {
        ErrorKey = errorKey;
        Detail = detail;
    }

    public string ErrorKey { get; }

    /// <summary>Technical description, never translated and never shown alone.</summary>
    public string Detail { get; }
}

/// <summary>
/// Owns <c>config.json</c>. The player's whole setup — language, account choice,
/// optional mods, heap — lives in that one file, so losing it means dragging them
/// back through onboarding. Every write therefore lands atomically and every
/// unreadable file is kept aside instead of being overwritten.
/// </summary>
public sealed partial class ConfigStore : IConfigStore
{
    /// <summary>Heap the launcher falls back to when the machine will not say how much RAM it has.</summary>
    public const int FallbackMaxRamMb = 4096;

    /// <summary>Never hand the JVM less than this, or the game cannot even reach the title screen.</summary>
    private const int MinHeapFloorMb = 256;

    private const int MaxHeapFloorMb = 1024;
    private const int MaxHeapCeilingMb = 65536;

    /// <summary>Half of a 4 GB machine is below what a modded pack needs, so the default never goes under this.</summary>
    private const int DefaultHeapFloorMb = 2048;

    /// <summary>
    /// Past 8 GB extra heap buys nothing for this pack and only makes GC pauses
    /// longer, so the automatic default stops here even on a 64 GB machine.
    /// </summary>
    private const int DefaultHeapCeilingMb = 8192;

    private const int MinWindowWidth = 320;
    private const int MinWindowHeight = 240;
    private const int MaxWindowWidth = 7680;
    private const int MaxWindowHeight = 4320;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        // The file is hand-editable in practice: players paste JVM args into it,
        // so tolerate comments and a trailing comma rather than declaring the
        // whole setup lost over a stray character.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    private readonly LauncherPaths _paths;

    /// <summary>
    /// Serialises the whole load/save cycle. Several screens can save at once
    /// (language picker, optional mods, settings) and two interleaved writes to
    /// the same temp file would produce exactly the corrupt file this class
    /// exists to prevent.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private LauncherConfig _current = new();

    public ConfigStore(LauncherPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>
    /// The live config. Callers mutate it in place and then call
    /// <see cref="SaveAsync"/>; the values are already clamped, so a consumer
    /// can use <c>Game.MaxRamMb</c> without re-checking it.
    /// </summary>
    public LauncherConfig Current => _current;
    public string? LastRecoveryKey => LoadWarningKey;

    /// <summary>
    /// Translation key describing a problem found while loading, or null when
    /// the file was fine. Set when the previous config was quarantined, so the
    /// UI can explain why the player is seeing onboarding again.
    /// </summary>
    public string? LoadWarningKey { get; private set; }

    /// <summary>Untranslated detail for <see cref="LoadWarningKey"/>: the backup name and the parse error.</summary>
    public string? LoadWarningDetail { get; private set; }

    /// <summary>
    /// Reads the config from disk. A missing file is normal on a fresh install
    /// and yields <see cref="LauncherConfig.FirstLaunch"/>; a corrupt file is
    /// moved aside so support can still recover it.
    /// </summary>
    public async Task<LauncherConfig> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            LoadWarningKey = null;
            LoadWarningDetail = null;

            LauncherConfig? loaded = null;

            if (File.Exists(_paths.ConfigFile))
            {
                try
                {
                    await using var stream = new FileStream(
                        _paths.ConfigFile,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 4096,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);

                    loaded = await JsonSerializer
                        .DeserializeAsync<LauncherConfig>(stream, ReadOptions, ct)
                        .ConfigureAwait(false);

                    if (loaded is null)
                    {
                        // Valid JSON that is not an object (e.g. literal "null"):
                        // nothing to restore, but still worth keeping.
                        Quarantine("the file did not contain a configuration object");
                    }
                }
                catch (JsonException ex)
                {
                    Quarantine(ex.Message);
                    loaded = null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Locked or unreadable is not corruption — do not move a file
                    // we cannot even open, just tell the caller why the setup is
                    // missing this run.
                    LoadWarningKey = "error.config.unreadable";
                    LoadWarningDetail = ex.Message;
                    loaded = null;
                }
            }

            _current = loaded ?? new LauncherConfig { FirstLaunch = true };
            Normalize(_current);
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Writes the config to a temp file and moves it over the real one, so a
    /// crash or a power cut mid-save leaves the previous good file intact
    /// instead of a half-written one the next start cannot parse.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Normalising here as well as on load means a caller that assigned a
            // wild value from the settings screen cannot persist it.
            Normalize(_current);
            await WriteAsync(_current, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Mutates and saves under the same lock, for callers that must not have
    /// another save slip in between their change and the write.
    /// </summary>
    public async Task UpdateAsync(Action<LauncherConfig> mutate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            mutate(_current);
            Normalize(_current);
            await WriteAsync(_current, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Forces every stored value into a range the rest of the launcher can rely
    /// on. Anything out of range came from a hand edit or an older schema, and
    /// silently fixing it beats failing to launch over it.
    /// </summary>
    public static void Normalize(LauncherConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        config.Game ??= new GameSettings();
        config.OptionalMods ??= new Dictionary<string, bool>();
        config.SeenOptionalMods ??= new List<string>();

        var game = config.Game;

        // Unknown hand-edited presets act like a legacy custom setup. Choosing
        // a valid preset is still required before a new installation completes.
        game.JvmPreset = JvmPresets.IsKnown(game.JvmPreset)
            ? game.JvmPreset!.Trim().ToLowerInvariant()
            : null;

        game.DownloadConcurrency = Math.Clamp(game.DownloadConcurrency, 1, 16);

        // 0 is the documented "decide for me": resolve it once, here, so nothing
        // downstream has to know how to guess a heap size.
        if (game.MaxRamMb <= 0)
            game.MaxRamMb = DetectDefaultMaxRamMb();

        game.MaxRamMb = Math.Clamp(game.MaxRamMb, MaxHeapFloorMb, MaxHeapCeilingMb);

        // -Xms above -Xmx makes the JVM refuse to start, so the minimum can
        // never win over the maximum.
        game.MinRamMb = Math.Clamp(game.MinRamMb, MinHeapFloorMb, game.MaxRamMb);

        game.ExtraJvmArgs = game.ExtraJvmArgs?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(game.JavaPath))
            game.JavaPath = null;

        // A zero or absurd window size reaches the game as --width/--height and
        // gives an unusable window, so fall back to the documented defaults.
        game.WindowWidth = game.WindowWidth <= 0
            ? new GameSettings().WindowWidth
            : Math.Clamp(game.WindowWidth, MinWindowWidth, MaxWindowWidth);

        game.WindowHeight = game.WindowHeight <= 0
            ? new GameSettings().WindowHeight
            : Math.Clamp(game.WindowHeight, MinWindowHeight, MaxWindowHeight);

        if (config.SchemaVersion <= 0)
            config.SchemaVersion = 1;
    }

    /// <summary>
    /// Heap to use when the player never chose one: about half of installed RAM,
    /// bounded so a 4 GB laptop still gets a playable pack and a workstation does
    /// not hand the JVM 32 GB it will only make GC pauses out of.
    /// </summary>
    public static int DetectDefaultMaxRamMb()
    {
        var totalMb = DetectPhysicalMemoryMb();
        if (totalMb <= 0)
            return FallbackMaxRamMb;

        var half = (int)Math.Min(totalMb / 2, int.MaxValue);
        return Math.Clamp(half, DefaultHeapFloorMb, DefaultHeapCeilingMb);
    }

    /// <summary>
    /// Installed physical RAM in MB, or 0 when it cannot be determined. Reads
    /// <c>/proc/meminfo</c> on Linux and asks the kernel directly on Windows —
    /// managed APIs report the GC's view, not the machine's.
    /// </summary>
    public static long DetectPhysicalMemoryMb()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsPhysicalMemoryMb();

            if (OperatingSystem.IsLinux())
                return LinuxPhysicalMemoryMb();
        }
        catch (Exception ex) when (ex is IOException
                                     or UnauthorizedAccessException
                                     or FormatException
                                     or OverflowException
                                     or DllNotFoundException
                                     or EntryPointNotFoundException)
        {
            return 0;
        }

        return 0;
    }

    private async Task WriteAsync(LauncherConfig config, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_paths.ConfigFile);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = _paths.ConfigFile + ".tmp";

        try
        {
            await using (var stream = new FileStream(
                             temp,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, config, WriteOptions, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            // Past this point the temp file is complete, so the move is not
            // cancellable: aborting between "wrote everything" and "published it"
            // would throw away a good save for no benefit.
            File.Move(temp, _paths.ConfigFile, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            // The real file was never touched; drop the partial temp so the next
            // save does not inherit a truncated buffer.
            TryDelete(temp);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            throw new ConfigStoreException("error.config.saveFailed", ex.Message, ex);
        }
    }

    /// <summary>
    /// Moves an unparseable config aside under a numbered name. Numbered rather
    /// than timestamped so a player reading the folder can tell at a glance which
    /// backup is which, and never deleted, because it is the only copy of their
    /// setup that still exists.
    /// </summary>
    private void Quarantine(string reason)
    {
        var target = NextBackupPath();

        try
        {
            File.Move(_paths.ConfigFile, target, overwrite: false);
            LoadWarningKey = "error.config.corrupt";
            LoadWarningDetail = $"{Path.GetFileName(target)}: {reason}";
        }
        catch (Exception move) when (move is IOException or UnauthorizedAccessException)
        {
            try
            {
                // A move can fail where a copy succeeds (different volume, open
                // handle on Windows). Copying still preserves the content, and the
                // next save is then allowed to replace the original.
                File.Copy(_paths.ConfigFile, target, overwrite: false);
                LoadWarningKey = "error.config.corrupt";
                LoadWarningDetail = $"{Path.GetFileName(target)}: {reason}";
            }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException)
            {
                LoadWarningKey = "error.config.corruptBackupFailed";
                LoadWarningDetail = $"{reason}; backup failed: {copy.Message}";
            }
        }
    }

    private string NextBackupPath()
    {
        for (var n = 1; n <= 999; n++)
        {
            var candidate = _paths.ConfigFile + ".bad-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!File.Exists(candidate))
                return candidate;
        }

        // 999 unreadable configs means something is very wrong; fall back to a
        // name that cannot already be taken rather than overwriting a backup.
        return _paths.ConfigFile + ".bad-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is harmless: the next save recreates it with
            // FileMode.Create. Nothing useful to do, and nothing to report.
        }
    }

    private static long LinuxPhysicalMemoryMb()
    {
        const string memInfo = "/proc/meminfo";
        if (!File.Exists(memInfo))
            return 0;

        foreach (var line in File.ReadLines(memInfo))
        {
            if (!line.StartsWith("MemTotal:", StringComparison.Ordinal))
                continue;

            // Format is "MemTotal:       16316252 kB".
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var kilobytes))
                return 0;

            return kilobytes / 1024;
        }

        return 0;
    }

    private static long WindowsPhysicalMemoryMb()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
            return 0;

        return (long)(status.ullTotalPhys / (1024UL * 1024UL));
    }

    /// <summary>Layout of Win32 <c>MEMORYSTATUSEX</c>; field order is part of the ABI.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
