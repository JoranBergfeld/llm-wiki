#!/bin/sh
# Install or update the `wiki` CLI from the rolling `latest` prerelease.
#
#   curl -fsSL https://raw.githubusercontent.com/JoranBergfeld/llm-wiki/main/scripts/install.sh | sh
#
# Re-running it is the update path: `latest` is recreated at the newest green
# commit on main, so this always fetches that build.
#
# Environment:
#   WIKI_INSTALL_DIR  where to put the binary (default: $HOME/.local/bin)
#   WIKI_VERSION      release tag to install (default: latest)
#
# POSIX sh on purpose - this runs on whatever /bin/sh a fresh box has.
set -eu

REPO="JoranBergfeld/llm-wiki"
TAG="${WIKI_VERSION:-latest}"
INSTALL_DIR="${WIKI_INSTALL_DIR:-$HOME/.local/bin}"

die() {
    echo "install.sh: $1" >&2
    exit 1
}

os="$(uname -s)"
arch="$(uname -m)"

case "$os" in
    Linux)
        case "$arch" in
            x86_64 | amd64) rid="linux-x64" ;;
            aarch64 | arm64) rid="linux-arm64" ;;
            *) die "unsupported architecture '$arch' on Linux. Build from source: see the README." ;;
        esac
        ;;
    Darwin)
        case "$arch" in
            arm64) rid="osx-arm64" ;;
            # CI does not build osx-x64 - GitHub's Intel-Mac runners queue long
            # enough to stall the gated release - and an arm64 binary will not
            # run under Rosetta, which translates the other direction.
            x86_64) die "Intel macOS has no published binary. Build from source: see the README." ;;
            *) die "unsupported architecture '$arch' on macOS. Build from source: see the README." ;;
        esac
        ;;
    MINGW* | MSYS* | CYGWIN*)
        die "on Windows use scripts/install.ps1 from PowerShell instead."
        ;;
    *)
        die "unsupported OS '$os'. Build from source: see the README."
        ;;
esac

asset="wiki-$rid.tar.gz"
url="https://github.com/$REPO/releases/download/$TAG/$asset"

if command -v curl > /dev/null 2>&1; then
    fetch() { curl -fsSL "$1" -o "$2"; }
elif command -v wget > /dev/null 2>&1; then
    fetch() { wget -qO "$2" "$1"; }
else
    die "need curl or wget on PATH."
fi

tmp="$(mktemp -d)"
# Clean up the scratch dir on any exit path, including the failure ones.
trap 'rm -rf "$tmp"' EXIT INT TERM

echo "Downloading $asset from the '$TAG' release..."
fetch "$url" "$tmp/$asset" || die "download failed: $url"

# Verify against the SHA256SUMS published alongside the archives. This proves
# the archive is the one that release produced - integrity, not provenance;
# both files come from the same host, so it catches a truncated or corrupted
# download, not a compromised release. `wiki --version` reports the commit the
# binary was built from, which is the chain back to source.
#
# Not fatal if the tooling is missing: sha256sum is coreutils, shasum is the
# perl script macOS ships instead, and a box with neither should still be able
# to install. It IS fatal if we have a checksum and it disagrees.
echo "Verifying checksum..."
if fetch "https://github.com/$REPO/releases/download/$TAG/SHA256SUMS" "$tmp/SHA256SUMS" 2> /dev/null; then
    expected="$(awk -v f="$asset" '$2 == f || $2 == "*" f { print $1 }' "$tmp/SHA256SUMS")"
    [ -n "$expected" ] || die "SHA256SUMS has no entry for $asset."

    if command -v sha256sum > /dev/null 2>&1; then
        actual="$(sha256sum "$tmp/$asset" | cut -d' ' -f1)"
    elif command -v shasum > /dev/null 2>&1; then
        actual="$(shasum -a 256 "$tmp/$asset" | cut -d' ' -f1)"
    else
        actual=""
        echo "  no sha256sum or shasum on PATH - skipping verification." >&2
    fi

    if [ -n "$actual" ] && [ "$actual" != "$expected" ]; then
        die "checksum mismatch for $asset.
  expected $expected
  actual   $actual
Refusing to install. Retry, and if it persists, open an issue."
    fi
    [ -n "$actual" ] && echo "  ok ($expected)"
else
    # Releases published before SHA256SUMS existed, or a WIKI_VERSION pointing
    # at one, have nothing to check against.
    echo "  no SHA256SUMS published for '$TAG' - skipping verification." >&2
fi

tar -xzf "$tmp/$asset" -C "$tmp" || die "could not extract $asset"
[ -f "$tmp/wiki" ] || die "archive did not contain a 'wiki' binary"

mkdir -p "$INSTALL_DIR"
# Stage inside the destination directory, then rename over the target. The
# rename is what matters: it replaces the directory entry rather than writing
# through the existing inode, so a currently-executing `wiki` keeps its own
# copy and we never hit ETXTBSY updating a running binary.
#
# Staging here rather than renaming straight out of "$tmp" is deliberate -
# mktemp -d and $INSTALL_DIR are usually on different filesystems, and a
# cross-device mv silently degrades to copy-then-unlink, which writes through
# the target inode and reintroduces exactly the failure this avoids.
staged="$INSTALL_DIR/.wiki.$$.new"
# Leave no staged file behind if anything below fails; the EXIT trap above
# only covers "$tmp".
trap 'rm -rf "$tmp"; rm -f "$staged"' EXIT INT TERM
cp "$tmp/wiki" "$staged"
chmod +x "$staged"
mv -f "$staged" "$INSTALL_DIR/wiki"

version="$("$INSTALL_DIR/wiki" --version 2>/dev/null || echo "unknown")"
echo "Installed wiki $version to $INSTALL_DIR/wiki"

# Only nag about PATH when it is actually missing; the surrounding colons make
# this an exact element match rather than a substring hit.
case ":$PATH:" in
    *":$INSTALL_DIR:"*) ;;
    *)
        echo
        echo "$INSTALL_DIR is not on your PATH. Add it:"
        echo "  export PATH=\"$INSTALL_DIR:\$PATH\""
        ;;
esac
