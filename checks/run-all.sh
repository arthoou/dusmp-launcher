#!/usr/bin/env bash
# Every check, then publish — and only publish when every one of them passed.
#
# The exit status has to come from the check itself. Piping into `tail` hands
# back tail's status instead, which silently published a build whose UI check
# was failing.
set -uo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"

failed=()
run() {
  local name="$1"; shift
  local output
  if output="$("$@" 2>&1)"; then
    printf '%s\n' "$output" | tail -1
  else
    failed+=("$name")
    printf '  FALHOU: %s\n' "$name"
    printf '%s\n' "$output" | tail -5
  fi
}

run "ui-startup" node checks/ui-startup.cjs
run "skin-model" node checks/skin-model.cjs
for project in StartupAssets ExternalDetection AuthFlow DownloadIntegrity; do
  run "$project" "$DOTNET" run --project "checks/$project" -c Release
done

if [ ${#failed[@]} -ne 0 ]; then
  printf '\nNao publiquei. Checks com falha: %s\n' "${failed[*]}" >&2
  exit 1
fi

[ "${1:-}" = "--no-publish" ] && { echo "Todos os checks passaram."; exit 0; }

# The publish has to be judged on its own exit status first.
#
# This used to be `./build.sh all | grep -Ei error && exit 1`, which reads
# backwards under `set -o pipefail`: when the build really fails the pipeline
# inherits its non-zero status, the `&&` does not fire, and the script falls
# through and announces a publish that never happened. It did exactly that on a
# build that died with "Text file busy".
if ! output="$(./build.sh all 2>&1)"; then
  printf '%s
' "$output" | grep -Ei "error|erro |busy|espaco|space" | head -5
  echo "Nao publiquei: o build falhou." >&2
  exit 1
fi
if printf '%s
' "$output" | grep -qEi "error|erro "; then
  printf '%s
' "$output" | grep -Ei "error|erro " | head -5
  echo "Nao publiquei: o build passou mas reclamou." >&2
  exit 1
fi
echo "Todos os checks passaram. Publicado."
