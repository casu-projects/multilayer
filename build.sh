#!/bin/bash
ROOT="$(cd "$(dirname "$0")" && pwd)"
DIST="$ROOT/__dist__"
TMP="$DIST/.publish-tmp"

echo "=== CasuMP 빌드 시작 ==="
mkdir -p "$DIST"

rm -f "$DIST"/casu-* "$DIST"/CasuMod.dll "$DIST"/libsteam_api.so "$DIST"/steam_appid.txt "$DIST"/Steamworks.NET.dll
rm -rf "$TMP"
mkdir -p "$TMP"

for p in server agent steam; do
  echo "── $p 퍼블리시..."
  (cd "$ROOT/$p" && dotnet publish -c Release -r linux-x64 --self-contained true \
     -p:PublishSingleFile=true \
     -p:IncludeNativeLibrariesForSelfExtract=true \
     -p:DebugType=None -p:DebugSymbols=false \
     -o "$TMP" 2>&1 | tail -1)
done

mv "$TMP/CasuMpServer" "$DIST/casu-server"
mv "$TMP/CasuMpAgent"  "$DIST/casu-agent"
mv "$TMP/CasuMpSteam"  "$DIST/casu-steam"
[ -f "$TMP/libsteam_api.so" ] && mv "$TMP/libsteam_api.so" "$DIST/"
[ -f "$TMP/steam_appid.txt" ] && mv "$TMP/steam_appid.txt" "$DIST/"
[ -f "$ROOT/../assets/dependency/casualties-together/libsteam_api.so" ] && cp "$ROOT/../assets/dependency/casualties-together/libsteam_api.so" "$DIST/"

SWNET="$ROOT/../assets/dependency/casualties-together/Steamworks.NET.dll"
if [ ! -f "$SWNET" ]; then
  echo "오류: $SWNET 없음 — Linux Pack=4 Steamworks.NET.dll이 필요합니다." >&2
  exit 1
fi
cp "$SWNET" "$DIST/"
rm -rf "$TMP"

echo "── map wwwroot..."
rm -rf "$DIST/map"
mkdir -p "$DIST/map"
cp -r "$ROOT/server/map/wwwroot" "$DIST/map/"

echo "── mod 빌드..."
MOD_LOG="$DIST/.mod-build.log"
rm -f "$ROOT/mod/bin/Release/CasuMod.dll"
(cd "$ROOT/mod" && dotnet build -c Release -p:DebugType=None >"$MOD_LOG" 2>&1)
MOD_RC=$?
if [ "$MOD_RC" -ne 0 ] || [ ! -f "$ROOT/mod/bin/Release/CasuMod.dll" ]; then
  tail -40 "$MOD_LOG" >&2
  echo "오류: mod 빌드 실패" >&2
  exit 1
fi
tail -1 "$MOD_LOG"
cp "$ROOT/mod/bin/Release/CasuMod.dll" "$DIST/"
rm -f "$MOD_LOG"

[ -f "$DIST/run.json" ]  || cp "$ROOT/../assets/multilayer-default-jsons/run.json"  "$DIST/"
[ -f "$DIST/rule.json" ] || cp "$ROOT/../assets/multilayer-default-jsons/rule.json" "$DIST/"

for p in server agent steam mod; do
  rm -rf "$ROOT/$p/bin" "$ROOT/$p/obj"
done

echo ""
ls -lh "$DIST"
