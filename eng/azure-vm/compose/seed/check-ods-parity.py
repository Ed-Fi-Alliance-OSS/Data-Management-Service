#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

"""Compare a deployment's Grand Bend data with an ODS/API populated template, table by table.

Reads the row count of every edfi.* COPY block in the ODS template's pg_dump and compares it with
the same-named edfi."<Table>" in each DMS database (case-insensitive; ODS table names carry no
underscores, so DMS child tables are skipped). Descriptors compare as totals: ODS edfi.descriptor
against DMS dms."Descriptor". Exits 1 when a table differs that --allow-diff does not name.

Usage (on the VM, from eng/azure-vm/compose):
  python3 seed/check-ods-parity.py --package-version 7.3.20067 \
      --database edfi_st --database edfi_mt --database edfi_mt_t2 --allow-diff schoolyeartype
"""

import argparse
import re
import subprocess
import sys
import tempfile
import urllib.request
import zipfile
from pathlib import Path

FEED = "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/flat2"
DEFAULT_PACKAGE = "EdFi.Suite3.Ods.Populated.Template.PostgreSQL.Standard.6.1.0"
COUNT_SQL = """
SELECT table_name,
       (xpath('/row/c/text()', query_to_xml(format('select count(*) as c from %I.%I', table_schema, table_name), false, true, '')))[1]::text::bigint
FROM information_schema.tables WHERE table_schema = 'edfi' AND table_type = 'BASE TABLE'
UNION ALL SELECT '__descriptors__', count(*) FROM dms."Descriptor";
"""


def ods_counts(template: Path) -> dict[str, int]:
    """Row count per edfi table in the template's pg_dump, streamed from the package."""
    with zipfile.ZipFile(template) as package:
        sql = next((n for n in package.namelist() if n.lower().endswith(".sql")), None)
        if sql is None:
            sys.exit(f"ERROR: no .sql file in {template}")
        counts: dict[str, int] = {}
        table = None
        for raw in package.open(sql):
            line = raw.decode("utf-8", "replace")
            if table is None:
                match = re.match(r"^COPY (\w+)\.(\w+) \(", line)
                if match and match.group(1).lower() == "edfi":
                    table = match.group(2).lower()
                    counts[table] = 0
                continue
            if line.startswith("\\."):
                table = None
                continue
            counts[table] += 1
    return counts


def dms_counts(container: str, user: str, database: str) -> dict[str, int]:
    output = subprocess.run(
        ["docker", "exec", container, "psql", "-U", user, "-d", database, "-tAF", "|", "-c", COUNT_SQL],
        check=True, capture_output=True, text=True,
    ).stdout
    return {name.lower(): int(count) for name, count in (line.split("|", 1) for line in output.splitlines() if "|" in line)}


def download(package_id: str, version: str, directory: Path) -> Path:
    lower = package_id.lower()
    target = directory / f"{lower}.{version}.nupkg"
    print(f"Downloading {package_id} {version} ...")
    urllib.request.urlretrieve(f"{FEED}/{lower}/{version}/{lower}.{version}.nupkg", target)
    return target


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--template", type=Path, help="local ODS populated template .nupkg")
    parser.add_argument("--package-id", default=DEFAULT_PACKAGE, help=f"feed package id (default {DEFAULT_PACKAGE})")
    parser.add_argument("--package-version", help="feed package version to download when --template is not given")
    parser.add_argument("--database", action="append", required=True, help="DMS database to compare (repeatable)")
    parser.add_argument("--allow-diff", action="append", default=[], help="table allowed to differ (repeatable)")
    parser.add_argument("--container", default="dms-sec-postgres")
    parser.add_argument("--user", default="postgres")
    args = parser.parse_args()

    with tempfile.TemporaryDirectory() as scratch:
        if args.template:
            template = args.template
        elif args.package_version:
            template = download(args.package_id, args.package_version, Path(scratch))
        else:
            parser.error("give --template or --package-version")
        ods = ods_counts(template)

    allowed = {name.lower() for name in args.allow_diff}
    failed = False
    for database in args.database:
        dms = dms_counts(args.container, args.user, database)
        pairs = {t: (ods[t], dms[t]) for t in sorted(set(ods) & set(dms)) if t != "descriptor" and (ods[t] or dms[t])}
        pairs["descriptor"] = (ods.get("descriptor", 0), dms.get("__descriptors__", 0))
        differ = {t: counts for t, counts in pairs.items() if counts[0] != counts[1]}
        unexpected = [t for t in differ if t not in allowed]
        print(f"== {database}: {len(pairs)} populated tables compared, {len(pairs) - len(differ)} equal, "
              f"{len(differ)} differ ({len(differ) - len(unexpected)} allowed)")
        for key in ("school", "student", "studentschoolassociation", "candidate", "descriptor"):
            if key in pairs:
                print(f"   {key:28} ODS {pairs[key][0]:>7}  DMS {pairs[key][1]:>7}")
        for table, (o, d) in sorted(differ.items(), key=lambda item: -abs(item[1][0] - item[1][1])):
            note = "allowed" if table in allowed else "MISMATCH"
            print(f"   DIFF {table:42} ODS {o:>7}  DMS {d:>7}  ({d - o:+d}) {note}")
        failed = failed or bool(unexpected)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
