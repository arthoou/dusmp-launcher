# DUSMP Launcher

Launcher de Minecraft do DUSMP. Toda a lógica (API, arquivos, autenticação,
download, inicialização do jogo) fica no backend em **C# / .NET 8**. A interface
é **HTML/CSS/JS dentro de um WebView**, então dá para mexer no visual sem
recompilar a lógica.

Roda em **Linux e Windows** com o mesmo código.

> O prompt original falava em "Tidebound". Aqui está tudo como **DUSMP**, com a
> identidade visual do site (dourado sobre preto quente) e a API em
> `api.dusmp.online`. Se "Tidebound" era o nome de verdade, é só trocar o texto
> em `Localization/Languages/*.json` e os assets em `UI/assets/`.

## Rodar

```bash
cd "apps/dusmp-launcher" && bash build.sh run
```

O comando acima usa a API real em `https://api.dusmp.online`. Para abrir o
executável Linux já publicado, use `./dist/linux/launcher/DusmpLauncher`.
Na primeira abertura, o idioma padrão é inglês (`en-US`), independentemente do
idioma do sistema. Depois da seleção, o launcher usa o idioma salvo pelo jogador.
Se a API ainda não tiver uma versão do modpack publicada, o launcher informa
que falta o manifesto. Publique os arquivos no `/admin` e use **Tentar novamente**.

`DUSMP_API_MODE=fixtures bash build.sh run` é o **modo de desenvolvimento**: em vez de bater em
`api.dusmp.online`, o launcher lê os manifests embutidos em
`src/DusmpLauncher/Network/Fixtures/`. A API para produção está implementada em
`apps/DUSMP site/modpack-api.js`; o domínio `api.dusmp.online` precisa ser
encaminhado ao serviço Node para disponibilizá-la publicamente. Os downloads são **reais** — os
arquivos apontam para URLs públicas de verdade, com tamanho e SHA-256 de verdade
— então a barra de progresso mostra bytes reais, nunca progresso falso.

### Dependências de sistema

- **Linux**: `libwebkit2gtk-4.1-0` e `libgtk-3-0` (Debian 13 já vem com os dois).
  Em distro mais antiga com apenas `webkit2gtk-4.0`, o Photino 4 não carrega.
- **Windows**: WebView2 Runtime (já vem no Windows 11; no 10 o instalador do
  Edge coloca).
- **Java**: o launcher provisiona sozinho via CmlLib; não precisa Java no PATH.

O bootstrap de Java é executado antes dos processadores Forge/NeoForge. Em uma
instalação limpa, o launcher baixa o componente declarado pelo Minecraft, valida
os arquivos e verifica a versão do executável antes de continuar. O runtime
fica dentro da instância e é reutilizado nas próximas aberturas.

### Variáveis de ambiente

| Variável | Para que serve |
|---|---|
| `DUSMP_API_MODE=fixtures` | usa os manifests embutidos em vez da API |
| `DUSMP_API_BASE` | aponta para outra API (staging) |
| `DUSMP_LAUNCHER_HOME` | muda a pasta de dados (padrão: `~/.local/share/DusmpLauncher`) |
| `DUSMP_UI_DIR` | serve a UI de uma pasta em disco, para editar HTML/CSS sem rebuildar |
| `DUSMP_DEVTOOLS=1` | liga o DevTools do WebView |
| `DUSMP_MS_CLIENT_ID` | client id do app Azure para o login Microsoft |
| `CURSEFORGE_API_KEY` | substitui a chave CurseForge embutida; valor em branco desativa as consultas |
| `DUSMP_CURSEFORGE_API_KEY` | alternativa legada, usada quando `CURSEFORGE_API_KEY` não está definida |

Os downloads consultam primeiro o Modrinth e depois o CurseForge, usando os hashes
do manifesto. O CurseForge já vem com uma chave configurada. Se o arquivo não for
encontrado, não permitir download por terceiros ou a API falhar, o launcher usa o
espelho DUSMP e mantém a verificação SHA-256.

Para mexer no visual sem recompilar:

```bash
DUSMP_UI_DIR="$PWD/src/DusmpLauncher/UI" DUSMP_API_MODE=fixtures ~/.dotnet/dotnet run --project src/DusmpLauncher
```

