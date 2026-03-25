// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Shared table name constants for the <c>dms.*</c> schema, used by both
/// <see cref="CoreDdlEmitter"/> (DDL) and <see cref="SeedDmlEmitter"/> (DML).
/// <c>dms.EffectiveSchema</c> is defined in <see cref="EffectiveSchemaTableDefinition"/>
/// so runtime readers and DDL share one authoritative definition.
/// </summary>
internal static class DmsTableNames
{
    public static readonly DbSchemaName DmsSchema = EffectiveSchemaTableDefinition.Table.Schema;

    public static readonly DbTableName Descriptor = new(DmsSchema, "Descriptor");
    public static readonly DbTableName Document = new(DmsSchema, "Document");
    public static readonly DbTableName DocumentCache = new(DmsSchema, "DocumentCache");
    public static readonly DbTableName DocumentChangeEvent = new(DmsSchema, "DocumentChangeEvent");
    public static readonly DbTableName ReferentialIdentity = new(DmsSchema, "ReferentialIdentity");
    public static readonly DbTableName ResourceKey = new(DmsSchema, "ResourceKey");
    public static readonly DbTableName SchemaComponent = new(DmsSchema, "SchemaComponent");

    public const string ChangeVersionSequence = "ChangeVersionSequence";
    public const string CollectionItemIdSequence = "CollectionItemIdSequence";

    // User-Defined Table Types (SQL Server TVPs) for authorization query parameterization
    public const string BigIntTableType = "BigIntTable";
    public const string UniqueIdentifierTableType = "UniqueIdentifierTable";
}
