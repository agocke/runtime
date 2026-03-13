#!/usr/bin/env bash

# --- begin runfiles.bash initialization v3 ---
# Copy-pasted from the Bazel Bash runfiles library v3.
set -uo pipefail; set +e; f=bazel_tools/tools/bash/runfiles/runfiles.bash
source "${RUNFILES_DIR:-/dev/null}/$f" 2>/dev/null || \
  source "$(grep -sm1 "^$f " "${RUNFILES_MANIFEST_FILE:-/dev/null}" | cut -f2- -d' ')" 2>/dev/null || \
  source "$0.runfiles/$f" 2>/dev/null || \
  source "$(grep -sm1 "^$f " "$0.runfiles_manifest" | cut -f2- -d' ')" 2>/dev/null || \
  source "$(grep -sm1 "^$f " "$0.exe.runfiles_manifest" | cut -f2- -d' ')" 2>/dev/null || \
  { echo>&2 "ERROR: cannot find $f"; exit 1; }; f=; set -e
# --- end runfiles.bash initialization v3 ---

# Resolve the HelixSubmit tool from runfiles.
HELIX_SUBMIT="$(rlocation TEMPLATED_helix_submit)"

# Resolve the shared testhost directory (Helix correlation payload).
TESTHOST="$(readlink -f "$(rlocation TEMPLATED_testhost)")"

# Locate the test output directory by resolving a known file within it.
# All test files (DLLs, configs, data) are co-located in this directory.
TEST_DIR_ANCHOR="$(rlocation TEMPLATED_test_dir_anchor)"
TEST_DIR="$(dirname "$(readlink -f "$TEST_DIR_ANCHOR")")"

# Build HelixSubmit arguments.
ARGS=(
    "--queue=TEMPLATED_queue"
    "--command=TEMPLATED_command"
    "--work-item-name=TEMPLATED_work_item_name"
    "--timeout=TEMPLATED_timeout"
    "--base-url=TEMPLATED_base_url"
    "--source=TEMPLATED_source"
    "--creator=TEMPLATED_creator"
    "--correlation-payload-dir=$TESTHOST"
    "--test-payload-dir=$TEST_DIR"
)

# Pass Helix access token from environment if available.
if [ -n "${HELIX_ACCESS_TOKEN:-}" ]; then
    ARGS+=("--token=$HELIX_ACCESS_TOKEN")
fi

# Direct test results to Bazel's undeclared outputs directory.
if [ -n "${TEST_UNDECLARED_OUTPUTS_DIR:-}" ]; then
    ARGS+=("--results-dir=$TEST_UNDECLARED_OUTPUTS_DIR")
fi

exec "$HELIX_SUBMIT" "${ARGS[@]}"
