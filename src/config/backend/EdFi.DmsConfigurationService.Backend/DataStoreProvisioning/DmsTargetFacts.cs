// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>One <c>DocumentJson</c> column (incl. <c>DocumentCache</c>) and its engine column type (6.9c item 3).</summary>
public sealed record DmsDocumentJsonColumn(string SchemaName, string TableName, string ColumnType);

/// <summary>
/// One inventoried catalog object as the delivered inventory query reports it (F-P8,
/// <c>Get-InventoryObjectQuerySql</c>): its schema, its name (function names carry their identity-argument
/// signature; table-attached objects are qualified by their table), and its type tag.
/// </summary>
public sealed record DmsInventoryObject(string SchemaName, string Name, string Type);

/// <summary>
/// One user-created database-level object (F-P9, <c>Get-InventoryDatabaseObjectQuerySql</c>): state that
/// lives in the database but in no schema. The authoritative DDL creates none, so every row is a violation.
/// </summary>
public sealed record DmsDatabaseLevelObject(string Kind, string Name);

/// <summary>
/// The catalog facts the provider-neutral validator judges (6.9c): the effective-schema row, the engine
/// facts, the physical baseline, the full inventory enumeration (schemas, objects, principals, database-level
/// objects), the engine's security rows, and the Populated-kind data predicate. The engine reads them on an
/// instance-bound session; <see cref="DmsTargetValidator"/> compares them with the manifest and the
/// deployment expectations. Nothing here is a value taken from a request.
/// </summary>
public sealed record DmsTargetFacts
{
    /// <summary>The <c>dms.EffectiveSchema</c> row (item 1).</summary>
    public required DmsEffectiveSchemaRead EffectiveSchema { get; init; }

    /// <summary>The live engine major version (item 3).</summary>
    public required int EngineMajorVersion { get; init; }

    /// <summary>SQL Server <c>compatibility_level</c>; null on PostgreSQL (item 3).</summary>
    public required int? CompatibilityLevel { get; init; }

    /// <summary>Every <c>dms.*.DocumentJson</c> column and its type (item 3).</summary>
    public required IReadOnlyList<DmsDocumentJsonColumn> DocumentJsonColumns { get; init; }

    /// <summary>Every schema in the inventory-enumeration scope, including the always-present <c>public</c> or <c>dbo</c> (items 4 to 6).</summary>
    public required IReadOnlyList<string> SchemaNames { get; init; }

    /// <summary>Every inventoried object in those schemas (items 4 and 5).</summary>
    public required IReadOnlyList<DmsInventoryObject> InventoryObjects { get; init; }

    /// <summary>Non-built-in database principals; SQL Server only, always empty on PostgreSQL (items 4 and 5).</summary>
    public required IReadOnlyList<string> Principals { get; init; }

    /// <summary>User-created database-level objects (item 5).</summary>
    public required IReadOnlyList<DmsDatabaseLevelObject> DatabaseLevelObjects { get; init; }

    /// <summary>The engine's ownership, privilege, or security-state rows (item 7).</summary>
    public required DmsSecurityFacts Security { get; init; }

    /// <summary>
    /// Whether at least one non-descriptor, non-school-year document exists (item 8, the Populated
    /// predicate); null when <c>dms.Document</c> or <c>dms.ResourceKey</c> is missing.
    /// </summary>
    public required bool? HasPopulatedDocument { get; init; }
}
