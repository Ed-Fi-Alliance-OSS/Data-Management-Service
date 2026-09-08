// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.SchemaTools.Introspection;

/// <summary>
/// Top-level manifest containing all database structural objects from a provisioned database.
/// Arrays are sorted by natural key for deterministic output.
/// </summary>
public sealed record ProvisionedSchemaManifest(
    string ManifestVersion,
    string Dialect,
    IReadOnlyList<SchemaEntry> Schemas,
    IReadOnlyList<TableEntry> Tables,
    IReadOnlyList<ColumnEntry> Columns,
    IReadOnlyList<ConstraintEntry> Constraints,
    IReadOnlyList<IndexEntry> Indexes,
    IReadOnlyList<ViewEntry> Views,
    IReadOnlyList<TriggerEntry> Triggers,
    IReadOnlyList<SequenceEntry> Sequences,
    IReadOnlyList<TableTypeEntry> TableTypes,
    IReadOnlyList<FunctionEntry> Functions,
    SeedData SeedData
);

public sealed record SchemaEntry(string SchemaName);

public sealed record TableEntry(string SchemaName, string TableName);

public sealed record ColumnEntry(
    string SchemaName,
    string TableName,
    string ColumnName,
    int OrdinalPosition,
    string DataType,
    bool IsNullable,
    string? DefaultExpression,
    bool IsComputed
);

public sealed record ConstraintEntry(
    string SchemaName,
    string TableName,
    string ConstraintName,
    string ConstraintType,
    IReadOnlyList<string> Columns,
    string? ReferencedSchema,
    string? ReferencedTable,
    IReadOnlyList<string>? ReferencedColumns
);

public sealed record IndexEntry(
    string SchemaName,
    string TableName,
    string IndexName,
    bool IsUnique,
    IReadOnlyList<string> Columns
);

public sealed record ViewEntry(string SchemaName, string ViewName, string Definition);

public sealed record TriggerEntry(
    string SchemaName,
    string TableName,
    string TriggerName,
    string EventManipulation,
    string ActionTiming,
    string Definition,
    string? FunctionName
);

public sealed record SequenceEntry(
    string SchemaName,
    string SequenceName,
    string DataType,
    long StartValue,
    long IncrementBy
);

public sealed record TableTypeColumnEntry(
    string ColumnName,
    int OrdinalPosition,
    string DataType,
    bool IsNullable
);

public sealed record TableTypeEntry(
    string SchemaName,
    string TableTypeName,
    IReadOnlyList<TableTypeColumnEntry> Columns
);

public sealed record FunctionEntry(
    string SchemaName,
    string FunctionName,
    string ReturnType,
    IReadOnlyList<string> ParameterTypes,
    string Definition
);

public sealed record SeedData(
    EffectiveSchemaEntry EffectiveSchema,
    IReadOnlyList<SchemaComponentEntry> SchemaComponents,
    IReadOnlyList<ResourceKeyEntry> ResourceKeys
);

public sealed record EffectiveSchemaEntry(
    short EffectiveSchemaSingletonId,
    string ApiSchemaFormatVersion,
    string EffectiveSchemaHash,
    short ResourceKeyCount,
    string ResourceKeySeedHash
);

public sealed record SchemaComponentEntry(
    string EffectiveSchemaHash,
    string ProjectEndpointName,
    string ProjectName,
    string ProjectVersion,
    bool IsExtensionProject
);

public sealed record ResourceKeyEntry(
    short ResourceKeyId,
    string ProjectName,
    string ResourceName,
    string ResourceVersion
);
