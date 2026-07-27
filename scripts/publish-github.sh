#!/usr/bin/env bash
#
# Veröffentlicht den aktuellen main-Stand als EINEN Commit auf GitHub
# (git@github.com:Catweazle74/freecol-collimation.git, Remote „github").
#
# Warum nicht einfach `git push github main`? Das lokale GitLab bleibt die
# Arbeits-Quelle mit voller Historie; auf GitHub geht ein kuratierter Stand
# OHNE die privaten Pfade (.claude/, docs/dev/ — lokale Tooling-Konfiguration
# und Infrastruktur-Notizen mit LAN-Details). Statt History-Rewrite wird pro
# Veröffentlichung ein gefilterter Tree als Folge-Commit auf github/main
# gesetzt — die öffentliche Historie ist damit eine saubere Kette von
# Release-Ständen.
#
# Aufruf:  ./scripts/publish-github.sh [Commit-Message]
#          Default-Message: „Aktualisiere auf <kurzer main-SHA>"

set -euo pipefail
cd "$(dirname "$0")/.."

REMOTE="github"
REMOTE_URL="git@github.com:Catweazle74/freecol-collimation.git"
EXCLUDES=(".claude" "docs/dev")

git remote get-url "$REMOTE" >/dev/null 2>&1 || git remote add "$REMOTE" "$REMOTE_URL"

if [ -n "$(git status --porcelain -uno)" ]; then
    echo "FEHLER: Uncommittete Änderungen — erst committen oder verwerfen." >&2
    exit 1
fi

MSG="${1:-Aktualisiere auf $(git rev-parse --short HEAD)}"

# Gefilterten Tree in einem temporären Index bauen (Arbeitsverzeichnis bleibt
# unberührt).
TMP_INDEX=$(mktemp)
trap 'rm -f "$TMP_INDEX"' EXIT
GIT_INDEX_FILE="$TMP_INDEX" git read-tree HEAD
for path in "${EXCLUDES[@]}"; do
    GIT_INDEX_FILE="$TMP_INDEX" git rm -r --cached --ignore-unmatch -q "$path"
done
TREE=$(GIT_INDEX_FILE="$TMP_INDEX" git write-tree)

# Auf den bisherigen öffentlichen Stand aufsetzen (falls vorhanden).
git fetch -q "$REMOTE" main 2>/dev/null || true
if PARENT=$(git rev-parse -q --verify "refs/remotes/$REMOTE/main" 2>/dev/null); then
    if [ "$(git rev-parse "$PARENT^{tree}")" = "$TREE" ]; then
        echo "Öffentlicher Stand ist bereits aktuell — nichts zu tun."
        exit 0
    fi
    COMMIT=$(git commit-tree "$TREE" -p "$PARENT" -m "$MSG")
else
    COMMIT=$(git commit-tree "$TREE" -m "Veröffentliche FreeCol")
fi

git push "$REMOTE" "$COMMIT:refs/heads/main"
echo "Veröffentlicht: $COMMIT → $REMOTE/main"
