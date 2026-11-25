#!/usr/bin/env bash
# Downloads the two dynamic frameworks an iOS app on SdlVulkan.Renderer bundles, into the folder given:
#   SDL3.xcframework     from SDL's own release .dmg
#   MoltenVK.xcframework from Khronos's MoltenVK release (Vulkan on Metal; iOS has no Vulkan)
#
# Run by SdlVulkan.Renderer.targets (beside this file, in the package's buildTransitive folder), which passes the
# versions and the SHA-256 of each download in the environment. The pins live there, not here.
#
# macOS only (hdiutil), as an iOS build is. Idempotent: a framework already present is kept. Each one is copied into
# a partial folder inside the destination and only then renamed into place, so an interrupted run does not leave a
# half-copied framework that the next build would take for a finished one.
set -euo pipefail

dest="${1:?usage: fetch-ios-frameworks.sh <destination folder>}"
: "${SDL_VERSION:?}" "${SDL_SHA256:?}" "${MOLTENVK_VERSION:?}" "${MOLTENVK_SHA256:?}"

verify() { # file expected-sha256
  local actual
  actual="$(shasum -a 256 "$1" | cut -d' ' -f1)"
  if [ "$actual" != "$2" ]; then
    echo "Checksum mismatch for $(basename "$1"): expected $2, got $actual" >&2
    exit 1
  fi
}

mkdir -p "$dest"
work="$(mktemp -d)"
partial="$dest/.partial-$$"
cleanup() {
  if [ -d "$work/sdl" ]; then hdiutil detach "$work/sdl" > /dev/null 2>&1 || true; fi
  rm -rf "$work" "$partial"
}
trap cleanup EXIT
mkdir -p "$partial"

if [ ! -d "$dest/SDL3.xcframework" ]; then
  echo "SdlVulkan.Renderer: fetching SDL $SDL_VERSION for iOS"
  curl -fsSL -o "$work/SDL3.dmg" \
    "https://github.com/libsdl-org/SDL/releases/download/release-$SDL_VERSION/SDL3-$SDL_VERSION.dmg"
  verify "$work/SDL3.dmg" "$SDL_SHA256"
  hdiutil attach -nobrowse -readonly -mountpoint "$work/sdl" "$work/SDL3.dmg" > /dev/null
  cp -R "$work/sdl/SDL3.xcframework" "$partial/"
  hdiutil detach "$work/sdl" > /dev/null
  mv "$partial/SDL3.xcframework" "$dest/"
fi

if [ ! -d "$dest/MoltenVK.xcframework" ]; then
  echo "SdlVulkan.Renderer: fetching MoltenVK $MOLTENVK_VERSION for iOS"
  curl -fsSL -o "$work/MoltenVK-ios.tar" \
    "https://github.com/KhronosGroup/MoltenVK/releases/download/$MOLTENVK_VERSION/MoltenVK-ios.tar"
  verify "$work/MoltenVK-ios.tar" "$MOLTENVK_SHA256"
  mkdir -p "$work/moltenvk"
  tar -xf "$work/MoltenVK-ios.tar" -C "$work/moltenvk"
  # The DYNAMIC one: the renderer loads it by path, which a static library cannot be.
  framework="$(find "$work/moltenvk" -type d -path '*dynamic*' -name 'MoltenVK.xcframework' | head -n 1)"
  if [ -z "$framework" ]; then
    echo "No dynamic MoltenVK.xcframework in MoltenVK-ios.tar $MOLTENVK_VERSION; its layout was:" >&2
    find "$work/moltenvk" -maxdepth 4 -name '*.xcframework' >&2
    exit 1
  fi
  cp -R "$framework" "$partial/"
  mv "$partial/MoltenVK.xcframework" "$dest/"
fi

# The device slice each must carry, at the path the renderer's AppleVulkan loads from once it is in the bundle.
for f in "SDL3.xcframework/ios-arm64/SDL3.framework/SDL3" "MoltenVK.xcframework/ios-arm64/MoltenVK.framework/MoltenVK"; do
  if [ ! -f "$dest/$f" ]; then
    echo "Missing the device slice $f; the slices are:" >&2
    ls "$dest/${f%%/*}" >&2
    exit 1
  fi
done
echo "SdlVulkan.Renderer: iOS frameworks ready in $dest"
