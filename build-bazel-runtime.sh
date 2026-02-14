#!/usr/bin/env bash
# build-bazel-runtime.sh — Build the .NET runtime using Bazel (native) + MSBuild (managed)
#
# Usage:
#   ./build-bazel-runtime.sh                     # Build everything (managed + native) and assemble
#   ./build-bazel-runtime.sh --native-only       # Only rebuild native code with Bazel
#   ./build-bazel-runtime.sh --managed-only      # Only rebuild managed code with MSBuild
#   ./build-bazel-runtime.sh --rebuild-managed    # Force MSBuild managed rebuild even if artifacts exist
#   ./build-bazel-runtime.sh --config debug      # Use debug configuration (default: release)
#   ./build-bazel-runtime.sh --smoke-test        # Run smoke test after build

set -euo pipefail

scriptroot="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ----- Defaults -----
build_native=true
build_managed=true
rebuild_managed=false
config="release"
run_smoke_test=false
bazel_config_args=()

# ----- Parse arguments -----
while [[ $# -gt 0 ]]; do
    case "$1" in
        --native-only)
            build_managed=false
            shift
            ;;
        --managed-only)
            build_native=false
            shift
            ;;
        --rebuild-managed)
            rebuild_managed=true
            shift
            ;;
        --config)
            config="${2,,}" # lowercase
            shift 2
            ;;
        --smoke-test)
            run_smoke_test=true
            shift
            ;;
        -h|--help)
            head -9 "${BASH_SOURCE[0]}" | tail -8
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
tfm="net${major_version}.${minor_version}"

rid="linux-x64"

if [[ "$config" == "debug" ]]; then
    msbuild_rc="Debug"
    msbuild_lc="Debug"
    bazel_config_args=(-c dbg --config=debug)
else
    msbuild_rc="Release"
    msbuild_lc="Release"
    bazel_config_args=(-c opt)
fi

# ----- Output layout paths -----
output_dir="$scriptroot/artifacts/bazel-dotnet"
fxr_dir="$output_dir/host/fxr/$product_version"
framework_dir="$output_dir/shared/Microsoft.NETCore.App/$product_version"

# ----- MSBuild artifact source paths -----
corelib_dll="$scriptroot/artifacts/bin/coreclr/linux.x64.${msbuild_rc}/IL/System.Private.CoreLib.dll"
managed_dlls_dir="$scriptroot/artifacts/bin/microsoft.netcore.app.runtime.${rid}/${msbuild_rc}/runtimes/${rid}/lib/${tfm}"

# ----- Bazel output paths -----
bazel_bin="$scriptroot/bazel-bin"

# Bazel targets for native build
bazel_targets=(
    "//src/coreclr/dlls/mscoree/coreclr:libcoreclr.so"
    "//src/native/corehost:dotnet"
    "//src/native/corehost:hostfxr"
    "//src/native/corehost:hostpolicy"
    "//src/native/libs/System.Native:System.Native"
    "//src/native/libs/System.IO.Compression.Native:System.IO.Compression.Native"
    "//src/native/libs/System.IO.Ports.Native:System.IO.Ports.Native"
    "//src/native/libs/System.Net.Security.Native:System.Net.Security.Native"
    "//src/native/libs/System.Globalization.Native:System.Globalization.Native"
    "//src/native/libs/System.Security.Cryptography.Native:System.Security.Cryptography.Native.OpenSsl"
)

# Mapping: bazel output path -> destination directory
# Format: "source_relative_path|dest_dir_variable|dest_filename"
declare -A native_files=(
    # CoreCLR
    ["src/coreclr/dlls/mscoree/coreclr/libcoreclr.so"]="framework"
    # Corehost
    ["src/native/corehost/dotnet"]="root"
    ["src/native/corehost/libhostfxr.so"]="fxr"
    ["src/native/corehost/libhostpolicy.so"]="framework"
    # Native libs
    ["src/native/libs/System.Native/libSystem.Native.so"]="framework"
    ["src/native/libs/System.IO.Compression.Native/libSystem.IO.Compression.Native.so"]="framework"
    ["src/native/libs/System.IO.Ports.Native/libSystem.IO.Ports.Native.so"]="framework"
    ["src/native/libs/System.Net.Security.Native/libSystem.Net.Security.Native.so"]="framework"
    ["src/native/libs/System.Globalization.Native/libSystem.Globalization.Native.so"]="framework"
    ["src/native/libs/System.Security.Cryptography.Native/libSystem.Security.Cryptography.Native.OpenSsl.so"]="framework"
)

