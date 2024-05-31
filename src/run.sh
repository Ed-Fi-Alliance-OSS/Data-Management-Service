#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

set -e
set +x

envsubst < /app/appsettings.template.json > /app/appsettings.json

until pg_isready -h ${POSTGRES_HOST} -p ${POSTGRES_PORT} -U ${POSTGRES_ADMIN_USER}; do
  echo "Waiting for PostgreSQL to start..."
  sleep 2
done

echo "PostgreSQL is ready."

if [ "$NEED_DATABASE_SETUP" = true ]; then

  DATABASE="edfi_datamanagementservice"

  echo "Installing Data Management Service schema."
  dotnet Installer/EdFi.DataManagementService.Backend.Installer.dll -e postgresql -c "host=${POSTGRES_HOST};port=${POSTGRES_PORT};username=${POSTGRES_ADMIN_USER};password=${POSTGRES_ADMIN_PASSWORD};database=${DATABASE};"

  export NEED_DATABASE_SETUP=false

else
  echo "Skipping Data Management Service schema installation."
fi

dotnet EdFi.DataManagementService.Frontend.AspNetCore.dll

