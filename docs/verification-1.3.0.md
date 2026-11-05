# Verificação local da versão 1.3.0

Data: 29 de outubro de 2026. Ambiente: Debian 13, Linux x64, .NET SDK 8.0.425,
Node 20.19.2. Publicação mantida localmente por solicitação do usuário.

## Resultado e artefatos reais

O pipeline compilou launcher e updater para `linux-x64` e `win-x64`, verificou
cabeçalhos/versões, tamanhos e hashes e anunciou o manifesto estável por último.
Os arquivos estão em `apps/DUSMP site/data/launcher-releases/1.3.0/`.

| Artefato | Bytes |
| --- | ---: |
| Linux: DusmpLauncher | 78.519.591 |
| Linux: DUSMP-Launcher-1.3.0-linux-x64.tar.gz | 80.240.811 |
| Windows: DusmpLauncher.exe | 79.554.950 |

Os executáveis em `dist/linux/launcher` e `dist/win/launcher` foram substituídos
atomicamente por cópias exatas desses artefatos. Os dois incluem a chave do
CurseForge solicitada; seu valor não é reproduzido neste relatório.

## Principais arquivos

- Site: `download-launcher.html`, `launcher-downloads.js`, `launcher-downloads.css`,
  `server/lib/launcher-releases.cjs` e `server/lib/public-files.cjs`.
- Launcher: `Updates/ReleaseManifest.cs`, `LauncherUpdateService.cs`,
  `UpdateTransaction.cs` e `UpdateProcessLock.cs`, integrados a `Program.cs`,
  `LauncherCore.cs`, à ponte da interface e às traduções.
- Publicação: `packaging/release/release.cjs`, validador compartilhado
  `manifest.cjs`, verificador de binários `binary.cjs`, e instalador Linux
  `packaging/linux/install-launcher.sh`.

## Verificações executadas

- `bash checks/run-all.sh --no-publish`: todos os checks passaram, incluindo
  interface, skins, autenticação, detecção externa, downloads, Java, perfis de
  RAM e o novo check LauncherUpdates.
- `node --test packaging/release/release.test.cjs`: 17 testes passaram. Incluem
  contrato, assinaturas inválidas, proteção de caminhos, hashes, PE/ELF,
  versões, publicação concorrente e instalação por usuário. Um processo .NET
  real com FileShare.None bloqueou corretamente o instalador Linux via flock.
- `dotnet run --project checks/LauncherUpdates -c Release -- --native-transactions`:
  passaram os cenários Linux com executáveis e processos reais: atualização
  assinada e reinício; falha da versão nova com restauração e reinício da antiga;
  interrupção após a troca e recuperação na abertura seguinte. Os arquivos de
  teste ficam isolados e usam chave efêmera, nunca a chave de produção.
- No site, `npm test`: 17 testes passaram, sendo 10 grupos HTTP e 7 grupos de
  interface em DOM simulado. As URLs antigas, navegação, seleção de SO,
  idiomas PT/EN/ES, histórico, falhas/retry e proteção dos dados foram exercitados.
- No site, `npm run check`: passou.
- `node packaging/release/release.cjs verify --version 1.3.0`: passou, com os
  executáveis reais e todos os arquivos da publicação.
- O check C# `--verify-release` autenticou o manifesto publicado usando a chave
  pública de produção incorporada ao launcher.
- `node packaging/release/verify-http.cjs http://127.0.0.1:<porta>`: passou contra
  o servidor real do site, com o manifesto de produção. Foram transferidos por
  completo os três artefatos acima, comparando bytes/SHA-256; também passaram
  HEAD, Content-Length, ETag, Range/206, metadados compartilhados e histórico.
- A interface não contém botão de atualização manual nem opção de pular. Um
  estado de atualização pendente impede a tela principal, inclusive quando o
  estado anterior já era main.
- Conferência posterior no navegador real: página em desktop 1366×900, idioma
  português, escolha manual Windows/Linux, histórico e navegação entre início,
  FAQ e downloads. Em largura móvel de aproximadamente 390 px, o menu abriu
  corretamente e a largura do documento ficou dentro da viewport.
- Os destinos dos dois botões foram conferidos no DOM real após a última
  correção: continuam nas URLs HTTPS públicas `https://dusmp.online/...`,
  inclusive com a página aberta localmente. Localhost é apenas ambiente de teste.

## Limites da validação

- Windows foi compilado e os arquivos PE/versões foram inspecionados no Linux.
  Não foi executado teste Windows nativo do WebView2, substituição e reinício.
- Os testes de processos Linux usam um harness de console com o código real de
  transação. A confirmação do aplicativo distribuído depende do `ready` real da
  interface. Não se declara aqui um teste gráfico do launcher.
- O navegador ficou indisponível no início e foi liberado depois. A página foi
  então inspecionada visualmente em desktop e exercitada em viewport móvel,
  conforme as verificações acima. A captura desktop está em
  `docs/screenshots/downloads-desktop.png`.
- Espaço livre é verificado antes de baixar/instalar; não foi provocado um disco
  fisicamente cheio. Permissões insuficientes foram exercitadas em diretório
  Linux real sem escrita.
- Não houve deploy público. Para executar localmente: na pasta do site, rode
  `npm start` e abra `http://127.0.0.1:2000/download-launcher`.
- Linux requer glibc 2.38+, GTK 3 e WebKitGTK 4.1. O pacote é tar.gz com
  instalador por usuário. Windows é um executável portátil e requer WebView2.

A operação completa e a publicação das próximas versões estão documentadas em
[launcher-releases.md](launcher-releases.md). A chave privada de assinatura fica
fora dos dois projetos e precisa de backup restrito para futuras publicações.
