// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Model;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal static class PostgresqlDescriptorReadTestSupport
{
    /// <summary>
    /// Dispenses a DocumentId from the same sequence the descriptor row's column DEFAULT draws from,
    /// so a seeded id can never collide with one the write path produces.
    /// </summary>
    public static async Task<long> NextDocumentIdAsync(PostgresqlGeneratedDdlTestDatabase database)
    {
        return await database.ExecuteScalarAsync<long>(
            """
            SELECT nextval('"dms"."DocumentIdSequence"');
            """
        );
    }

    public static async Task<long> SeedDescriptorAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DescriptorReadSeed seed
    )
    {
        var resourceKeyId = DescriptorReadIntegrationTestSupport.GetDescriptorResourceKeyIdOrThrow(
            mappingSet,
            resource
        );
        var documentId = await NextDocumentIdAsync(database);

        await InsertDescriptorRowAsync(database, resource, documentId, resourceKeyId, seed);

        return documentId;
    }

    public static async Task InsertDescriptorRowAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        QualifiedResourceName resource,
        long documentId,
        short resourceKeyId,
        DescriptorReadSeed seed
    )
    {
        var discriminator = seed.Discriminator ?? resource.ResourceName;

        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Descriptor" (
                "DocumentId",
                "DocumentUuid",
                "ResourceKeyId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "EffectiveBeginDate",
                "EffectiveEndDate",
                "Discriminator",
                "Uri"
            )
            VALUES (
                @documentId,
                @documentUuid,
                @resourceKeyId,
                @namespace,
                @codeValue,
                @shortDescription,
                @description,
                @effectiveBeginDate,
                @effectiveEndDate,
                @discriminator,
                @uri
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("documentUuid", seed.DocumentUuid.Value),
            new NpgsqlParameter("resourceKeyId", resourceKeyId),
            new NpgsqlParameter("namespace", seed.Namespace),
            new NpgsqlParameter("codeValue", seed.CodeValue),
            new NpgsqlParameter("shortDescription", seed.ShortDescription),
            new NpgsqlParameter("description", (object?)seed.Description ?? DBNull.Value),
            new NpgsqlParameter(
                "effectiveBeginDate",
                seed.EffectiveBeginDate is not null
                    ? seed.EffectiveBeginDate.Value.ToDateTime(TimeOnly.MinValue)
                    : DBNull.Value
            ),
            new NpgsqlParameter(
                "effectiveEndDate",
                seed.EffectiveEndDate is not null
                    ? seed.EffectiveEndDate.Value.ToDateTime(TimeOnly.MinValue)
                    : DBNull.Value
            ),
            new NpgsqlParameter("discriminator", discriminator),
            new NpgsqlParameter("uri", seed.Uri)
        );
    }

    /// <summary>
    /// Reads the row that carries the document metadata for a descriptor. The descriptor row is the
    /// document, so this is the descriptor row.
    /// </summary>
    public static Task<IReadOnlyDictionary<string, object?>> ReadDocumentRowAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId
    ) => ReadDescriptorStampRowAsync(database, documentId);

    /// <summary>
    /// Reads the descriptor row's authoritative stamps. The descriptor row owns
    /// <c>DocumentUuid</c>, <c>ContentVersion</c>/<c>IdentityVersion</c> and their timestamps.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, object?>> ReadDescriptorStampRowAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                "DocumentId",
                "DocumentUuid",
                "ResourceKeyId",
                "ContentVersion",
                "IdentityVersion",
                "ContentLastModifiedAt",
                "IdentityLastModifiedAt",
                "CreatedAt"
            FROM "dms"."Descriptor"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return GetSingleRowOrThrow(rows, "Descriptor", documentId);
    }

    public static async Task<IReadOnlyDictionary<string, object?>> ReadDescriptorRowAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                "DocumentId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "EffectiveBeginDate",
                "EffectiveEndDate",
                "Discriminator",
                "Uri"
            FROM "dms"."Descriptor"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return GetSingleRowOrThrow(rows, "Descriptor", documentId);
    }

    public static async Task<bool> DescriptorRowExistsAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        return await database.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM "dms"."Descriptor"
                WHERE "DocumentId" = @documentId
            );
            """,
            new NpgsqlParameter("documentId", documentId)
        );
    }

    private static IReadOnlyDictionary<string, object?> GetSingleRowOrThrow(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        string tableName,
        long documentId
    )
    {
        return rows.Count switch
        {
            1 => rows[0],
            0 => throw new InvalidOperationException(
                $"Expected exactly one {tableName} row for DocumentId {documentId}, but found none."
            ),
            _ => throw new InvalidOperationException(
                $"Expected exactly one {tableName} row for DocumentId {documentId}, but found {rows.Count}."
            ),
        };
    }
}
