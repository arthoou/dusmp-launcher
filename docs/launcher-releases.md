# Distribuição e atualização do DUSMP Launcher

O site e o launcher usam o mesmo manifesto assinado em
`https://dusmp.online/launcher/releases/stable.json`. Uma atualização autenticada
é aplicada automaticamente antes da interface principal. O launcher verifica a
assinatura com a chave pública incorporada, seleciona o executável para seu
sistema e arquitetura, e verifica tamanho e SHA-256 antes de instalar.

## Publicar uma versão

No ambiente Linux de publicação, na pasta `apps/dusmp-launcher`:

```bash
node packaging/release/release.cjs publish --version 1.3.0
```

Esse comando compila launcher e updater autocontidos para Linux x64 e Windows
x64, confere o cabeçalho de cada executável, confere sua versão, empacota o
Linux, calcula os hashes, assina os metadados e publica nos arquivos locais do
site irmão `../DUSMP site`. `--version` é passada ao build .NET; não é necessário
editar diferentes números de versão manualmente. `--minimum-version` define a
versão mínima que pode iniciar quando o servidor estiver indisponível; o padrão
é `1.2.0`. `--mandatory` registra também a política de compatibilidade no
manifesto. Uma atualização válida disponível sempre é instalada automaticamente.

Para incluir notas de lançamento e selecionar outra cópia do site:

```bash
node packaging/release/release.cjs publish \
  --version 1.3.1 --minimum-version 1.3.0 \
  --notes-file docs/release-notes.txt \
  --site-root '/caminho/do/DUSMP site'
```

O arquivo de notas acima deve conter o texto real dessa versão. Não existe um
arquivo fictício de release incluído como se fosse uma publicação.

O publicador usa `DOTNET` quando definida; caso contrário usa
`~/.dotnet/dotnet`. Node 20+, .NET SDK 8 e `tar` são necessários. O build precisa
conseguir restaurar os runtimes Windows/Linux e os pacotes NuGet existentes.
O publicador Linux consegue compilar o `.exe` de Windows; isso não executa um
teste Windows nativo.

`--skip-build` usa `dist/linux/{launcher,updater}` e
`dist/win/{launcher,updater}`. Ele também recusa binários de outra versão,
arquitetura ou formato; não use esse argumento para contornar um build falho.
Sem esse argumento os builds ficam isolados em uma pasta temporária.

Antes de atualizar o manifesto estável, o processo exige as duas plataformas,
releitura dos hashes dos executáveis e pacotes, manifesto assinado válido e
espaço para staging. Uma trava impede dois publicadores simultâneos. Uma versão
publicada nunca é sobrescrita e o número da versão estável precisa aumentar.
O diretório completo da versão é renomeado no mesmo sistema de arquivos; o
manifesto estável é trocado por último.

## Preparar arquivos para enviar pelo painel

Para gerar a atualização e enviá-la pela aba de atualizações do launcher no
painel do site:

```bash
node packaging/release/release.cjs prepare \
  --version 1.3.1 --minimum-version 1.3.0 \
  --output-dir '/caminho/existente/launcher-1.3.1' \
  --notes-file docs/release-notes.txt
```

`prepare` usa o mesmo build, conferência dos executáveis e assinatura de
`publish`. Aceita também `--mandatory`, `--key-file` e `--skip-build`, com as
mesmas regras descritas acima. A pasta pai do destino precisa existir e o
destino deve ser novo, sem links simbólicos no caminho. A pasta só aparece
completa depois da verificação; uma pasta existente nunca é sobrescrita.

O pacote preparado contém estes quatro arquivos:

```text
launcher-1.3.1/
  manifest.json
  linux-x64/DusmpLauncher
  linux-x64/DUSMP-Launcher-1.3.1-linux-x64.tar.gz
  windows-x64/DusmpLauncher.exe
```

O manifesto já contém as URLs públicas de `https://dusmp.online`. Preparar o
pacote não requer uma cópia do site, não altera o estável nem anuncia a versão
para os jogadores. A publicação acontece ao concluir o envio pelo painel, que
confere a assinatura e os arquivos. No painel, basta enviar `manifest.json`,
o `.tar.gz` e o `.exe`: o executável Linux separado é opcional, pois o servidor
pode obtê-lo do pacote e conferir seu hash assinado. Também é possível
selecionar a pasta inteira. A chave privada permanece no ambiente de preparação.

## Arquivos publicados e servidor

