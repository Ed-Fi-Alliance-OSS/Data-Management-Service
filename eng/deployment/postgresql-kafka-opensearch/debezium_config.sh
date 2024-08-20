#!/bin/sh
#-- SPDX-License-Identifier: Apache-2.0
#-- Licensed to the Ed-Fi Alliance under one or more agreements.
#-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
#-- See the LICENSE and NOTICES files in the project root for more information.

set -e
set +x

echo "Setting up the wal level"
echo "host    replication    postgres         kafka-postgresql-source    trust" >> /var/lib/postgresql/data/pg_hba.conf
echo "wal_level = logical" >> /var/lib/postgresql/data/postgresql.conf
