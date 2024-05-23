#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

set -e
set +x

until pg_isready -h ${POSTGRES_HOST} -p ${POSTGRES_PORT} -U ${POSTGRES_USER}; do
  echo "Waiting for PostgreSQL to start..."
  sleep 2
done

echo "PostgreSQL is ready. Install Data Management Service schema."

dotnet Installer/EdFi.DataManagementService.Backend.Installer.dll postgresql "host=${POSTGRES_HOST};port=${POSTGRES_PORT};username=${POSTGRES_USER};password=${POSTGRES_PASSWORD};database=edfi_datamanagementservice;"

dotnet EdFi.DataManagementService.Frontend.AspNetCore.dll

