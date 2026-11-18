#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.
#
# Builds doclist.json (per-resource document UUIDs) from a restored Northridge database using doclist.sql.
# Usage: make-doclist.sh <database-name> <output-json>
# Environment: DMS1565_SA_PASSWORD_FILE, DMS1565_CONTAINER (default dms1565-nr-mssql)
set -euo pipefail
[ $# -eq 2 ] || { echo "usage: make-doclist.sh <database-name> <output-json>" >&2; exit 64; }
HERE=$(cd "$(dirname "$0")" && pwd)
PASSWORD_FILE=${DMS1565_SA_PASSWORD_FILE:?set DMS1565_SA_PASSWORD_FILE}
CONTAINER=${DMS1565_CONTAINER:-dms1565-nr-mssql}
RAW=$(mktemp)
trap 'rm -f "$RAW"' EXIT
SQLCMDPASSWORD=$(cat "$PASSWORD_FILE") docker exec -i -e SQLCMDPASSWORD "$CONTAINER" \
  /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -W -d "$1" -h -1 -s '|' -i /dev/stdin \
  < "$HERE/doclist.sql" > "$RAW"
python3 -I - "$RAW" "$2" <<'PY'
import collections, json, sys
lists = collections.OrderedDict()
for line in open(sys.argv[1]).read().splitlines():
    parts = [part.strip() for part in line.split("|")]
    if len(parts) < 3:
        continue
    uuid = next(part for part in parts[1:] if len(part) == 36 and part.count("-") == 4)
    lists.setdefault(parts[0], []).append(uuid.lower())
open(sys.argv[2], "w").write(json.dumps(lists, indent=1))
print({resource: len(uuids) for resource, uuids in lists.items()})
PY