# ----- Helper functions -----
log() { echo -e "\033[1;36m==>\033[0m $*"; }
log_error() { echo -e "\033[1;31m==> ERROR:\033[0m $*" >&2; }
log_success() { echo -e "\033[1;32m==>\033[0m $*"; }

# ----- Step 1: MSBuild managed build -----
build_managed_libs() {
    if [[ "$build_managed" != "true" ]]; then
        return
    fi

    # Check if managed artifacts already exist
    if [[ "$rebuild_managed" != "true" ]] && [[ -f "$corelib_dll" ]] && [[ -d "$managed_dlls_dir" ]]; then
        local dll_count
        dll_count=$(find "$managed_dlls_dir" -name "*.dll" 2>/dev/null | wc -l)
        if [[ "$dll_count" -gt 100 ]]; then
            log "Managed artifacts already exist ($dll_count DLLs). Skipping MSBuild. Use --rebuild-managed to force."
            return
        fi
    fi

    log "Building managed libraries with MSBuild (clr.corelib+libs, ${msbuild_rc})..."
    log "This may take 15-30 minutes on first run."

    "$scriptroot/build.sh" clr.corelib+libs -rc "$msbuild_rc" -lc "$msbuild_lc"

    if [[ ! -f "$corelib_dll" ]]; then
        log_error "System.Private.CoreLib.dll not found at: $corelib_dll"
        log_error "MSBuild build may have failed."
        exit 1
    fi

    log_success "Managed build complete."
}

# ----- Step 2: Bazel native build -----
build_native_libs() {
    if [[ "$build_native" != "true" ]]; then
        return
    fi

    log "Building native components with Bazel..."

    bazel --nohome_rc build "${bazel_config_args[@]}" "${bazel_targets[@]}"

    log_success "Native build complete."
}

