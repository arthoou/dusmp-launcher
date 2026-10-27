# External launcher icons

The logos below identify third-party applications. DUSMP is not affiliated with those projects. Bundled SVGs are embedded application resources; displaying them requires no network request.

- `prism.svg`: [PrismLauncher/PrismLauncher](https://raw.githubusercontent.com/PrismLauncher/PrismLauncher/d859ffcdddbb020dcd2514a919acc47c3e00887b/program_info/org.prismlauncher.PrismLauncher.svg), unchanged. Copyright the Prism Launcher contributors. Licensed [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/). Source commit `d859ffcdddbb020dcd2514a919acc47c3e00887b`.
- `polymc.svg`: [PolyMC/PolyMC](https://raw.githubusercontent.com/PolyMC/PolyMC/1da6b4e11d78313545d5422ac4e06f232368a10e/program_info/org.polymc.PolyMC.svg), unchanged. Copyright the Polymc Launcher contributors. Licensed [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/). Source commit `1da6b4e11d78313545d5422ac4e06f232368a10e`.

- `atlauncher.svg`: [ATLauncher/ATLauncher](https://raw.githubusercontent.com/ATLauncher/ATLauncher/b7c2fbc55f059cae0c628a93ca5c1b8d4ab5314d/packaging/linux/_common/atlauncher.svg), unchanged. Copyright the ATLauncher authors. Licensed [GPL-3.0](https://www.gnu.org/licenses/gpl-3.0.html); bundled as a separate data file, which GPL-3.0 section 5 treats as mere aggregation. Source commit `b7c2fbc55f059cae0c628a93ca5c1b8d4ab5314d`.

## Installed icons take priority

When a launcher is actually installed, the launcher shows **its own icon from this machine** rather than anything bundled here: `ExternalLauncherDiscovery` reads the `Icon=` key of the matching `.desktop` entry, resolves it through the freedesktop hicolor directories, and inlines the file as a `data:` URL. That file belongs to the user's own installation, so nothing is redistributed. Only `.svg` and `.png` are accepted, at most 192 KB, and an `Icon=` value containing a path separator is refused.

## Monograms

`generic.svg` and the marks for Modrinth App, CurseForge, Minecraft Launcher, SKLauncher, TLauncher, MultiMC and GDLauncher are **original DUSMP artwork**: a rounded tile carrying the project's colour and one or two letters. They are not reproductions of any third-party logo, and they exist because those projects' real logos are not ours to ship — Modrinth's [branding policy](https://github.com/modrinth/code/blob/main/COPYING.md) and MultiMC's [branding policy](https://multimc.org/#Branding) reserve their logo rights, CurseForge's and TLauncher's and SKLauncher's marks carry no redistribution grant, GDLauncher is BUSL-licensed, and Mojang's usage guidelines do not permit shipping a Minecraft mark. A monogram avoids both a broken image and a misappropriated logo. Where a project *is* installed, the section above replaces the monogram with the genuine icon anyway.

## Integration references

- [Prism data locations](https://prismlauncher.org/wiki/getting-started/data-location/)
- [Prism command line](https://prismlauncher.org/wiki/getting-started/command-line-interface/)
- [Flatpak user/system installs and launch command](https://docs.flatpak.org/en/latest/using-flatpak.html)
- [Desktop Entry Exec syntax](https://specifications.freedesktop.org/desktop-entry/latest/exec-variables.html)

Detection is read-only. Prism is the managed adapter. Other entries indicate a known installation; they do not claim that DUSMP can modify its instance format.
