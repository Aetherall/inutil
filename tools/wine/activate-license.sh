#!/usr/bin/env bash
# Activate a Unity Personal seat for the wine prefix's machine id, using your own
# credentials from <repo>/.env (USERNAME=... / PASSWORD=...). The .env is gitignored;
# credentials never enter the repo.
#
# Encodes two gotchas found the hard way:
#  1. Extract creds with sed, NOT `source` — a password/email with shell-special
#     chars gets silently truncated by `source`, producing a 401 "Input Error".
#  2. After activation, the entitlement is written to both AppData\Local and
#     ProgramData; if those two copies differ, Unity's resolver rejects BOTH as
#     "duplicated entitlement group ids with different contents" and the editor
#     sees no license. We make them byte-identical.
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/env.sh"

ENV_FILE="$REPO_ROOT/.env"
[ -f "$ENV_FILE" ] || { echo "!! $ENV_FILE missing. Create it with:\n   USERNAME=you@example.com\n   PASSWORD=yourUnityPassword" >&2; exit 1; }
CLIENT_UNIX="$WINEPREFIX/drive_c/Unity/$UNITY_VERSION/Editor/Data/Resources/Licensing/Client/Unity.Licensing.Client.exe"
[ -f "$CLIENT_UNIX" ] || { echo "!! licensing client missing — run 'unity-setup' first" >&2; exit 1; }

# literal extraction (no shell expansion), strip one layer of surrounding quotes
strip() { local v="$1"; case "$v" in \"*\") v="${v#\"}"; v="${v%\"}";; \'*\') v="${v#\'}"; v="${v%\'}";; esac; printf '%s' "$v"; }
U="$(strip "$(sed -n 's/^USERNAME=//p' "$ENV_FILE")")"
P="$(strip "$(sed -n 's/^PASSWORD=//p' "$ENV_FILE")")"
[ -n "$U" ] && [ -n "$P" ] || { echo "!! USERNAME/PASSWORD not both set in $ENV_FILE" >&2; exit 1; }

echo ">> activating Unity Personal seat (machine-bound) ..."
wine "$LICENSE_CLIENT_WIN" --activate-all --include-personal --username "$U" --password "$P" 2>&1 \
  | grep -iE 'Activation processed|Seat ID|ASSIGN_SEAT|Successfully updated|error occured|Unauthorized|Input Error' || true

# dedup the two entitlement files so the resolver accepts the Personal seat
FRESH="$WINEPREFIX/drive_c/users/$(whoami 2>/dev/null || echo steamuser)/AppData/Local/Unity/licenses/UnityEntitlementLicense.xml"
[ -f "$FRESH" ] || FRESH="$(find "$WINEPREFIX/drive_c/users" -ipath '*AppData/Local/Unity/licenses/UnityEntitlementLicense.xml' 2>/dev/null | head -1)"
STALE="$WINEPREFIX/drive_c/ProgramData/Unity/licenses/UnityEntitlementLicense.xml"
if [ -f "$FRESH" ] && [ -f "$STALE" ] && ! cmp -s "$FRESH" "$STALE"; then
  newest="$FRESH"; [ "$STALE" -nt "$FRESH" ] && newest="$STALE"
  cp "$newest" "$FRESH"; cp "$newest" "$STALE"
  echo ">> reconciled duplicate entitlement files"
fi

echo ">> verifying entitlement ..."
if grep -q 'UnityPersonal' "$FRESH" 2>/dev/null; then
  echo ">> OK — UnityPersonal entitlement present."
else
  echo "!! no UnityPersonal entitlement found; check credentials / Unity account" >&2; exit 1
fi