```text
apps/DUSMP site/data/launcher-releases/
  stable.json
  history.json
  1.3.0/
    manifest.json
    linux-x64/
      DusmpLauncher
      DUSMP-Launcher-1.3.0-linux-x64.tar.gz
    windows-x64/
      DusmpLauncher.exe
```

O servidor existente precisa servir `/launcher/releases/...` pelo módulo de
releases; `data/` não deve ficar exposto como pasta estática geral. O manifesto
estável/histórico usa cache curto e os arquivos versionados são imutáveis. O
servidor verifica arquivos antes de anunciá-los, aceita retomada HTTP Range e
envia os arquivos como downloads reais. As URLs antigas do site permanecem nas
rotas existentes.

A publicação feita pelo script é **local**. O domínio HTTPS precisa apontar para
essa instalação do site e seu processo Node precisa estar ativo para downloads
públicos e atualizações reais. O script não configura DNS/túneis, não envia
credenciais e não afirma ter feito deploy remoto. Se a infraestrutura copiar
releases para outro host, copie primeiro o diretório completo de cada versão e
troque `stable.json` apenas depois de verificar os arquivos no destino. Uma
sincronização que substitua `stable.json` antes dos arquivos quebra essa ordem.

## Assinatura e chave privada

Envelope de transporte:

```json
{
  "schemaVersion": 1,
  "keyId": "dusmp-release-1",
  "payload": "base64 dos bytes UTF-8 exatos do JSON",
  "signature": "base64 de ECDSA P-256/SHA-256 IEEE-P1363 (64 bytes)"
}
```

O payload contém `schemaVersion`, `channel`, `version`, `minimumVersion`,
`mandatory`, `publishedAt`, `notes` e `artifacts`. Cada artefato contém `os`,
`arch`, `url`, `fileName`, `size` e `sha256`. O campo opcional `download` contém
o arquivo para instalação inicial, seu tamanho/hash, `format` e `compatibility`.
O executável bruto é usado pela atualização; o Linux oferece também o pacote
tar.gz com instalador. Apenas HTTPS nos hosts oficiais DUSMP é aceito. Nomes e
caminhos precisam corresponder exatamente à versão/plataforma assinadas.

A chave privada padrão fica em
`~/.local/share/DUSMPRelease/signing-key.pem`, fora dos dois projetos, com pasta
`0700` e arquivo `0600`. A chave pública está em
`packaging/release/release-public-key.pem`, no launcher e no módulo do site.
`DUSMP_RELEASE_KEY_FILE` ou `--key-file` seleciona um arquivo de chave fornecido
pelo ambiente seguro de publicação. O publicador recusa chaves públicas que
não correspondam à chave já incorporada.

Faça uma cópia de segurança criptografada da chave privada em armazenamento
restrito e separado. Ela nunca deve entrar em Git, frontend, arquivo de release,
log ou variável pública. Sem ela não é possível publicar atualizações aceitas
pelas instalações que confiam nessa chave.

Para preparar uma chave para um **novo produto/ambiente**, sem sobrescrever uma
chave existente:

```bash
node packaging/release/release.cjs keygen --key-file '/pasta/restrita/signing-key.pem'
```

Esse comando exporta a chave pública ao lado. Gerar uma chave diferente não
altera automaticamente a confiança dos launchers já distribuídos. Rotação
exige planejar a incorporação da nova chave em uma versão ainda assinada pela
chave anterior. Não regenere a chave de produção a cada build.

Em CI, disponibilize o PEM privado como arquivo secreto apenas no job de
publicação, com `0600`, rode os testes e execute o mesmo comando. Separe jobs
de pull requests dos jobs com acesso à assinatura. Uma assinatura do manifesto
autentica os bytes do executável; ela não equivale a um certificado Authenticode
Windows. SmartScreen pode tratar o `.exe` sem certificado comercial como
aplicativo sem reputação. Os scripts de instalador IExpress existentes foram
preservados para builds em Windows.

## Formatos e instalação

**Windows:** `DusmpLauncher.exe` é um executável portátil x64 real, autocontido
em .NET, para Windows 10/11 com WebView2 Runtime. Salve-o numa pasta gravável do
usuário. Na versão 1.3.1, concluir a configuração inicial cria os atalhos na área de
trabalho e no menu Iniciar. Eles apontam para esse executável e são atualizados
nas próximas aberturas. As pastas oficiais do Windows respeitam também a área
de trabalho redirecionada para o OneDrive. Os scripts
`packaging/windows/build.ps1` e `build-installer.ps1`
continuam disponíveis para gerar o instalador IExpress em Windows. PowerShell,
IExpress e testes de execução nativa Windows não estão disponíveis neste host
Linux; Wine não substitui esses testes.

