// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Shared table name constants for the <c>dms.*</c> schema, used by both
/// <see cref="CoreDdlEmitter"/> (DDL) and <see cref="SeedDmlEmitter"/> (DML).
/// </summary>
internal static class DmsTableNames
{
    private static readonly DbSchemaName _dmsSchema = new("dms");

    public static readonly DbTableName Document = new(_dmsSchema, "Document");
    public static readonly DbTableName EffectiveSchema = new(_dmsSchema, "EffectiveSchema");
    public static readonly DbTableName ReferentialIdentity = new(_dmsSchema, "ReferentialIdentity");
    public static readonly DbTableName ResourceKey = new(_dmsSchema, "ResourceKey");
    public static readonly DbTableName SchemaComponent = new(_dmsSchema, "SchemaComponent");

    public static readonly string ChangeVersionSequence = "ChangeVersionSequence";
}
