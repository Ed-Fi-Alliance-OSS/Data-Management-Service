#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.
#
# Stages the core-only EdFi.DataStandard52.ApiSchema 1.0.335 package as a DMS ApiSchema workspace: the
# package's ApiSchema directory unmodified under ed-fi/, plus a bootstrap-api-schema-manifest.json.
# Usage: stage-apischema.sh <output-dir>   (restore the repository once so the package is in the NuGet cache)
set -euo pipefail
[ $# -eq 1 ] || { echo "usage: stage-apischema.sh <output-dir>" >&2; exit 64; }
PACKAGES=$(dotnet nuget locals global-packages --list | sed 's/^[^:]*: *//' | tr -d '\r')
SOURCE="$PACKAGES/edfi.datastandard52.apischema/1.0.335/contentFiles/any/any/ApiSchema"
[ -f "$SOURCE/ApiSchema.json" ] || { echo "ApiSchema 1.0.335 is not in the NuGet cache ($SOURCE)" >&2; exit 1; }
mkdir -p "$1/ed-fi"
cp -R "$SOURCE/." "$1/ed-fi/"
cat > "$1/bootstrap-api-schema-manifest.json" <<'JSON'
{
  "version": 1,
  "projects": [
    {
      "projectName": "Ed-Fi",
      "projectEndpointName": "ed-fi",
      "isExtensionProject": false,
      "schemaPath": "ed-fi/ApiSchema.json",
      "discoverySpecPath": "ed-fi/discovery-spec.json",
      "xsdDirectory": "ed-fi/xsd"
    }
  ]
}
JSON
shasum -a 256 "$1/ed-fi/ApiSchema.json"
