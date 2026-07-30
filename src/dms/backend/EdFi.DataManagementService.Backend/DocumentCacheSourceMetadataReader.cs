// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal interface IDocumentCacheSourceMetadataReader
{
    Task<DocumentCacheSourceMetadataReadResult> ReadAsync(
        DocumentCacheMaterializationRequest request,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheCurrentSourceMetadataReadResult> ReadCurrentAsync(
        DocumentCacheMaterializationRequest request,
        CancellationToken cancellationToken = default
    );
}

internal abstract record DocumentCacheSourceMetadataReadResult
{
    private DocumentCacheSourceMetadataReadResult() { }

    public sealed record Found(DocumentCacheResolvedSourceMetadata Metadata)
        : DocumentCacheSourceMetadataReadResult
    {
        public DocumentCacheResolvedSourceMetadata Metadata { get; } =
            Metadata ?? throw new ArgumentNullException(nameof(Metadata));
    }

    public sealed record MissingSource : DocumentCacheSourceMetadataReadResult
    {
        private MissingSource() { }

        public static MissingSource Instance { get; } = new();
    }
}

internal abstract record DocumentCacheCurrentSourceMetadataReadResult
{
    private DocumentCacheCurrentSourceMetadataReadResult() { }

    public sealed record Found(DocumentCacheCurrentSourceMetadata Metadata)
        : DocumentCacheCurrentSourceMetadataReadResult
    {
        public DocumentCacheCurrentSourceMetadata Metadata { get; } =
            Metadata ?? throw new ArgumentNullException(nameof(Metadata));
    }

    public sealed record MissingSource : DocumentCacheCurrentSourceMetadataReadResult
    {
        private MissingSource() { }

        public static MissingSource Instance { get; } = new();
    }
}

internal sealed record DocumentCacheCurrentSourceMetadata(
    long DocumentId,
    DocumentUuid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion,
    DateTimeOffset ContentLastModifiedAt
)
{
    public long DocumentId { get; } = RequirePositive(DocumentId, nameof(DocumentId));

    public long ContentVersion { get; } = RequirePositive(ContentVersion, nameof(ContentVersion));

    private static long RequirePositive(long value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"{parameterName} must be positive.");
        }

        return value;
    }
}

internal abstract record DocumentCacheResolvedSourceMetadata
{
    private DocumentCacheResolvedSourceMetadata(
        long documentId,
        DocumentUuid documentUuid,
        short resourceKeyId,
        ResourceKeyEntry resourceKey,
        ConcreteResourceModel concreteResourceModel,
        long contentVersion,
        DateTimeOffset contentLastModifiedAt
    )
    {
        DocumentId = RequirePositive(documentId, nameof(documentId));
        DocumentUuid = documentUuid;
        ResourceKeyId = resourceKeyId;
        ResourceKey = resourceKey ?? throw new ArgumentNullException(nameof(resourceKey));
        ProjectName = ResourceKey.Resource.ProjectName;
        ResourceName = ResourceKey.Resource.ResourceName;
        ResourceVersion = ResourceKey.ResourceVersion;
        ConcreteResourceModel =
            concreteResourceModel ?? throw new ArgumentNullException(nameof(concreteResourceModel));
        ContentVersion = RequirePositive(contentVersion, nameof(contentVersion));
        ContentLastModifiedAt = contentLastModifiedAt;
    }

    public long DocumentId { get; }

    public DocumentUuid DocumentUuid { get; }

    public short ResourceKeyId { get; }

    public ResourceKeyEntry ResourceKey { get; }

    public string ProjectName { get; }

    public string ResourceName { get; }

    public string ResourceVersion { get; }

    public ConcreteResourceModel ConcreteResourceModel { get; }

    public long ContentVersion { get; }

    public DateTimeOffset ContentLastModifiedAt { get; }

    public bool HasSameCanonicalMetadata(DocumentCacheCurrentSourceMetadata current) =>
        current.DocumentId == DocumentId
        && current.DocumentUuid == DocumentUuid
        && current.ResourceKeyId == ResourceKeyId
        && current.ContentVersion == ContentVersion
        && current.ContentLastModifiedAt == ContentLastModifiedAt;

    public sealed record OrdinaryResource : DocumentCacheResolvedSourceMetadata
    {
        public OrdinaryResource(
            long documentId,
            DocumentUuid documentUuid,
            short resourceKeyId,
            ResourceKeyEntry resourceKey,
            ConcreteResourceModel concreteResourceModel,
            long contentVersion,
            DateTimeOffset contentLastModifiedAt,
            ResourceReadPlan readPlan
        )
            : base(
                documentId,
                documentUuid,
                resourceKeyId,
                resourceKey,
                concreteResourceModel,
                contentVersion,
                contentLastModifiedAt
            )
        {
            ReadPlan = readPlan ?? throw new ArgumentNullException(nameof(readPlan));
        }

