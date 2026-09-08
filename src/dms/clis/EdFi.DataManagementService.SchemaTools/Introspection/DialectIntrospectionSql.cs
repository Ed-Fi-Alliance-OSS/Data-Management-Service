// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.SchemaTools.Introspection;

/// <summary>
/// Holds the dialect-specific SQL strings used by <see cref="SchemaIntrospectorBase"/>
/// to query database catalog metadata. Each query aliases columns to shared names
/// (e.g., <c>schema_name</c>, <c>table_name</c>) so the base class reader logic is
/// dialect-agnostic.
/// </summary>
/// <remarks>
/// Mirrors the <see cref="Provisioning.DialectSql"/> pattern from
/// <see cref="Provisioning.DatabaseProvisionerBase"/>.
/// Catalog queries (<c>SchemasSql</c> through <c>TableTypeColumnsSql</c>) accept a
/// schema allowlist filter via <c>@schemas</c> parameter or an <c>IN (...)</c> clause
/// injected by the base class. Seed-data queries (<c>EffectiveSchemaSql</c>,
/// <c>SchemaComponentsSql</c>, <c>ResourceKeysSql</c>) query <c>dms.*</c> tables
/// directly without filtering.
/// </remarks>
public sealed record DialectIntrospectionSql(
    string SchemasSql,
    string TablesSql,
    string ColumnsSql,
    string ConstraintsSql,
    string ConstraintColumnsSql,
    string IndexesSql,
    string IndexColumnsSql,
    string ViewsSql,
    string TriggersSql,
    string SequencesSql,
    string FunctionsSql,
    string FunctionParametersSql,
    string TableTypesSql,
    string TableTypeColumnsSql,
    string EffectiveSchemaSql,
    string SchemaComponentsSql,
    string ResourceKeysSql
);
