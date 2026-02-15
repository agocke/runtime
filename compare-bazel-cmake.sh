#!/usr/bin/env bash
# compare-bazel-cmake.sh — Compare Bazel-built and CMake-built native binaries
#
# Verifies that the Bazel build produces equivalent binaries to CMake by comparing:
#   - Exported dynamic symbols (nm -D)
#   - Required shared library dependencies (NEEDED)
#   - SONAME
#   - ELF section layout and sizes
#   - Overall file size (stripped)
#
# Usage:
#   ./compare-bazel-cmake.sh                          # Auto-detect paths (debug)
#   ./compare-bazel-cmake.sh --config release         # Use release configuration
#   ./compare-bazel-cmake.sh --bazel-dir <path> --cmake-coreclr-dir <path> --cmake-nativelibs-dir <path> --cmake-corehost-dir <path>
#   ./compare-bazel-cmake.sh --build                  # Build both before comparing
#   ./compare-bazel-cmake.sh --section-tolerance 10   # Allow 10% section size variance

set -euo pipefail

scriptroot="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ----- Defaults -----
config="debug"
build_first=false
section_tolerance=5  # percent
bazel_dir=""
cmake_coreclr_dir=""
cmake_nativelibs_dir=""
cmake_corehost_dir=""
verbose=false

# ----- Parse arguments -----
while [[ $# -gt 0 ]]; do
    case "$1" in
        --config)
            config="${2,,}"
            shift 2
            ;;
        --build)
            build_first=true
            shift
            ;;
        --section-tolerance)
            section_tolerance="$2"
            shift 2
            ;;
        --bazel-dir)
            bazel_dir="$2"
            shift 2
            ;;
        --cmake-coreclr-dir)
            cmake_coreclr_dir="$2"
            shift 2
            ;;
        --cmake-nativelibs-dir)
            cmake_nativelibs_dir="$2"
            shift 2
            ;;
        --cmake-corehost-dir)
            cmake_corehost_dir="$2"
            shift 2
            ;;
        --verbose|-v)
            verbose=true
            shift
            ;;
        -h|--help)
            head -15 "${BASH_SOURCE[0]}" | tail -14
            exit 0
            ;;
        *)
            echo "Unknown argument: $1"
            exit 1
            ;;
    esac
done

# ----- Derived variables -----
# Read product version from eng/Versions.props
major_version=$(grep '<MajorVersion>' "$scriptroot/eng/Versions.props" | sed 's/.*<MajorVersion>\(.*\)<\/MajorVersion>.*/\1/')
minor_version=$(grep '<MinorVersion>' "$scriptroot/eng/Versions.props" | sed 's/.*<MinorVersion>\(.*\)<\/MinorVersion>.*/\1/')
patch_version=$(grep '<PatchVersion>' "$scriptroot/eng/Versions.props" | sed 's/.*<PatchVersion>\(.*\)<\/PatchVersion>.*/\1/')
product_version="${major_version}.${minor_version}.${patch_version}"

if [[ "$config" == "debug" ]]; then
    build_type="Debug"
    bazel_config_args=(-c dbg --config=debug)
else
    build_type="Release"
    bazel_config_args=(-c opt)
fi

# CMake output paths (from build-runtime.sh, build-native.sh, corehost/build.sh)
cmake_coreclr_dir="${cmake_coreclr_dir:-$scriptroot/artifacts/bin/coreclr/linux.x64.$build_type}"
cmake_nativelibs_dir="${cmake_nativelibs_dir:-$scriptroot/artifacts/bin/native/net${major_version}.${minor_version}-linux-$build_type-x64}"
cmake_corehost_dir="${cmake_corehost_dir:-$scriptroot/artifacts/bin/linux-x64.$build_type/corehost}"

# Bazel output path
bazel_dir="${bazel_dir:-$scriptroot/bazel-bin/runtime_native}"

