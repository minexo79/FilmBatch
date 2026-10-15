#!/bin/bash
# build/build-dawn.sh — 以 macOS 部署目標 14.0 重建 WebGPUSharp 0.5.7 使用的 Dawn。
#
# 背景：NuGet WebGPUSharp 0.5.7 內附的 webgpu_dawn.dylib（osx-arm64 與 osx-x64）
# 與 https://github.com/EmilSV/webgpu-dawn-build 的 dawn-m152-4178cb7 版位元組一致
# （sha256 皆已量測確認，見 artifacts/dawn/PROVENANCE.txt），其 Dawn commit 為
# 4178cb7e771b700743bc489037390777b98be39d，建置時未設 MACOSX_DEPLOYMENT_TARGET，
# 因此 minos 鎖在建置機的 macOS 26.0。
#
# 本腳本以相同 commit + 與上游 build_dawn.ps1 macOS 分支相同的 CMake 選項重建，
# 只把 CMAKE_OSX_DEPLOYMENT_TARGET 設為 ${MINOS}（預設 14.0，可用 DAWN_MINOS 覆寫，
# 但不得低於 14.0：Magick.Native 內附版 minos 為 arm64 14.0／x64 15.0，
# 整包 LSMinimumSystemVersion 取所有 Mach-O 的最高者，把 Dawn 降得更低無法降低
# 整包下限，只會製造未經測試的支援宣稱）。產物是同一組 WebGPU C ABI：
# 建置後會以 nm 比對 NuGet 版與重建版的 _wgpu* 匯出符號，新檔必須是 superset 才接受。
# Mach-O 載入命令一律由編譯器產生，不做任何二進位 minos 修補。
#
# 用法：  build/build-dawn.sh [arm64|x86_64|all]   （無參數 = 兩者都建）
# 產物：  artifacts/dawn/osx-arm64/webgpu_dawn.dylib
#         artifacts/dawn/osx-x64/webgpu_dawn.dylib
#         artifacts/dawn/PROVENANCE.txt（由磁碟實際產物量測產生，非事先宣稱）
# 環境：  DAWN_MINOS（預設 14.0；低於 14.0 直接拒絕）
#         DAWN_WORKDIR（預設 /tmp/filmbatch-dawn-build；原始碼與建置暫存，產物才進 artifacts/）
#         DAWN_JOBS（預設 hw.ncpu）、DAWN_CLEAN=1（清掉舊建置目錄重來）
#         DAWN_MIN_DISK_KB（預設 8388608，即 8 GB）
# 需求：  macOS + Xcode CLT（含 Metal SDK）、git、cmake（≥3.22）、python3、
#         系統內建 nm / vtool / lipo / shasum；先 dotnet restore
#         （取 NuGet 內附 Dawn 作 ABI 比對基準）；建置期需連線
#         dawn.googlesource.com（原始碼與第三方依賴）；WORKDIR 所在磁碟至少
#         8 GB 可用；x86_64 跨架構建置需 Rosetta。
set -euo pipefail

DAWN_COMMIT="4178cb7e771b700743bc489037390777b98be39d"   # dawn-m152-4178cb7，WebGPUSharp 0.5.7 同源
WEBGPUSHARP_VERSION="0.5.7"   # 須與 Directory.Packages.props 的 WebGPUSharp 版本一致
WORKDIR="${DAWN_WORKDIR:-/tmp/filmbatch-dawn-build}"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT_DIR="$REPO_ROOT/artifacts/dawn"

fail() { echo "build-dawn.sh：$1" >&2; exit 1; }

usage() {
    echo "用法：build/build-dawn.sh [arm64|x86_64|all]   （無參數 = 兩者都建）"
    echo "環境：DAWN_MINOS（預設 14.0，不可低於 14.0）、DAWN_WORKDIR、DAWN_JOBS、DAWN_CLEAN=1"
}

# 引數：無參數即兩個架構都建；all 同義；其餘逐一辨認，非法即錯。
WANT_ARM64=0
WANT_X64=0
if [[ $# -eq 0 ]]; then
    WANT_ARM64=1
    WANT_X64=1
else
    for a in "$@"; do
        case "$a" in
            -h|--help) usage; exit 0 ;;
            all) WANT_ARM64=1; WANT_X64=1 ;;
            arm64|aarch64) WANT_ARM64=1 ;;
            x86_64|x64) WANT_X64=1 ;;
            *) fail "不支援的架構：$a（用法：build/build-dawn.sh [arm64|x86_64|all]）" ;;
        esac
    done
