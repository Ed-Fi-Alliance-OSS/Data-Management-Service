#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

set -e
set +x

until pg_isready -h 172.17.0.1 -p 5402 -U postgres; do
  echo "Waiting for PostgreSQL to start..."
  sleep 2
done

echo "PostgreSQL is ready. Install Data Management Service schema."

dotnet Installer/EdFi.DataManagementService.Backend.Installer.dll postgresql "host=172.17.0.1;port=5402;username=postgres;password=P@ssw0rd;database=edfi_datamanagementservice;"

dotnet EdFi.DataManagementService.Frontend.AspNetCore.dll

