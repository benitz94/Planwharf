#!/bin/sh
# Planwharf installer for Linux (also works on macOS).
#
# It downloads the latest stable Planwharf release from GitHub, checks its SHA-256,
# and puts it in ~/.local/share/planwharf. It adds the command `planwharf` and a menu entry.
# It never uses sudo and touches nothing outside your home folder.
# Running it again updates Planwharf and keeps your projects and settings.
#
# Read it before you run it:
#   curl -fsSL https://raw.githubusercontent.com/benitz94/Planwharf/main/install.sh -o install.sh
#   less install.sh
#   sh install.sh
set -eu

REPO="benitz94/Planwharf"
DEST="${PLANWHARF_HOME:-$HOME/.local/share/planwharf}"
BIN_DIR="$HOME/.local/bin"
APPS_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"

say() { printf '%s\n' "$*"; }
fail() { printf 'Planwharf install: %s\n' "$*" >&2; exit 1; }
have() { command -v "$1" >/dev/null 2>&1; }

# 1. What Planwharf needs.
have node || fail "Node.js is missing. Install Node.js 18 or newer (for example from your distribution or https://nodejs.org), then run this again."
NODE_MAJOR=$(node -p 'process.versions.node.split(".")[0]')
[ "$NODE_MAJOR" -ge 18 ] || fail "Node.js $NODE_MAJOR is too old. Install Node.js 18 or newer, then run this again."
if have curl; then fetch() { curl -fsSL "$1" -o "$2"; }
elif have wget; then fetch() { wget -q "$1" -O "$2"; }
else fail "Neither curl nor wget was found. Install one of them, then run this again."; fi
if have sha256sum; then sha() { sha256sum "$1" | cut -d' ' -f1; }
elif have shasum; then sha() { shasum -a 256 "$1" | cut -d' ' -f1; }
else fail "sha256sum was not found, so the download cannot be checked."; fi

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT INT TERM

# 2. The latest stable release and its checksum, as GitHub reports them.
say "Looking for the latest Planwharf release..."
fetch "https://api.github.com/repos/$REPO/releases/latest" "$TMP/release.json"
eval "$(node -e '
  const r = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
  const a = (r.assets || []).find(x => /^Planwharf-v\d+\.\d+\.\d+\.zip$/.test(x.name));
  if (r.prerelease || r.draft || !a || !/^sha256:[a-f0-9]{64}$/.test(a.digest || "")) { console.log("fail \"The latest release has no checked download.\""); process.exit(0); }
  const q = s => "\x27" + s.replace(/\x27/g, "") + "\x27";
  console.log("TAG=" + q(r.tag_name) + "; ZIP_URL=" + q(a.browser_download_url) + "; ZIP_NAME=" + q(a.name) + "; ZIP_SHA=" + q(a.digest.slice(7)));
' "$TMP/release.json")"

say "Downloading $ZIP_NAME..."
fetch "$ZIP_URL" "$TMP/$ZIP_NAME"
[ "$(sha "$TMP/$ZIP_NAME")" = "$ZIP_SHA" ] || fail "The download does not match its checksum. Nothing was installed."

# 3. Unpack with whatever is available.
mkdir -p "$TMP/unpacked"
if have unzip; then unzip -q "$TMP/$ZIP_NAME" -d "$TMP/unpacked"
elif have python3; then python3 -c 'import sys, zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' "$TMP/$ZIP_NAME" "$TMP/unpacked"
else fail "unzip or python3 is needed to unpack the download."; fi
[ -f "$TMP/unpacked/Planwharf/planwharf.cjs" ] || fail "The download does not look like Planwharf. Nothing was installed."

# 4. Copy over the app files. Your projects (plan.json) and their history (.attempts-*) stay.
mkdir -p "$DEST"
cp -R "$TMP/unpacked/Planwharf/." "$DEST/"
chmod +x "$DEST/planwharf.sh"

# 5. The `planwharf` command and a menu entry.
mkdir -p "$BIN_DIR"
cat > "$BIN_DIR/planwharf" <<EOF
#!/bin/sh
exec sh "$DEST/planwharf.sh" "\$@"
EOF
chmod +x "$BIN_DIR/planwharf"
if [ "$(uname -s)" = "Linux" ]; then
  mkdir -p "$APPS_DIR"
  cat > "$APPS_DIR/planwharf.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Planwharf
Comment=Your AI crew in one chat window
Exec=$BIN_DIR/planwharf
Terminal=false
Categories=Development;
EOF
fi

say ""
say "Planwharf $TAG is installed in $DEST."
case ":$PATH:" in
  *":$BIN_DIR:"*) say "Start it with: planwharf" ;;
  *) say "Start it with: $BIN_DIR/planwharf"
     say "(Add $BIN_DIR to your PATH to type just 'planwharf'.)" ;;
esac
say "Planwharf can update itself later from Settings > Updates. Running this script again also updates it."
