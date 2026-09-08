// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

internal interface IDocumentCacheDescriptorHydrator
{
    Task<DocumentCacheDescriptorHydrationResult> HydrateAsync(
        DocumentCacheMaterializationRequest request,
        DocumentCacheResolvedSourceMetadata.DescriptorResource source,
        CancellationToken cancellationToken = default
    );
}

internal abstract record DocumentCacheDescriptorHydrationResult
{
    private DocumentCacheDescriptorHydrationResult() { }

    public sealed record Found(DescriptorReadRow DescriptorRow) : DocumentCacheDescriptorHydrationResult
    {
        public DescriptorReadRow DescriptorRow { get; } =
            DescriptorRow ?? throw new ArgumentNullException(nameof(DescriptorRow));
    }

    public sealed record StableDescriptorBodyMissing : DocumentCacheDescriptorHydrationResult
    {
        private StableDescriptorBodyMissing() { }

        public static StableDescriptorBodyMissing Instance { get; } = new();
    }
}

internal sealed class DocumentCacheDescriptorHydrator(
    IDocumentCacheMaterializationDataStore materializationDataStore
) : IDocumentCacheDescriptorHydrator
{
    private const string DocumentIdParameterName = "@documentId";
    private const string ResourceKeyIdParameterName = "@resourceKeyId";

    private readonly IDocumentCacheMaterializationDataStore _materializationDataStore =
        materializationDataStore ?? throw new ArgumentNullException(nameof(materializationDataStore));

    public Task<DocumentCacheDescriptorHydrationResult> HydrateAsync(
        DocumentCacheMaterializationRequest request,
        DocumentCacheResolvedSourceMetadata.DescriptorResource source,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);

        return _materializationDataStore.ExecuteReaderAsync(
            request,
            BuildReadCommand(
                request.TargetContext.MappingSet.Key.Dialect,
                source.DocumentId,
                source.ResourceKeyId
            ),
            (reader, ct) => ReadSingleOrDefaultAsync(reader, source, ct),
            cancellationToken
        );
    }

    private static async Task<DocumentCacheDescriptorHydrationResult> ReadSingleOrDefaultAsync(
        IRelationalCommandReader reader,
        DocumentCacheResolvedSourceMetadata.DescriptorResource source,
        CancellationToken cancellationToken
    )
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return DocumentCacheDescriptorHydrationResult.StableDescriptorBodyMissing.Instance;
        }

        var result = ReadCurrentRow(reader, source);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("DocumentCache descriptor hydration returned multiple rows.");
        }

        return result;
    }

    private static DocumentCacheDescriptorHydrationResult ReadCurrentRow(
        IRelationalCommandReader reader,
        DocumentCacheResolvedSourceMetadata.DescriptorResource source
    )
    {
        var namespaceValue = ReadRequiredDescriptorStringField(reader, "Namespace");
        var codeValue = ReadRequiredDescriptorStringField(reader, "CodeValue");
        var shortDescription = ReadRequiredDescriptorStringField(reader, "ShortDescription");

        if (namespaceValue is null || codeValue is null || shortDescription is null)
        {
            return DocumentCacheDescriptorHydrationResult.StableDescriptorBodyMissing.Instance;
        }

        return new DocumentCacheDescriptorHydrationResult.Found(
            new DescriptorReadRow(
                source.DocumentId,
                source.DocumentUuid.Value,
                source.ContentVersion,
                source.ContentLastModifiedAt,
                source.ResourceKeyId,
                namespaceValue,
                codeValue,
                shortDescription,
                reader.GetNullableFieldValue<string>("Description"),
                reader.GetNullableDateFieldValue("EffectiveBeginDate"),
                reader.GetNullableDateFieldValue("EffectiveEndDate"),
                ReadOptionalStringField(reader, "Discriminator")
            )
        );
    }

    private static string? ReadRequiredDescriptorStringField(
        IRelationalCommandReader reader,
        string columnName
    )
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<string>(ordinal);
    }

    private static string? ReadOptionalStringField(IRelationalCommandReader reader, string columnName)
    {
        int ordinal;

        try
        {
            ordinal = reader.GetOrdinal(columnName);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }

        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<string>(ordinal);
    }

    private static RelationalCommand BuildReadCommand(
        SqlDialect dialect,
        long documentId,
        short resourceKeyId
    )
    {
        IReadOnlyList<RelationalParameter> parameters =
        [
            new(DocumentIdParameterName, documentId),
            new(ResourceKeyIdParameterName, resourceKeyId),
        ];

        return dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                SELECT
                    descriptor."Namespace" AS "Namespace",
                    descriptor."CodeValue" AS "CodeValue",
                    descriptor."ShortDescription" AS "ShortDescription",
                    descriptor."Description" AS "Description",
                    descriptor."EffectiveBeginDate" AS "EffectiveBeginDate",
                    descriptor."EffectiveEndDate" AS "EffectiveEndDate",
                    descriptor."Discriminator" AS "Discriminator"
                FROM dms."Descriptor" descriptor
                WHERE descriptor."DocumentId" = @documentId
                    AND descriptor."ResourceKeyId" = @resourceKeyId;
                """,
                parameters
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                SELECT
                    descriptor.[Namespace] AS [Namespace],
                    descriptor.[CodeValue] AS [CodeValue],
                    descriptor.[ShortDescription] AS [ShortDescription],
                    descriptor.[Description] AS [Description],
                    descriptor.[EffectiveBeginDate] AS [EffectiveBeginDate],
                    descriptor.[EffectiveEndDate] AS [EffectiveEndDate],
                    descriptor.[Discriminator] AS [Discriminator]
                FROM [dms].[Descriptor] descriptor
                WHERE descriptor.[DocumentId] = @documentId
                    AND descriptor.[ResourceKeyId] = @resourceKeyId;
                """,
                parameters
            ),
            _ => throw new NotSupportedException(
                $"DocumentCache descriptor hydration does not support SQL dialect '{dialect}'."
            ),
        };
    }
}
