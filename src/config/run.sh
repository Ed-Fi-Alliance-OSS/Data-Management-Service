#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

set -e
set +x

datastore=$(echo "${AppSettings__Datastore:-postgresql}" | tr '[:upper:]' '[:lower:]')

if [ "$datastore" = "postgresql" ]; then
  # Safely extract a few environment variables from the admin connection string
  host=$(echo ${DatabaseSettings__DatabaseConnection} | grep -Eo "host([^;]+)" | awk -F= '{print $2}')
  port=$(echo ${DatabaseSettings__DatabaseConnection} | grep -Eo "port([^;]+)" | awk -F= '{print $2}')
  username=$(echo ${DatabaseSettings__DatabaseConnection} | grep -Eo "username([^;]+)" | awk -F= '{print $2}')

  until pg_isready -h ${host} -p ${port} -U ${username}; do
    echo "Waiting for PostgreSQL to start..."
    sleep 2
  done

  echo "PostgreSQL is ready."
else
  # sqlcmd is not available in this image, so wait for the SQL Server TCP endpoint
  # (parsed from "Server=host[,port];...") to accept connections before starting.
  server=$(echo ${DatabaseSettings__DatabaseConnection} | grep -Eio "server=([^;]+)" | head -1 | awk -F= '{print $2}')
  host=$(echo ${server} | awk -F, '{print $1}')
  port=$(echo ${server} | awk -F, '{print $2}')
  port=${port:-1433}

  until (exec 3<>"/dev/tcp/${host}/${port}") 2>/dev/null; do
    echo "Waiting for SQL Server to start..."
    sleep 2
  done

  echo "SQL Server is accepting TCP connections."
fi

echo "Running EdFi.DmsConfigurationService.Frontend.AspNetCore..."
dotnet EdFi.DmsConfigurationService.Frontend.AspNetCore.dll

