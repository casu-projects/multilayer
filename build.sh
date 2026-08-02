#!/bin/bash
# CasuMP 빌드 시스템 — orchestrator/agent/gateway 단일파일 self-contained 퍼블리시 + mod DLL.
# 최종 산출물은 project/__dist__/ 로 이동한다 (dist = 배포 단위).
# 컴포넌트 config(orchestrator/agent/gateway.json)는 각 프로그램이 첫 실행 시 pwd(__dist__)에
# 기본값으로 자동 생성한다 — build.sh는 건드리지 않는다. run.json/rule.json만 정본 기본값을
# 복사한다 (없을 때만 — 이후 __dist__에서 편집).
#
# 사용법:
#   ./build.sh          # 빌드 (기존 산출물만 정리 — config/데이터 보존)
set -eo pipefail   # 파이프라인에서 빌드 실패가 삼켜지지 않도록 (tail이 종료 코드를 덮지 않음)
ROOT="$(cd "$(dirname "$0")" && pwd)"
DIST="$ROOT/__dist__"
TMP="$DIST/.publish-tmp"

echo "=== CasuMP 빌드 시작 ==="
mkdir -p "$DIST"

# 0) 이전 산출물 정리 — config(*.json)/데이터(saves, instances 등)는 보존 (__dist__가 정본)
rm -f "$DIST"/casu-* "$DIST"/CasuMod.dll "$DIST"/libsteam_api.so "$DIST"/steam_appid.txt
rm -rf "$TMP"
mkdir -p "$TMP"

# 1) 콘솔 프로젝트 3종 — 단일파일 self-contained 퍼블리시 (트리밍 없음: System.Text.Json 리플렉션)
for p in orchestrator agent gateway; do
  echo "── $p 퍼블리시..."
  (cd "$ROOT/$p" && dotnet publish -c Release -r linux-x64 --self-contained true \
     -p:PublishSingleFile=true \
     -p:IncludeNativeLibrariesForSelfExtract=true \
     -p:DebugType=None -p:DebugSymbols=false \
     -o "$TMP" 2>&1 | tail -1)
done

mv "$TMP/CasuMpOrchestrator" "$DIST/casu-orchestrator"
mv "$TMP/CasuMpAgent"         "$DIST/casu-agent"
mv "$TMP/CasuMpGateway"       "$DIST/casu-gateway"
# gateway 네이티브 부수 파일 (Steam 모드 대비 — Steam 비활성이면 런타임에 불필요)
[ -f "$TMP/libsteam_api.so" ] && mv "$TMP/libsteam_api.so" "$DIST/"
[ -f "$TMP/steam_appid.txt" ] && mv "$TMP/steam_appid.txt" "$DIST/"
# Steam 모드: libsteam_api.so는 단일파일 번들에 content로 묻히면 SteamAPI.InitEx의
# DllImport가 앱 디렉토리에서 로드하지 못하므로 반드시 별도 파일로 배치한다 (구 시스템과 동일).
[ -f "$ROOT/../instance/libsteam_api.so" ] && cp "$ROOT/../instance/libsteam_api.so" "$DIST/"
rm -rf "$TMP"

# 2) mod — Release 빌드 → 단일 DLL (BepInEx 플러그인 — 단일파일 대상 아님)
echo "── mod 빌드..."
(cd "$ROOT/mod" && dotnet build -c Release -p:DebugType=None 2>&1 | tail -1)
cp "$ROOT/mod/bin/Release/CasuMod.dll" "$DIST/"

# 3) run.json/rule.json — 정본 기본값 복사 (없을 때만 — 이후 __dist__에서 편집)
[ -f "$DIST/run.json" ]  || cp "$ROOT/../assets/default-jsons/run.json"  "$DIST/"
[ -f "$DIST/rule.json" ] || cp "$ROOT/../assets/default-jsons/rule.json" "$DIST/"

# 4) 프로젝트 bin/obj 정리 — 빌드 산출물은 __dist__로 이동됨 (소스 트리 깔끔 유지,
#    재빌드는 항상 전체 빌드)
for p in orchestrator agent gateway mod; do
  rm -rf "$ROOT/$p/bin" "$ROOT/$p/obj"
done

echo ""
ls -lh "$DIST"