## Publicar

```bash
bash build.sh all
```

Isso gera executáveis autocontidos para Linux e Windows em `dist/{linux,win}/{launcher,updater}`. Para compilar, assinar e disponibilizar uma versão completa no site, use o fluxo de [publicação de releases](docs/launcher-releases.md):

```bash
node packaging/release/release.cjs publish --version 1.3.0
```

O site e o launcher consultam o mesmo manifesto assinado. O executável distribuído
verifica atualizações na tela inicial, baixa e valida a nova versão e reinicia
automaticamente, sem opção de pular. Um helper separado troca o arquivo depois
da saída do processo e recupera a versão anterior se a nova interface não iniciar.
O site oferece `.exe` portátil para Windows e pacote `.tar.gz` com instalador
por usuário para Linux. A publicação do comando acima é no diretório local do
site; a configuração do domínio e do processo de hospedagem permanece necessária.

No Windows, para compilar e criar o instalador autocontido, execute no PowerShell:

```powershell
& .\packaging\windows\build.ps1
```

O resultado é `dist\win\DUSMP-Launcher-Setup.exe`. O instalador instala no perfil
do usuário, cria atalhos no Menu Iniciar e na Área de Trabalho, registra o updater
no Agendador de Tarefas e abre o launcher.

No Windows, o launcher e o updater usam o subsistema gráfico para não abrir um
terminal. A UI embutida é interceptada em `http://localhost/`, seguindo a
integração WebView2 do Photino; no Linux continua em `app://launcher/`.
Esse endereço é resolvido em memória, sem servidor HTTP local.

Na configuração inicial, depois do idioma, o jogador precisa escolher o tipo de
dispositivo: **low 8gb** (sistema com 8 GB de RAM, G1 e 4 GB de heap máximo),
**low 16gb** (sistema com 16 GB de RAM, G1 e 6 GB de heap máximo) ou
**Standard 16gb+** (sistema com pelo menos 16 GB de RAM, Generational ZGC e 10 GB de
heap máximo). Os nomes indicam a RAM total do sistema; a alocação para o Minecraft
é menor. A escolha também pode ser alterada em Settings. O
controle de RAM continua editável e a escolha não redefine esse valor a cada
abertura. Instalações existentes conservam seus argumentos até uma escolha
explícita; a atualização solicita essa escolha uma vez, sem repetir o login.
O botão de pasta abre `DusmpLauncher/instances`, com as pastas das
edições dentro dele.

## Como está organizado

```
src/DusmpLauncher/
  Core/           LauncherCore (a máquina de estados), GameLauncher, LauncherPaths
  Authentication/ device code da Microsoft, conta offline, cofre de tokens
  Downloads/      download paralelo, resumível, com mirror e verificação SHA-256
  Modpack/        diff manifest x disco, mods opcionais, ledger de arquivos nossos
  Network/        ApiClient, fixtures de dev, escolha de região, ping
  Localization/   ~130 idiomas no catálogo, 10 traduzidos, ~250 países
  Configuration/  config.json (sem segredo nenhum dentro)
  Bridge/         a ponte JS <-> C# e o servidor de assets app://
  Models/         manifest, arquivo, região, perfil, plano de instalação
  UI/             index.html + css/ + js/ (a camada visual, e só ela)
```

A regra que mantém isso saudável: **a UI não decide nada**. Ela não vê token,
não sabe URL de API, não calcula hash e não escolhe o que baixar. Só renderiza o
estado que o C# manda e chama comandos.

As verificações de inicialização abaixo não abrem janelas nem usam dados do jogador:

```bash
~/.dotnet/dotnet run --project checks/StartupAssets -c Release
node checks/ui-startup.cjs
```

Elas conferem os recursos embutidos em `app://launcher/`, as traduções e o
tratamento das mensagens do backend na interface.

## A ponte C# <-> UI

O Photino dá exatamente dois primitivos, e a ponte é construída em cima deles:

- JS → C#: `window.external.sendMessage(json)`
- C# → JS: `window.external.receiveMessage(cb)`

