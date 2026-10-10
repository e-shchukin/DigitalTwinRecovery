#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build -c Release
dotnet run --no-build -c Release -- --self-test
dotnet run --no-build -c Release -- --run --runs "${RUNS:-1000}" --seed "${SEED:-20260809}" --output "${OUTPUT:-results.local}"
