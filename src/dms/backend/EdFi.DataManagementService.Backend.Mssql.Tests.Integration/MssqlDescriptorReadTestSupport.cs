// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal static class MssqlDescriptorReadTestSupport
{
    public static async Task<long> InsertDocumentAsync(
        MssqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid,
        short resourceKeyId
    )
    {
        return await database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([DocumentId] bigint);
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT INSERTED.[DocumentId] INTO @Inserted ([DocumentId])
            VALUES (@documentUuid, @resourceKeyId);
            SELECT TOP (1) [DocumentId] FROM @Inserted;
            """,
            new SqlParameter("@documentUuid", documentUuid.Value),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );
    }

    public static async Task<long> SeedDescriptorAsync(
        MssqlGeneratedDdlTestDatabase database,
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
        MssqlGeneratedDdlTestDatabase database,
        QualifiedResourceName resource,
        long documentId,
        DescriptorReadSeed seed
    )
    {
        var discriminator = seed.Discriminator ?? resource.ResourceName;

        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO [dms].[Descriptor] (
                [DocumentId],
                [Namespace],
                [CodeValue],
                [ShortDescription],
                [Description],
                [EffectiveBeginDate],
                [EffectiveEndDate],
                [Discriminator],
                [Uri]
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
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@namespace", seed.Namespace),
            new SqlParameter("@codeValue", seed.CodeValue),
            new SqlParameter("@shortDescription", seed.ShortDescription),
            new SqlParameter("@description", (object?)seed.Description ?? DBNull.Value),
            new SqlParameter(
                "@effectiveBeginDate",
                seed.EffectiveBeginDate is not null
                    ? seed.EffectiveBeginDate.Value.ToDateTime(TimeOnly.MinValue)
                    : DBNull.Value
            ),
            new SqlParameter(
                "@effectiveEndDate",
                seed.EffectiveEndDate is not null
                    ? seed.EffectiveEndDate.Value.ToDateTime(TimeOnly.MinValue)
                    : DBNull.Value
            ),
            new SqlParameter("@discriminator", discriminator),
            new SqlParameter("@uri", seed.Uri)
        );
    }

    public static async Task<IReadOnlyDictionary<string, object?>> ReadDocumentRowAsync(
        MssqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                [DocumentId],
                [DocumentUuid],
                [ResourceKeyId],
                [ContentVersion],
                [IdentityVersion],
                [ContentLastModifiedAt],
                [IdentityLastModifiedAt],
                [CreatedAt]
            FROM [dms].[Document]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );

        return GetSingleRowOrThrow(rows, "Document", documentId);
    }

    public static async Task<IReadOnlyDictionary<string, object?>> ReadDescriptorRowAsync(
        MssqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                [DocumentId],
                [Namespace],
                [CodeValue],
                [ShortDescription],
                [Description],
                [EffectiveBeginDate],
                [EffectiveEndDate],
                [Discriminator],
                [Uri]
            FROM [dms].[Descriptor]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );

        return GetSingleRowOrThrow(rows, "Descriptor", documentId);
    }

    public static async Task<bool> DescriptorRowExistsAsync(
        MssqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        return await database.ExecuteScalarAsync<bool>(
            """
            SELECT CAST(
                CASE
                    WHEN EXISTS (
                        SELECT 1
                        FROM [dms].[Descriptor]
                        WHERE [DocumentId] = @documentId
                    )
                    THEN 1
                    ELSE 0
                END AS bit
            );
            """,
            new SqlParameter("@documentId", documentId)
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
