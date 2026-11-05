#!/usr/bin/env bash
# Installs this downloaded bundle for the current user. No sudo is needed.
set -euo pipefail

bundle_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
install_parent="$HOME/.local/lib"
install_dir="$install_parent/DUSMPLauncher"
bin_dir="$HOME/.local/bin"
menu_root="${XDG_DATA_HOME:-$HOME/.local/share}"
if [[ "$menu_root" != /* ]]; then menu_root="$HOME/.local/share"; fi
menu_dir="$menu_root/applications"
data_dir="${DUSMP_LAUNCHER_HOME:-${XDG_DATA_HOME:-$HOME/.local/share}/DusmpLauncher}"
if [[ "$data_dir" != /* ]]; then data_dir="$PWD/$data_dir"; fi
runtime_lock="$data_dir/updates/instance.lock"
stage_dir=''
backup_dir=''
installed=0

if [[ "${1:-}" == '--help' ]]; then
  echo 'Usage: bash install.sh'
  echo 'Installs under ~/.local/lib/DUSMPLauncher, creates desktop and menu shortcuts, and keeps player data untouched.'
  exit 0
fi
if [[ $# -ne 0 ]]; then echo 'Unsupported option. Use --help.' >&2; exit 2; fi
if [[ "$HOME" == *$'\n'* || "$HOME" == *$'\r'* ]]; then echo 'Home path contains unsupported line breaks.' >&2; exit 1; fi
if [[ "$HOME" != /* ]]; then echo 'Home path must be absolute.' >&2; exit 1; fi
if [[ "$(uname -m)" != 'x86_64' ]]; then echo 'This bundle requires Linux x86-64.' >&2; exit 1; fi
if ! command -v flock >/dev/null 2>&1; then echo 'The util-linux flock command is required for safe installation.' >&2; exit 1; fi

check_path() {
  local current="$1"
  while [[ "$current" != '/' && -n "$current" ]]; do
    if [[ -L "$current" ]]; then echo "Symbolic links are refused in the install path: $current" >&2; exit 1; fi
    current="$(dirname -- "$current")"
  done
}

# Read user-dirs.dirs as data. Some xdg-user-dir implementations source this
# file, so call them only when every non-comment line is a safe assignment.
decode_user_dir() {
  local value="$1" char
  decoded_user_dir=''
  if [[ "$value" == '$HOME' || "$value" == '$HOME/'* ]]; then
    decoded_user_dir="$HOME"
    value="${value:5}"
  elif [[ "$value" != /* ]]; then
    return 1
  fi
  while [[ -n "$value" ]]; do
    char="${value:0:1}"; value="${value:1}"
    if [[ "$char" == '\' ]]; then
      [[ -n "$value" ]] || return 1
      char="${value:0:1}"; value="${value:1}"
      [[ "$char" == '\' || "$char" == '"' || "$char" == '$' || "$char" == '`' ]] || return 1
    elif [[ "$char" == '"' || "$char" == '$' || "$char" == '`' ]]; then
      return 1
    fi
    decoded_user_dir+="$char"
  done
}

config_root="${XDG_CONFIG_HOME:-$HOME/.config}"
if [[ "$config_root" != /* ]]; then config_root="$HOME/.config"; fi
desktop_path=''
config_desktop=''
user_dirs_safe=1
user_dir_pattern='^[[:space:]]*XDG_([A-Z_]+)_DIR="(([^"\\]|\\.)*)"[[:space:]]*(#.*)?$'
if [[ -f "$config_root/user-dirs.dirs" ]]; then
  while IFS= read -r config_line || [[ -n "$config_line" ]]; do
    if [[ "$config_line" =~ ^[[:space:]]*(#.*)?$ ]]; then continue; fi
    if [[ "$config_line" =~ $user_dir_pattern ]]; then
      user_dir_kind="${BASH_REMATCH[1]}"
      if decode_user_dir "${BASH_REMATCH[2]}"; then
        if [[ "$user_dir_kind" == DESKTOP ]]; then config_desktop="$decoded_user_dir"; fi
      else
        user_dirs_safe=0
      fi
    else
      user_dirs_safe=0
    fi
  done < "$config_root/user-dirs.dirs"
fi
# The shell implementation also evals its HOME fallback. Let our parser handle
# unusual HOME characters instead of feeding them through that implementation.
safe_xdg_home='^[/[:alnum:]_.-]+$'
if [[ "$user_dirs_safe" -eq 1 && "$HOME" =~ $safe_xdg_home ]] && command -v xdg-user-dir >/dev/null 2>&1; then
  desktop_path="$(XDG_CONFIG_HOME="$config_root" xdg-user-dir DESKTOP 2>/dev/null)" || desktop_path=''
fi
# Prefer the safely parsed assignment if a shell-based xdg-user-dir mishandles
# spaces in XDG_CONFIG_HOME, or if unsafe unrelated config lines prevented it.
if [[ -n "$config_desktop" && "$desktop_path" != "$config_desktop" ]]; then desktop_path="$config_desktop"; fi
if [[ "$desktop_path" != /* || "$desktop_path" == *$'\n'* || "$desktop_path" == *$'\r'* ]]; then desktop_path="$HOME/Desktop"; fi
while [[ "$desktop_path" != / && "$desktop_path" == */ ]]; do desktop_path="${desktop_path%/}"; done
if [[ "$desktop_path" == "$HOME" || "$desktop_path" == / ]]; then
  echo 'Desktop shortcuts are disabled by your desktop configuration; the application menu shortcut will still be created.' >&2
  desktop_path=''
fi