fi

# 版本正規化與比較（只比數字點分段，足夠用於 macOS minos）。
norm_ver() {
    local v="${1#v}"
    if [[ "$v" != *.* ]]; then v="$v.0"; fi
    printf '%s' "$v"
}
ver_lt() {
    local i a b
    for i in 1 2 3; do
        a="$(printf '%s' "$1" | cut -d. -f"$i")"
        b="$(printf '%s' "$2" | cut -d. -f"$i")"
        a="$((10#${a:-0}))"; b="$((10#${b:-0}))"
        if [[ "$a" -lt "$b" ]]; then return 0; fi
        if [[ "$a" -gt "$b" ]]; then return 1; fi
    done
    return 1
}

MINOS="$(norm_ver "${DAWN_MINOS:-14.0}")"
[[ "$MINOS" =~ ^[0-9]+\.[0-9]+(\.[0-9]+)?$ ]] || fail "DAWN_MINOS 格式錯誤：$MINOS（例：14.0）"
if ver_lt "$MINOS" "14.0"; then
    fail "拒絕 DAWN_MINOS=$MINOS：Magick.Native 內附版 minos 為 arm64 14.0／x64 15.0，整包下限取所有 Mach-O 的最高者；14.0 以下無測試機器，不做未經驗證的支援宣稱"
fi

for tool in git cmake python3 nm vtool lipo shasum sysctl df uname; do
    command -v "$tool" >/dev/null 2>&1 || fail "缺少必要工具：$tool"
done
command -v xcrun >/dev/null 2>&1 || fail "缺少 xcrun（請安裝 Xcode Command Line Tools）"
SDK_PATH="$(xcrun --show-sdk-path 2>/dev/null)" || fail "xcrun 找不到 macOS SDK（請安裝 Xcode Command Line Tools）"
[[ -d "$SDK_PATH/System/Library/Frameworks/Metal.framework" ]] || fail "macOS SDK 缺少 Metal.framework：$SDK_PATH"

mkdir -p "$WORKDIR"
AVAIL_KB="$(df -k "$WORKDIR" | tail -1 | awk '{print $4}')"
MIN_DISK_KB="${DAWN_MIN_DISK_KB:-8388608}"
if [[ "$AVAIL_KB" -lt "$MIN_DISK_KB" ]]; then
    fail "WORKDIR 磁碟可用空間不足（${AVAIL_KB}KB，需 ${MIN_DISK_KB}KB；可用 DAWN_WORKDIR 改路徑或 DAWN_MIN_DISK_KB 放寬）"
fi

NUGET_DAWN="$HOME/.nuget/packages/webgpusharp/$WEBGPUSHARP_VERSION/runtimes"
BUNDLED_ARM64="$NUGET_DAWN/osx-arm64/native/webgpu_dawn.dylib"
BUNDLED_X64="$NUGET_DAWN/osx-x64/native/webgpu_dawn.dylib"
[[ -f "$BUNDLED_ARM64" && -f "$BUNDLED_X64" ]] || fail "找不到 NuGet 內附的 Dawn（請先 dotnet restore FilmBatch.sln）：$BUNDLED_ARM64"

echo "== 取得 Dawn 原始碼（commit ${DAWN_COMMIT}，macOS deployment target ${MINOS}）"
if [[ -e "$WORKDIR/dawn" && ! -d "$WORKDIR/dawn/.git" ]]; then
    fail "$WORKDIR/dawn 已存在但不是 git checkout；請清空或改設 DAWN_WORKDIR"
fi
if [[ ! -d "$WORKDIR/dawn/.git" ]]; then
    git clone --no-checkout --filter=blob:none https://dawn.googlesource.com/dawn "$WORKDIR/dawn"
