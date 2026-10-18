#!/usr/bin/env bash
# 下載 RAW 測試語料（raw.pixls.us 等 CC0 樣本），逐檔核對 SHA-256。
# 用法：tools/raw-oracle/fetch-samples.sh DEST
# 清單即測試釘選檔 tests/FilmBatch.Raw.Tests/Corpus/*.tsv：第 1 欄檔名、第 2 欄 SHA-256、最後一欄 URL。
set -euo pipefail
dest=${1:?usage: fetch-samples.sh DEST}
corpus="$(cd "$(dirname "$0")/../../tests/FilmBatch.Raw.Tests/Corpus" && pwd)"
mkdir -p "$dest"
ok=0
fail=0
while IFS=$'\t' read -r name sha url; do
  out="$dest/$name"
  if [ -f "$out" ] && [ "$(shasum -a 256 "$out" | cut -d' ' -f1)" = "$sha" ]; then
    ok=$((ok + 1))
    continue
  fi
  if curl -fsSL --retry 3 --retry-delay 2 -o "$out.part" "$url" \
    && [ "$(shasum -a 256 "$out.part" | cut -d' ' -f1)" = "$sha" ]; then
    mv "$out.part" "$out"
    ok=$((ok + 1))
    echo "OK   $name"
  else
    rm -f "$out.part"
    fail=$((fail + 1))
    echo "FAIL $name $url" >&2
  fi
done < <(awk -F'\t' '!/^#/ && NF >= 3 { print $1 "\t" $2 "\t" $NF }' "$corpus"/sony.tsv "$corpus"/vendors.tsv "$corpus"/refusals.tsv)
echo "done: ok=$ok fail=$fail"
[ "$fail" -eq 0 ]
