// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

internal interface IDocumentCacheDescriptorHydrator
{
    Task<DocumentCacheDescriptorHydrationResult> HydrateAsync(
        DocumentCacheResolvedSourceMetadata.DescriptorResource source,
        SqlDialect dialect,
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

    public sealed record MissingSource : DocumentCacheDescriptorHydrationResult
    {
        private MissingSource() { }

        public static MissingSource Instance { get; } = new();
    }

    public sealed record SourceChanged : DocumentCacheDescriptorHydrationResult
    {
        private SourceChanged() { }

        public static SourceChanged Instance { get; } = new();
    }

    public sealed record StableDescriptorBodyMissing : DocumentCacheDescriptorHydrationResult
    {
        private StableDescriptorBodyMissing() { }

        public static StableDescriptorBodyMissing Instance { get; } = new();
    }
}

internal sealed class DocumentCacheDescriptorHydrator(IRelationalCommandExecutor commandExecutor)
    : IDocumentCacheDescriptorHydrator
{
    private const string DocumentIdParameterName = "@documentId";
    private const string ResourceKeyIdParameterName = "@resourceKeyId";
    private const string DescriptorDocumentIdColumnName = "DescriptorDocumentId";
    private const string DescriptorResourceKeyIdColumnName = "DescriptorResourceKeyId";

    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

    public Task<DocumentCacheDescriptorHydrationResult> HydrateAsync(
        DocumentCacheResolvedSourceMetadata.DescriptorResource source,
        SqlDialect dialect,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);

        return _commandExecutor.ExecuteReaderAsync(
            BuildReadCommand(dialect, source.DocumentId, source.ResourceKeyId),
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
            return DocumentCacheDescriptorHydrationResult.MissingSource.Instance;
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
        var documentId = reader.GetRequiredFieldValue<long>("DocumentId");
        var documentUuid = reader.GetRequiredFieldValue<Guid>("DocumentUuid");
        var contentVersion = reader.GetRequiredFieldValue<long>("ContentVersion");
        var contentLastModifiedAt = ReadRequiredDateTimeOffsetFieldValue(
            reader,
            "ContentLastModifiedAt",
            documentId,
            source.ResourceKeyId
        );
        var resourceKeyId = reader.GetRequiredFieldValue<short>("ResourceKeyId");

        if (
            documentId != source.DocumentId
            || documentUuid != source.DocumentUuid.Value
            || contentVersion != source.ContentVersion
            || contentLastModifiedAt != source.ContentLastModifiedAt
            || resourceKeyId != source.ResourceKeyId
        )
        {
            return DocumentCacheDescriptorHydrationResult.SourceChanged.Instance;
        }

        if (reader.IsDBNull(reader.GetOrdinal(DescriptorDocumentIdColumnName)))
        {
            return DocumentCacheDescriptorHydrationResult.StableDescriptorBodyMissing.Instance;
        }

        var descriptorDocumentId = reader.GetRequiredFieldValue<long>(DescriptorDocumentIdColumnName);
        var descriptorResourceKeyId = reader.GetRequiredFieldValue<short>(DescriptorResourceKeyIdColumnName);
        if (descriptorDocumentId != source.DocumentId || descriptorResourceKeyId != source.ResourceKeyId)
        {
            return DocumentCacheDescriptorHydrationResult.StableDescriptorBodyMissing.Instance;
        }

        var namespaceValue = ReadRequiredDescriptorStringField(reader, "Namespace");
        var codeValue = ReadRequiredDescriptorStringField(reader, "CodeValue");
        var shortDescription = ReadRequiredDescriptorStringField(reader, "ShortDescription");

        if (namespaceValue is null || codeValue is null || shortDescription is null)
        {
            return DocumentCacheDescriptorHydrationResult.StableDescriptorBodyMissing.Instance;
        }

        return new DocumentCacheDescriptorHydrationResult.Found(
            new DescriptorReadRow(
                documentId,
                documentUuid,
                contentVersion,
                contentLastModifiedAt,
                resourceKeyId,
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

    private static DateTimeOffset ReadRequiredDateTimeOffsetFieldValue(
        IRelationalCommandReader reader,
        string columnName,
        long documentId,
        short resourceKeyId
    )
    {
        var ordinal = reader.GetOrdinal(columnName);

        if (reader.IsDBNull(ordinal))
        {
            throw new DescriptorReadInvariantException(
                $"Descriptor read corruption detected for DocumentId {documentId} (ResourceKeyId={resourceKeyId}): "
                    + $"dms.Document.{columnName} must not be null."
            );
        }

        var value = reader.GetFieldValue<object>(ordinal);

        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(
                dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime
            ),
            string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                $"Descriptor read expected a DateTimeOffset-compatible value for dms.Document.{columnName}, "
                    + $"but received '{value.GetType().Name}'."
            ),
        };
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
                    document."DocumentId" AS "DocumentId",
                    document."DocumentUuid" AS "DocumentUuid",
                    document."ContentVersion" AS "ContentVersion",
                    document."ContentLastModifiedAt" AS "ContentLastModifiedAt",
                    document."ResourceKeyId" AS "ResourceKeyId",
                    descriptor."DocumentId" AS "DescriptorDocumentId",
                    descriptor."ResourceKeyId" AS "DescriptorResourceKeyId",
                    descriptor."Namespace" AS "Namespace",
                    descriptor."CodeValue" AS "CodeValue",
                    descriptor."ShortDescription" AS "ShortDescription",
                    descriptor."Description" AS "Description",
                    descriptor."EffectiveBeginDate" AS "EffectiveBeginDate",
                    descriptor."EffectiveEndDate" AS "EffectiveEndDate",
                    descriptor."Discriminator" AS "Discriminator"
                FROM dms."Document" document
                LEFT JOIN dms."Descriptor" descriptor
                    ON descriptor."DocumentId" = document."DocumentId"
                    AND descriptor."ResourceKeyId" = @resourceKeyId
                WHERE document."DocumentId" = @documentId;
                """,
                parameters
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                SELECT
                    document.[DocumentId] AS [DocumentId],
                    document.[DocumentUuid] AS [DocumentUuid],
                    document.[ContentVersion] AS [ContentVersion],
                    document.[ContentLastModifiedAt] AS [ContentLastModifiedAt],
                    document.[ResourceKeyId] AS [ResourceKeyId],
                    descriptor.[DocumentId] AS [DescriptorDocumentId],
                    descriptor.[ResourceKeyId] AS [DescriptorResourceKeyId],
                    descriptor.[Namespace] AS [Namespace],
                    descriptor.[CodeValue] AS [CodeValue],
                    descriptor.[ShortDescription] AS [ShortDescription],
                    descriptor.[Description] AS [Description],
                    descriptor.[EffectiveBeginDate] AS [EffectiveBeginDate],
                    descriptor.[EffectiveEndDate] AS [EffectiveEndDate],
                    descriptor.[Discriminator] AS [Discriminator]
                FROM [dms].[Document] document
                LEFT JOIN [dms].[Descriptor] descriptor
                    ON descriptor.[DocumentId] = document.[DocumentId]
                    AND descriptor.[ResourceKeyId] = @resourceKeyId
                WHERE document.[DocumentId] = @documentId;
                """,
                parameters
            ),
            _ => throw new NotSupportedException(
                $"DocumentCache descriptor hydration does not support SQL dialect '{dialect}'."
            ),
        };
    }
}