fi
git -C "$WORKDIR/dawn" fetch --filter=blob:none origin "$DAWN_COMMIT" >/dev/null 2>&1 || true
git -C "$WORKDIR/dawn" cat-file -e "$DAWN_COMMIT^{commit}" || fail "上游找不到 pin 住的 commit $DAWN_COMMIT"
git -C "$WORKDIR/dawn" checkout --detach "$DAWN_COMMIT"
HEAD_NOW="$(git -C "$WORKDIR/dawn" rev-parse HEAD)"
[[ "$HEAD_NOW" == "$DAWN_COMMIT" ]] || fail "checkout 後 HEAD=$HEAD_NOW，與 pin $DAWN_COMMIT 不符"

JOBS="${DAWN_JOBS:-$(sysctl -n hw.ncpu 2>/dev/null || echo 4)}"
export MACOSX_DEPLOYMENT_TARGET="$MINOS"
HOST_ARCH="$(uname -m)"

build_one() {
    local arch="$1" cmake_arch rid bundled build_dir cache
    case "$arch" in
        arm64)  cmake_arch="arm64";  rid="osx-arm64"; bundled="$BUNDLED_ARM64" ;;
        x86_64) cmake_arch="x86_64"; rid="osx-x64";   bundled="$BUNDLED_X64" ;;
    esac
    build_dir="$WORKDIR/build-$rid"

    if [[ "$HOST_ARCH" != "$cmake_arch" ]]; then
        arch -"$cmake_arch" true >/dev/null 2>&1 || fail "${rid} 跨架構建置需 Rosetta（arch -$cmake_arch 失敗）"
    fi

    if [[ "${DAWN_CLEAN:-0}" == "1" && -d "$build_dir" ]]; then
        rm -rf "$build_dir"
    fi
    cache="$build_dir/CMakeCache.txt"
    if [[ -f "$cache" ]]; then
        cached_minos="$(grep '^CMAKE_OSX_DEPLOYMENT_TARGET:' "$cache" | cut -d= -f2 || true)"
        cached_arch="$(grep '^CMAKE_OSX_ARCHITECTURES:' "$cache" | cut -d= -f2 || true)"
        if [[ "$cached_minos" != "$MINOS" || "$cached_arch" != "$cmake_arch" ]]; then
            fail "舊建置目錄設定（$cached_arch／minos $cached_minos）與本次（$cmake_arch／minos $MINOS）不符；刪掉 $build_dir 或設 DAWN_CLEAN=1"
        fi
    fi

    echo "== 建置 ${rid}（CMAKE_OSX_DEPLOYMENT_TARGET=${MINOS}，-j${JOBS}）"
    cmake -S "$WORKDIR/dawn" -B "$build_dir" \
        -D CMAKE_OSX_ARCHITECTURES="$cmake_arch" \
        -D CMAKE_OSX_DEPLOYMENT_TARGET="$MINOS" \
        -D DAWN_FETCH_DEPENDENCIES=ON \
        -D CMAKE_BUILD_TYPE=Release \
        -D BUILD_SHARED_LIBS=OFF \
        -D BUILD_SAMPLES=OFF \
        -D DAWN_BUILD_TESTS=OFF \
        -D DAWN_ENABLE_NULL=OFF \
        -D DAWN_ENABLE_OPENGLES=OFF \
        -D DAWN_ENABLE_METAL=ON \
        -D DAWN_ENABLE_VULKAN=OFF \
        -D DAWN_USE_GLFW=OFF \
        -D DAWN_BUILD_SAMPLES=OFF \
        -D TINT_BUILD_TESTS=OFF \
        -D DAWN_BUILD_MONOLITHIC_LIBRARY=SHARED \
        -D DAWN_FORCE_SYSTEM_COMPONENT_LOAD=ON \
        -D FORCE_SYSTEM_COMPONENT_LOAD=ON
    cmake --build "$build_dir" --config Release --target webgpu_dawn --parallel "$JOBS"

    local built="$build_dir/src/dawn/native/libwebgpu_dawn.dylib"
    [[ -f "$built" ]] || fail "找不到建置產物：$built"

    echo "== 驗證 ${rid}：架構、minos 與 ABI"
    local built_archs
    built_archs="$(lipo -archs "$built")"
    [[ "$built_archs" == "$cmake_arch" ]] || fail "產物架構為 $built_archs，預期 $cmake_arch"
    vtool -show-build "$built"
    local built_minos
    built_minos="$(vtool -show-build "$built" | awk '/minos/{print $2; exit}')"
    [[ "$(norm_ver "$built_minos")" == "$MINOS" ]] || fail "建置產物 minos=${built_minos}，預期 $MINOS"
    # ABI：重建版的 _wgpu* 匯出必須包含 NuGet 內附版的每一個符號（同一 webgpu.h ABI）。
    # （diff|grep 管線以 || true 收尾，避免 pipefail/set -e 在「零缺失」的成功路徑誤殺腳本。）
    local missing
    missing="$(diff <(nm -gU "$bundled" | awk '{print $NF}' | grep '^_wgpu' | sort) \
                   <(nm -gU "$built"   | awk '{print $NF}' | grep '^_wgpu' | sort) \
               | grep '^< ' || true)"
    if [[ -n "$missing" ]]; then
        echo "$missing" >&2
        fail "ABI 驗證失敗：重建版缺少內附版的匯出符號（↑）"
    fi

    mkdir -p "$OUT_DIR/$rid"
    cp "$built" "$OUT_DIR/$rid/webgpu_dawn.dylib"
    echo "== 產物：$OUT_DIR/$rid/webgpu_dawn.dylib（minos ${built_minos}，sha256 $(shasum -a 256 "$OUT_DIR/$rid/webgpu_dawn.dylib" | cut -d' ' -f1)）"
}

