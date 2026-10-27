using System.Net;
using System.Security.Cryptography;
using System.Text;
using DusmpLauncher.Configuration;
using DusmpLauncher.Core;
using DusmpLauncher.Downloads;
using DusmpLauncher.Models;
using DusmpLauncher.Modpack;

// Proves the one property that makes a public mirror safe to use at all: the
// launcher installs the file the manifest names, or it installs nothing. A CDN
// that answers with a different build of the same mod must be rejected, not
// written to the mods folder.

var root = Path.Combine(Path.GetTempPath(), "dusmp-download-integrity-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
try
{
    var wanted = Encoding.UTF8.GetBytes("o jar exato que o manifesto fixou");
    var wrongVersion = Encoding.UTF8.GetBytes("outra versao do mesmo mod, servida por um espelho");
    var wantedSha = Convert.ToHexString(SHA256.HashData(wanted)).ToLowerInvariant();

    // 1. A mirror that serves a different build is refused outright.
    var target = Path.Combine(root, "mods", "exemplo.jar");
    var onlyWrong = await RunAsync(new[] { "https://espelho.invalido/errado.jar" },
        _ => Bytes(wrongVersion), new DownloadJob("mods/exemplo.jar", target, wanted.Length, wantedSha,
            new[] { "https://espelho.invalido/errado.jar" }));
    Check(!onlyWrong.Success, "Um espelho com a versao errada nao pode ser aceito");
    Check(!File.Exists(target), "Nenhum arquivo pode sobrar quando o hash nao bate");

    // 2. With the wrong mirror first and DUSMP second, the manifest's file wins.
    var served = new List<string>();
    var both = await RunAsync(new[] { "https://espelho.invalido/errado.jar", "https://api.dusmp.online/certo.jar" },
        url => { served.Add(url); return Bytes(url.Contains("dusmp") ? wanted : wrongVersion); },
        new DownloadJob("mods/exemplo.jar", target, wanted.Length, wantedSha,
            new[] { "https://espelho.invalido/errado.jar", "https://api.dusmp.online/certo.jar" }));
    Check(both.Success, "O download deve cair para a origem DUSMP quando o espelho erra");
    Check(File.Exists(target), "O arquivo correto deve ser gravado");
    Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(target))).ToLowerInvariant() == wantedSha,
        "O arquivo gravado precisa ser exatamente o do manifesto");
    Check(served.Any(u => u.Contains("espelho")), "O espelho errado precisa ter sido tentado antes");

    // 3. Same bytes, wrong length in the manifest: still refused.
    var mismatched = Path.Combine(root, "mods", "tamanho.jar");
    var wrongSize = await RunAsync(new[] { "https://api.dusmp.online/certo.jar" }, _ => Bytes(wanted),
        new DownloadJob("mods/tamanho.jar", mismatched, wanted.Length + 10, wantedSha,
            new[] { "https://api.dusmp.online/certo.jar" }));
    Check(!wrongSize.Success && !File.Exists(mismatched), "Um tamanho diferente do manifesto precisa ser recusado");

    // A limpeza da instalacao apaga arquivos: tem que remover o que o launcher
    // instalou e preservar o que e do jogador, sem excecao.
    var home = Path.Combine(root, "home");
    Environment.SetEnvironmentVariable("DUSMP_LAUNCHER_HOME", home);
    var paths = LauncherPaths.Resolve();
    paths.EnsureCreated();

    string Instance(params string[] parts) => Path.Combine(new[] { paths.Instance }.Concat(parts).ToArray());
    void Seed(string relative, string body = "x")
    {
        var full = Instance(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
    }

    // Instalado pelo launcher.
    Seed("mods/sodium.jar");
    Seed("versions/neoforge-21.1.228/neoforge-21.1.228.json");
    Seed("libraries/net/neoforged/x.jar");
    Seed("config/algum.toml");
    // Do jogador.
    Seed("saves/meu mundo/level.dat", "mundo");
    Seed("screenshots/foto.png");
    Seed("resourcepacks/pack.zip");
    Seed("logs/latest.log");
    Seed("options.txt", "fov:90");

    var removedCount = LauncherPaths.ResetInstance(paths);
    Check(removedCount > 0, "A limpeza precisa remover alguma coisa");

    foreach (var gone in new[] { "mods", "versions", "libraries", "config" })
        Check(!Directory.Exists(Instance(gone)), $"'{gone}' devia ter sido removido");

    foreach (var kept in new[] { "saves/meu mundo/level.dat", "screenshots/foto.png", "resourcepacks/pack.zip", "logs/latest.log", "options.txt" })
        Check(File.Exists(Instance(kept.Split('/'))), $"'{kept}' NAO podia ter sido apagado");

    Check(File.ReadAllText(Instance("saves", "meu mundo", "level.dat")) == "mundo", "O mundo foi corrompido");
    Check(LauncherPaths.ResetInstance(paths) == 0, "Uma segunda limpeza nao deve ter nada a remover");

    // Leitura dos requisitos de loader declarados pelos proprios mods. O parser e
    // baseado em regex sobre o mods.toml, entao precisa de teste proprio.
    Check(LoaderCompatibility.LowerBound("[21.1.247,)") == "21.1.247", "Faixa aberta a direita");
    Check(LoaderCompatibility.LowerBound("[21.1.229,21.2)") == "21.1.229", "Faixa fechada");
    Check(LoaderCompatibility.LowerBound("[21.1.5]") == "21.1.5", "Versao unica");
    Check(LoaderCompatibility.LowerBound("") is null && LoaderCompatibility.LowerBound("qualquer") is null, "Entrada invalida vira null");
    Check(LoaderCompatibility.Compare(LoaderCompatibility.Parse("21.1.247"), LoaderCompatibility.Parse("21.1.228")) > 0, "247 e maior que 228");
    Check(LoaderCompatibility.Compare(LoaderCompatibility.Parse("21.1.9"), LoaderCompatibility.Parse("21.1.10")) < 0, "Comparacao e numerica, nao textual");
    Check(LoaderCompatibility.Compare(LoaderCompatibility.Parse("21.1"), LoaderCompatibility.Parse("21.1.0")) == 0, "Componente ausente vale zero");

    var modsDir = Path.Combine(root, "mods");
    Directory.CreateDirectory(modsDir);
    Jar(Path.Combine(modsDir, "supplementaries.jar"), "supplementaries", "[21.1.247,)");
    Jar(Path.Combine(modsDir, "sophisticatedcore.jar"), "sophisticatedcore", "[21.1.229,)");
    Jar(Path.Combine(modsDir, "tranquilo.jar"), "tranquilo", "[21.1.100,)");

    var tooNew = LoaderCompatibility.FindTooNew(modsDir, "21.1.228");
    Check(tooNew.Count == 2, $"Deviam sobrar 2 mods exigentes, vieram {tooNew.Count}");
    // O primeiro tem de ser a MAIOR exigencia: e a menor versao que serve a todos.
    Check(tooNew[0].MinimumVersion == "21.1.247", "A maior exigencia precisa vir primeiro");
    Check(tooNew[0].ModId == "supplementaries", "O id do mod veio do bloco errado do toml");
    Check(LoaderCompatibility.FindTooNew(modsDir, "21.1.247").Count == 0, "Com o loader novo o suficiente nao sobra nada");
    Check(LoaderCompatibility.FindTooNew(modsDir, null).Count == 0, "Sem versao fixada nao ha o que comparar");

    // 5. The player's optional-mod answers have to survive the config file.
    //
    // Reading the file replaces the config object wholesale. The manager used to
    // be handed that object once, at startup, before anything had been read — so
    // from the first load onwards it was writing choices into a copy nobody saved,
    // the file kept its empty map, and on the next start every mod the player had
    // turned off counted as wanted and downloaded itself again.
    var store = new ConfigStore(paths);
    var optional = new DusmpLauncher.Modpack.OptionalModsManager(() => store.Current, () => store.SaveAsync());
    await store.LoadAsync();

    var remote = new[]
    {
        new OptionalMod { Id = "distant-horizons", Name = "Distant Horizons", DefaultEnabled = true },
        new OptionalMod { Id = "iris", Name = "Iris", DefaultEnabled = true },
    };
    await optional.SetEnabledAsync("distant-horizons", false);
    Check(store.Current.OptionalMods is { Count: > 0 }, "A escolha nem chegou no objeto que o launcher usa");
    Check(optional.CurrentChoices(remote)["distant-horizons"] == false, "A escolha nao vale nem na mesma sessao");

    // A second launcher reading the same folder is what the player gets next time.
    var reread = new ConfigStore(paths);
    await reread.LoadAsync();
    var afterRestart = new DusmpLauncher.Modpack.OptionalModsManager(() => reread.Current, () => reread.SaveAsync());
    Check(afterRestart.CurrentChoices(remote)["distant-horizons"] == false,
        "Depois de reiniciar o mod desligado voltou a ser baixado");
    Check(afterRestart.CurrentChoices(remote)["iris"], "Um mod que o jogador nao mexeu tem de seguir o padrao");

    // The API serving no optional list at all must not be read as "the player
    // wants everything": an unmentioned id counts as wanted by the install plan.
    Check(afterRestart.CurrentChoices(Array.Empty<OptionalMod>())["distant-horizons"] == false,
        "Com a lista remota vazia a escolha salva foi descartada");

    // 6. Uma build do NeoForge da serie errada tem de ser recusada antes de
    //    qualquer download. O NeoForge se numera pelo jogo (1.21.1 -> 21.1.x),
    //    e uma build de outra serie nem existe para aquela versao: o instalador
    //    morria com "404 (Not Found)" contra o maven, sem dizer o que estava
    //    errado nem quem podia corrigir.
    var manifests = new ManifestManager(paths);
    ManifestValidationException? rejected = null;
    try
    {
        manifests.Validate(new ModpackManifest { Version = "0.0.1", Minecraft = "1.21.1", Loader = "neoforge", LoaderVersion = "21.2.247" });
    }
    catch (ManifestValidationException error) { rejected = error; }
    Check(rejected is not null, "Um NeoForge 21.2.247 num pack de 1.21.1 tem de ser recusado");
    Check(rejected!.ErrorKey == "error.manifest.loaderMismatch", "A recusa precisa dizer que o loader nao e dessa versao: " + rejected.ErrorKey);
    Check(rejected.Detail.Contains("21.1"), "O detalhe precisa dizer qual serie serve: " + rejected.Detail);

    // E o par certo passa, junto com os casos que esta regra nao cobre.
    manifests.Validate(new ModpackManifest { Version = "0.0.1", Minecraft = "1.21.1", Loader = "neoforge", LoaderVersion = "21.1.247" });
    manifests.Validate(new ModpackManifest { Version = "0.0.1", Minecraft = "1.21", Loader = "neoforge", LoaderVersion = "21.0.5" });
    manifests.Validate(new ModpackManifest { Version = "0.0.1", Minecraft = "1.20.1", Loader = "forge", LoaderVersion = "47.4.10" });
    manifests.Validate(new ModpackManifest { Version = "0.0.1", Minecraft = "25w01a", Loader = "neoforge", LoaderVersion = "21.9.9" });

    Console.WriteLine("PASS: espelho com versao errada recusado, queda para a origem DUSMP, tamanho conferido; limpeza preserva mundos e configuracoes do jogador; requisitos de loader lidos dos mods; escolhas de mods opcionais sobrevivem ao reinicio; loader da serie errada recusado antes de baixar.");
}
finally { try { Directory.Delete(root, true); } catch (IOException) { } }

