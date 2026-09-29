#!/usr/bin/env bash
# Uses the packed packages the way a user does (#136): restores tests/PackageConsumers from artifacts/package/release
# only, into an empty package cache, then builds and runs each consumer, and installs and runs the tool. CI runs it
# after packing; locally, run `dotnet pack AvroSharp.slnx -c Release` first. Needs the .NET 8 and .NET 10 runtimes.
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
packages="$root/artifacts/package/release"
consumers="$root/tests/PackageConsumers"
# The most recently packed version: the folder may also hold older ones.
version=$(ls -t "$packages"/AvroSharp.*.nupkg | sed -n 's#.*/AvroSharp\.\([0-9][^/]*\)\.nupkg#\1#p' | head -1)
if [ -z "$version" ]; then
  echo "No AvroSharp package in $packages: pack first." >&2
  exit 1
fi

echo "Testing the packed packages, version $version"

# An empty cache, so a cached package of the same version cannot stand in for the one just packed.
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
export NUGET_PACKAGES="$work/packages"
properties="-p:AvroSharpVersion=$version"

# A .NET 10 app with the generator and codecs: generated types, a zstandard container file, schema defaults.
dotnet run --project "$consumers/App" -c Release "$properties"

# A netstandard2.0 library on C# 7.3 with the generator: the generated code compiles there.
dotnet build "$consumers/NetStandardLib" -c Release "$properties"

# A .NET 8 app using AvroSharp.CodeGen directly, on the .NET 8 runtime.
dotnet run --project "$consumers/CodeGenApp" -c Release "$properties"

# The tool, installed from the packed package. The SDK picks its net10.0 build here; its net8.0 build is run on
# .NET 8 as well (LatestPatch keeps it on 8.0.x), since RollForward=Major lets it run wherever .NET 8 or later is.
dotnet tool install AvroSharp.Tool --version "$version" --tool-path "$work/tool" --configfile "$consumers/nuget.config"
schema="$consumers/App/Schemas/order.avsc"
"$work/tool/avrosharp" --version
"$work/tool/avrosharp" schema fingerprint "$schema"
"$work/tool/avrosharp" gen "$schema" -o "$work/generated"
test -f "$work/generated/consumer/Order.g.cs"

net8=$(find "$work/tool/.store" -path '*/tools/net8.0/any/AvroSharp.Tool.dll' | head -1)
if [ -z "$net8" ]; then
  echo "The tool package has no net8.0 build." >&2
  exit 1
fi

DOTNET_ROLL_FORWARD=LatestPatch dotnet "$net8" schema canonical "$schema"
DOTNET_ROLL_FORWARD=LatestPatch dotnet "$net8" gen "$schema" -o "$work/generated-net8"
test -f "$work/generated-net8/consumer/Order.g.cs"

echo "The packed packages work."
