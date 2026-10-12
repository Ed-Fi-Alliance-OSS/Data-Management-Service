// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.SchemaTools.Provisioning;

/// <summary>
/// The PostgreSQL server or database does not meet the DMS platform floor. The message names both
/// requirements whichever one failed, followed by the server-reported value that failed. It never
/// carries a database name or connection string, so the managed command can print it verbatim.
/// </summary>
public sealed class PostgresqlPlatformCompatibilityException(string detected)
    : InvalidOperationException($"{RequirementMessage} Detected: {detected}.")
{
    public const string RequirementMessage =
        "DMS requires PostgreSQL 18 or later with a UTF-8 database encoding. "
        + "Upgrade the server to PostgreSQL 18 or later and provision into a UTF-8 database; "
        + "when SchemaTools creates the database, the server's template1 database must be UTF-8.";
}
