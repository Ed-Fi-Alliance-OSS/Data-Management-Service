// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Builds the <c>dms.Document</c> row statements a write needs, and the expression by which a later
/// statement of the same command consumes the identity the insert generated.
/// </summary>
/// <remarks>
/// <c>dms.Document.DocumentId</c> is an identity column, so unlike <c>CollectionItemId</c> it cannot be
/// reserved ahead of the insert without a DDL change. Within one command the value is produced and
/// consumed server-side: a later statement re-derives it from the unique <c>DocumentUuid</c>. A scalar
/// subquery rather than a common table expression is what makes that portable — a CTE belongs to the
/// statement that declares it and cannot be referenced from a following statement's <c>VALUES</c> list,
/// while a scalar subquery compiles in every position a bind marker occupied on both dialects.
/// </remarks>
internal static class RelationalDocumentRowCommandBuilder
{
    public static RelationalCommand BuildInsertCommand(
        SqlDialect dialect,
        DocumentUuid documentUuid,
        short resourceKeyId
    )
    {
        return dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId")
                VALUES (@documentUuid, @resourceKeyId)
                RETURNING "DocumentId";
                """,
                [
                    new RelationalParameter("@documentUuid", documentUuid.Value),
                    new RelationalParameter("@resourceKeyId", resourceKeyId),
                ]
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
                VALUES (@documentUuid, @resourceKeyId);
                SELECT SCOPE_IDENTITY();
                """,
                [
                    new RelationalParameter("@documentUuid", documentUuid.Value),
                    new RelationalParameter("@resourceKeyId", resourceKeyId),
                ]
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
    }

    /// <summary>
    /// The scalar subquery yielding the <c>DocumentId</c> of the row whose <c>DocumentUuid</c> is bound to
    /// <paramref name="documentUuidParameterName"/>. Emitted wherever a bind marker for the root document
    /// id stood.
    /// </summary>
    public static string BuildDocumentIdSubquery(SqlDialect dialect, string documentUuidParameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentUuidParameterName);

        return dialect switch
        {
            SqlDialect.Pgsql =>
                $"""(SELECT "DocumentId" FROM dms."Document" WHERE "DocumentUuid" = {documentUuidParameterName})""",
            SqlDialect.Mssql =>
                $"(SELECT [DocumentId] FROM [dms].[Document] WHERE [DocumentUuid] = {documentUuidParameterName})",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
    }
}
