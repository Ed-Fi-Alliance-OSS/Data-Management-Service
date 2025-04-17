#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

set -e
set +x

envsubst < /app/appsettings.template.json > /app/appsettings.json
envsubst < /app/appsettings.template.json > /app/SchoolYearLoader/appsettings.json
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

if [ "$USE_API_SCHEMA_PATH" = true ]; then
    echo "Using Api Schema Path."

    echo "Downloading Package ${CORE_PACKAGE}..."
    dotnet /app/ApiSchemaDownloader/EdFi.DataManagementService.ApiSchemaDownloader.dll -p ${CORE_PACKAGE} -d ${API_SCHEMA_PATH} -v ${CORE_PACKAGE_VERSION}

    echo "Downloading Package ${TPDM_PACKAGE}..."
    dotnet /app/ApiSchemaDownloader/EdFi.DataManagementService.ApiSchemaDownloader.dll -p ${TPDM_PACKAGE} -d ${API_SCHEMA_PATH} -v ${TPDM_PACKAGE_VERSION}

    echo "Downloading Package ${SAMPLE_PACKAGE}..."
    dotnet /app/ApiSchemaDownloader/EdFi.DataManagementService.ApiSchemaDownloader.dll -p ${SAMPLE_PACKAGE} -d ${API_SCHEMA_PATH} -v ${SAMPLE_PACKAGE_VERSION}

    echo "Downloading Package ${HOMOGRAPH_PACKAGE}..."
    dotnet /app/ApiSchemaDownloader/EdFi.DataManagementService.ApiSchemaDownloader.dll -p ${HOMOGRAPH_PACKAGE} -d ${API_SCHEMA_PATH} -v ${HOMOGRAPH_PACKAGE_VERSION}
fi

echo "Running EdFi.DataManagementService.Frontend.SchoolYearLoader Cli START_YEAR=${START_YEAR}  END_YEAR=${END_YEAR} CURRENT_SCHOOL_YEAR=${CURRENT_SCHOOL_YEAR"
dotnet /app/SchoolYearLoader/EdFi.DataManagementService.Frontend.SchoolYearLoader.dll --startYear ${START_YEAR} --endYear ${END_YEAR} --currentSchoolYear  ${CURRENT_SCHOOL_YEAR}

dotnet EdFi.DataManagementService.Frontend.AspNetCore.dll
