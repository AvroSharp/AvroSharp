#!/usr/bin/env bash
# Checks the test runs' coverage reports and merges them into a summary of the shipped assemblies
# (artifacts/coverage/SummaryGithub.md, and the job summary on GitHub). Run after `dotnet test --coverage`; CI and
# build/ci-local.sh both use it.
set -euo pipefail
cd "$(dirname "$0")/.."

assemblies=(AvroSharp AvroSharp.Aws.Glue AvroSharp.Aws.Glue.Kafka AvroSharp.Azure.SchemaRegistry AvroSharp.Codecs AvroSharp.CodeGen AvroSharp.Confluent AvroSharp.Generators AvroSharp.KafkaFlow AvroSharp.Tool)

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
  -targetdir:artifacts/coverage "-reporttypes:MarkdownSummaryGithub;JsonSummary" "-assemblyfilters:${filters%;}"

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  cat artifacts/coverage/SummaryGithub.md >> "$GITHUB_STEP_SUMMARY"
fi

# The README's coverage badge (shields.io's endpoint format), which the docs workflow publishes with the site.
line=$(sed -n 's/.*"linecoverage": *\([0-9.]*\).*/\1/p' artifacts/coverage/Summary.json | head -1)
if [ -z "$line" ]; then
  echo "::error::No line coverage in artifacts/coverage/Summary.json"
  exit 1
fi
color=$(awk -v c="$line" 'BEGIN { print c >= 90 ? "brightgreen" : c >= 80 ? "green" : c >= 70 ? "yellowgreen" : "orange" }')
printf '{"schemaVersion":1,"label":"coverage","message":"%s%%","color":"%s"}\n' "$(awk -v c="$line" 'BEGIN { printf "%.1f", c }')" "$color" \
  > artifacts/coverage/coverage.json
cat artifacts/coverage/coverage.json
