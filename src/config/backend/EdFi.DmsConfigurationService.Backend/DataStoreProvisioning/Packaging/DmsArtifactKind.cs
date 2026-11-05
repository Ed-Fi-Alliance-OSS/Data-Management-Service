// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;

/// <summary>The restore artifact a template package carries (F-P2).</summary>
public enum DmsArtifactKind
{
    /// <summary>A plain <c>pg_dump --no-owner</c> SQL file, replayed with <c>psql</c>.</summary>
    PostgresqlSqlDump,

    /// <summary>A <c>BACKUP DATABASE</c> file, restored with <c>RESTORE DATABASE … WITH MOVE</c>.</summary>
    MssqlBackup,
}
