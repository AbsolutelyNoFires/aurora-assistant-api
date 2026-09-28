#!/bin/bash
# Build a release zip that unpacks into Aurora's folder: Patches/AuroraAssistantApi/...
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION=$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' src/AuroraAssistantApi/AuroraAssistantApi.csproj)
dotnet build src/AuroraAssistantApi -c Release -v q -nologo   # needs AuroraDir (see README)
OUT=dist/stage/Patches/AuroraAssistantApi
rm -rf dist/stage && mkdir -p "$OUT"
cp src/AuroraAssistantApi/bin/Release/net48/AuroraAssistantApi.dll "$OUT/"
cp README.md LICENSE "$OUT/" 2>/dev/null || cp README.md "$OUT/"
(cd dist/stage && rm -f "../aurora-assistant-api-$VERSION.zip" && python3 -m zipfile -c "../aurora-assistant-api-$VERSION.zip" Patches)
rm -rf dist/stage
echo "dist/aurora-assistant-api-$VERSION.zip"
