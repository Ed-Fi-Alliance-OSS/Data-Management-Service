// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// One row of the delivered PostgreSQL ownership query (F-P9b, <c>Get-PostgresqlTemplateOwnershipQuerySql</c>
/// in its full scope): an object the connected administrator does not own. <see cref="Owner"/> is the plain
/// role name; <see cref="Identity"/> is the <c>%I</c>-quoted object identity.
/// </summary>
public sealed record DmsPostgresqlOwnershipRow(string Kind, string Owner, string Identity);

/// <summary>
/// One row of the delivered PostgreSQL privilege query (F-P9b, <c>Get-PostgresqlPrivilegeQuerySql</c>): a
/// privilege held on a schema, relation, column, function, or standalone type outside the system schemas,
/// with the engine's built-in defaults expanded. <see cref="Owner"/> and <see cref="Grantee"/> are
/// <c>%I</c>-quoted role names; <see cref="Grantee"/> is <c>-</c> for PUBLIC.
/// </summary>
public sealed record DmsPostgresqlPrivilegeRow(
    string Kind,
    string Privilege,
    string Owner,
    string Grantee,
    string Identity
);

/// <summary>
/// The engine's security rows for 6.9c item 7, judged by the provider-neutral rule sets in
/// <see cref="DmsTargetValidator"/>. The SQL Server case (security state, F-P9b) arrives with its engine
/// (Step 4.1).
/// </summary>
public abstract record DmsSecurityFacts
{
    /// <summary>PostgreSQL ownership and privilege rows (D-15).</summary>
    public sealed record Postgresql(
        IReadOnlyList<DmsPostgresqlOwnershipRow> Ownership,
        IReadOnlyList<DmsPostgresqlPrivilegeRow> Privileges
    ) : DmsSecurityFacts;
}
