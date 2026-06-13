#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

set -e
set +x

# Safely extract a few environment variables from the admin connection string
host=$(echo ${DATABASE_CONNECTION_STRING_ADMIN} | grep -Eo "host([^;]+)" | awk -F= '{print $2}')
port=$(echo ${DATABASE_CONNECTION_STRING_ADMIN} | grep -Eo "port([^;]+)" | awk -F= '{print $2}')
username=$(echo ${DATABASE_CONNECTION_STRING_ADMIN} | grep -Eo "username([^;]+)" | awk -F= '{print $2}')

until pg_isready -h ${host} -p ${port} -U ${username}; do
  echo "Waiting for PostgreSQL to start..."
  sleep 2
done

echo "PostgreSQL is ready."

if [ "$NEED_DATABASE_SETUP" = true ]; then

  echo "Installing Data Management Service schema."

  dotnet Installer/EdFi.DataManagementService.Backend.Installer.dll -e postgresql -c ${DATABASE_CONNECTION_STRING_ADMIN}

  export NEED_DATABASE_SETUP=false

else
  echo "Skipping Data Management Service schema installation."
fi

if [ "$AppSettings__UseApiSchemaPath" = true ]; then
    echo "Using Api Schema Path."

    schema_packages="${SCHEMA_PACKAGES:-[]}"
    package_count=$(echo "$schema_packages" | jq 'length')

    if [ "$package_count" -gt 0 ]; then
        if [ -z "${AppSettings__ApiSchemaPath:-}" ]; then
            echo "AppSettings__ApiSchemaPath is required when SCHEMA_PACKAGES contains package entries."
            exit 1
        fi

        generated_packages_dir="${AppSettings__ApiSchemaPath}/Packages"
        downloaded_packages_dir="${AppSettings__ApiSchemaPath}/DownloadedPackages"
        echo "Clearing generated ApiSchema package extraction output under ${AppSettings__ApiSchemaPath}."
        rm -rf -- "$generated_packages_dir" "$downloaded_packages_dir"
        mkdir -p "${AppSettings__ApiSchemaPath}"

        echo "$schema_packages" | jq -c '.[]' | while read -r item
        do
            version=$(echo "$item" | jq -r '.version')
            feedUrl=$(echo "$item" | jq -r '.feedUrl')
            name=$(echo "$item" | jq -r '.name')

            echo "Downloading Package $name..."
            dotnet /app/ApiSchemaDownloader/EdFi.DataManagementService.ApiSchemaDownloader.dll -p "$name" -d "${AppSettings__ApiSchemaPath}" -v "$version" -f "$feedUrl"
        done

        manifest_path="${AppSettings__ApiSchemaPath}/bootstrap-api-schema-manifest.json"
        manifest_temp_path=$(mktemp)
        projects_temp_path=$(mktemp)
        : > "$projects_temp_path"

        echo "$schema_packages" | jq -r '.[].name' | while read -r name
        do
            package_manifest_path="${AppSettings__ApiSchemaPath}/Packages/${name}/package-manifest.json"
            if [ ! -f "$package_manifest_path" ]; then
                echo "ApiSchema package manifest not found: $package_manifest_path"
                exit 1
            fi

            package_manifest_version=$(jq -r '.version' "$package_manifest_path")
            if [ "$package_manifest_version" != "1" ]; then
                echo "Unsupported ApiSchema package manifest version $package_manifest_version in $package_manifest_path"
                exit 1
            fi

            jq --arg packageDir "Packages/${name}" '{
                projectName: .projectName,
                projectEndpointName: .projectEndpointName,
                isExtensionProject: .isExtensionProject,
                schemaPath: ($packageDir + "/" + .schemaPath),
                discoverySpecPath: (if .discoverySpecPath == null then null else $packageDir + "/" + .discoverySpecPath end),
                xsdDirectory: (if .xsdDirectory == null then null else $packageDir + "/" + .xsdDirectory end)
            }' "$package_manifest_path" >> "$projects_temp_path"
        done

        jq -n --slurpfile projects "$projects_temp_path" '{ version: 1, projects: $projects }' > "$manifest_temp_path"
        mkdir -p "${AppSettings__ApiSchemaPath}"
        mv "$manifest_temp_path" "$manifest_path"
        rm -f "$projects_temp_path"
    fi
fi

echo "Running EdFi.DataManagementService.Frontend.AspNetCore..."
dotnet EdFi.DataManagementService.Frontend.AspNetCore.dll
