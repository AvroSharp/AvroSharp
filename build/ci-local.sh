#!/usr/bin/env bash
# Runs the steps of the Linux CI job (.github/workflows/ci.yml) in order, so passing here means passing that job:
# restore, build, tests on every target with coverage, the coverage check, tests without hardware intrinsics, the
# samples, the Native AOT smoke test, pack, and the package consumers. Meant for the dev container (#139); see
# CONTRIBUTING.md for what it does not cover.
set -euo pipefail
cd "$(dirname "$0")/.."

export CI=true DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true NUGET_CERT_REVOCATION_MODE=offline
export AVROSHARP_RANDOM_SCHEMA_FAILURES="$PWD/TestResults/random-schema-failure"

case "$(uname -m)" in
  x86_64) rid=linux-x64 ;;
  aarch64 | arm64) rid=linux-arm64 ;;
  *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

step() { echo; echo "==> $*"; }

# Results of an earlier run would be counted by the coverage check.
rm -rf TestResults artifacts/bin/*/*/TestResults artifacts/coverage

step Restore
dotnet restore AvroSharp.slnx

step Build
dotnet build AvroSharp.slnx -c Release --no-restore

step Test
dotnet test --solution AvroSharp.slnx -c Release --no-build --coverage --coverage-output-format cobertura

step Coverage summary
build/coverage-summary.sh

# AvroSharp.Confluent's and AvroSharp.KafkaFlow's Redpanda tests, and the Confluent and KafkaFlowEvents samples, need
# Docker with Linux containers; without it they are left out, as on CI's other runners.
docker=no
if docker info > /dev/null 2>&1; then
  docker=yes
  docker compose -f samples/Confluent/compose.yaml up -d --wait
  step Test against Redpanda
  dotnet test --project tests/AvroSharp.Confluent.Tests -c Release --no-build --treenode-filter "/*/*/RedpandaTests/*"
  dotnet test --project tests/AvroSharp.KafkaFlow.Tests -c Release --no-build --treenode-filter "/*/*/KafkaFlowRedpandaTests/*"
fi

step Test without hardware intrinsics
DOTNET_EnableHWIntrinsic=0 dotnet test --project tests/AvroSharp.Tests -c Release --no-build -f net10.0

step Samples
for sample in samples/*/; do
  if { [ "$sample" = samples/Confluent/ ] || [ "$sample" = samples/KafkaFlowEvents/ ]; } && [ "$docker" = no ]; then
    echo "Skipping $sample: it needs Docker"
    continue
  fi
  dotnet run --project "$sample" -c Release --no-build
done
if [ "$docker" = yes ]; then
  docker compose -f samples/Confluent/compose.yaml down
fi

step Native AOT smoke test
dotnet publish tests/AvroSharp.AotSmoke -c Release -r "$rid"
"./artifacts/publish/AvroSharp.AotSmoke/release_$rid/AvroSharp.AotSmoke"

step Pack
dotnet pack AvroSharp.slnx -c Release --no-build

step Package consumers
build/test-packages.sh

echo
echo "The Linux CI job's steps passed ($rid). Coverage: artifacts/coverage/SummaryGithub.md"
