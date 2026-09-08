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
  # to accept connections before starting. SqlClient accepts several aliases for
  # the server keyword and an optional protocol prefix:
  # "Server|Data Source|Address|Addr|Network Address=[tcp:]host[,port]".
  server=$(echo "${DatabaseSettings__DatabaseConnection}" \
    | grep -Eio "(^|;)[[:space:]]*(server|data source|address|addr|network address)[[:space:]]*=[^;]+" \
    | head -1 \
    | sed -E 's/^;?[[:space:]]*[^=]+=[[:space:]]*//; s/^[Tt][Cc][Pp]://')
  host=$(echo "${server}" | awk -F, '{gsub(/[[:space:]]/, "", $1); print $1}')
  port=$(echo "${server}" | awk -F, '{gsub(/[[:space:]]/, "", $2); print $2}')
  port=${port:-1433}

  if [ -z "${host}" ]; then
    # Let .NET surface a real connection error rather than blocking startup forever.
    echo "Could not parse a SQL Server host from DatabaseSettings__DatabaseConnection; skipping TCP readiness wait."
  else
    # nc rather than bash's /dev/tcp: the published image runs this script under ash.
    until nc -z -w 2 "${host}" "${port}" 2>/dev/null; do
      echo "Waiting for SQL Server to start..."
      sleep 2
    done

    echo "SQL Server is accepting TCP connections."
  fi
fi

echo "Running EdFi.DmsConfigurationService.Frontend.AspNetCore..."
dotnet EdFi.DmsConfigurationService.Frontend.AspNetCore.dll