# Auto-detect Bazel framework version from layout (may differ from Versions.props)
bazel_version="$product_version"
if [[ -d "$bazel_dir/shared/Microsoft.NETCore.App" ]]; then
    detected_version=$(ls "$bazel_dir/shared/Microsoft.NETCore.App/" 2>/dev/null | head -1)
    if [[ -n "$detected_version" ]]; then
        bazel_version="$detected_version"
    fi
fi
bazel_framework_dir="$bazel_dir/shared/Microsoft.NETCore.App/$bazel_version"
bazel_fxr_dir="$bazel_dir/host/fxr/$bazel_version"

# ----- Color helpers -----
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
CYAN='\033[1;36m'
NC='\033[0m' # No Color

log()      { echo -e "${CYAN}==>${NC} $*"; }
log_pass() { echo -e "  ${GREEN}✓${NC} $*"; }
log_fail() { echo -e "  ${RED}✗${NC} $*"; }
log_warn() { echo -e "  ${YELLOW}!${NC} $*"; }
log_info() { if [[ "$verbose" == "true" ]]; then echo -e "    $*"; fi; }

# ----- Counters -----
total_checks=0
passed_checks=0
failed_checks=0
skipped_binaries=0

check_pass() {
    total_checks=$((total_checks + 1))
    passed_checks=$((passed_checks + 1))
    log_pass "$1"
}

check_fail() {
    total_checks=$((total_checks + 1))
    failed_checks=$((failed_checks + 1))
    log_fail "$1"
}

# ----- Build both if requested -----
build_both() {
    if [[ "$build_first" != "true" ]]; then
        return
    fi

    log "Building with CMake (./build.sh clr+libs+host -rc $config)..."
    "$scriptroot/build.sh" clr+libs+host -rc "$config"

    log "Building with Bazel..."
    bazel --nohome_rc build "${bazel_config_args[@]}" //:runtime_native
}

# ----- Comparison functions -----

# Compare exported dynamic symbols (the public API surface)
# Strips symbol version tags (@@V1.0) for comparison since CMake uses version scripts
compare_symbols() {
    local cmake_bin="$1"
    local bazel_bin="$2"
    local name="$3"
    local tmpdir
    tmpdir=$(mktemp -d)

    # Strip version tags (@@...) for fair comparison
    nm -D --defined-only "$cmake_bin" 2>/dev/null | awk '{print $NF}' | sed 's/@@.*//' | sort -u > "$tmpdir/cmake_symbols"
    nm -D --defined-only "$bazel_bin" 2>/dev/null | awk '{print $NF}' | sed 's/@@.*//' | sort -u > "$tmpdir/bazel_symbols"

    local cmake_count bazel_count
    cmake_count=$(wc -l < "$tmpdir/cmake_symbols")
    bazel_count=$(wc -l < "$tmpdir/bazel_symbols")

    local diff_output
    diff_output=$(diff "$tmpdir/cmake_symbols" "$tmpdir/bazel_symbols" || true)

    if [[ -z "$diff_output" ]]; then
        check_pass "Exported symbols match ($cmake_count symbols)"
    else
        local only_cmake only_bazel
        only_cmake=$(diff "$tmpdir/cmake_symbols" "$tmpdir/bazel_symbols" | grep '^< ' | sed 's/^< //' || true)
        only_bazel=$(diff "$tmpdir/cmake_symbols" "$tmpdir/bazel_symbols" | grep '^> ' | sed 's/^> //' || true)

        check_fail "Exported symbols differ (CMake: $cmake_count, Bazel: $bazel_count)"
        if [[ -n "$only_cmake" ]]; then
            log_info "Only in CMake: $(echo "$only_cmake" | tr '\n' ' ')"
        fi
        if [[ -n "$only_bazel" ]]; then
            log_info "Only in Bazel: $(echo "$only_bazel" | tr '\n' ' ')"
        fi
    fi

    # Check symbol versioning as a separate informational note
    local cmake_versioned bazel_versioned
    cmake_versioned=$(nm -D --defined-only "$cmake_bin" 2>/dev/null | grep -c '@@' || true)
    bazel_versioned=$(nm -D --defined-only "$bazel_bin" 2>/dev/null | grep -c '@@' || true)
    if [[ "$cmake_versioned" -gt 0 && "$bazel_versioned" -eq 0 ]]; then
        log_warn "Symbol versioning: CMake has $cmake_versioned versioned symbols, Bazel has none (version script missing)"
    fi

    rm -rf "$tmpdir"
}

