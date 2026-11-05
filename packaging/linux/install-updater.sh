#!/usr/bin/env bash
set -euo pipefail

publish_dir=${1:?Pass the DusmpUpdater publish directory}
install_dir="$HOME/.local/lib/DusmpUpdater"
mkdir -p "$install_dir" "$HOME/.local/bin" "$HOME/.config/systemd/user"
cp -a "$publish_dir"/. "$install_dir"/
ln -sfn "$install_dir/DusmpUpdater" "$HOME/.local/bin/DusmpUpdater"
install -m 0644 "$(dirname "$0")/dusmp-updater.service" "$HOME/.config/systemd/user/dusmp-updater.service"
systemctl --user daemon-reload
systemctl --user enable --now dusmp-updater.service
