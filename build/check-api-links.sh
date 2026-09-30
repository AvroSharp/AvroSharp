#!/usr/bin/env bash
# Checks that every absolute link to the API reference in the repository's Markdown names a page the site has.
# Pages that GitHub and nuget.org show too (the READMEs and guides) link the API with absolute URLs, which DocFX doesn't
# check. Run after DocFX has built the site: build/check-api-links.sh [site folder, default artifacts/docs]
set -euo pipefail

site=${1:-artifacts/docs}
prefix='https://avrosharp.github.io/AvroSharp/docs/api/'
if [[ ! -d "$site/docs/api" ]]; then
  echo "::error::$site/docs/api does not exist; build the site first"
  exit 1
fi

status=0
checked=0
while IFS=: read -r file line url; do
  checked=$((checked + 1))
  page=${url#"$prefix"}
  page=${page%%#*}
  if [[ -n "$page" && ! -f "$site/docs/api/$page" ]]; then
    echo "::error file=$file,line=$line::No API page $page"
    status=1
  fi
done < <(git ls-files '*.md' | xargs grep -noE "${prefix//./\\.}[A-Za-z0-9._#-]*" || true)

echo "Checked $checked API links"
exit $status
