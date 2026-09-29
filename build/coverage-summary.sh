#!/usr/bin/env bash
# Checks the test runs' coverage reports and merges them into a summary of the shipped assemblies
# (artifacts/coverage/SummaryGithub.md, and the job summary on GitHub). Run after `dotnet test --coverage`; CI and
# build/ci-local.sh both use it.
set -euo pipefail
cd "$(dirname "$0")/.."

assemblies=(AvroSharp AvroSharp.Codecs AvroSharp.CodeGen AvroSharp.Generators AvroSharp.Tool)

# Every report must have data, and together they must cover each shipped assembly: collection that silently records
# nothing would otherwise shrink the summary without failing anything (#133).
reports=$(find artifacts/bin TestResults -name '*.cobertura.xml' 2>/dev/null || true)
empty=$(for f in $reports; do grep -q '<package ' "$f" || echo "$f"; done)
if [ -z "$reports" ] || [ -n "$empty" ]; then
  echo "::error::Coverage reports with no data: ${empty:-no reports at all}"
  exit 1
fi

for assembly in "${assemblies[@]}"; do
  # shellcheck disable=SC2086 # the report paths have no spaces
  if ! grep -Eq "<package [^>]*name=\"$assembly\"" $reports; then
    echo "::error::No coverage report includes $assembly"
    exit 1
  fi
done

filters=$(printf '+%s;' "${assemblies[@]}")
dotnet tool restore > /dev/null
dotnet tool run reportgenerator "-reports:artifacts/bin/**/TestResults/**/*.cobertura.xml;TestResults/**/*.cobertura.xml" \
  -targetdir:artifacts/coverage -reporttypes:MarkdownSummaryGithub "-assemblyfilters:${filters%;}"

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  cat artifacts/coverage/SummaryGithub.md >> "$GITHUB_STEP_SUMMARY"
fi