# Compare NEEDED shared library dependencies
compare_needed() {
    local cmake_bin="$1"
    local bazel_bin="$2"
    local name="$3"

    local cmake_needed bazel_needed
    cmake_needed=$(readelf -d "$cmake_bin" 2>/dev/null | grep NEEDED | awk '{print $NF}' | tr -d '[]' | sort)
    bazel_needed=$(readelf -d "$bazel_bin" 2>/dev/null | grep NEEDED | awk '{print $NF}' | tr -d '[]' | sort)

    if [[ "$cmake_needed" == "$bazel_needed" ]]; then
        local count
        count=$(echo "$cmake_needed" | wc -w)
        check_pass "Dynamic dependencies match ($count libs)"
    else
        check_fail "Dynamic dependencies differ"
        log_info "CMake NEEDED: $(echo $cmake_needed)"
        log_info "Bazel NEEDED: $(echo $bazel_needed)"
    fi
}

# Compare SONAME
compare_soname() {
    local cmake_bin="$1"
    local bazel_bin="$2"
    local name="$3"

    local cmake_soname bazel_soname
    cmake_soname=$(readelf -d "$cmake_bin" 2>/dev/null | grep SONAME | awk '{print $NF}' | tr -d '[]' || echo "(none)")
    bazel_soname=$(readelf -d "$bazel_bin" 2>/dev/null | grep SONAME | awk '{print $NF}' | tr -d '[]' || echo "(none)")

    if [[ "$cmake_soname" == "$bazel_soname" ]]; then
        check_pass "SONAME matches: $cmake_soname"
    else
        check_fail "SONAME differs (CMake: $cmake_soname, Bazel: $bazel_soname)"
    fi
}