        public ResourceReadPlan ReadPlan { get; }
    }

    public sealed record DescriptorResource(
        long DocumentId,
        DocumentUuid DocumentUuid,
        short ResourceKeyId,
        ResourceKeyEntry ResourceKey,
        ConcreteResourceModel ConcreteResourceModel,
        long ContentVersion,
        DateTimeOffset ContentLastModifiedAt
    )
        : DocumentCacheResolvedSourceMetadata(
            DocumentId,
            DocumentUuid,
            ResourceKeyId,
            ResourceKey,
            ConcreteResourceModel,
            ContentVersion,
            ContentLastModifiedAt
        );

    private static long RequirePositive(long value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"{parameterName} must be positive.");
        }

        return value;
    }
}

internal sealed class DocumentCacheSourceMetadataReader(
    IDocumentCacheMaterializationDataStore materializationDataStore
) : IDocumentCacheSourceMetadataReader
{
    private const string DocumentIdParameterName = "@documentId";

    private readonly IDocumentCacheMaterializationDataStore _materializationDataStore =
        materializationDataStore ?? throw new ArgumentNullException(nameof(materializationDataStore));

    public async Task<DocumentCacheSourceMetadataReadResult> ReadAsync(
        DocumentCacheMaterializationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var sourceReadResult = await ReadCurrentAsync(request, cancellationToken).ConfigureAwait(false);

        return sourceReadResult switch
        {
            DocumentCacheCurrentSourceMetadataReadResult.MissingSource =>
                DocumentCacheSourceMetadataReadResult.MissingSource.Instance,
            DocumentCacheCurrentSourceMetadataReadResult.Found found =>
                new DocumentCacheSourceMetadataReadResult.Found(ResolveMetadata(request, found.Metadata)),
            _ => throw new InvalidOperationException(
                $"DocumentCache current source metadata reader returned unsupported result type '{sourceReadResult.GetType().Name}'."
            ),
        };
    }

    public async Task<DocumentCacheCurrentSourceMetadataReadResult> ReadCurrentAsync(
        DocumentCacheMaterializationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = await _materializationDataStore
            .ExecuteReaderAsync(
                request,
                BuildReadCommand(request.TargetContext.MappingSet.Key.Dialect, request.DocumentId),
                ReadSingleOrDefaultAsync,
                cancellationToken
            )
            .ConfigureAwait(false);

        return source is null
            ? DocumentCacheCurrentSourceMetadataReadResult.MissingSource.Instance
            : new DocumentCacheCurrentSourceMetadataReadResult.Found(source);
    }

    private static async Task<DocumentCacheCurrentSourceMetadata?> ReadSingleOrDefaultAsync(
        IRelationalCommandReader reader,
        CancellationToken cancellationToken
    )
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var row = new DocumentCacheCurrentSourceMetadata(
            reader.GetRequiredFieldValue<long>("DocumentId"),
            new DocumentUuid(reader.GetRequiredFieldValue<Guid>("DocumentUuid")),
            reader.GetRequiredFieldValue<short>("ResourceKeyId"),
            reader.GetRequiredFieldValue<long>("ContentVersion"),
            ReadDateTimeOffsetField(reader, "ContentLastModifiedAt")
        );

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("DocumentCache source metadata read returned multiple rows.");
        }

        return row;
    }

    private static DateTimeOffset ReadDateTimeOffsetField(IRelationalCommandReader reader, string columnName)
    {
        var value = reader.GetRequiredFieldValue<object>(columnName);

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
                $"DocumentCache source metadata expected a DateTimeOffset-compatible value for dms.Document.{columnName}, "
                    + $"but received '{value.GetType().Name}'."
            ),
        };
    }

    private static DocumentCacheResolvedSourceMetadata ResolveMetadata(
        DocumentCacheMaterializationRequest request,
        DocumentCacheCurrentSourceMetadata source
    )
    {
        var targetContext = request.TargetContext;
        var mappingSet = targetContext.MappingSet;

        if (!mappingSet.ResourceKeyById.TryGetValue(source.ResourceKeyId, out var resourceKey))
        {
            throw BuildTargetMappingException(
                request,
                source,
                DocumentCacheTargetMappingFailureReason.ResourceKeyMissingFromMappingSet
            );
        }

        if (!mappingSet.TryGetConcreteResourceModel(resourceKey.Resource, out var concreteResourceModel))
        {
            throw BuildTargetMappingException(
                request,
                source,
                DocumentCacheTargetMappingFailureReason.ConcreteResourceModelMissing,
                resourceKey
            );
        }

        if (
            concreteResourceModel.ResourceKey != resourceKey
            || concreteResourceModel.RelationalModel.Resource != resourceKey.Resource
            || concreteResourceModel.RelationalModel.StorageKind != concreteResourceModel.StorageKind
        )
        {
            throw BuildTargetMappingException(
                request,
                source,
                DocumentCacheTargetMappingFailureReason.ConcreteResourceModelMismatch,
                resourceKey
            );
        }

        return concreteResourceModel.StorageKind switch
        {
            ResourceStorageKind.RelationalTables => ResolveOrdinaryResourceMetadata(
                request,
                source,
                resourceKey,
                concreteResourceModel
            ),
            ResourceStorageKind.SharedDescriptorTable =>
                new DocumentCacheResolvedSourceMetadata.DescriptorResource(
                    source.DocumentId,
                    source.DocumentUuid,
                    source.ResourceKeyId,
                    resourceKey,
                    concreteResourceModel,
                    source.ContentVersion,
                    source.ContentLastModifiedAt
                ),
            _ => throw BuildTargetMappingException(
                request,
                source,
                DocumentCacheTargetMappingFailureReason.UnsupportedResourceStorageKind,
                resourceKey
            ),
        };
    }

    private static DocumentCacheResolvedSourceMetadata.OrdinaryResource ResolveOrdinaryResourceMetadata(
        DocumentCacheMaterializationRequest request,
        DocumentCacheCurrentSourceMetadata source,
        ResourceKeyEntry resourceKey,
        ConcreteResourceModel concreteResourceModel
    )
    {
        var mappingSet = request.TargetContext.MappingSet;

        if (!mappingSet.ReadPlansByResource.TryGetValue(resourceKey.Resource, out var readPlan))
        {
            throw BuildTargetMappingException(
                request,
                source,
                DocumentCacheTargetMappingFailureReason.ReadPlanMissing,
                resourceKey
            );
        }

        return new DocumentCacheResolvedSourceMetadata.OrdinaryResource(
            source.DocumentId,
            source.DocumentUuid,
            source.ResourceKeyId,
            resourceKey,
            concreteResourceModel,
            source.ContentVersion,
            source.ContentLastModifiedAt,
            readPlan
        );
    }

    private static DocumentCacheTargetMappingException BuildTargetMappingException(
        DocumentCacheMaterializationRequest request,
        DocumentCacheCurrentSourceMetadata source,
        DocumentCacheTargetMappingFailureReason reason,
        ResourceKeyEntry? resourceKey = null
    )
    {
        var metadata = new DocumentCacheMaterializerFailureMetadata(
            request.TargetContext.TargetKey,
            request.TargetContext.MappingSet.Key,
            request.Purpose,
            request.DocumentId
        )
        {
            SelectedRequiredContentVersion = request.SelectedRequiredContentVersion,
            ResourceKeyId = source.ResourceKeyId,
            ProjectName = resourceKey?.Resource.ProjectName,
            ResourceName = resourceKey?.Resource.ResourceName,
            ResourceVersion = resourceKey?.ResourceVersion,
        };

        return new DocumentCacheTargetMappingException(reason, metadata);
    }

    private static RelationalCommand BuildReadCommand(SqlDialect dialect, long documentId) =>
        dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                SELECT
                    document."DocumentId" AS "DocumentId",
                    document."DocumentUuid" AS "DocumentUuid",
                    document."ResourceKeyId" AS "ResourceKeyId",
                    document."ContentVersion" AS "ContentVersion",
                    document."ContentLastModifiedAt" AS "ContentLastModifiedAt"
                FROM dms."Document" document
                WHERE document."DocumentId" = @documentId;
                """,
                [new RelationalParameter(DocumentIdParameterName, documentId)]
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                SELECT
                    document.[DocumentId] AS [DocumentId],
                    document.[DocumentUuid] AS [DocumentUuid],
                    document.[ResourceKeyId] AS [ResourceKeyId],
                    document.[ContentVersion] AS [ContentVersion],
                    document.[ContentLastModifiedAt] AS [ContentLastModifiedAt]
                FROM [dms].[Document] document
                WHERE document.[DocumentId] = @documentId;
                """,
                [new RelationalParameter(DocumentIdParameterName, documentId)]
            ),
            _ => throw new NotSupportedException(
                $"DocumentCache source metadata reader does not support SQL dialect '{dialect}'."
            ),
        };
}
