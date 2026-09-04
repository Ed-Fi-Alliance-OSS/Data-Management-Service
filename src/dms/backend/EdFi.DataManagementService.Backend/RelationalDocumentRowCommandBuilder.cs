// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
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
    /// <param name="createdByOwnershipTokenId">
    /// The API client's <c>CreatorOwnershipTokenId</c>, or <see langword="null"/> when the client has none.
    /// </param>
    /// <remarks>
    /// <para>
    /// <c>CreatedByOwnershipTokenId</c> is stamped on every create, whether or not the resource is configured
    /// with <c>OwnershipBased</c>. That is what lets a claim set later enforce ownership over data written
    /// before it was configured, so the column list must not become conditional on configured strategies.
    /// </para>
    /// <para>
    /// The column and its parameter are emitted even when the value is null, so each dialect has exactly one
    /// statement text. Omitting the column for a null token would double the statement-text cardinality for
    /// no benefit and cost plan reuse on both engines.
    /// </para>
    /// </remarks>
    public static RelationalCommand BuildInsertCommand(
        SqlDialect dialect,
        DocumentUuid documentUuid,
        short resourceKeyId,
        short? createdByOwnershipTokenId
    )
    {
        return dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId", "CreatedByOwnershipTokenId")
                VALUES (@documentUuid, @resourceKeyId, @createdByOwnershipTokenId)
                RETURNING "DocumentId";
                """,
                [
                    new RelationalParameter("@documentUuid", documentUuid.Value),
                    new RelationalParameter("@resourceKeyId", resourceKeyId),
                    BuildCreatedByOwnershipTokenIdParameter(createdByOwnershipTokenId),
                ]
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId], [CreatedByOwnershipTokenId])
                VALUES (@documentUuid, @resourceKeyId, @createdByOwnershipTokenId);
                SELECT SCOPE_IDENTITY();
                """,
                [
                    new RelationalParameter("@documentUuid", documentUuid.Value),
                    new RelationalParameter("@resourceKeyId", resourceKeyId),
                    BuildCreatedByOwnershipTokenIdParameter(createdByOwnershipTokenId),
                ]
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
    }

    /// <summary>
    /// The <c>CreatedByOwnershipTokenId</c> parameter, typed explicitly as <see cref="DbType.Int16"/>.
    /// </summary>
    /// <remarks>
    /// The type is declared rather than left to provider inference because the value is nullable and reaches
    /// the driver as <c>DBNull</c>, which carries no type of its own. Both providers would otherwise have to
    /// infer a type for a null — PostgreSQL from the insert target, SQL Server by defaulting to a string type
    /// and relying on an implicit conversion. Declaring <c>smallint</c> makes the null and non-null cases bind
    /// identically on both engines. <c>ConfigureParameter</c> survives composite statement rewriting, so the
    /// co-batched write path binds it the same way this one does.
    /// </remarks>
    private static RelationalParameter BuildCreatedByOwnershipTokenIdParameter(
        short? createdByOwnershipTokenId
    ) =>
        new(
            "@createdByOwnershipTokenId",
            createdByOwnershipTokenId,
            static parameter => parameter.DbType = DbType.Int16
        );

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