# PROVENANCE 只描述磁碟上實際存在的產物；缺席的架構如實註記，不做成功宣稱。
write_provenance() {
    {
        echo "FilmBatch macOS Dawn 重建產物"
        echo "================================"
        echo "來源        : https://dawn.googlesource.com/dawn @ $DAWN_COMMIT"
        echo "上游 tag    : EmilSV/webgpu-dawn-build dawn-m152-4178cb7"
        echo "同源證據    : WebGPUSharp $WEBGPUSHARP_VERSION NuGet 內附 dylib 與上游 release 資產 sha256 一致（建置時量測）："
        echo "                osx-arm64 內附 $(shasum -a 256 "$BUNDLED_ARM64" | cut -d' ' -f1)"
        echo "                osx-x64   內附 $(shasum -a 256 "$BUNDLED_X64" | cut -d' ' -f1)"
        echo "建置參數    : 與上游 build_dawn.ps1 macOS 分支相同，外加 CMAKE_OSX_DEPLOYMENT_TARGET=$MINOS"
        echo "建置機器    : $(sw_vers -productName 2>/dev/null || printf 'macOS') $(sw_vers -productVersion 2>/dev/null || printf 'unknown') ($(uname -m))，SDK $(xcrun --show-sdk-version 2>/dev/null || printf 'unknown')"
        local f
        for rid in osx-arm64 osx-x64; do
            f="$OUT_DIR/$rid/webgpu_dawn.dylib"
            if [[ -f "$f" ]]; then
                echo "$rid  : minos $(vtool -show-build "$f" | awk '/minos/{print $2; exit}')"
                echo "                sha256 $(shasum -a 256 "$f" | cut -d' ' -f1)"
                echo "                _wgpu* 匯出 $(nm -gU "$f" | awk '{print $NF}' | grep -c '^_wgpu') 個（為內附版的 superset，建置時檢查）"
            else
                echo "$rid  : （本次未建置，磁碟無產物）"
            fi
        done
        echo "ABI 驗證    : nm -gU _wgpu* 匯出符號為 NuGet 版的 superset（建置時檢查，未通過即失敗）"
        echo "minos 驗證  : vtool -show-build 顯示 ${MINOS}（建置時檢查，未通過即失敗）"
        echo "注意        : 14.0 為編譯期部署目標；僅在建置機 macOS 上驗證載入，14.x 實機行為未驗證（無舊系統測試機）"
        echo "產生時間    : $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    } > "$OUT_DIR/PROVENANCE.txt"
}

if [[ "$WANT_ARM64" == "1" ]]; then build_one arm64; fi
if [[ "$WANT_X64" == "1" ]]; then build_one x86_64; fi
write_provenance
echo "== 完成。build/Build.cs 會自動採用 artifacts/dawn/<rid>/webgpu_dawn.dylib"
