#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.

# Run the full DMS load test suite (both phases)

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
cd "$SCRIPT_DIR/.."

# Pass all arguments to the main runner with --phase full (default)
./run-load-test.sh --phase full "$@"