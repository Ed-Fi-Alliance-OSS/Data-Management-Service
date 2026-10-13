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

    public sealed record OwnerMissing(DocumentCacheCurrentSourceMetadata Metadata)
        : DocumentCacheSourceMetadataReadResult
    {
        public DocumentCacheCurrentSourceMetadata Metadata { get; } =
            Metadata ?? throw new ArgumentNullException(nameof(Metadata));
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
    DateTimeOffset? ContentLastModifiedAt
)
{
    public long DocumentId { get; } =
        DocumentCacheMaterializerGuards.RequirePositive(DocumentId, nameof(DocumentId));

    public long ContentVersion { get; } =
        DocumentCacheMaterializerGuards.RequirePositive(ContentVersion, nameof(ContentVersion));

    public bool HasSameDocumentMetadata(DocumentCacheCurrentSourceMetadata current) =>
        current.DocumentId == DocumentId
        && current.DocumentUuid == DocumentUuid
        && current.ResourceKeyId == ResourceKeyId
        && current.ContentVersion == ContentVersion;
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
        DocumentId = DocumentCacheMaterializerGuards.RequirePositive(documentId, nameof(documentId));
        DocumentUuid = documentUuid;
        ResourceKeyId = resourceKeyId;
        ResourceKey = resourceKey ?? throw new ArgumentNullException(nameof(resourceKey));
        ProjectName = ResourceKey.Resource.ProjectName;
        ResourceName = ResourceKey.Resource.ResourceName;
        ResourceVersion = ResourceKey.ResourceVersion;
        ConcreteResourceModel =
            concreteResourceModel ?? throw new ArgumentNullException(nameof(concreteResourceModel));
        ContentVersion = DocumentCacheMaterializerGuards.RequirePositive(
            contentVersion,
            nameof(contentVersion)
        );
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
        HasSameDocumentMetadata(current) && current.ContentLastModifiedAt == ContentLastModifiedAt;

    public bool HasSameDocumentMetadata(DocumentCacheCurrentSourceMetadata current) =>
        current.DocumentId == DocumentId
        && current.DocumentUuid == DocumentUuid
        && current.ResourceKeyId == ResourceKeyId
        && current.ContentVersion == ContentVersion;

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

        var source = await ReadDocumentAsync(request, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return DocumentCacheSourceMetadataReadResult.MissingSource.Instance;
        }

        return await ReadResolvedMetadataAsync(request, source, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DocumentCacheCurrentSourceMetadataReadResult> ReadCurrentAsync(
        DocumentCacheMaterializationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = await ReadDocumentAsync(request, cancellationToken).ConfigureAwait(false);

        if (source is null)
        {
            return DocumentCacheCurrentSourceMetadataReadResult.MissingSource.Instance;
        }

        var timestamp = await ReadOwnerTimestampAsync(request, source, cancellationToken)
            .ConfigureAwait(false);
        if (timestamp is null)
        {
            // A canonical delete may have committed after the document read but before the owner read.
            // Refresh document metadata so coherence checks distinguish deletion or drift from an orphan.
            var current = await ReadDocumentAsync(request, cancellationToken).ConfigureAwait(false);
            return current is null
                ? DocumentCacheCurrentSourceMetadataReadResult.MissingSource.Instance
                : new DocumentCacheCurrentSourceMetadataReadResult.Found(current);
        }

        return new DocumentCacheCurrentSourceMetadataReadResult.Found(
            source with
            {
                ContentLastModifiedAt = timestamp,
            }
        );
    }

    private Task<DocumentCacheCurrentSourceMetadata?> ReadDocumentAsync(
        DocumentCacheMaterializationRequest request,
        CancellationToken cancellationToken
    ) =>
        _materializationDataStore.ExecuteReaderAsync(
            request,
            BuildDocumentReadCommand(request.TargetContext.MappingSet.Key.Dialect, request.DocumentId),
            ReadSingleOrDefaultAsync,
            cancellationToken
        );

    private async Task<DateTimeOffset?> ReadOwnerTimestampAsync(
        DocumentCacheMaterializationRequest request,
        DocumentCacheCurrentSourceMetadata source,
        CancellationToken cancellationToken
    )
    {
        var mappingSet = request.TargetContext.MappingSet;
        if (
            !mappingSet.ResourceKeyById.TryGetValue(source.ResourceKeyId, out var resourceKey)
            || !mappingSet.TryGetConcreteResourceModel(resourceKey.Resource, out var concreteResourceModel)
            || concreteResourceModel.ResourceKey != resourceKey
            || concreteResourceModel.RelationalModel.Resource != resourceKey.Resource
            || concreteResourceModel.RelationalModel.StorageKind != concreteResourceModel.StorageKind
        )
        {
            return null;
        }

        DbTableName? ownerTable = concreteResourceModel.StorageKind switch
        {
            ResourceStorageKind.RelationalTables => concreteResourceModel.RelationalModel.Root.Table,
            ResourceStorageKind.SharedDescriptorTable => new DbTableName(
                new DbSchemaName("dms"),
                "Descriptor"
            ),
            _ => null,
        };

        if (ownerTable is null)
        {
            return null;
        }

        return await ReadOwnerTimestampAsync(request, source.DocumentId, ownerTable.Value, cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<DateTimeOffset?> ReadOwnerTimestampAsync(
        DocumentCacheMaterializationRequest request,
        long documentId,
        DbTableName ownerTable,
        CancellationToken cancellationToken
    ) =>
        _materializationDataStore.ExecuteReaderAsync(
            request,
            BuildOwnerReadCommand(request.TargetContext.MappingSet.Key.Dialect, ownerTable, documentId),
            ReadOwnerTimestampRowAsync,
            cancellationToken
        );

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
            null
        );

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("DocumentCache source metadata read returned multiple rows.");
        }

        return row;
    }

    private static async Task<DateTimeOffset?> ReadOwnerTimestampRowAsync(
        IRelationalCommandReader reader,
        CancellationToken cancellationToken
    )
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var timestamp = ReadDateTimeOffsetField(reader, "ContentLastModifiedAt");
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("DocumentCache owner timestamp read returned multiple rows.");
        }

        return timestamp;
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
                $"DocumentCache source metadata expected a DateTimeOffset-compatible value for owner.{columnName}, "
                    + $"but received '{value.GetType().Name}'."
            ),
        };
    }

    private async Task<DocumentCacheSourceMetadataReadResult> ReadResolvedMetadataAsync(
        DocumentCacheMaterializationRequest request,
        DocumentCacheCurrentSourceMetadata source,
        CancellationToken cancellationToken
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

        if (resourceKey.ResourceKeyId != source.ResourceKeyId)
        {
            throw BuildTargetMappingException(
                request,
                source,
                DocumentCacheTargetMappingFailureReason.ResourceKeyMetadataMismatch,
                resourceKey
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

        switch (concreteResourceModel.StorageKind)
        {
            case ResourceStorageKind.RelationalTables:
                return await ReadOrdinaryResourceMetadataAsync(
                        request,
                        source,
                        resourceKey,
                        concreteResourceModel,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            case ResourceStorageKind.SharedDescriptorTable:
                var timestamp = await ReadOwnerTimestampAsync(
                        request,
                        source.DocumentId,
                        new DbTableName(new DbSchemaName("dms"), "Descriptor"),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return timestamp is { } descriptorTimestamp
                    ? new DocumentCacheSourceMetadataReadResult.Found(
                        new DocumentCacheResolvedSourceMetadata.DescriptorResource(
                            source.DocumentId,
                            source.DocumentUuid,
                            source.ResourceKeyId,
                            resourceKey,
                            concreteResourceModel,
                            source.ContentVersion,
                            descriptorTimestamp
                        )
                    )
                    : new DocumentCacheSourceMetadataReadResult.OwnerMissing(source);
            default:
                throw BuildTargetMappingException(
                    request,
                    source,
                    DocumentCacheTargetMappingFailureReason.UnsupportedResourceStorageKind,
                    resourceKey
                );
        }
    }

    private async Task<DocumentCacheSourceMetadataReadResult> ReadOrdinaryResourceMetadataAsync(
        DocumentCacheMaterializationRequest request,
        DocumentCacheCurrentSourceMetadata source,
        ResourceKeyEntry resourceKey,
        ConcreteResourceModel concreteResourceModel,
        CancellationToken cancellationToken
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

        if (!ReadPlanMatchesConcreteResourceModel(readPlan, concreteResourceModel))
        {
            throw BuildTargetMappingException(
                request,
                source,
                DocumentCacheTargetMappingFailureReason.ReadPlanMetadataMismatch,
                resourceKey
            );
        }

        var ownerTimestamp = await ReadOwnerTimestampAsync(
                request,
                source.DocumentId,
                concreteResourceModel.RelationalModel.Root.Table,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (ownerTimestamp is not { } timestamp)
        {
            return new DocumentCacheSourceMetadataReadResult.OwnerMissing(source);
        }

        return new DocumentCacheSourceMetadataReadResult.Found(
            new DocumentCacheResolvedSourceMetadata.OrdinaryResource(
                source.DocumentId,
                source.DocumentUuid,
                source.ResourceKeyId,
                resourceKey,
                concreteResourceModel,
                source.ContentVersion,
                timestamp,
                readPlan
            )
        );
    }

    private static bool ReadPlanMatchesConcreteResourceModel(
        ResourceReadPlan readPlan,
        ConcreteResourceModel concreteResourceModel
    )
    {
        var readPlanModel = readPlan.Model;
        var concreteRelationalModel = concreteResourceModel.RelationalModel;

        return readPlanModel.Resource == concreteRelationalModel.Resource
            && readPlanModel.PhysicalSchema == concreteRelationalModel.PhysicalSchema
            && readPlanModel.StorageKind == ResourceStorageKind.RelationalTables
            && readPlanModel.StorageKind == concreteResourceModel.StorageKind
            && TableShapeMatches(readPlanModel.Root, concreteRelationalModel.Root)
            && TableInventoryMatches(
                readPlanModel.TablesInDependencyOrder,
                concreteRelationalModel.TablesInDependencyOrder
            )
            && ReadPlanTableInventoryMatches(
                readPlan.TablePlansInDependencyOrder,
                concreteRelationalModel.TablesInDependencyOrder
            );
    }

    private static bool TableInventoryMatches(
        IReadOnlyList<DbTableModel> readPlanTables,
        IReadOnlyList<DbTableModel> concreteModelTables
    )
    {
        if (readPlanTables.Count != concreteModelTables.Count)
        {
            return false;
        }

        for (var index = 0; index < readPlanTables.Count; index++)
        {
            if (!TableShapeMatches(readPlanTables[index], concreteModelTables[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ReadPlanTableInventoryMatches(
        IReadOnlyList<TableReadPlan> tableReadPlans,
        IReadOnlyList<DbTableModel> concreteModelTables
    )
    {
        if (tableReadPlans.Count != concreteModelTables.Count)
        {
            return false;
        }

        for (var index = 0; index < tableReadPlans.Count; index++)
        {
            if (
                !TableShapeMatches(
                    tableReadPlans[index].TableModel,
                    ToHydrationProjectionTableModel(concreteModelTables[index])
                )
            )
            {
                return false;
            }
        }

        return true;
    }

    private static DbTableModel ToHydrationProjectionTableModel(DbTableModel tableModel)
    {
        if (tableModel.Columns.All(IsHydrationProjectionColumn))
        {
            return tableModel;
        }

        return tableModel with
        {
            Columns = [.. tableModel.Columns.Where(IsHydrationProjectionColumn)],
        };
    }

    private static bool IsHydrationProjectionColumn(DbColumnModel column) =>
        column.Kind
            is not (ColumnKind.MirroredContentVersion or ColumnKind.AuthoritativeContentLastModifiedAt);

    private static bool TableShapeMatches(DbTableModel readPlanTable, DbTableModel concreteModelTable) =>
        readPlanTable.Table == concreteModelTable.Table
        && readPlanTable.JsonScope.Canonical == concreteModelTable.JsonScope.Canonical
        && readPlanTable.IdentityMetadata.TableKind == concreteModelTable.IdentityMetadata.TableKind
        && ColumnInventoryMatches(readPlanTable.Columns, concreteModelTable.Columns);

    private static bool ColumnInventoryMatches(
        IReadOnlyList<DbColumnModel> readPlanColumns,
        IReadOnlyList<DbColumnModel> concreteModelColumns
    )
    {
        if (readPlanColumns.Count != concreteModelColumns.Count)
        {
            return false;
        }

        for (var index = 0; index < readPlanColumns.Count; index++)
        {
            if (!ColumnShapeMatches(readPlanColumns[index], concreteModelColumns[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ColumnShapeMatches(DbColumnModel readPlanColumn, DbColumnModel concreteModelColumn) =>
        readPlanColumn.ColumnName == concreteModelColumn.ColumnName
        && readPlanColumn.Kind == concreteModelColumn.Kind
        && readPlanColumn.ScalarType == concreteModelColumn.ScalarType
        && readPlanColumn.IsNullable == concreteModelColumn.IsNullable
        && readPlanColumn.SourceJsonPath?.Canonical == concreteModelColumn.SourceJsonPath?.Canonical
        && readPlanColumn.TargetResource == concreteModelColumn.TargetResource
        && readPlanColumn.Storage == concreteModelColumn.Storage
        && readPlanColumn.IsWritable == concreteModelColumn.IsWritable;

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

    private static RelationalCommand BuildDocumentReadCommand(SqlDialect dialect, long documentId) =>
        dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                SELECT
                    document."DocumentId" AS "DocumentId",
                    document."DocumentUuid" AS "DocumentUuid",
                    document."ResourceKeyId" AS "ResourceKeyId",
                    document."ContentVersion" AS "ContentVersion"
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
                    document.[ContentVersion] AS [ContentVersion]
                FROM [dms].[Document] document
                WHERE document.[DocumentId] = @documentId;
                """,
                [new RelationalParameter(DocumentIdParameterName, documentId)]
            ),
            _ => throw new NotSupportedException(
                $"DocumentCache source metadata reader does not support SQL dialect '{dialect}'."
            ),
        };

    private static RelationalCommand BuildOwnerReadCommand(
        SqlDialect dialect,
        DbTableName ownerTable,
        long documentId
    )
    {
        var table = SqlIdentifierQuoter.QuoteTableName(dialect, ownerTable);
        var timestamp = SqlIdentifierQuoter.QuoteIdentifier(
            dialect,
            new DbColumnName("ContentLastModifiedAt")
        );
        var documentIdColumn = SqlIdentifierQuoter.QuoteIdentifier(dialect, new DbColumnName("DocumentId"));

        return new RelationalCommand(
            $"SELECT owner.{timestamp} AS {timestamp} FROM {table} owner WHERE owner.{documentIdColumn} = @documentId;",
            [new RelationalParameter(DocumentIdParameterName, documentId)]
        );
    }
}
