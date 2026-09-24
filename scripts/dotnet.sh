#!/usr/bin/env bash
# Runs the .NET SDK in a container, with the repository mounted, so a machine without the 10.x SDK can
# still build, test and run dotnet-ef.
#
#   bash scripts/dotnet.sh build DocReader.slnx
#   bash scripts/dotnet.sh test tests/unit/DocReader.UnitTests
#   bash scripts/dotnet.sh ef migrations add Name --project src/DocReader.Infrastructure --startup-project apps/api
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SDK_IMAGE="${DOCREADER_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"

# Git Bash on Windows rewrites arguments that look like Unix paths, which turns /src into a host path.
# Hand Docker a native path and switch the rewriting off.
if command -v cygpath >/dev/null 2>&1; then
  HOST_ROOT="$(cygpath --windows "${REPO_ROOT}")"
  export MSYS_NO_PATHCONV=1
  export MSYS2_ARG_CONV_EXCL="*"
else
  HOST_ROOT="${REPO_ROOT}"
fi

if [ "${1:-}" = "ef" ]; then
  shift
  COMMAND="dotnet tool restore >/dev/null && dotnet dotnet-ef $(printf '%q ' "$@")"
else
  COMMAND="dotnet $(printf '%q ' "$@")"
fi

exec docker run --rm -t \
  -v "${HOST_ROOT}:/src" \
  -v docreader-nuget:/root/.nuget/packages \
  -w /src \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e DOTNET_NOLOGO=1 \
  "${SDK_IMAGE}" \
  bash -lc "${COMMAND}"
