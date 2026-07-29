// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Shared PostgreSQL predicates and diagnostics for safely creating or reusing the
/// security-definer enqueue owner role.
/// </summary>
public static class PgsqlEnqueueOwnerPrerequisiteSql
{
    public const string RoleName = "edfi_dms_enqueue_owner";
    public const string DirectMembershipOptions = "SET TRUE, INHERIT FALSE, ADMIN FALSE";

    public const string CreateRoleCapabilityDiagnostic =
        "PostgreSQL provisioning principal must be SUPERUSER or CREATEROLE to create edfi_dms_enqueue_owner before provisioning.";

    public const string LockedDownRoleDiagnostic =
        "PostgreSQL role edfi_dms_enqueue_owner exists but is not locked down as NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS. Drop or repair the role before provisioning.";

    public const string OutgoingMembershipPreflightDiagnostic =
        "PostgreSQL role edfi_dms_enqueue_owner must not hold outgoing privilege-bearing memberships before provisioning.";

    public const string OutgoingMembershipSecurityDiagnostic =
        "PostgreSQL role edfi_dms_enqueue_owner must not hold outgoing privilege-bearing memberships.";

    public const string UnsafeDirectMembershipDiagnostic =
        "PostgreSQL provisioning principal has an unsafe direct membership in edfi_dms_enqueue_owner; required options are SET TRUE, INHERIT FALSE, ADMIN FALSE.";

    public const string MissingRequiredDirectMembershipDiagnostic =
        "PostgreSQL provisioning principal must have direct SET TRUE, INHERIT FALSE, ADMIN FALSE membership in existing edfi_dms_enqueue_owner before provisioning.";

    /// <summary>
    /// Gets the read-only provider prerequisite query used by <c>ddl provision</c>.
    /// </summary>
    public static string ProviderPrerequisiteSql
    {
        get
        {
            var writer = new SqlWriter(SqlDialectFactory.Create(SqlDialect.Pgsql));
            EmitProviderPrerequisiteSql(writer);
            return writer.ToString();
        }
    }

    internal static string UnsafeRoleAttributePredicate(string roleAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleAlias);