Em cima disso, `UI/js/bridge.js` expõe `call(command, payload)` (que devolve
`Promise`) e `on(event, handler)`. Os nomes estão em
`Bridge/BridgeProtocol.cs` — esse arquivo é a fonte da verdade dos dois lados.

Comandos que a UI pode chamar: `loginMicrosoft`, `loginOffline`,
`selectLanguage`, `selectCountry`, `toggleOptionalMod`, `startGame`,
`openSettings`, `saveSettings`, `repairInstallation`, `retryStartup`,
`validateOfflineName`, `openExternal`, `openInstanceFolder`, `windowMinimize`,
`windowClose`, `getState`, `getLanguages`, `getCountries`, `getOptionalMods`,
`finishOnboarding`, `logout`, `cancelMicrosoftLogin`, `ready`.

Eventos que o C# empurra: `downloadProgress`, `screen` (estado inteiro),
`language`, `deviceCode`, `optionalMods`, `serverStatus`, `gameState`, `notice`,
`log`.

## Microsoft sign-in

> **O login falha na etapa Xbox/Minecraft?** O ID de compatibilidade
> (`00000000402b5328`) ainda consegue iniciar o login por código, mas isso não
> comprova autorização nas etapas Xbox Live e Minecraft. O launcher mostra
> qual serviço recusou a solicitação. Para distribuir o DUSMP com login próprio,
> registre um aplicativo autorizado:
>
> 1. <https://portal.azure.com> → **Microsoft Entra ID** → **App registrations** →
>    **New registration**.
> 2. Em *Supported account types*, escolha **Personal Microsoft accounts only**.
> 3. Em *Redirect URI*, selecione **Public client/native (mobile & desktop)** e
>    informe `http://localhost`.
> 4. Depois de criar: **Authentication** → ative **Allow public client flows**.
> 5. Peça acesso à API do Minecraft para o app
>    (<https://help.minecraft.net/hc/en-us/articles/16254801392141>) — sem isso o
>    `login_with_xbox` responde 403.
> 6. Rode o launcher com o *Application (client) ID* (um GUID):
>
> ```bash
> DUSMP_MS_CLIENT_ID=00000000-0000-0000-0000-000000000000 ./dist/linux/launcher/DusmpLauncher
> ```
>
> **Onde colocar o client id sem recompilar:** no painel admin, aba
> **Settings** → *Launcher version (JSON)*, acrescente o campo
> `microsoftClientId`:
>
> ```json
> { "version": "1.0.0", "minimumVersion": "1.0.0", "mandatory": false,
>   "microsoftClientId": "00000000-0000-0000-0000-000000000000" }
> ```
>
> Todo launcher passa a usar esse id na próxima abertura. O launcher só aceita
> um GUID aqui — qualquer outra coisa é ignorada, para um valor errado não jogar
> todos os jogadores de volta no id embutido. `DUSMP_MS_CLIENT_ID` continua
> tendo prioridade, para testar local.
>
> Com um GUID, o launcher troca sozinho para os endpoints modernos e para
> authorization code + PKCE com callback em localhost — sem código para digitar.
> Nunca coloque *client secret* num launcher desktop.


The launcher opens the system browser automatically. With a DUSMP-owned Microsoft
Entra public desktop application ID in `DUSMP_MS_CLIENT_ID`, it uses authorization
code + PKCE and a temporary localhost callback. Register `http://localhost` as a
mobile/desktop redirect, allow personal Microsoft accounts, and enable public
client flows for the optional device-code fallback. Never distribute a client
secret in a desktop launcher. Minecraft service access must be granted to the app.

The legacy compatibility device flow also opens the default browser. When Microsoft
returns `https://www.microsoft.com/link`, the launcher includes the public user code
in its `otc` parameter. Server-provided `verification_uri_complete` takes priority.
Microsoft Entra's `/devicelogin` endpoint does not guarantee prefilled codes; in that
fallback the code remains available under alternative sign-in options. The launcher
polls in the background and continues automatically once authorization completes.

Cancel and "Use another device" stop the previous attempt before starting another.
On Linux the opener uses `xdg-open`, with `gio open` as a fallback. No browser profile
or operating-system settings are changed. Refresh tokens remain in the encrypted
store (DPAPI on Windows, AES-GCM with a `0600` key on Linux), never in `config.json`.

References: [Microsoft device authorization](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code),
[Prism Microsoft Link handling](https://github.com/PrismLauncher/PrismLauncher/blob/develop/launcher/ui/dialogs/MSALoginDialog.cpp),
[XboxAuthNet token prefixes](https://github.com/AlphaBs/XboxAuthNet/blob/4708acacd19e93ea3956ad67b6fe29a81a971f02/src/XboxLive/XboxAuthConstants.cs).

Os arquivos de interface usam tabelas JSON em `Localization/Languages/`.
O seletor oferece somente idiomas com traduções incluídas no executável.

## Launchers externos

Integrações disponíveis: **Prism Launcher** e **Minecraft Launcher** oficial.
O login acontece no launcher escolhido. Em **Settings → Launcher → Change** é
possível mudar o modo; em **Installation details → Choose folder** é possível
corrigir a pasta detectada. Cancelar a seleção de pasta mantém a escolha atual.

- Windows: executáveis instalados, Prism portátil e Minecraft da Microsoft Store
  registrado para o usuário. Os perfis Win32 e Microsoft Store são preservados
  separadamente.
- Linux: instalações nativas, Flatpak por usuário/sistema e Prism AppImage em
  `Applications`, `AppImages`, `Downloads` ou na pasta selecionada. Os atalhos
  `.desktop` conservam argumentos e variáveis de ambiente. AppImages precisam
  estar executáveis e ter seus requisitos de execução instalados.
- Prism: instância `DUSMP` separada, com `InstanceDir` personalizado respeitado.
  Minecraft e loader em `mmc-pack.json` acompanham as atualizações do modpack.
- Minecraft Launcher: perfil `DUSMP`, com pasta de jogo isolada. Após abrir o
  launcher oficial, selecione esse perfil e clique em Play.

Outros launchers aparecem apenas na detecção, com integração indisponível.
Pastas pessoais e arquivos configurados como `writeOnce` são preservados.
Pastas de instância com links simbólicos são recusadas para não escrever fora
  da instalação registrada.

Referências de formato: [CLI do Prism](https://prismlauncher.org/wiki/getting-started/command-line-interface/),
[perfis Win32/Store no instalador Fabric](https://github.com/FabricMC/fabric-installer/blob/master/src/main/java/net/fabricmc/installer/client/ProfileInstaller.java),
[armazenamento do Flatpak](https://docs.flatpak.org/en/latest/sandbox-permissions.html).

### DUSMP Updater

`src/DusmpUpdater` gera um executável separado para atualização em segundo
plano. Por padrão, ele consulta a API a cada 30 minutos, aplica o manifesto
validado e os downloads com SHA-256 à instalação DUSMP e sincroniza as
instâncias Prism e perfis oficiais gerenciados sem abrir o Minecraft. Respeita as escolhas de
mods opcionais, o registro de arquivos do launcher e as pastas pessoais. Use
`DUSMP_UPDATER_POLL_MINUTES` para alterar o intervalo ou `DusmpUpdater --once`
para fazer uma única verificação.

Publique o updater como executável autocontido para cada sistema. No Linux,
`packaging/linux/install-updater.sh <diretório-publicado>` instala e ativa o
serviço systemd do usuário. No Windows, registre a tarefa de login com
`packaging/windows/install-updater-task.ps1 -UpdaterExe <caminho>`. Ambos
iniciam o processo em segundo plano no login do usuário. Este build ainda não
inclui ícone de bandeja nem janela gráfica própria de progresso. Esse processo
mantém os modpacks; a atualização do executável é responsabilidade do próprio
launcher e de seu helper de troca, conforme o fluxo de releases acima.

## O contrato da API

O launcher não tem URL espalhada pelo código: tudo passa por `IApiClient` e as
rotas ficam em `Network/ApiEndpoints.cs`. Base: `https://api.dusmp.online`.

| Rota | Devolve |
|---|---|
| `GET /health` | 200 para o teste de vida |
| `GET /launcher/version` | versão atual, versão mínima, URL de download, se é obrigatória |
| `GET /launcher/releases/stable.json` | manifesto assinado da distribuição oficial por plataforma |
| `GET /modpack/latest` | versão mais nova do modpack + URL do manifest |
| `GET /modpack/manifest?version=` | o manifest da versão (ou da mais nova) |
| `GET /mods/optional?version=` | lista de mods opcionais |
| `GET /regions` | regiões de infra, com `countries`, `cdnBases` e `pingUrl` |
| `GET /servers` | servidores para mostrar status na tela principal |
| `GET /status` | manutenção: `maintenance`, `blockPlay`, `message` por idioma |
| `GET /news?language=` | novidades |
| `POST /client/region` | recebe país + latências medidas, devolve região e CDNs |

### Manifest

```json
{
  "version": "1.0.4",
  "minecraft": "1.21.1",
  "loader": "neoforge",
  "loaderVersion": "21.1.209",
  "server": { "host": "play.dusmp.online", "port": 25565 },
  "java": { "majorVersion": 21, "recommendedMaxRamMb": 6144, "extraJvmArgs": [] },
  "files": [
    {
      "path": "mods/example.jar",
      "size": 1429382,
      "sha256": "…",
      "url": "https://cdn.dusmp.online/…",
      "mirrors": ["https://mirror2/…"],
      "optionalId": "simplevoicechat",
      "writeOnce": false
    }
  ]
}
```

Regras que o launcher aplica e que a API precisa respeitar:

- `path` é relativo à instância, sempre com `/`, e **não pode** escapar dela
  (`..` é rejeitado).
- `sha256` tem que ser 64 hex. O nome do arquivo nunca é usado para decidir
  integridade — só o hash.
- `optionalId` tem que casar com um id de `/mods/optional`.
- `writeOnce: true` = o launcher escreve uma vez e depois não mexe mais (configs
  que o jogador edita).
- `loader` aceito: `vanilla`, `forge`, `neoforge`, `fabric`, `quilt`.

### `POST /client/region`

Manda só o necessário — país escolhido, idioma da interface e as latências que o
launcher mediu:

```json
{ "country": "BR", "language": "pt-BR", "latencies": { "sa-east": 24, "na-east": 118 } }
```

A resposta manda a região e os CDNs a usar. O launcher **combina** país, região,
ping real e disponibilidade; país sozinho nunca decide. E a resposta é uma
sugestão: se esse POST falhar, o launcher escolhe local e segue.

## Atualização e arquivos do jogador

O launcher mantém um **ledger** (`managed-files.json`) do que ele mesmo escreveu.
Arquivo que sai do manifest e está no ledger pode ser removido; arquivo que ele
nunca escreveu **nunca** é tocado. Ele não varre a pasta apagando o que não
reconhece — é assim que launcher ruim apaga mod e config do jogador.

Protegidos sempre, mesmo fora do manifest: `saves/`, `screenshots/`,
`resourcepacks/`, `shaderpacks/`, `logs/`, `crash-reports/`, `backups/`,
`options.txt`, `servers.dat`, e a lista completa em
`Core/LauncherPaths.cs` (`PlayerOwnedDirectories` / `PlayerOwnedFiles`).

Para não reler tudo a cada abertura, o ledger guarda tamanho + mtime: só
recalcula o SHA-256 quando um dos dois mudou. O botão **Reparar** força rehash
de tudo.

## Downloads

Paralelo com limite configurável, retomada via `Range` em arquivo `.part`,
timeout por tentativa, retry com backoff, fallback entre mirrors, e rename para
o nome final **só depois** que o SHA-256 confere. Cancelar deixa os `.part` no
lugar para a próxima execução continuar.

A velocidade mostrada é medida numa janela deslizante real. Quando o total ainda
não é conhecido, a UI mostra o loader indeterminado e **nenhum** número, em vez
de inventar uma porcentagem.

## Config salva

`~/.local/share/DusmpLauncher/config.json` (Linux) ou
`%APPDATA%\DusmpLauncher\config.json` (Windows):

```json
{
  "firstLaunch": false,
  "language": "pt-BR",
  "country": "BR",
  "accountType": "microsoft",
  "optionalMods": { "simplevoicechat": true, "examplemod": false }
}
```

Token nenhum passa por aqui.
