using DusmpLauncher.Core;

var paths=LauncherPaths.Resolve();
paths.EnsureCreated();
using var stop=new CancellationTokenSource();
Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
try
{
    await new BackgroundUpdater(paths,stop.Token).RunAsync(once:args.Contains("--once",StringComparer.OrdinalIgnoreCase));
    return 0;
}
catch(OperationCanceledException) when(stop.IsCancellationRequested)
{
    return 0;
}