for item in "$install_dir" "$bin_dir" "$menu_dir" "$runtime_lock" "$bundle_dir/DusmpLauncher" "$bundle_dir/DusmpUpdater"; do check_path "$item"; done
if [[ -n "$desktop_path" ]]; then check_path "$desktop_path"; check_path "$desktop_path/DUSMP Launcher.desktop"; fi
if [[ "$menu_root" == *$'\n'* || "$menu_root" == *$'\r'* ]]; then echo 'Menu path contains unsupported line breaks.' >&2; exit 1; fi
for executable in DusmpLauncher DusmpUpdater; do
  if [[ ! -f "$bundle_dir/$executable" || ! -x "$bundle_dir/$executable" ]]; then echo "Missing executable $executable in this bundle." >&2; exit 1; fi
done
if [[ -e "$bin_dir/DusmpLauncher" && ! -L "$bin_dir/DusmpLauncher" ]]; then echo 'An unmanaged ~/.local/bin/DusmpLauncher already exists. Move it before installing.' >&2; exit 1; fi
if [[ -L "$bin_dir/DusmpLauncher" && "$(readlink -- "$bin_dir/DusmpLauncher")" != "$install_dir/DusmpLauncher" ]]; then echo 'An unrelated DusmpLauncher shortcut already exists.' >&2; exit 1; fi
if [[ -e "$install_dir" && ! -d "$install_dir" ]]; then echo 'The application install path is not a directory.' >&2; exit 1; fi

# .NET FileShare.None on Linux uses the same flock lock. Keep this descriptor
# open throughout installation so a running launcher or its updater excludes us.
mkdir -p -- "$data_dir/updates"
exec 9>>"$runtime_lock"
if ! flock -n 9; then echo 'Close DUSMP Launcher and wait for its update to finish before installing.' >&2; exit 1; fi

mkdir -p -- "$install_parent" "$bin_dir" "$menu_dir"

cleanup() {
  local status=$?
  if [[ "$installed" -eq 0 && -n "$backup_dir" && -d "$backup_dir" && ! -e "$install_dir" ]]; then mv -- "$backup_dir" "$install_dir" || true; fi
  if [[ -n "$stage_dir" && "$stage_dir" == "$install_parent"/.dusmp-launcher-stage.* && -d "$stage_dir" ]]; then rm -rf -- "$stage_dir"; fi
  exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

stage_dir="$(mktemp -d "$install_parent/.dusmp-launcher-stage.XXXXXXXX")"
install -m 0755 -- "$bundle_dir/DusmpLauncher" "$stage_dir/DusmpLauncher"
install -m 0755 -- "$bundle_dir/DusmpUpdater" "$stage_dir/DusmpUpdater"
install -m 0644 -- "$bundle_dir/README.txt" "$stage_dir/README.txt"
if [[ -f "$bundle_dir/icon.png" ]]; then
  check_path "$bundle_dir/icon.png"
  install -m 0644 -- "$bundle_dir/icon.png" "$stage_dir/icon.png"
fi
version="$("$stage_dir/DusmpLauncher" --launcher-version)"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then echo 'The staged launcher failed its version check.' >&2; exit 1; fi
if [[ -e "$install_dir" ]]; then
  backup_dir="$install_parent/DUSMPLauncher.previous.$(date +%s).$$"
  mv -- "$install_dir" "$backup_dir"
fi
mv -- "$stage_dir" "$install_dir"
stage_dir=''
installed=1

link_tmp="$bin_dir/.DusmpLauncher.$$"
ln -s -- "$install_dir/DusmpLauncher" "$link_tmp"
mv -Tf -- "$link_tmp" "$bin_dir/DusmpLauncher"

# Desktop entries quote argument characters; % is a Desktop Entry field code.
desktop_exec="$(printf '%s' "$install_dir/DusmpLauncher" | sed 's/\\/\\\\/g; s/"/\\"/g; s/\$/\\$/g; s/`/\\`/g; s/%/%%/g')"
desktop_icon='application-x-executable'
if [[ -f "$install_dir/icon.png" ]]; then
  desktop_icon="$(printf '%s' "$install_dir/icon.png" | sed 's/\\/\\\\/g')"
fi
desktop_tmp="$(mktemp "$menu_dir/.dusmp-launcher.XXXXXXXX")"
cat > "$desktop_tmp" <<EOF
[Desktop Entry]
Type=Application
Name=DUSMP Launcher
Comment=Play DUSMP Minecraft
Exec="$desktop_exec"
Icon=$desktop_icon
Terminal=false
Categories=Game;
StartupNotify=true
EOF
chmod 0644 -- "$desktop_tmp"
mv -f -- "$desktop_tmp" "$menu_dir/dusmp-launcher.desktop"
if [[ -n "$desktop_path" ]]; then
  if mkdir -p -- "$desktop_path" && desktop_tmp="$(mktemp "$desktop_path/.dusmp-launcher.XXXXXXXX")"; then
    if install -m 0755 -- "$menu_dir/dusmp-launcher.desktop" "$desktop_tmp" && mv -f -- "$desktop_tmp" "$desktop_path/DUSMP Launcher.desktop"; then
      if command -v gio >/dev/null 2>&1; then gio set "$desktop_path/DUSMP Launcher.desktop" metadata::trusted true >/dev/null 2>&1 || true; fi
      echo "Desktop shortcut created at: $desktop_path/DUSMP Launcher.desktop"
    else
      rm -f -- "$desktop_tmp"
      echo 'Could not create the desktop shortcut; open DUSMP Launcher from the application menu.' >&2
    fi
  else
    echo 'Could not access the desktop folder; open DUSMP Launcher from the application menu.' >&2
  fi
fi
echo "DUSMP Launcher $version installed. Open it from the desktop or application menu."
echo "Player accounts, settings and Minecraft data remain in $data_dir."
if [[ -n "$backup_dir" ]]; then echo "Previous application files retained at: $backup_dir"; fi
