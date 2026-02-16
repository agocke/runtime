#!/usr/bin/env bash
# compare-compile-commands.sh — Compare compilation inputs between Bazel and CMake/MSBuild
#
# Verifies that Bazel compilations use the same defines, includes, flags, and source
# files as the corresponding CMake (C/C++) or MSBuild (C#) compilations.
#
# Prerequisites:
#   - Both build systems must have been run first (or use --build to do it automatically)
#   - For C/C++: CMake build produces compile_commands.json
#   - For C#: MSBuild build produces .binlog files
#   - Bazel aquery is run to extract Bazel compilation commands
#
# Usage:
#   ./compare-compile-commands.sh                      # Auto-detect paths (debug)
#   ./compare-compile-commands.sh --config release     # Use release configuration
#   ./compare-compile-commands.sh --build              # Build both first, then compare
#   ./compare-compile-commands.sh --json               # Output as JSON
#   ./compare-compile-commands.sh --native-only        # Only compare C/C++ targets
#   ./compare-compile-commands.sh --managed-only       # Only compare C# targets

set -euo pipefail

scriptroot="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
tooldir="$scriptroot/eng/tools/CompileCommandComparer"

# ----- Defaults -----
config="debug"
build_first=false
json_output=false
native_only=false
managed_only=false
bazel_aquery_path=""
compile_commands_path=""
msbuild_binlog_path=""

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
        --json)
            json_output=true
            shift
            ;;
        --native-only)
            native_only=true
            shift
            ;;
        --managed-only)
            managed_only=true
            shift
            ;;
        --bazel-aquery)
            bazel_aquery_path="$2"
            shift 2
            ;;
        --compile-commands)
            compile_commands_path="$2"
            shift 2
            ;;
        --msbuild-binlog)
            msbuild_binlog_path="$2"
            shift 2
            ;;
        -h|--help)
            head -20 "${BASH_SOURCE[0]}" | tail -18
            exit 0
            ;;
        *)
            echo "Unknown argument: $1"
            exit 1
            ;;
    esac
done

# ----- Derived variables -----
if [[ "$config" == "debug" ]]; then
    build_type="Debug"
    bazel_config_args=(-c dbg --config=debug)
else
    build_type="Release"
    bazel_config_args=(-c opt)
fi

major_version=$(grep '<MajorVersion>' "$scriptroot/eng/Versions.props" | sed 's/.*<MajorVersion>\(.*\)<\/MajorVersion>.*/\1/')
minor_version=$(grep '<MinorVersion>' "$scriptroot/eng/Versions.props" | sed 's/.*<MinorVersion>\(.*\)<\/MinorVersion>.*/\1/')

# Paths
compile_commands_path="${compile_commands_path:-$scriptroot/artifacts/obj/coreclr/linux.x64.$build_type/compile_commands.json}"
msbuild_binlog_path="${msbuild_binlog_path:-$scriptroot/artifacts/log/$build_type/Build.binlog}"
bazel_aquery_path="${bazel_aquery_path:-$scriptroot/artifacts/obj/bazel-aquery.json}"
mapping_path="$tooldir/target-mapping.json"

# ----- Color helpers -----
CYAN='\033[1;36m'
NC='\033[0m'
log() { echo -e "${CYAN}==>${NC} $*"; }

# ----- Build if requested -----
if [[ "$build_first" == "true" ]]; then
    log "Building with CMake/MSBuild..."
    "$scriptroot/build.sh" clr+libs+host -rc "$config" /bl:"$msbuild_binlog_path"

    log "Building with Bazel..."
    bazel --nohome_rc build "${bazel_config_args[@]}" //:runtime_native
fi

# ----- Generate Bazel aquery output -----
if [[ ! -f "$bazel_aquery_path" ]] || [[ "$build_first" == "true" ]]; then
    log "Generating Bazel aquery output..."
    mkdir -p "$(dirname "$bazel_aquery_path")"

    # Query all compile actions for the runtime target
    bazel --nohome_rc aquery \
        'mnemonic("CppCompile|CCompile|CSharpCompile|CoreCompile|DotnetCompile", deps(//:runtime_native))' \
        --output=jsonproto \
        "${bazel_config_args[@]}" \
        > "$bazel_aquery_path" 2>/dev/null || {
        echo "Warning: Bazel aquery failed. Trying broader query..."
        bazel --nohome_rc aquery \
            'deps(//:runtime_native)' \
            --output=jsonproto \
            "${bazel_config_args[@]}" \
            > "$bazel_aquery_path"
    }

    log "Bazel aquery output saved to $bazel_aquery_path"
fi

# ----- Build the comparison tool -----
log "Building comparison tool..."
dotnet build "$tooldir/CompileCommandComparer.csproj" --nologo -v quiet 2>/dev/null || {
    log "Building tool (first time may take a moment)..."
    dotnet build "$tooldir/CompileCommandComparer.csproj" --nologo
}

# ----- Run comparison -----
tool_args=(
    --repo-root "$scriptroot"
    --mapping "$mapping_path"
)

if [[ "$managed_only" != "true" ]] && [[ -f "$compile_commands_path" ]]; then
    tool_args+=(--compile-commands "$compile_commands_path")
elif [[ "$managed_only" != "true" ]]; then
    echo "Warning: compile_commands.json not found at $compile_commands_path"
    echo "  Run './build.sh clr' first to generate it, or use --compile-commands to specify the path."
fi

if [[ "$native_only" != "true" ]] && [[ -f "$msbuild_binlog_path" ]]; then
    tool_args+=(--msbuild-binlog "$msbuild_binlog_path")
elif [[ "$native_only" != "true" ]]; then
    echo "Warning: binlog not found at $msbuild_binlog_path"
    echo "  Run './build.sh clr+libs' first with /bl: flag, or use --msbuild-binlog to specify the path."
fi

if [[ -f "$bazel_aquery_path" ]]; then
    tool_args+=(--bazel-aquery "$bazel_aquery_path")
else
    echo "Error: Bazel aquery output not found at $bazel_aquery_path"
    echo "  Run Bazel build first, or use --bazel-aquery to specify the path."
    exit 2
fi

if [[ "$json_output" == "true" ]]; then
    tool_args+=(--json)
fi

log "Running compile command comparison..."
echo ""
dotnet run --project "$tooldir/CompileCommandComparer.csproj" --no-build -- "${tool_args[@]}"