        return $"{roleAlias}.rolcanlogin OR {roleAlias}.rolinherit OR {roleAlias}.rolsuper OR {roleAlias}.rolcreatedb OR {roleAlias}.rolcreaterole OR {roleAlias}.rolreplication OR {roleAlias}.rolbypassrls";
    }

    internal static string OutgoingPrivilegeBearingMembershipPredicate(string membershipAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(membershipAlias);

        return $"({membershipAlias}.admin_option OR {membershipAlias}.inherit_option OR {membershipAlias}.set_option)";
    }

    internal static string CreateroleAdminOnlyBootstrapMembershipPredicate(
        string membershipAlias,
        string sessionCanCreateRoleExpression
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(membershipAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionCanCreateRoleExpression);

        return $"({membershipAlias}.admin_option AND NOT {membershipAlias}.inherit_option AND NOT {membershipAlias}.set_option AND {sessionCanCreateRoleExpression})";
    }

    internal static string UnsafeDirectMembershipOptionsPredicate(string membershipAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(membershipAlias);

        return $"({membershipAlias}.admin_option OR {membershipAlias}.inherit_option OR NOT {membershipAlias}.set_option)";
    }

    internal static void EmitOutgoingPrivilegeBearingMembershipSelect(
        SqlWriter writer,
        string memberExpression
    )
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberExpression);

        writer.AppendLine("SELECT 1");
        writer.AppendLine("FROM pg_catalog.pg_auth_members membership");
        writer.AppendLine($"WHERE membership.member = {memberExpression}");
        writer.AppendLine($"AND {OutgoingPrivilegeBearingMembershipPredicate("membership")}");
    }

    internal static void EmitUnsafeDirectMembershipSelect(
        SqlWriter writer,
        string ownerRoleExpression,
        string sessionRoleExpression,
        string sessionCanCreateRoleExpression
    )
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerRoleExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoleExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionCanCreateRoleExpression);

        writer.AppendLine("SELECT 1");
        writer.AppendLine("FROM pg_catalog.pg_auth_members membership");
        writer.AppendLine($"WHERE membership.roleid = {ownerRoleExpression}");
        writer.AppendLine($"AND membership.member = {sessionRoleExpression}");
        writer.AppendLine(
            $"AND NOT {CreateroleAdminOnlyBootstrapMembershipPredicate("membership", sessionCanCreateRoleExpression)}"
        );
        writer.AppendLine($"AND {UnsafeDirectMembershipOptionsPredicate("membership")}");
    }

    internal static void EmitRequiredDirectMembershipSelect(
        SqlWriter writer,
        string ownerRoleExpression,
        string sessionRoleExpression
    )
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerRoleExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoleExpression);

        writer.AppendLine("SELECT 1");
        writer.AppendLine("FROM pg_catalog.pg_auth_members membership");
        writer.AppendLine($"WHERE membership.roleid = {ownerRoleExpression}");
        writer.AppendLine($"AND membership.member = {sessionRoleExpression}");
        writer.AppendLine("AND NOT membership.admin_option");
        writer.AppendLine("AND NOT membership.inherit_option");
        writer.AppendLine("AND membership.set_option");
    }

    private static void EmitProviderPrerequisiteSql(SqlWriter writer)
    {
        writer.AppendLine("WITH owner_role AS (");
        using (writer.Indent())
        {
            writer.AppendLine($"SELECT pg_catalog.to_regrole('{RoleName}') AS oid");
        }
        writer.AppendLine("),");
        writer.AppendLine("session_role AS (");
        using (writer.Indent())
        {
            writer.AppendLine("SELECT oid, rolsuper, rolcreaterole");
            writer.AppendLine("FROM pg_catalog.pg_roles");
            writer.AppendLine("WHERE rolname = SESSION_USER");
        }
        writer.AppendLine(")");
        writer.AppendLine($"SELECT '{CreateRoleCapabilityDiagnostic}'");
        writer.AppendLine("FROM owner_role, session_role");
        writer.AppendLine("WHERE owner_role.oid IS NULL");
        writer.AppendLine("AND NOT (session_role.rolsuper OR session_role.rolcreaterole)");
        writer.AppendLine("UNION ALL");
        writer.AppendLine($"SELECT '{LockedDownRoleDiagnostic}'");
        writer.AppendLine("FROM owner_role");
        writer.AppendLine(
            "INNER JOIN pg_catalog.pg_roles owner_role_attributes ON owner_role_attributes.oid = owner_role.oid"
        );
        writer.AppendLine("WHERE owner_role.oid IS NOT NULL");
        writer.AppendLine($"AND ({UnsafeRoleAttributePredicate("owner_role_attributes")})");
        writer.AppendLine("UNION ALL");
        writer.AppendLine($"SELECT '{OutgoingMembershipPreflightDiagnostic}'");
        writer.AppendLine("FROM owner_role");
        writer.AppendLine("WHERE owner_role.oid IS NOT NULL");
        writer.AppendLine("AND EXISTS (");
        using (writer.Indent())
        {
            EmitOutgoingPrivilegeBearingMembershipSelect(writer, "owner_role.oid");
        }
        writer.AppendLine(")");
        writer.AppendLine("UNION ALL");
        writer.AppendLine($"SELECT '{UnsafeDirectMembershipDiagnostic}'");
        writer.AppendLine("FROM owner_role, session_role");
        writer.AppendLine("WHERE owner_role.oid IS NOT NULL");
        writer.AppendLine("AND EXISTS (");
        using (writer.Indent())
        {
            EmitUnsafeDirectMembershipSelect(
                writer,
                "owner_role.oid",
                "session_role.oid",
                "session_role.rolcreaterole"
            );
        }
        writer.AppendLine(")");
        writer.AppendLine("UNION ALL");
        writer.AppendLine($"SELECT '{MissingRequiredDirectMembershipDiagnostic}'");
        writer.AppendLine("FROM owner_role, session_role");
        writer.AppendLine("WHERE owner_role.oid IS NOT NULL");
        writer.AppendLine("AND NOT session_role.rolsuper");
        writer.AppendLine("AND NOT EXISTS (");
        using (writer.Indent())
        {
            EmitRequiredDirectMembershipSelect(writer, "owner_role.oid", "session_role.oid");
        }
        writer.AppendLine(")");
    }
}
