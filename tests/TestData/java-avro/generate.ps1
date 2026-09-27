<#
.SYNOPSIS
Writes the reference container files in this folder with Apache Avro Java's avro-tools, one per codec.

.DESCRIPTION
weather-<codec>.avro recompresses Apache's weather.avro (five records, one block). many-<codec>.avro holds 8000
generated records of the same schema, so it has several blocks. Both sets are written once per codec that Java
supports, so AvroSharp's codecs are checked against Java's output, not only against themselves.

.PARAMETER Java
The java executable (Java 11 or later).

.PARAMETER AvroTools
avro-tools-<version>.jar, from https://repo1.maven.org/maven2/org/apache/avro/avro-tools/
#>
param(
    [Parameter(Mandatory)] [string] $Java,
    [Parameter(Mandatory)] [string] $AvroTools
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$weather = Join-Path $here '..\apache-avro\weather.avro'
$codecs = 'deflate', 'snappy', 'bzip2', 'xz', 'zstandard'

# Deterministic records with some variety, so that the data compresses but not to nothing.
$schema = Join-Path $here 'many.avsc'
$json = Join-Path ([System.IO.Path]::GetTempPath()) 'avrosharp-many.json'
$stations = '011990-99999', '012650-99999', '010010-99999', '727930-24233', '725300-94846'
$state = [uint32]12345
$lines = foreach ($i in 0..7999) {
    $state = [uint32](([uint64]$state * 1103515245 + 12345) % 4294967296)
    $temp = [int](($state -shr 16) % 700) - 300
    '{{"station":"{0}","time":{1},"temp":{2}}}' -f $stations[$i % $stations.Length], (-619524000000 + [long]$i * 3600000), $temp
}
[System.IO.File]::WriteAllLines($json, [string[]]$lines)

# avro-tools opens file arguments through Hadoop's file system, which needs winutils.exe on Windows, so the data
# goes through stdin and stdout instead ('-' means stdin for fromjson). Start-Process redirects them byte for byte.
function Invoke-AvroTools([string[]] $Arguments, [string] $In, [string] $Out) {
    $process = Start-Process -FilePath $Java -ArgumentList (@('-jar', $AvroTools) + $Arguments) -NoNewWindow -Wait -PassThru `
        -RedirectStandardInput $In -RedirectStandardOutput $Out
    if ($process.ExitCode -ne 0) { throw "avro-tools $($Arguments -join ' ') failed with exit code $($process.ExitCode)" }
}

# avro-tools' default --level (-1) is rejected by xz, so each level is given: CodecFactory's defaults.
$levels = @{ deflate = @('--level', '6'); xz = @('--level', '6'); zstandard = @('--level', '3'); snappy = @(); bzip2 = @() }

foreach ($codec in $codecs) {
    $level = $levels[$codec]
    Invoke-AvroTools (@('recodec', '--codec', $codec) + $level) $weather (Join-Path $here "weather-$codec.avro")
    Invoke-AvroTools (@('fromjson', '--codec', $codec) + $level + @('--schema', ((Get-Content $schema -Raw) -replace '\s+', '' -replace '"', '\"'), '-')) $json (Join-Path $here "many-$codec.avro")
}

Remove-Item $json
& $Java -jar $AvroTools 2>&1 | Select-Object -First 1
