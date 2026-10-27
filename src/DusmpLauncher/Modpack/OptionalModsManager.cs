using DusmpLauncher.Configuration;
using DusmpLauncher.Models;

namespace DusmpLauncher.Modpack;

/// <summary>
/// Holds the player's optional-mod answers against the list the API serves.
/// <para>
/// It owns no storage of its own: the config object and a save callback are
/// handed in, because the launcher has exactly one config file and exactly one
/// component that writes it. Everything here mutates that shared object and then
/// asks the owner to persist it.
/// </para>
/// </summary>
public sealed class OptionalModsManager : IOptionalModsManager
{
    private readonly Func<LauncherConfig> _config;
    private readonly Func<Task> _saveAsync;

    /// <summary>
    /// The UI can toggle several switches faster than the config file can be
    /// written; this keeps two saves from interleaving on the same dictionary.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="config">
    /// Reads the live config object every time, rather than holding one.
    /// Loading the config file replaces that object wholesale, so a reference
    /// taken at startup is orphaned the moment the launcher reads the file:
    /// every choice the player made was written into the discarded copy while
    /// the saved one kept its empty map, and on the next start every optional
    /// mod came back enabled and downloaded itself again.
    /// </param>
    /// <param name="saveAsync">Persists the config. Invoked only when something actually changed.</param>
    public OptionalModsManager(Func<LauncherConfig> config, Func<Task> saveAsync)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
    }

    /// <summary>
    /// Mod ids are opaque API identifiers, so they are compared exactly — the same
    /// comparison the config dictionary uses when it is deserialised.
    /// </summary>
    private static StringComparer IdComparer => StringComparer.Ordinal;

    /// <summary>Choices survive a config file that stored <c>null</c> for the map.</summary>
    private Dictionary<string, bool> Choices
    {
        get
        {
            var config = _config();
            return config.OptionalMods ??= new Dictionary<string, bool>();
        }
    }

    private List<string> Seen
    {
        get
        {
            var config = _config();
            return config.SeenOptionalMods ??= new List<string>();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<OptionalModView> Merge(IReadOnlyList<OptionalMod> remote)
    {
        if (remote is null || remote.Count == 0)
            return Array.Empty<OptionalModView>();

        var choices = Choices;
        var seen = new HashSet<string>(Seen, IdComparer);
        var emitted = new HashSet<string>(IdComparer);
        var views = new List<OptionalModView>(remote.Count);

        foreach (var mod in remote)
        {
            if (mod is null || string.IsNullOrWhiteSpace(mod.Id))
                continue;

            // A duplicated id would render two switches writing the same key.
            if (!emitted.Add(mod.Id))
                continue;

            // A saved answer always wins: the default only speaks for mods the
            // player has never been asked about.
            var enabled = choices.TryGetValue(mod.Id, out var saved) ? saved : mod.DefaultEnabled;

            // "New" is decided purely by the seen list, so a mod added to a later
            // manifest still gets its badge even though it arrives enabled.
            views.Add(new OptionalModView(mod, enabled, !seen.Contains(mod.Id)));
        }

        return views;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, bool> CurrentChoices(IReadOnlyList<OptionalMod> remote)
    {
        // Everything the player has answered, then the current list on top of it.
        //
        // Built from the remote list alone, an empty or unreachable list produced
        // an empty map — and the install plan counts a file nobody mentioned as
        // wanted, so a start without that list quietly reinstated every mod the
        // player had turned off.
        var choices = new Dictionary<string, bool>(Choices, IdComparer);
        foreach (var view in Merge(remote))
            choices[view.Mod.Id] = view.Enabled;

        return choices;
    }

    /// <inheritdoc />
    public async Task SetEnabledAsync(string modId, bool enabled, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("Optional mod id is empty.", nameof(modId));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();

            var changed = false;

            if (!Choices.TryGetValue(modId, out var current) || current != enabled)
            {
                Choices[modId] = enabled;
                changed = true;
            }

            // Answering a mod is the strongest possible form of having seen it, so
            // the badge never survives a toggle.
            if (!Seen.Contains(modId, IdComparer))
            {
                Seen.Add(modId);
                changed = true;
            }

            if (!changed)
                return;

            await _saveAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task MarkAllSeenAsync(IReadOnlyList<OptionalMod> remote, CancellationToken ct = default)
    {
        if (remote is null || remote.Count == 0)
            return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();

            var seen = Seen;
            var known = new HashSet<string>(seen, IdComparer);
            var changed = false;

            foreach (var mod in remote)
            {
                if (mod is null || string.IsNullOrWhiteSpace(mod.Id))
                    continue;

                if (known.Add(mod.Id))
                {
                    seen.Add(mod.Id);
                    changed = true;
                }
            }

            if (!changed)
                return;

            await _saveAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
