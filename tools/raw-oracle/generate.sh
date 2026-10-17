#!/usr/bin/env bash
# 以釘選的 LightCraft 產生 RAW 移植的參考輸出（FILMBATCH_RAW_ORACLE）。
# 用法：tools/raw-oracle/generate.sh LIGHTCRAFT_CHECKOUT SAMPLES_DIR ORACLE_DIR
# LIGHTCRAFT_CHECKOUT 必須是 https://github.com/storytold/lightcraft 的 commit f34c7a8a89e0d1bade0a6889185f40acecc84a9f。
set -euo pipefail
lightcraft=${1:?usage: generate.sh LIGHTCRAFT_CHECKOUT SAMPLES_DIR ORACLE_DIR}
samples=${2:?usage: generate.sh LIGHTCRAFT_CHECKOUT SAMPLES_DIR ORACLE_DIR}
oracle=${3:?usage: generate.sh LIGHTCRAFT_CHECKOUT SAMPLES_DIR ORACLE_DIR}
here="$(cd "$(dirname "$0")" && pwd)"
pinned=f34c7a8a89e0d1bade0a6889185f40acecc84a9f
[ "$(git -C "$lightcraft" rev-parse HEAD)" = "$pinned" ] || { echo "LightCraft checkout is not at $pinned" >&2; exit 1; }

# 兩個 dump 範例放進上游 crate（stagedump 需要 lightcraft-raw 的 serde_json 開發相依；f34c7a8a 的 Cargo.toml 以 [dev-dependencies] 結尾）
cp "$here/stagedump.rs" "$lightcraft/crates/raw/examples/stagedump.rs"
cp "$here/loaddump.rs" "$lightcraft/crates/engine/examples/loaddump.rs"
grep -q '^serde_json' "$lightcraft/crates/raw/Cargo.toml" || printf '%s\n' '# FilmBatch port oracle dumps' 'serde_json = { workspace = true }' >> "$lightcraft/crates/raw/Cargo.toml"
(cd "$lightcraft" && cargo build --release -p lightcraft-raw --example stagedump && cargo build --release -p lightcraft-engine --example loaddump)
bin="$lightcraft/target/release/examples"

corpus="$here/../../tests/FilmBatch.Raw.Tests/Corpus"
mkdir -p "$oracle/stages" "$oracle/load"
awk -F'\t' '!/^#/ && NF >= 3 { print $1 }' "$corpus"/sony.tsv "$corpus"/vendors.tsv "$corpus"/refusals.tsv | while read -r name; do
  stem="${name%.*}"
  # 上游拒解的樣本不產生 stages：測試據此略過（拒解本身由 refusals.tsv 釘選）
  if "$bin/stagedump" "$samples/$name" "$oracle/stages/$stem" >/dev/null 2>"$oracle/stages/$stem.err"; then
    rm -f "$oracle/stages/$stem.err"
    for edge in 200 1600; do
      "$bin/loaddump" "$samples/$name" "$oracle/load/$stem" "$edge" >/dev/null
    done
    echo "OK   $name"
  else
    rm -rf "$oracle/stages/$stem"
    echo "SKIP $name (LightCraft refuses it: $(tr -d '\n' < "$oracle/stages/$stem.err"))"
  fi
done
