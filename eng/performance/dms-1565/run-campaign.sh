#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.
#
# DMS-1565 campaign: three baseline/changed pairs (B, C, B, C, B, C), each block a fresh restore and a
# fresh host, with the resource order rotated per pair. Usage: run-campaign.sh <output-dir> <first-block-number>
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
[ $# -ge 1 ] || { echo "usage: run-campaign.sh <output-dir> [first-block-number]" >&2; exit 64; }
mkdir -p "$1" && OUT=$(cd "$1" && pwd) || { echo "cannot create output directory '$1'" >&2; exit 64; }
FIRST=${2:-1}
WORK=${DMS1565_WORK:?set DMS1565_WORK}
HARNESS=src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Mssql/Given_A_Dms1565_Northridge_Put_Latency_Block.cs

# Both arms must run byte-identical measurement code.
cmp -s "$WORK/arm-baseline/$HARNESS" "$WORK/arm-changed/$HARNESS" ||
  { echo "the harness differs between arm-baseline and arm-changed (or is missing)" >&2; exit 64; }
O1=sessions,courseOfferings,studentSchoolAssociations,classPeriods,sections
O2=courseOfferings,studentSchoolAssociations,classPeriods,sections,sessions
O3=studentSchoolAssociations,classPeriods,sections,sessions,courseOfferings
block=$FIRST
for order in $O1 $O2 $O3; do
  for arm in baseline changed; do
    echo "$(date -u +%FT%TZ) start block $block $arm"
    "$HERE/run-block.sh" "$arm" "$block" "$order" 50 300 "$OUT" || { echo "block $block $arm FAILED"; exit 1; }
    block=$((block + 1))
  done
done
echo "$(date -u +%FT%TZ) campaign complete"
