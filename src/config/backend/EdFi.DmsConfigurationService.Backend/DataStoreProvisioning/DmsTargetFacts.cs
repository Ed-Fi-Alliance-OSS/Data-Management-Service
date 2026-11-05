// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>One <c>DocumentJson</c> column (incl. <c>DocumentCache</c>) and its engine column type (6.9c item 3).</summary>
public sealed record DmsDocumentJsonColumn(string SchemaName, string TableName, string ColumnType);

/// <summary>
/// The catalog facts the provider-neutral validator judges (6.9c items 1 to 3 and 6). Step 3.1 adds the
/// inventory, ownership, privilege, and security-state rows (items 4, 5, 7) when the validator lands.
/// </summary>
/// <param name="EffectiveSchema">The <c>dms.EffectiveSchema</c> row.</param>
/// <param name="EngineMajorVersion">The live engine major version.</param>
/// <param name="CompatibilityLevel">SQL Server <c>compatibility_level</c>; null on PostgreSQL.</param>
/// <param name="DocumentJsonColumns">Every <c>dms.*.DocumentJson</c> column and its type.</param>
/// <param name="SchemaNames">Every non-system schema name in the database.</param>
public sealed record DmsTargetFacts(
    DmsEffectiveSchemaRead EffectiveSchema,
    int EngineMajorVersion,
    int? CompatibilityLevel,
    IReadOnlyList<DmsDocumentJsonColumn> DocumentJsonColumns,
    IReadOnlyList<string> SchemaNames
);
