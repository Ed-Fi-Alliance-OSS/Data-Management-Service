// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Model;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal static class PostgresqlDescriptorReadTestSupport
{
    public static async Task<long> InsertDocumentAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid,
        short resourceKeyId
    )
    {
        return await database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
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
        var documentId = await InsertDocumentAsync(database, seed.DocumentUuid, resourceKeyId);

        await InsertDescriptorRowAsync(database, resource, documentId, seed);

        return documentId;
    }

    public static async Task InsertDescriptorRowAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        QualifiedResourceName resource,
        long documentId,
        DescriptorReadSeed seed
    )
    {
        var discriminator = seed.Discriminator ?? resource.ResourceName;

        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Descriptor" (
                "DocumentId",
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

    public static async Task<IReadOnlyDictionary<string, object?>> ReadDocumentRowAsync(
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
            FROM "dms"."Document"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return GetSingleRowOrThrow(rows, "Document", documentId);
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