**Linux:** o pacote tar.gz contém `DusmpLauncher`, `DusmpUpdater`, `install.sh`
e instruções. Extraia e execute `bash install.sh`. O instalador não exige sudo,
instala em `~/.local/lib/DUSMPLauncher`, cria atalhos no menu e na área de
trabalho configurada no sistema e mantém a instalação anterior em
`DUSMPLauncher.previous.*`. Não ativa um serviço de
modpack sem escolha do usuário. O executável também pode ser aberto diretamente
na pasta extraída.

O instalador respeita a mesma trava de processo do launcher em
`updates/instance.lock`, incluindo `DUSMP_LAUNCHER_HOME`/`XDG_DATA_HOME` quando
definidas. É preciso fechar o launcher e aguardar qualquer atualização antes
de instalar. O comando `flock`, do util-linux, é uma dependência do instalador;
a trava é liberada pelo kernel ao encerrar o processo.

Dependências reais do binário atual: Linux x86-64, **glibc 2.38+**, GTK 3 e
WebKitGTK 4.1, além de uma sessão gráfica. O requisito glibc foi observado no
`Photino.Native.so` atual com `readelf --version-info`; Debian 13 foi o ambiente
de execução. Distribuições com glibc anterior precisam de um build nativo
compatível; embalar em AppImage por si só não resolve isso. Ferramentas AppImage
não estão instaladas, portanto o download fornecido é explicitamente tar.gz.

Configurações/contas/Minecraft ficam em `~/.local/share/DusmpLauncher` no Linux
e `%APPDATA%\DusmpLauncher` no Windows, fora do executável substituído. Pastas
controladas pelo gerenciador de pacotes do sistema não devem ser atualizadas
pelo auto-updater; use uma instalação gravável por usuário.

## Verificação e recuperação de publicação interrompida

```bash
node --test packaging/release/release.test.cjs
node packaging/release/release.cjs verify --version 1.3.0
node packaging/release/verify-http.cjs https://dusmp.online
```

`verify` autentica o manifesto versionado, confere hashes/tamanhos e confirma
cabeçalhos/versões dos executáveis. No Linux executa apenas o comando de versão
do launcher; não abre a interface ou lê dados do jogador. Os testes automatizados
usam arquivos sintéticos isolados em `/tmp`, nunca publicados como releases.

Se uma publicação parar depois da criação do diretório versionado, mas antes
da troca do estável, confirme que o processo anterior terminou. Uma trava órfã
em `data/launcher-releases/.publish.lock` contém PID e host; remova apenas essa
trava após confirmar que o PID não é mais o publicador ativo. Em seguida:

```bash
node packaging/release/release.cjs verify --version 1.3.0
node packaging/release/release.cjs announce --version 1.3.0
```

`announce` reaplica as verificações e só anuncia uma versão superior à estável;
ele não recompila nem sobrescreve uma versão existente. Pastas `.staging-*`
órfãs nunca são anunciadas. Uma release que falhou na validação não deve ser
consertada sobrescrevendo arquivos já publicados: corrija o código e publique
um número novo. Para reverter comportamento em produção, publique uma nova
versão que restaure esse comportamento; o publicador bloqueia downgrade.

Os testes de publicação comprovam contrato criptográfico, rejeição de URLs
arbitrárias/metadados inválidos, validação PE/ELF, exclusão de publicadores
concorrentes e instalação Linux com espaços/caracteres especiais, preservação
de dados e backup. Validação nativa Windows, execução em outras distribuições e
disponibilidade do domínio público continuam verificações separadas.

Os testes do launcher podem ser repetidos sem acessar dados do jogador:

```bash
bash checks/run-all.sh --no-publish
~/.dotnet/dotnet run --project checks/LauncherUpdates -c Release -- --native-transactions
```

O segundo comando gera dois executáveis autocontidos de teste, com chave
efêmera incorporada, e exercita processos Linux reais: troca e reinício com
confirmação, falha da versão nova com restauração, e interrupção durante a
troca seguida de recuperação. Ele não publica esses executáveis nem usa a
chave privada de produção. A confirmação do teste é feita por um processo de
console; a aplicação real só confirma depois que sua interface envia `ready`.
Instalações antigas anteriores a 1.3.0 precisam instalar esta primeira versão
uma vez; o binário antigo não possui o verificador de assinatura nem o helper.
