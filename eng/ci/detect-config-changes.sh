#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Shared by the workflow and behavioral tests; GitHub supplies the event/diff context.
set -euo pipefail

# emit <fresh_build_required> <config_relevant>
emit() {
  echo "fresh_build_required=$1" >> "$GITHUB_OUTPUT"
  echo "config_relevant=$2" >> "$GITHUB_OUTPUT"
}

# Non-pull_request events (merge_group, workflow_dispatch) always run the full
# suite, so config_relevant is reported true and only fresh_build_required matters.
if [[ "${GITHUB_EVENT_NAME}" == "workflow_dispatch" ]]; then
  emit true true
  exit 0
fi

if [[ "${GITHUB_EVENT_NAME}" == "pull_request" ]]; then
  git fetch --no-tags origin "${GITHUB_BASE_REF}:refs/remotes/origin/${GITHUB_BASE_REF}"
  changed_files="$(git diff --no-renames --name-only "origin/${GITHUB_BASE_REF}...HEAD")"
elif [[ "${GITHUB_EVENT_NAME}" == "merge_group" ]]; then
  base_sha="${MERGE_GROUP_BASE_SHA}"

  if [[ -z "$base_sha" ]] || ! changed_files="$(git diff --no-renames --name-only "$base_sha" "${GITHUB_SHA}")"; then
    emit true true
    exit 0
  fi
else
  # Unreachable with the current triggers: workflow_dispatch returned above and the
  # only other events are pull_request and merge_group. Run the full suite rather
  # than infer a diff base for an event this script does not model.
  emit true true
  exit 0
fi

fresh_build_required=false
# For merge_group, force config_relevant true so nothing is skipped when
# validating the merged result; only pull_request narrows to the changed area.
if [[ "${GITHUB_EVENT_NAME}" == "pull_request" ]]; then
  config_relevant=false
else
  config_relevant=true
fi

while IFS= read -r file; do
  case "$file" in
    src/Directory.Packages.props|src/nuget.config|src/config/Dockerfile|src/plugins/*|eng/docker-compose/*)
      fresh_build_required=true
      ;;
  esac
  case "$file" in
    .github/workflows/*|build-config.ps1|package-helpers.psm1|eng/*|src/Directory.Packages.props|src/nuget.config|src/config/*|src/plugins/*|src/dms/PluginFixtureStaging.targets)
      config_relevant=true
      ;;
  esac
done <<< "$changed_files"

emit "$fresh_build_required" "$config_relevant"
