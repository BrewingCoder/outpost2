#!/usr/bin/env bash
# Tamp build-script entry point. Scaffolded by `tamp init` — safe to edit; safe to delete.
# Restores the local tool manifest on first run, then forwards to `dotnet tamp <args>`.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STAMP="$SCRIPT_DIR/.config/.tamp-tools-restored"

if [ -f "$SCRIPT_DIR/.config/dotnet-tools.json" ] && [ ! -f "$STAMP" ]; then
    (cd "$SCRIPT_DIR" && dotnet tool restore) && mkdir -p "$SCRIPT_DIR/.config" && touch "$STAMP"
fi

cd "$SCRIPT_DIR" && exec dotnet tamp "$@"