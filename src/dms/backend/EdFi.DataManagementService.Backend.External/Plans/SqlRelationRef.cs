// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External.Plans;

/// <summary>
/// Dialect-neutral relation reference for SQL emitters.
/// </summary>
public abstract record SqlRelationRef
{
    /// <summary>
    /// Physical table relation (always schema-qualified in emitted SQL).
    /// </summary>
    /// <param name="Table">Physical table name.</param>
    public sealed record PhysicalTable(DbTableName Table) : SqlRelationRef;

    /// <summary>
    /// Temporary relation (never schema-qualified in emitted SQL).
    /// </summary>
    /// <param name="Name">Temporary table name (for example, <c>page</c> or <c>#page</c>).</param>
    public sealed record TempTable(string Name) : SqlRelationRef;
}