static HttpResponseMessage Bytes(byte[] body) =>
    new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

static async Task<DownloadResult> RunAsync(string[] _, Func<string, HttpResponseMessage> serve, DownloadJob job)
{
    using var http = new HttpClient(new StubHandler(serve));
    var manager = new DownloadManager(http, new StubConfig());
    return await manager.RunAsync(new[] { job });
}

static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); }
// Um jar minimo com o mods.toml que o NeoForge le. As dependencias vem ANTES do
// bloco [[mods]] de proposito: e o arranjo que fazia o id do mod ser lido errado.
static void Jar(string path, string modId, string range)
{
    using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
    var entry = archive.CreateEntry("META-INF/neoforge.mods.toml");
    using var writer = new StreamWriter(entry.Open());
    writer.Write($"""
        modLoader="javafml"
        [[dependencies.{modId}]]
        modId="neoforge"
        type="required"
        versionRange="{range}"
        [[mods]]
        modId="{modId}"
        version="1.0.0"
        """);
}


sealed class StubHandler(Func<string, HttpResponseMessage> serve) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(serve(request.RequestUri!.ToString()));
}

sealed class StubConfig : IConfigStore
{
    public LauncherConfig Current { get; } = new();
    public string? LastRecoveryKey => null;
    public Task<LauncherConfig> LoadAsync(CancellationToken ct = default) => Task.FromResult(Current);
    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
}
