using System.Net;
using System.Security.Cryptography;
using System.Text;
using DusmpLauncher.Configuration;
using DusmpLauncher.Core;
using DusmpLauncher.Downloads;
using DusmpLauncher.Models;
using DusmpLauncher.Modpack;
using DusmpLauncher.Network;

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

    // 7. As APIs do Modrinth e do CurseForge.
    //
    //    O resolvedor existe para que um jar publico venha do CDN do autor em
    //    vez do nosso. Ele nunca pode quebrar a instalacao, mas tambem nao pode
    //    transformar "chave errada" e "mod nao esta la" na mesma coisa.
    var jar = new ModFile { Path = "mods/exemplo.jar", Size = 10, Sha256 = wantedSha, Sha1 = "b".PadRight(40, 'b'), Fingerprint = 123456789 };
    var asked = new List<string>();
    HttpClient Api(Func<string, HttpResponseMessage> serve) => new(new StubHandler(url => { asked.Add(url); return serve(url); }));

    // Uma chave explicitamente vazia desativa o CurseForge.
    asked.Clear();
    await new ExternalMirrorResolver(Api(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }), null, curseForgeApiKey: "")
        .ResolveAsync(new[] { jar });
    Check(!asked.Any(u => u.Contains("curseforge")), "Sem chave o CurseForge nao pode ser chamado");
    Check(asked.Any(u => u.Contains("modrinth")), "O Modrinth e publico e tem de ser consultado sempre");

    // A chave embutida funciona sem configurar o ambiente; os overrides
    // continuam tendo prioridade e a chave so e enviada ao CurseForge.
    async Task<string?> SentCurseForgeKey(string? key = null)
    {
        string? sentKey = null;
        var leakedKey = false;
        using var http = new HttpClient(new StubHandler(
            url => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(url.Contains("curseforge") ? "{\"data\":{\"exactMatches\":[]}}" : "{}") },
            request =>
            {
                if (!request.Headers.TryGetValues("x-api-key", out var values)) return;
                if (request.RequestUri!.Host == "api.curseforge.com") sentKey = values.FirstOrDefault();
                else leakedKey = true;
            }));
        await new ExternalMirrorResolver(http, curseForgeApiKey: key).ResolveAsync(new[] { jar });
        Check(!leakedKey, "A chave do CurseForge nao pode ser enviada ao Modrinth");
        return sentKey;
    }

    var savedKey = Environment.GetEnvironmentVariable(ExternalMirrorResolver.CurseForgeApiKeyVariable);
    var savedLegacyKey = Environment.GetEnvironmentVariable(ExternalMirrorResolver.LegacyCurseForgeApiKeyVariable);
    try
    {
        Environment.SetEnvironmentVariable(ExternalMirrorResolver.CurseForgeApiKeyVariable, null);
        Environment.SetEnvironmentVariable(ExternalMirrorResolver.LegacyCurseForgeApiKeyVariable, null);
        Check(!string.IsNullOrWhiteSpace(await SentCurseForgeKey()), "O CurseForge precisa usar a chave embutida sem variaveis de ambiente");
        Environment.SetEnvironmentVariable(ExternalMirrorResolver.LegacyCurseForgeApiKeyVariable, "chave-legada");
        Check(await SentCurseForgeKey() == "chave-legada", "A variavel legada deve substituir a chave embutida");
        Environment.SetEnvironmentVariable(ExternalMirrorResolver.CurseForgeApiKeyVariable, " chave-do-ambiente ");
        Check(await SentCurseForgeKey() == "chave-do-ambiente", "CURSEFORGE_API_KEY tem prioridade sobre a variavel legada");
        Check(await SentCurseForgeKey("chave-de-teste") == "chave-de-teste", "A chave explicita tem prioridade e usa o cabecalho x-api-key");
        Check(await SentCurseForgeKey(" ") is null, "Uma chave explicita em branco desativa o CurseForge mesmo com ambiente configurado");
    }
    finally
    {
        Environment.SetEnvironmentVariable(ExternalMirrorResolver.CurseForgeApiKeyVariable, savedKey);
        Environment.SetEnvironmentVariable(ExternalMirrorResolver.LegacyCurseForgeApiKeyVariable, savedLegacyKey);
    }

    // Um 429 nao pode virar insistencia nem resultado inventado. Os dois
    // servicos sao independentes: o Modrinth pedir calma nao diz nada sobre o
    // CurseForge, entao o freio e por host.
    asked.Clear();
    var throttled = await new ExternalMirrorResolver(Api(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)), null, "chave-de-teste")
        .ResolveAsync(new[] { jar });
    Check(throttled.Count == 0, "Um 429 nao pode produzir espelho nenhum");
    Check(asked.Count(u => u.Contains("modrinth")) == 1, $"O Modrinth nao pode ser reconsultado depois de um 429: {asked.Count(u => u.Contains("modrinth"))} chamadas");
    Check(asked.Count(u => u.Contains("curseforge")) == 1, "Um 429 do Modrinth nao diz nada sobre o CurseForge");

    // downloadUrl nulo e a forma do CurseForge dizer que o autor desligou a
    // distribuicao por terceiros. Isso nao e erro e nao pode virar URL inventada:
    // o arquivo continua vindo do espelho do DUSMP.
    var refused = await new ExternalMirrorResolver(Api(url => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(url.Contains("curseforge")
            ? "{\"data\":{\"exactMatches\":[{\"file\":{\"id\":2,\"fileFingerprint\":123456789,\"fileLength\":10,\"downloadUrl\":null}}]}}"
            : "{}"),
    }), null, "chave-de-teste").ResolveAsync(new[] { jar });
    Check(!refused.ContainsKey("mods/exemplo.jar"), "Um mod cuja distribuicao o autor desligou nao pode ganhar URL publica");

    // Controle: com uma downloadUrl de verdade o espelho tem de ser usado. Sem
    // isto a asercao acima passaria ate com a leitura do CurseForge quebrada.
    var allowed = await new ExternalMirrorResolver(Api(url => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(url.Contains("curseforge")
            ? "{\"data\":{\"exactMatches\":[{\"file\":{\"id\":2,\"fileFingerprint\":123456789,\"fileLength\":10,\"downloadUrl\":\"https://edge.forgecdn.net/files/1/2/exemplo.jar\"}}]}}"
            : "{}"),
    }), null, "chave-de-teste").ResolveAsync(new[] { jar });
    Check(allowed.TryGetValue("mods/exemplo.jar", out var urls) && urls.Any(u => u.Contains("forgecdn")),
        "Um mod que o autor permite distribuir tem de vir do CDN do CurseForge");

    Console.WriteLine("PASS: espelho com versao errada recusado, queda para a origem DUSMP, tamanho conferido; limpeza preserva mundos e configuracoes do jogador; requisitos de loader lidos dos mods; escolhas de mods opcionais sobrevivem ao reinicio; loader da serie errada recusado antes de baixar; chaves, freio de 429 e distribuicao negada nas APIs externas.");
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


sealed class StubHandler(Func<string, HttpResponseMessage> serve, Action<HttpRequestMessage>? inspect = null) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        inspect?.Invoke(request);
        return Task.FromResult(serve(request.RequestUri!.ToString()));
    }
}

sealed class StubConfig : IConfigStore
{
    public LauncherConfig Current { get; } = new();
    public string? LastRecoveryKey => null;
    public Task<LauncherConfig> LoadAsync(CancellationToken ct = default) => Task.FromResult(Current);
    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
}
