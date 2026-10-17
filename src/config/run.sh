#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

set -e
set +x

datastore=$(echo "${AppSettings__Datastore:-postgresql}" | tr '[:upper:]' '[:lower:]')

# Prints the value of one setting in a connection string, following the .NET connection string
# rules: a single- or double-quoted value may contain semicolons, a doubled quote inside it is a
# literal quote, spaces around keys and unquoted values are ignored, and the last occurrence of a
# key wins. Keys match exactly, so Host= is not host=. A whole value wrapped in one pair of quotes,
# as docker run --env-file passes it, is unwrapped first.
connection_string_value() {
  printf '%s' "$1" | awk -v want="$2" -v sq="'" '
    { s = s (NR > 1 ? "\n" : "") $0 }
    END {
      n = length(s)
      first = substr(s, 1, 1)
      if (n >= 2 && (first == "\"" || first == sq) && substr(s, n, 1) == first) {
        s = substr(s, 2, n - 2)
        n -= 2
      }
      i = 1
      while (i <= n) {
        key = ""
        while (i <= n && substr(s, i, 1) != "=" && substr(s, i, 1) != ";") {
          key = key substr(s, i, 1)
          i++
        }
        if (i > n || substr(s, i, 1) == ";") {
          i++
          continue
        }
        i++
        while (i <= n && (substr(s, i, 1) == " " || substr(s, i, 1) == "\t")) {
          i++
        }
        value = ""
        quote = substr(s, i, 1)
        if (quote == "\"" || quote == sq) {
          i++
          while (i <= n) {
            c = substr(s, i, 1)
            if (c == quote && substr(s, i + 1, 1) == quote) {
              value = value quote
              i += 2
            } else if (c == quote) {
              i++
              break
            } else {
              value = value c
              i++
            }
          }
          while (i <= n && substr(s, i, 1) != ";") {
            i++
          }
        } else {
          while (i <= n && substr(s, i, 1) != ";") {
            value = value substr(s, i, 1)
            i++
          }
          sub(/[ \t]+$/, "", value)
        }
        i++
        sub(/^[ \t]+/, "", key)
        sub(/[ \t]+$/, "", key)
        if (key == want) {
          result = value
          found = 1
        }
      }
      if (found) {
        print result
      }
    }'
}

if [ "$datastore" = "postgresql" ]; then
  # Safely extract a few environment variables from the admin connection string
  host=$(connection_string_value "${DatabaseSettings__DatabaseConnection:-}" host)
  port=$(connection_string_value "${DatabaseSettings__DatabaseConnection:-}" port)
  username=$(connection_string_value "${DatabaseSettings__DatabaseConnection:-}" username)

  if [ -z "${host}" ]; then
    # Let .NET surface a real connection error rather than blocking startup forever.
    echo "No host= setting found in DatabaseSettings__DatabaseConnection; skipping the PostgreSQL readiness wait."
  else
    until pg_isready -h "${host}" -p "${port}" -U "${username}"; do
      echo "Waiting for PostgreSQL to start..."
      sleep 2
    done

    echo "PostgreSQL is ready."
  fi
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