# Compare ELF section sizes (with tolerance), ignoring expected strip-related differences
compare_sections() {
    local cmake_bin="$1"
    local bazel_bin="$2"
    local name="$3"
    local tmpdir
    tmpdir=$(mktemp -d)

    # Check stripped status
    local cmake_stripped bazel_stripped
    cmake_stripped="no"
    bazel_stripped="no"
    local cmake_file_info bazel_file_info
    cmake_file_info=$(file "$cmake_bin")
    bazel_file_info=$(file "$bazel_bin")
    if echo "$cmake_file_info" | grep -q "not stripped"; then
        cmake_stripped="no"
    elif echo "$cmake_file_info" | grep -q "stripped"; then
        cmake_stripped="yes"
    fi
    if echo "$bazel_file_info" | grep -q "not stripped"; then
        bazel_stripped="no"
    elif echo "$bazel_file_info" | grep -q "stripped"; then
        bazel_stripped="yes"
    fi

    if [[ "$cmake_stripped" != "$bazel_stripped" ]]; then
        log_warn "Strip status: CMake=$cmake_stripped, Bazel=$bazel_stripped (size/section comparisons account for this)"
    fi

    # Extract section names and sizes, filtering out strip-related sections
    # .gnu_debuglink, .symtab, .strtab, .note.gnu.property differ between stripped/unstripped
    local strip_filter='\.gnu_debuglink|\.symtab|\.strtab|\.note\.gnu\.property|\.note\.gnu\.pr'
    readelf -S "$cmake_bin" 2>/dev/null | grep '\[' | awk '{for(i=1;i<=NF;i++) if($i ~ /^\./) {name=$i; size=$(i+4); print name, size}}' | grep -vE "$strip_filter" | sort > "$tmpdir/cmake_sections" || true
    readelf -S "$bazel_bin" 2>/dev/null | grep '\[' | awk '{for(i=1;i<=NF;i++) if($i ~ /^\./) {name=$i; size=$(i+4); print name, size}}' | grep -vE "$strip_filter" | sort > "$tmpdir/bazel_sections" || true

    # Compare section names first
    local cmake_names bazel_names
    cmake_names=$(awk '{print $1}' "$tmpdir/cmake_sections" | sort)
    bazel_names=$(awk '{print $1}' "$tmpdir/bazel_sections" | sort)

    local section_diff
    section_diff=$(diff <(echo "$cmake_names") <(echo "$bazel_names") || true)

    if [[ -n "$section_diff" ]]; then
        check_fail "ELF sections differ in layout"
        log_info "Section diff: $(echo "$section_diff" | head -5)"
        rm -rf "$tmpdir"
        return
    fi

    # Compare section sizes with tolerance
    local size_mismatches=0
    local total_sections=0
    local details=""

    while IFS=' ' read -r section_name cmake_size; do
        local bazel_size
        bazel_size=$(grep -F "$section_name " "$tmpdir/bazel_sections" | awk '{print $2}' || true)
        if [[ -z "$bazel_size" ]]; then
            continue
        fi

        ((total_sections++)) || true

        # Convert hex to decimal
        local cmake_dec bazel_dec
        cmake_dec=$((16#${cmake_size}))
        bazel_dec=$((16#${bazel_size}))

        if [[ "$cmake_dec" -eq 0 && "$bazel_dec" -eq 0 ]]; then
            continue
        fi

        # Calculate percentage difference
        local diff_pct=0
        if [[ "$cmake_dec" -gt 0 ]]; then
            local abs_diff=$(( cmake_dec > bazel_dec ? cmake_dec - bazel_dec : bazel_dec - cmake_dec ))
            diff_pct=$(( (abs_diff * 100) / cmake_dec ))
        fi

        if [[ "$diff_pct" -gt "$section_tolerance" ]]; then
            ((size_mismatches++)) || true
            details="$details\n      $section_name: CMake=${cmake_dec} Bazel=${bazel_dec} (${diff_pct}% diff)"
        fi
    done < "$tmpdir/cmake_sections"

    if [[ "$size_mismatches" -eq 0 ]]; then
        check_pass "ELF section sizes within ${section_tolerance}% tolerance ($total_sections sections)"
    else
        check_fail "ELF section sizes: $size_mismatches/$total_sections sections exceed ${section_tolerance}% tolerance"
        if [[ "$verbose" == "true" && -n "$details" ]]; then
            echo -e "$details"
        fi
    fi

    rm -rf "$tmpdir"
}

# Compare overall file size (strip both to temp files for fair comparison)
compare_filesize() {
    local cmake_bin="$1"
    local bazel_bin="$2"
    local name="$3"

    local cmake_size bazel_size
    cmake_size=$(stat -c%s "$cmake_bin")
    bazel_size=$(stat -c%s "$bazel_bin")

    local cmake_human bazel_human
    cmake_human=$(numfmt --to=iec "$cmake_size")
    bazel_human=$(numfmt --to=iec "$bazel_size")

    # Raw size comparison (informational)
    local raw_diff_pct=0
    if [[ "$cmake_size" -gt 0 ]]; then
        local abs_diff=$(( cmake_size > bazel_size ? cmake_size - bazel_size : bazel_size - cmake_size ))
        raw_diff_pct=$(( (abs_diff * 100) / cmake_size ))
    fi
    log_info "Raw size: CMake=${cmake_human} Bazel=${bazel_human} (${raw_diff_pct}% diff)"

    # Strip both to temp files for fair comparison
    local tmpdir
    tmpdir=$(mktemp -d)
    cp "$cmake_bin" "$tmpdir/cmake"
    cp "$bazel_bin" "$tmpdir/bazel"
    chmod +w "$tmpdir/cmake" "$tmpdir/bazel"
    strip --strip-debug --strip-unneeded "$tmpdir/cmake" 2>/dev/null || true
    strip --strip-debug --strip-unneeded "$tmpdir/bazel" 2>/dev/null || true

    local cmake_stripped_size bazel_stripped_size
    cmake_stripped_size=$(stat -c%s "$tmpdir/cmake")
    bazel_stripped_size=$(stat -c%s "$tmpdir/bazel")

    local cmake_stripped_human bazel_stripped_human
    cmake_stripped_human=$(numfmt --to=iec "$cmake_stripped_size")
    bazel_stripped_human=$(numfmt --to=iec "$bazel_stripped_size")

    local diff_pct=0
    if [[ "$cmake_stripped_size" -gt 0 ]]; then
        local abs_diff=$(( cmake_stripped_size > bazel_stripped_size ? cmake_stripped_size - bazel_stripped_size : bazel_stripped_size - cmake_stripped_size ))
        diff_pct=$(( (abs_diff * 100) / cmake_stripped_size ))
    fi

    if [[ "$diff_pct" -le "$section_tolerance" ]]; then
        check_pass "Stripped size: CMake=${cmake_stripped_human} Bazel=${bazel_stripped_human} (${diff_pct}% diff)"
    else
        check_fail "Stripped size: CMake=${cmake_stripped_human} Bazel=${bazel_stripped_human} (${diff_pct}% diff, exceeds ${section_tolerance}%)"
    fi

    rm -rf "$tmpdir"
}

# Compare one binary pair
compare_binary() {
    local cmake_bin="$1"
    local bazel_bin="$2"
    local name="$3"

    echo ""
    log "Comparing: $name"

    if [[ ! -f "$cmake_bin" ]]; then
        log_warn "CMake binary not found: $cmake_bin"
        skipped_binaries=$((skipped_binaries + 1))
        return
    fi
    if [[ ! -f "$bazel_bin" ]]; then
        log_warn "Bazel binary not found: $bazel_bin"
        skipped_binaries=$((skipped_binaries + 1))
        return
    fi

    local cmake_human bazel_human
    cmake_human=$(numfmt --to=iec "$(stat -c%s "$cmake_bin")")
    bazel_human=$(numfmt --to=iec "$(stat -c%s "$bazel_bin")")
    log_info "CMake: $cmake_bin ($cmake_human)"
    log_info "Bazel: $bazel_bin ($bazel_human)"

    compare_symbols "$cmake_bin" "$bazel_bin" "$name"

    # Only compare NEEDED/SONAME for shared libraries
    if [[ "$name" == *.so ]]; then
        compare_needed "$cmake_bin" "$bazel_bin" "$name"
        compare_soname "$cmake_bin" "$bazel_bin" "$name"
    fi

    compare_sections "$cmake_bin" "$bazel_bin" "$name"
    compare_filesize "$cmake_bin" "$bazel_bin" "$name"
}

# ----- Binary mapping -----
# Maps: name -> cmake_path bazel_path
declare -A BINARY_MAP

populate_binary_map() {
    # CoreCLR
    BINARY_MAP["libcoreclr.so"]="$cmake_coreclr_dir/libcoreclr.so|$bazel_framework_dir/libcoreclr.so"

    # Corehost
    BINARY_MAP["dotnet"]="$cmake_corehost_dir/dotnet|$bazel_dir/dotnet"
    BINARY_MAP["libhostfxr.so"]="$cmake_corehost_dir/libhostfxr.so|$bazel_fxr_dir/libhostfxr.so"
    BINARY_MAP["libhostpolicy.so"]="$cmake_corehost_dir/libhostpolicy.so|$bazel_framework_dir/libhostpolicy.so"

    # Native interop libraries
    BINARY_MAP["libSystem.Native.so"]="$cmake_nativelibs_dir/libSystem.Native.so|$bazel_framework_dir/libSystem.Native.so"
    BINARY_MAP["libSystem.IO.Compression.Native.so"]="$cmake_nativelibs_dir/libSystem.IO.Compression.Native.so|$bazel_framework_dir/libSystem.IO.Compression.Native.so"
    BINARY_MAP["libSystem.IO.Ports.Native.so"]="$cmake_nativelibs_dir/libSystem.IO.Ports.Native.so|$bazel_framework_dir/libSystem.IO.Ports.Native.so"
    BINARY_MAP["libSystem.Net.Security.Native.so"]="$cmake_nativelibs_dir/libSystem.Net.Security.Native.so|$bazel_framework_dir/libSystem.Net.Security.Native.so"
    BINARY_MAP["libSystem.Globalization.Native.so"]="$cmake_nativelibs_dir/libSystem.Globalization.Native.so|$bazel_framework_dir/libSystem.Globalization.Native.so"
    BINARY_MAP["libSystem.Security.Cryptography.Native.OpenSsl.so"]="$cmake_nativelibs_dir/libSystem.Security.Cryptography.Native.OpenSsl.so|$bazel_framework_dir/libSystem.Security.Cryptography.Native.OpenSsl.so"
}

# ----- Main -----
main() {
    log "Bazel vs CMake Binary Comparison"
    log "  Configuration: $config ($build_type)"
    log "  Section size tolerance: ${section_tolerance}%"
    log "  Bazel dir: $bazel_dir"
    log "  CMake CoreCLR dir: $cmake_coreclr_dir"
    log "  CMake native libs dir: $cmake_nativelibs_dir"
    log "  CMake corehost dir: $cmake_corehost_dir"

    build_both
    populate_binary_map

    # Compare each binary
    for name in \
        dotnet \
        libhostfxr.so \
        libhostpolicy.so \
        libcoreclr.so \
        libSystem.Native.so \
        libSystem.IO.Compression.Native.so \
        libSystem.IO.Ports.Native.so \
        libSystem.Net.Security.Native.so \
        libSystem.Globalization.Native.so \
        libSystem.Security.Cryptography.Native.OpenSsl.so \
    ; do
        local paths="${BINARY_MAP[$name]}"
        local cmake_bin="${paths%%|*}"
        local bazel_bin="${paths##*|}"
        compare_binary "$cmake_bin" "$bazel_bin" "$name"
    done

    # ----- Summary -----
    echo ""
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    log "Summary"
    echo -e "  Total checks: $total_checks"
    echo -e "  ${GREEN}Passed:${NC} $passed_checks"
    if [[ "$failed_checks" -gt 0 ]]; then
        echo -e "  ${RED}Failed:${NC} $failed_checks"
    fi
    if [[ "$skipped_binaries" -gt 0 ]]; then
        echo -e "  ${YELLOW}Skipped binaries:${NC} $skipped_binaries (not found)"
    fi
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

    if [[ "$failed_checks" -gt 0 ]]; then
        echo ""
        echo -e "${RED}FAIL${NC}: $failed_checks check(s) failed. Run with --verbose for details."
        exit 1
    elif [[ "$skipped_binaries" -gt 0 && "$total_checks" -eq 0 ]]; then
        echo ""
        echo -e "${YELLOW}SKIP${NC}: No binaries found to compare. Build both CMake and Bazel first:"
        echo "  CMake: ./build.sh clr+libs+host"
        echo "  Bazel: bazel --nohome_rc build //:runtime_native"
        exit 1
    else
        echo ""
        echo -e "${GREEN}PASS${NC}: All $passed_checks checks passed."
        exit 0
    fi
}

main
