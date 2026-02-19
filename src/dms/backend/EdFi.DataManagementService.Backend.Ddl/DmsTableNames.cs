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
    public static readonly DbSchemaName DmsSchema = new("dms");

    public static readonly DbTableName Descriptor = new(DmsSchema, "Descriptor");
    public static readonly DbTableName Document = new(DmsSchema, "Document");
    public static readonly DbTableName DocumentCache = new(DmsSchema, "DocumentCache");
    public static readonly DbTableName DocumentChangeEvent = new(DmsSchema, "DocumentChangeEvent");
    public static readonly DbTableName EffectiveSchema = new(DmsSchema, "EffectiveSchema");
    public static readonly DbTableName ReferentialIdentity = new(DmsSchema, "ReferentialIdentity");
    public static readonly DbTableName ResourceKey = new(DmsSchema, "ResourceKey");
    public static readonly DbTableName SchemaComponent = new(DmsSchema, "SchemaComponent");

    public static readonly string ChangeVersionSequence = "ChangeVersionSequence";
}
