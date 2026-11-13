#!/usr/bin/env bash
# ============================================================================
# BuildIosLibgit2.sh —— 构建 iOS 内嵌 git 的静态库（libgit2 + mgc_shim）
#
# 背景：iOS 沙盒禁止 fork/exec 子进程，Android 那套「打包 git 可执行文件」在 iOS
# 不可行。本项目把 libgit2（https://libgit2.org）编成静态库链接进 App，
# C# 侧经 Services/IosLibgit2/ 的 [DllImport("__Internal")] 直连调用。
#
# 产物（提交进仓库，日常构建无需重跑本脚本）：
#   native-git/ios/libgit2-iphoneos-arm64.a       真机（ios-arm64）
#   native-git/ios/libgit2-iossimulator-arm64.a   模拟器（iossimulator-arm64）
# 两个平台的 arm64 切片无法 lipo 合并（平台不同），csproj 按 RuntimeIdentifier 选一。
#
# 依赖：Xcode 命令行工具、CMake（macOS 没有 brew 时可 `python3 -m pip install --user cmake`）
# 用法：bash scripts/BuildIosLibgit2.sh
# ============================================================================
set -euo pipefail

LIBGIT2_VERSION="1.9.1"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT_DIR="$REPO_ROOT/native-git/ios"
WORK_DIR="${TMPDIR:-/tmp}/mgc-libgit2-build"
SRC_DIR="$WORK_DIR/libgit2-$LIBGIT2_VERSION"

CMAKE="$(command -v cmake || true)"
[ -z "$CMAKE" ] && CMAKE="$HOME/Library/Python/*/bin/cmake" && CMAKE=$(ls $CMAKE 2>/dev/null | head -1)
[ -z "$CMAKE" ] && { echo "错误：未找到 cmake（python3 -m pip install --user cmake）"; exit 1; }

echo "==> 使用 CMake: $CMAKE"

# ---- 1. 取源码（优先走镜像加速；github 直连很慢时可用） ----
mkdir -p "$WORK_DIR"
if [ ! -d "$SRC_DIR" ]; then
    TARBALL="$WORK_DIR/libgit2-$LIBGIT2_VERSION.tar.gz"
    echo "==> 下载 libgit2 $LIBGIT2_VERSION 源码..."
    curl -fsSL --http1.1 -o "$TARBALL" \
        "https://gh-proxy.com/https://github.com/libgit2/libgit2/archive/refs/tags/v$LIBGIT2_VERSION.tar.gz" \
    || curl -fsSL --http1.1 -o "$TARBALL" \
        "https://codeload.github.com/libgit2/libgit2/tar.gz/refs/tags/v$LIBGIT2_VERSION"
    tar xzf "$TARBALL" -C "$WORK_DIR"
fi

# ---- 2. 编译配置（平台差异：CMAKE_OSX_SYSROOT） ----
# 关键开关：
#   BUILD_SHARED_LIBS=OFF       静态库（App Store 不允许装载动态库，只有系统框架可动态）
#   USE_SSH=OFF                 不带 libssh2：iOS 上 ssh 凭据交互也无法做，仅支持 https
#   USE_HTTPS=SecureTransport   TLS 走系统 Security.framework（沿用系统信任链，无需捆绑 CA）
#   USE_BUNDLED_ZLIB=OFF        压缩用系统 libz（链接期 -lz，见 csproj 的 LinkerFlags）
#   REGEX_BACKEND=builtin       iOS 无 PCRE，用内置实现
#   BUILD_CLI/TESTS=OFF         不需要 git 可执行文件与测试
COMMON_FLAGS="-DBUILD_SHARED_LIBS=OFF -DBUILD_TESTS=OFF -DBUILD_CLI=OFF -DBUILD_EXAMPLES=OFF \
  -DUSE_SSH=OFF -DUSE_HTTPS=SecureTransport -DUSE_BUNDLED_ZLIB=OFF -DREGEX_BACKEND=builtin \
  -DUSE_ICONV=OFF -DUSE_GSSAPI=OFF -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0 -DCMAKE_BUILD_TYPE=Release"

build_platform() {
    local name="$1" sysroot="$2" arch="$3" out="$4"
    echo "==> 构建 $name（$arch / $sysroot）..."
    "$CMAKE" -S "$SRC_DIR" -B "$WORK_DIR/b-$name" \
        -DCMAKE_SYSTEM_NAME=iOS -DCMAKE_OSX_SYSROOT="$sysroot" \
        -DCMAKE_OSX_ARCHITECTURES="$arch" $COMMON_FLAGS > /dev/null
    "$CMAKE" --build "$WORK_DIR/b-$name" -j "$(sysctl -n hw.ncpu)" > /dev/null

    echo "==> 编译 mgc_shim.c（$name）..."
    xcrun -sdk "$sysroot" clang -c "$REPO_ROOT/native-git/ios/mgc_shim.c" \
        -I"$SRC_DIR/include" -I"$SRC_DIR/src" -I"$SRC_DIR/src/util" \
        -o "$WORK_DIR/mgc_shim_$name.o"

    echo "==> 合并静态库 -> $out"
    libtool -static -o "$out" "$WORK_DIR/b-$name/libgit2.a" "$WORK_DIR/mgc_shim_$name.o" 2>/dev/null
    lipo -info "$out"
}

mkdir -p "$OUT_DIR"
build_platform "iphoneos"   "iphoneos"        "arm64" "$OUT_DIR/libgit2-iphoneos-arm64.a"
build_platform "iossimulator" "iphonesimulator" "arm64" "$OUT_DIR/libgit2-iossimulator-arm64.a"

echo "==> 完成。产物："
ls -lh "$OUT_DIR"
echo "提醒：升级 libgit2 版本后需重新编译，并检查 Services/IosLibgit2/ 与 shim 的 API 兼容性。"
