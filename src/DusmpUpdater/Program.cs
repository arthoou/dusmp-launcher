using DusmpLauncher.Core;

if (args.Contains("--launcher-version", StringComparer.OrdinalIgnoreCase))
{
    var version = typeof(Program).Assembly.GetName().Version;
    Console.WriteLine($"{version?.Major ?? 0}.{version?.Minor ?? 0}.{version?.Build ?? 0}");
    return 0;
}

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