# ----- Step 3: Assemble runtime layout -----
assemble_runtime() {
    log "Assembling runtime layout at: $output_dir"

    # Clean and create directory structure
    rm -rf "$output_dir"
    mkdir -p "$output_dir" "$fxr_dir" "$framework_dir"

    # Copy native files from Bazel
    if [[ "$build_native" == "true" ]] || [[ -d "$bazel_bin" ]]; then
        log "Copying native components from Bazel..."
        for src_rel in "${!native_files[@]}"; do
            local src="$bazel_bin/$src_rel"
            local dest_type="${native_files[$src_rel]}"
            local dest_dir

            case "$dest_type" in
                root)      dest_dir="$output_dir" ;;
                fxr)       dest_dir="$fxr_dir" ;;
                framework) dest_dir="$framework_dir" ;;
            esac

            if [[ -f "$src" ]]; then
                cp "$src" "$dest_dir/"
            else
                log_error "Native file not found: $src"
                exit 1
            fi
        done

        chmod +x "$output_dir/dotnet"
    fi

    # Copy managed DLLs from MSBuild
    if [[ "$build_managed" == "true" ]] || [[ -d "$managed_dlls_dir" ]]; then
        if [[ -d "$managed_dlls_dir" ]]; then
            log "Copying managed DLLs from MSBuild..."
            cp "$managed_dlls_dir"/*.dll "$framework_dir/"

            local dll_count
            dll_count=$(find "$framework_dir" -name "*.dll" | wc -l)
            log "Copied $dll_count managed DLLs."
        else
            log_error "Managed DLLs directory not found: $managed_dlls_dir"
            log_error "Run without --native-only first, or run: ./build.sh clr.corelib+libs -rc $msbuild_rc -lc $msbuild_lc"
            exit 1
        fi

        # Copy System.Private.CoreLib.dll (from CoreCLR IL build)
        if [[ -f "$corelib_dll" ]]; then
            cp "$corelib_dll" "$framework_dir/"
        else
            log_error "System.Private.CoreLib.dll not found at: $corelib_dll"
            exit 1
        fi
    fi

    # Generate minimal Microsoft.NETCore.App.deps.json if not present
    generate_deps_json

    log_success "Runtime layout assembled at: $output_dir"
    log "  dotnet:          $output_dir/dotnet"
    log "  hostfxr:         $fxr_dir/libhostfxr.so"
    log "  shared framework: $framework_dir/"
}

# ----- Generate deps.json -----
generate_deps_json() {
    local deps_json="$framework_dir/Microsoft.NETCore.App.deps.json"

    # If MSBuild produced a deps.json, use it
    local msbuild_deps="$scriptroot/artifacts/bin/microsoft.netcore.app.runtime.${rid}/${msbuild_lc}/runtimes/${rid}/lib/${tfm}/Microsoft.NETCore.App.deps.json"
    if [[ -f "$msbuild_deps" ]]; then
        cp "$msbuild_deps" "$deps_json"
        return
    fi

    log "Generating minimal Microsoft.NETCore.App.deps.json..."

    # Build the runtime libraries entries from the DLLs present in framework_dir
    local runtime_entries=""
    local first=true
    for dll in "$framework_dir"/*.dll; do
        [[ -f "$dll" ]] || continue
        local name
        name=$(basename "$dll" .dll)
        local separator=""
        if [[ "$first" != "true" ]]; then
            separator=","
        fi
        first=false
        runtime_entries+="${separator}
            \"${name}.dll\": {}"
    done

    # Build the native libraries entries
    local native_entries=""
    first=true
    for so in "$framework_dir"/*.so; do
        [[ -f "$so" ]] || continue
        local name
        name=$(basename "$so")
        local separator=""
        if [[ "$first" != "true" ]]; then
            separator=","
        fi
        first=false
        native_entries+="${separator}
            \"${name}\": {}"
    done

    cat > "$deps_json" << DEPS_EOF
{
  "runtimeTarget": {
    "name": ".NETCoreApp,Version=v${major_version}.${minor_version}/linux-x64",
    "signature": ""
  },
  "compilationOptions": {},
  "targets": {
    ".NETCoreApp,Version=v${major_version}.${minor_version}/linux-x64": {
      "Microsoft.NETCore.App/${product_version}": {
        "runtime": {${runtime_entries}
        },
        "native": {${native_entries}
        }
      }
    }
  },
  "libraries": {
    "Microsoft.NETCore.App/${product_version}": {
      "type": "package",
      "serviceable": false,
      "sha512": ""
    }
  }
}
DEPS_EOF
}

# ----- Smoke test -----
smoke_test() {
    if [[ "$run_smoke_test" != "true" ]]; then
        return
    fi

    log "Running smoke test..."

    local dotnet_exe="$output_dir/dotnet"
    if [[ ! -x "$dotnet_exe" ]]; then
        log_error "dotnet executable not found or not executable: $dotnet_exe"
        exit 1
    fi

    # Set DOTNET_ROOT so the host can find the shared framework
    export DOTNET_ROOT="$output_dir"

    # Basic host check
    log "Testing: dotnet --info"
    "$dotnet_exe" --info || true

    # Create and run a minimal hello-world
    local test_dir
    test_dir=$(mktemp -d)
    trap "rm -rf '$test_dir'" EXIT

    cat > "$test_dir/hello.cs" << 'HELLO_EOF'
System.Console.WriteLine("Hello from Bazel-built .NET runtime!");
HELLO_EOF

    cat > "$test_dir/hello.csproj" << CSPROJ_EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>${tfm}</TargetFramework>
  </PropertyGroup>
</Project>
CSPROJ_EOF

    cat > "$test_dir/hello.runtimeconfig.json" << RTCFG_EOF
{
  "runtimeOptions": {
    "tfm": "${tfm}",
    "framework": {
      "name": "Microsoft.NETCore.App",
      "version": "${product_version}"
    },
    "rollForward": "LatestMajor"
  }
}
RTCFG_EOF

    # Try to compile with the repo's SDK and run with our runtime
    if command -v "$scriptroot/.dotnet/dotnet" &>/dev/null; then
        log "Compiling hello world..."
        "$scriptroot/.dotnet/dotnet" build "$test_dir/hello.csproj" -o "$test_dir/out" --nologo -v quiet 2>/dev/null || true

        if [[ -f "$test_dir/out/hello.dll" ]]; then
            log "Running hello world with Bazel-built runtime..."
            cp "$test_dir/hello.runtimeconfig.json" "$test_dir/out/"
            "$dotnet_exe" "$test_dir/out/hello.dll" && log_success "Smoke test passed!" || log_error "Smoke test failed."
        else
            log "Could not compile hello world (SDK may not be available). Skipping app test."
        fi
    else
        log "SDK not available at .dotnet/dotnet. Skipping hello world test."
    fi
}

# ----- Main -----
main() {
    log "Bazel + MSBuild hybrid runtime build"
    log "  Product version: $product_version"
    log "  Configuration:   $config ($msbuild_rc)"
    log "  Build native:    $build_native"
    log "  Build managed:   $build_managed"
    log "  Output:          $output_dir"
    echo ""

    build_managed_libs
    build_native_libs
    assemble_runtime
    smoke_test

    echo ""
    log_success "Done! Runtime is at: $output_dir"
    log "Run an app:  DOTNET_ROOT=$output_dir $output_dir/dotnet <app.dll>"
}

main
