#!/usr/bin/env bash
# Builds the linux-x64 Native AOT Ingestor in the .NET AOT SDK container (Native AOT cannot cross-compile
# from Windows or macOS) and runs its self-test. Output: artifacts/collector/.
# Usage, from the repository root: deploy/collector/publish.sh
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
out="$repo/artifacts/collector"
image="mcr.microsoft.com/dotnet/sdk:10.0-noble-aot"

rm -rf "$out"
mkdir -p "$out"

# Git Bash on Windows would rewrite the container paths; docker needs a native host path there.
host_path() { if command -v cygpath >/dev/null; then cygpath -w "$1"; else echo "$1"; fi; }

# The source is copied inside the container so host bin/obj folders never mix with Linux builds.
# deploy/collector/local holds untracked per-machine tools and secrets; the build never needs them.
MSYS_NO_PATHCONV=1 docker run --rm \
  -v "$(host_path "$repo"):/src:ro" \
  -v "$(host_path "$out"):/out" \
  "$image" bash -c '
    set -euo pipefail
    mkdir /work
    tar -C /src --exclude=./data --exclude=./artifacts --exclude=./.git --exclude=./deploy/collector/local --exclude="*/bin" --exclude="*/obj" -cf - . | tar -C /work -xf -
    dotnet publish /work/src/TransitPulse.Ingestor -r linux-x64 -c Release -o /work/publish
    /work/publish/TransitPulse.Ingestor --self-test
    cp /work/publish/TransitPulse.Ingestor /work/publish/appsettings.json /out/
  '

cp "$repo"/deploy/collector/{install.sh,upload.sh,health.sh,gtfs-snapshot.sh,transitpulse-ingestor.service,transitpulse-upload.service,transitpulse-upload.timer,transitpulse-gtfs.service,transitpulse-gtfs.timer} "$out/"
git -C "$repo" rev-parse --short HEAD > "$out/VERSION"
ls -la "$out"
