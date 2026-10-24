#!/usr/bin/env bash
# Build CraftBox, sign it with your Samsung certificate profile and install it on the TV.
#   ./install.sh <tv-ip> [certificate-profile]
# Needs Tizen Studio (tizen + sdb on PATH, or at ~/tizen-studio) and a Samsung TV certificate
# profile created in Tizen Studio's Certificate Manager. See README.md.
set -euo pipefail

TV_IP="${1:?usage: ./install.sh <tv-ip> [certificate-profile]}"
PROFILE="${2:-}"
HERE="$(cd "$(dirname "$0")" && pwd)"
TIZEN_HOME="${TIZEN_HOME:-$HOME/tizen-studio}"
export PATH="$TIZEN_HOME/tools:$TIZEN_HOME/tools/ide/bin:$PATH"

command -v sdb >/dev/null || { echo "sdb not found: install Tizen Studio (see README.md)"; exit 1; }
command -v tizen >/dev/null || { echo "tizen CLI not found: install Tizen Studio (see README.md)"; exit 1; }

if [ -z "$PROFILE" ]; then
  # Use the active profile from Certificate Manager.
  PROFILE=$(tizen security-profiles list 2>/dev/null | awk '/\[O\]/ {print $1; exit}') || true
  [ -n "$PROFILE" ] || { echo "No active certificate profile. Create a Samsung TV profile in Certificate Manager, or pass its name."; exit 1; }
fi
echo "Certificate profile: $PROFILE"

echo "Connecting to TV at $TV_IP (Developer Mode must be on with this computer's IP)…"
sdb connect "$TV_IP:26101"
DEVICE=$(sdb devices | awk -v ip="$TV_IP" 'index($1, ip) == 1 {print $3; exit}')
[ -n "$DEVICE" ] || { echo "TV not listed by 'sdb devices'. Check Developer Mode and the Host PC IP on the TV."; exit 1; }
echo "Device: $DEVICE"

echo "Building…"
dotnet build "$HERE/CraftBox/CraftBox.csproj" -c Release
TPK="$HERE/TvTorrent/bin/Release/tizen60/org.craftbox.app-1.0.0.tpk"
[ -f "$TPK" ] || { echo "Build did not produce $TPK"; exit 1; }

echo "Signing with $PROFILE…"
tizen package -t tpk -s "$PROFILE" -- "$TPK"

echo "Installing…"
tizen install -n "$(basename "$TPK")" -t "$DEVICE" -- "$(dirname "$TPK")"
echo "Done. Open 'CraftBox' from the TV's Apps, then follow the on-screen settings address."
