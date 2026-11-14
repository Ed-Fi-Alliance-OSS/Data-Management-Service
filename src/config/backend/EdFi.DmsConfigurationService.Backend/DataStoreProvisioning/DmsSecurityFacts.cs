// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// One row of the delivered PostgreSQL ownership query (<c>Get-PostgresqlTemplateOwnershipQuerySql</c>
/// in its full scope): an object the connected administrator does not own. <see cref="Owner"/> is the plain
/// role name; <see cref="Identity"/> is the <c>%I</c>-quoted object identity.
/// </summary>
public sealed record DmsPostgresqlOwnershipRow(string Kind, string Owner, string Identity);

/// <summary>
/// One row of the delivered PostgreSQL privilege query (<c>Get-PostgresqlPrivilegeQuerySql</c>): a
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
/// One <c>permission</c> row of the delivered SQL Server security-state query
/// (<c>Get-MssqlSecurityStateQuerySql</c>): a row of <c>sys.database_permissions</c>, built-in principals
/// included. Judgment uses the numeric fields, <see cref="PermissionName"/>, and <see cref="StateDesc"/>;
/// <see cref="Grantee"/> and <see cref="Securable"/> only describe a row.
/// </summary>
public sealed record DmsMssqlPermissionRow(
    int Class,
    string PermissionName,
    string StateDesc,
    int GranteeId,
    int MajorId,
    int MinorId,
    string Grantee,
    string Securable
);

/// <summary>
/// One <c>member</c> row of the delivered SQL Server security-state query: a row of
/// <c>sys.database_role_members</c>. Judgment uses the principal ids; the names only describe a row.
/// </summary>
public sealed record DmsMssqlRoleMemberRow(int RoleId, int MemberId, string Role, string Member);

/// <summary>
/// The engine's security rows for the security check of target validation, judged by the provider-neutral rule sets in
/// <see cref="DmsTargetValidator"/>.
/// </summary>
public abstract record DmsSecurityFacts
{
    /// <summary>PostgreSQL ownership and privilege rows (D-15), and the server's superuser role names.</summary>
    public sealed record Postgresql(
        IReadOnlyList<DmsPostgresqlOwnershipRow> Ownership,
        IReadOnlyList<DmsPostgresqlPrivilegeRow> Privileges
    ) : DmsSecurityFacts
    {
        /// <summary>
        /// The roles the server reports as superusers when the rows were read. Reconciliation accepts an ordinary
        /// object owned by one of them, because a target created under an earlier superuser login keeps that owner
        /// after the login is switched (<see cref="DmsTargetValidator"/>). Empty unless the engine read it, which
        /// trusts no owner beyond the delivered rules.
        /// </summary>
        public IReadOnlyList<string> SuperuserRoles { get; init; } = [];
    }

    /// <summary>SQL Server database permissions and role memberships, the security state a backup carries and a RESTORE re-creates (D-15).</summary>
    public sealed record Mssql(
        IReadOnlyList<DmsMssqlPermissionRow> Permissions,
        IReadOnlyList<DmsMssqlRoleMemberRow> RoleMembers
    ) : DmsSecurityFacts;
}
