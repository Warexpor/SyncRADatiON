#!/usr/bin/env bash
# Run the unit tests if tests/SyncRADation.Tests exists. Extra args go to `dotnet test`.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SDK="${UNITY_DOTNET_SDK:-$HOME/Unity/Hub/Editor/6000.6.0f1/Editor/Data/DotNetSdk}"
TESTS="$ROOT/tests/SyncRADation.Tests"

if [[ ! -d "$TESTS" ]]; then
  echo "NOTE: $TESTS does not exist yet; nothing to run."
  exit 0
fi

export DOTNET_ROOT="$SDK"
export PATH="$SDK:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
cd "$ROOT"
exec dotnet test tests/SyncRADation.Tests "$@"
