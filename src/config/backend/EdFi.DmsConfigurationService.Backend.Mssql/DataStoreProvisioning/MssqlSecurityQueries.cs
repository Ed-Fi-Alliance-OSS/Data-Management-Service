// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// The delivered SQL Server security-state read, text for text: <c>Get-MssqlSecurityStateQuerySql</c>,
/// every row of <c>sys.database_permissions</c> and <c>sys.database_role_members</c>, built-in principals
/// included. Its rows are split here as <c>Assert-MssqlArtifactSecurityState</c> splits them: a member row
/// into at most five fields, a permission row into at most nine (the last field, a name, may contain the
/// separator), and the numeric fields must parse as integers.
/// </summary>
internal static class MssqlSecurityQueries
{
    private const string MemberPrefix = "member|";
    private const string PermissionKind = "permission";

    /// <summary>
    /// <c>permission|class|permission_name|state_desc|grantee_id|major_id|minor_id|grantee|securable</c> and
    /// <c>member|role_id|member_id|role|member</c> rows.
    /// </summary>
    public const string SecurityState =
        "SET NOCOUNT ON;\n"
        + "SELECT CONCAT(N'permission|', p.class, N'|', p.permission_name COLLATE DATABASE_DEFAULT, N'|', p.state_desc COLLATE DATABASE_DEFAULT, N'|',\n"
        + "  p.grantee_principal_id, N'|', p.major_id, N'|', p.minor_id, N'|', COALESCE(g.name COLLATE DATABASE_DEFAULT, N'?'), N'|',\n"
        + "  CASE p.class WHEN 0 THEN N'DATABASE'\n"
        + "    WHEN 1 THEN CONCAT(N'OBJECT::', OBJECT_SCHEMA_NAME(p.major_id) COLLATE DATABASE_DEFAULT, N'.', OBJECT_NAME(p.major_id) COLLATE DATABASE_DEFAULT,\n"
        + "      CASE WHEN p.minor_id <> 0 THEN N'.' + COL_NAME(p.major_id, p.minor_id) COLLATE DATABASE_DEFAULT END)\n"
        + "    WHEN 3 THEN CONCAT(N'SCHEMA::', SCHEMA_NAME(p.major_id) COLLATE DATABASE_DEFAULT)\n"
        + "    ELSE CONCAT(p.class_desc COLLATE DATABASE_DEFAULT, N'::', p.major_id, N'.', p.minor_id) END)\n"
        + "FROM sys.database_permissions p LEFT JOIN sys.database_principals g ON g.principal_id = p.grantee_principal_id\n"
        + "UNION ALL\n"
        + "SELECT CONCAT(N'member|', rm.role_principal_id, N'|', rm.member_principal_id, N'|', COALESCE(r.name COLLATE DATABASE_DEFAULT, N'?'), N'|', COALESCE(m.name COLLATE DATABASE_DEFAULT, N'?'))\n"
        + "FROM sys.database_role_members rm LEFT JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id\n"
        + "  LEFT JOIN sys.database_principals m ON m.principal_id = rm.member_principal_id\n"
        + "ORDER BY 1;";

    /// <summary>
    /// Splits the rows into permissions and memberships. False when any non-blank row is malformed, as the
    /// delivered gate refuses it; blank rows are skipped as it skips them.
    /// </summary>
    public static bool TryParseRows(
        IEnumerable<string?> rows,
        out List<DmsMssqlPermissionRow> permissions,
        out List<DmsMssqlRoleMemberRow> members
    )
    {
        ArgumentNullException.ThrowIfNull(rows);
        permissions = [];
        members = [];
        foreach (string? raw in rows)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string row = raw.Trim();
            if (row.StartsWith(MemberPrefix, StringComparison.Ordinal))
            {
                if (!TryParseMember(row, out DmsMssqlRoleMemberRow? member))
                {
                    return false;
                }

                members.Add(member);
                continue;
            }

            if (!TryParsePermission(row, out DmsMssqlPermissionRow? permission))
            {
                return false;
            }

            permissions.Add(permission);
        }

        return true;
    }

    private static bool TryParseMember(string row, [NotNullWhen(true)] out DmsMssqlRoleMemberRow? member)
    {
        member = null;
        string[] fields = row.Split('|', 5);
        if (
            fields.Length != 5
            || !TryParseInt(fields[1], out int roleId)
            || !TryParseInt(fields[2], out int memberId)
        )
        {
            return false;
        }

        member = new DmsMssqlRoleMemberRow(roleId, memberId, fields[3], fields[4]);
        return true;
    }

    private static bool TryParsePermission(
        string row,
        [NotNullWhen(true)] out DmsMssqlPermissionRow? permission
    )
    {
        permission = null;
        string[] fields = row.Split('|', 9);
        if (
            fields.Length != 9
            || !string.Equals(fields[0], PermissionKind, StringComparison.Ordinal)
            || !TryParseInt(fields[1], out int securableClass)
            || !TryParseInt(fields[4], out int granteeId)
            || !TryParseInt(fields[5], out int majorId)
            || !TryParseInt(fields[6], out int minorId)
        )
        {
            return false;
        }

        permission = new DmsMssqlPermissionRow(
            securableClass,
            fields[2],
            fields[3],
            granteeId,
            majorId,
            minorId,
            fields[7],
            fields[8]
        );
        return true;
    }

    /// <summary>PowerShell's <c>[int]::TryParse</c> on these fields: an optionally signed integer, surrounding white space allowed.</summary>
    private static bool TryParseInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
