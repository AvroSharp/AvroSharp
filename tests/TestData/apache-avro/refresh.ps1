#!/usr/bin/env pwsh
# Re-downloads the vendored Apache Avro test data from a pinned commit.
param(
    [string] $Commit = '8fa2067f70e3012cb3fd9a8839cd97e8c7cc1772' # release-1.12.2
)

$ErrorActionPreference = 'Stop'
$files = [ordered]@{
    'schema-tests.txt'               = 'share/test/data/schema-tests.txt'
    'weather.avro'                   = 'share/test/data/weather.avro'
    'weather-sorted.avro'            = 'share/test/data/weather-sorted.avro'
    'weather-snappy.avro'            = 'share/test/data/weather-snappy.avro'
    'weather.json'                   = 'share/test/data/weather.json'
    'syncInMeta.avro'                = 'share/test/data/syncInMeta.avro'
    'messageV1/README.md'            = 'share/test/data/messageV1/README.md'
    'messageV1/test_message.bin'     = 'share/test/data/messageV1/test_message.bin'
    'messageV1/test_schema.avsc'     = 'share/test/data/messageV1/test_schema.avsc'
    'LICENSE.txt'                    = 'LICENSE.txt'
    'NOTICE.txt'                     = 'NOTICE.txt'
}

foreach ($entry in $files.GetEnumerator()) {
    $url = "https://raw.githubusercontent.com/apache/avro/$Commit/$($entry.Value)"
    $target = Join-Path $PSScriptRoot $entry.Key
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    Invoke-WebRequest $url -OutFile $target
    Write-Host "Downloaded $($entry.Value)"
}
