#!/usr/bin/env pwsh
# Re-downloads the vendored Apache Avro test data from a pinned commit.
param(
    [string] $Commit = '8fa2067f70e3012cb3fd9a8839cd97e8c7cc1772' # release-1.12.2
)

$ErrorActionPreference = 'Stop'
$files = @{
    'schema-tests.txt' = 'share/test/data/schema-tests.txt'
    'LICENSE.txt'      = 'LICENSE.txt'
    'NOTICE.txt'       = 'NOTICE.txt'
}

foreach ($entry in $files.GetEnumerator()) {
    $url = "https://raw.githubusercontent.com/apache/avro/$Commit/$($entry.Value)"
    Invoke-WebRequest $url -OutFile (Join-Path $PSScriptRoot $entry.Key)
    Write-Host "Downloaded $($entry.Value)"
}
