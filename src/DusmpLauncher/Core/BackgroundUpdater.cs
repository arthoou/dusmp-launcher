using System.Net.Http;
using System.Diagnostics;
using DusmpLauncher.Configuration;
using DusmpLauncher.Downloads;
using DusmpLauncher.Modpack;
using DusmpLauncher.Network;

namespace DusmpLauncher.Core;

/// <summary>
/// Headless DUSMP updater process. It reuses the launcher manifest, file ledger,
/// download verifier and external-instance registry instead of maintaining a
/// second update implementation.
/// </summary>
public sealed class BackgroundUpdater
{
    private readonly LauncherPaths _paths;
    private readonly CancellationToken _stop;
    public BackgroundUpdater(LauncherPaths paths, CancellationToken stop) { _paths=paths; _stop=stop; }

    public async Task RunAsync(bool once)
    {
        var minutes=int.TryParse(Environment.GetEnvironmentVariable("DUSMP_UPDATER_POLL_MINUTES"),out var configured)?Math.Clamp(configured,5,1440):30;
        do
        {
            try { await UpdateAllAsync(_stop).ConfigureAwait(false); }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested) { break; }
            catch(Exception ex) { Console.Error.WriteLine($"Updater: {ex.Message}"); }
            if(once||_stop.IsCancellationRequested)break;
            try { await Task.Delay(TimeSpan.FromMinutes(minutes),_stop).ConfigureAwait(false); }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested) { break; }
        } while(!_stop.IsCancellationRequested);
    }

    private async Task UpdateAllAsync(CancellationToken ct)
    {
        if (GameOrLauncherIsRunning())
        { Console.WriteLine("Update deferred while a launcher or Minecraft process is running."); return; }
        using var http=Program.CreateHttpClient();
        var config=new ConfigStore(_paths); await config.LoadAsync(ct).ConfigureAwait(false); var api=new ApiClient(http); var mirror=new MirrorManager();
        var download=new DownloadManager(http,config); var manifestStore=new ManifestManager(_paths); var managed=new ModpackManager(_paths,Console.WriteLine);
        var optionalStore=new OptionalModsManager(()=>config.Current,()=>config.SaveAsync()); var game=new GameLauncher(_paths,http); var external=new ExternalLauncherManager(_paths);

        var latest=await api.GetModpackLatestAsync(config.Current.Edition, ct).ConfigureAwait(false);
        var manifest=await api.GetModpackManifestAsync(latest.Version,ct).ConfigureAwait(false); manifestStore.Validate(manifest);
        await manifestStore.SaveCacheAsync(manifest,ct).ConfigureAwait(false);
        var regions=await api.GetRegionsAsync(ct).ConfigureAwait(false);
        var region=regions.FirstOrDefault(r=>r.Available&&r.Id==config.Current.PreferredRegionId)??regions.Where(r=>r.Available).OrderBy(r=>r.Priority).FirstOrDefault();
        if(region is not null)mirror.SetCdnBases(region.CdnBases);
        var optional=await api.GetOptionalModsAsync(manifest.Version,ct).ConfigureAwait(false);
        var choices=optionalStore.CurrentChoices(optional);
        var plan=await managed.BuildPlanAsync(manifest,choices,force:false,null,ct).ConfigureAwait(false);
        await managed.ApplyDeletionsAsync(plan,ct).ConfigureAwait(false);
        if(config.Current.PlayMode!="external") await game.EnsureLoaderInstalledAsync(manifest,null,ct).ConfigureAwait(false);

        var jobs=plan.ToDownload.Select(file=>new DownloadJob(file.File.Path,file.AbsolutePath,file.File.Size,file.File.Sha256,mirror.ResolveUrls(file.File))).ToArray();
        if(jobs.Length>0)
        {
            var result=await download.RunAsync(jobs,ct).ConfigureAwait(false);
            if(!result.Success)throw new IOException(result.Failed.FirstOrDefault()?.Detail??"Download was cancelled.");
        }
        await managed.CommitAsync(plan,ct).ConfigureAwait(false);
        config.Current.InstalledModpackVersion=manifest.Version; await config.SaveAsync(ct).ConfigureAwait(false);

        var installations=await external.GetInstallationsAsync(ct).ConfigureAwait(false);
        var updatedCount = 0;
        foreach(var installation in installations.Where(x=>x.Managed&&x.Launcher is "prism" or "official"))
        {
            ct.ThrowIfCancellationRequested();
            if(!Directory.Exists(installation.InstancePath))continue;
            await external.SyncManagedFilesAsync(installation,manifest,choices,ct).ConfigureAwait(false);
            if(installation.Launcher=="official")
            {
                var root=installation.LauncherRoot??Path.GetDirectoryName(installation.InstancePath)!;
                var version=await game.EnsureLoaderInstalledInAsync(root,manifest,null,ct).ConfigureAwait(false);
                await external.WriteOfficialProfileAsync(installation,version,ct).ConfigureAwait(false);
            }
            updatedCount++;
        }
        Console.WriteLine($"DUSMP is current at modpack {manifest.Version}; updated {updatedCount} managed external instance(s).");
    }

    private static bool GameOrLauncherIsRunning()
    {
        foreach(var process in Process.GetProcesses())
        {
            using(process)
            {
                try { var name=process.ProcessName; if(new[] { "java", "javaw", "DusmpLauncher", "prismlauncher", "minecraft-launcher", "MinecraftLauncher", "Minecraft" }.Contains(name,StringComparer.OrdinalIgnoreCase))return true; }
                catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return false;
    }
}
