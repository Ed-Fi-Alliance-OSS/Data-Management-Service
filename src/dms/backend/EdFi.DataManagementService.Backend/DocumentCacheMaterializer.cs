// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

internal sealed class DocumentCacheMaterializer(
    IDocumentCacheSourceMetadataReader sourceMetadataReader,
    IDocumentHydrator documentHydrator,
    IRelationalReadMaterializer readMaterializer,
    IServedEtagComposer servedEtagComposer
) : IDocumentCacheMaterializer
{
    private readonly IDocumentCacheSourceMetadataReader _sourceMetadataReader =
        sourceMetadataReader ?? throw new ArgumentNullException(nameof(sourceMetadataReader));
    private readonly IDocumentHydrator _documentHydrator =
        documentHydrator ?? throw new ArgumentNullException(nameof(documentHydrator));
    private readonly IRelationalReadMaterializer _readMaterializer =
        readMaterializer ?? throw new ArgumentNullException(nameof(readMaterializer));
    private readonly IServedEtagComposer _servedEtagComposer =
        servedEtagComposer ?? throw new ArgumentNullException(nameof(servedEtagComposer));

    public async Task<DocumentCacheMaterializationResult> MaterializeAsync(
        DocumentCacheMaterializationRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var sourceReadResult = await _sourceMetadataReader
            .ReadAsync(request, request.CancellationToken)
            .ConfigureAwait(false);

        return sourceReadResult switch
        {
            DocumentCacheSourceMetadataReadResult.MissingSource => DocumentCacheMaterializationResult
                .MissingSource
                .Instance,
            DocumentCacheSourceMetadataReadResult.Found
            {
                Metadata: DocumentCacheResolvedSourceMetadata.OrdinaryResource ordinaryResource,
            } => await MaterializeOrdinaryResourceAsync(request, ordinaryResource).ConfigureAwait(false),
            DocumentCacheSourceMetadataReadResult.Found
            {
                Metadata: DocumentCacheResolvedSourceMetadata.DescriptorResource descriptorResource,
            } => throw BuildTargetMappingException(
                request,
                descriptorResource,
                DocumentCacheTargetMappingFailureReason.DescriptorMaterializationPathMissing
            ),
            DocumentCacheSourceMetadataReadResult.Found found => throw new InvalidOperationException(
                $"DocumentCache materializer received unsupported source metadata type '{found.Metadata.GetType().Name}'."
            ),
            _ => throw new InvalidOperationException(
                $"DocumentCache source metadata reader returned unsupported result type '{sourceReadResult.GetType().Name}'."
            ),
        };
    }

    private async Task<DocumentCacheMaterializationResult> MaterializeOrdinaryResourceAsync(
        DocumentCacheMaterializationRequest request,
        DocumentCacheResolvedSourceMetadata.OrdinaryResource source
    )
    {
        var hydratedPage = await _documentHydrator
            .HydrateAsync(
                source.ReadPlan,
                new PageKeysetSpec.Single(source.DocumentId),
                new HydrationExecutionOptions(UseSingleDocumentFastPath: true),
                request.CancellationToken
            )
            .ConfigureAwait(false);

        if (TryClassifyHydratedMetadata(source, hydratedPage) is { } nonSuccessResult)
        {
            return nonSuccessResult;
        }

        if (!HasRootBodyRow(source.ReadPlan, hydratedPage, source.DocumentId))
        {
            throw BuildProjectionProcessingException(
                request,
                source,
                DocumentCacheProjectionProcessingFailureReason.StableSourceBodyMissing
            );
        }

        var documentJsonNode = _readMaterializer.Materialize(
            new RelationalReadMaterializationRequest(
                source.ReadPlan,
                CreateDocumentMetadataRow(source),
                hydratedPage.TableRowsInDependencyOrder,
                hydratedPage.DescriptorRowsInPlanOrder,
                RelationalReadMaterializationMode.CacheProjection
            )
            {
                MappingSet = request.TargetContext.MappingSet,
                DocumentReferenceLookup = hydratedPage.DocumentReferenceLookup,
            }
        );

        if (documentJsonNode is not JsonObject documentJson)
        {
            throw BuildProjectionProcessingException(
                request,
                source,
                DocumentCacheProjectionProcessingFailureReason.DocumentJsonNotObject
            );
        }

        var streamEtag = DocumentCacheMaterializerStreamEtagComposer.ComposeForResource(
            _servedEtagComposer,
            request.TargetContext.MappingSet,
            source.ContentVersion
        );

        return new DocumentCacheMaterializationResult.Success(
            new DocumentCacheMaterializationCandidate(
                source.DocumentId,
                source.DocumentUuid,
                source.ProjectName,
                source.ResourceName,
                source.ResourceVersion,
                source.ContentVersion,
                source.ContentLastModifiedAt,
                streamEtag,
                documentJson
            )
        );
    }

    private static DocumentCacheMaterializationResult? TryClassifyHydratedMetadata(
        DocumentCacheResolvedSourceMetadata source,
        HydratedPage hydratedPage
    )
    {
        if (hydratedPage.DocumentMetadata.Count == 0)
        {
            return DocumentCacheMaterializationResult.SourceChangedDuringHydration.Instance;
        }

        if (hydratedPage.DocumentMetadata.Count != 1)
        {
            throw new InvalidOperationException(
                $"DocumentCache ordinary resource hydration for document id {source.DocumentId} returned "
                    + $"{hydratedPage.DocumentMetadata.Count} metadata rows, but exactly 1 was expected."
            );
        }

        var hydratedMetadata = hydratedPage.DocumentMetadata[0];

        if (
            hydratedMetadata.DocumentId != source.DocumentId
            || hydratedMetadata.DocumentUuid != source.DocumentUuid.Value
            || hydratedMetadata.ContentVersion != source.ContentVersion
            || hydratedMetadata.ContentLastModifiedAt != source.ContentLastModifiedAt
        )
        {
            return DocumentCacheMaterializationResult.SourceChangedDuringHydration.Instance;
        }

        return null;
    }

    private static bool HasRootBodyRow(ResourceReadPlan readPlan, HydratedPage hydratedPage, long documentId)
    {
        var rootTable = readPlan.Model.Root;
        var rootRows = hydratedPage.TableRowsInDependencyOrder.FirstOrDefault(rows =>
            rows.TableModel.Table == rootTable.Table
        );

        if (rootRows is null)
        {
            return false;
        }

        var documentIdColumn = ResolveRootDocumentIdColumn(rootTable);
        var documentIdOrdinal = FindColumnOrdinal(rootRows.TableModel, documentIdColumn);

        return rootRows.Rows.Any(row =>
        {
            if (documentIdOrdinal >= row.Length)
            {
                throw new InvalidOperationException(
                    $"DocumentCache ordinary resource hydration row for table '{rootRows.TableModel.Table}' "
                        + $"had {row.Length} values, but root DocumentId ordinal {documentIdOrdinal} was required."
                );
            }

            return TryReadInt64(row[documentIdOrdinal], out var rowDocumentId) && rowDocumentId == documentId;
        });
    }

    private static DbColumnName ResolveRootDocumentIdColumn(DbTableModel rootTable)
    {
        if (rootTable.IdentityMetadata.RootScopeLocatorColumns.Count == 1)
        {
            return rootTable.IdentityMetadata.RootScopeLocatorColumns[0];
        }

        var parentKeyColumns = rootTable
            .Columns.Where(column => column.Kind == ColumnKind.ParentKeyPart)
            .Select(column => column.ColumnName)
            .ToArray();

        if (parentKeyColumns.Length == 1)
        {
            return parentKeyColumns[0];
        }

        throw new InvalidOperationException(
            $"DocumentCache ordinary resource read plan root table '{rootTable.Table}' does not expose a single root DocumentId column."
        );
    }

    private static int FindColumnOrdinal(DbTableModel table, DbColumnName columnName)
    {
        for (var ordinal = 0; ordinal < table.Columns.Count; ordinal++)
        {
            if (table.Columns[ordinal].ColumnName == columnName)
            {
                return ordinal;
            }
        }

        throw new InvalidOperationException(
            $"DocumentCache ordinary resource read plan table '{table.Table}' does not contain root DocumentId column '{columnName.Value}'."
        );
    }

    private static bool TryReadInt64(object? value, out long result)
    {
        switch (value)
        {
            case long longValue:
                result = longValue;
                return true;
            case int intValue:
                result = intValue;
                return true;
            case short shortValue:
                result = shortValue;
                return true;
            case decimal decimalValue
                when decimal.Truncate(decimalValue) == decimalValue
                    && decimalValue is >= long.MinValue and <= long.MaxValue:
                result = (long)decimalValue;
                return true;
            case string stringValue
                when long.TryParse(
                    stringValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsed
                ):
                result = parsed;
                return true;
            default:
                result = default;
                return false;
        }
    }

    private static DocumentMetadataRow CreateDocumentMetadataRow(
        DocumentCacheResolvedSourceMetadata source
    ) =>
        new(
            source.DocumentId,
            source.DocumentUuid.Value,
            source.ContentVersion,
            source.ContentVersion,
            source.ContentLastModifiedAt,
            source.ContentLastModifiedAt
        );

    private static DocumentCacheProjectionProcessingException BuildProjectionProcessingException(
        DocumentCacheMaterializationRequest request,
        DocumentCacheResolvedSourceMetadata source,
        DocumentCacheProjectionProcessingFailureReason reason
    )
    {
        return new DocumentCacheProjectionProcessingException(reason, BuildFailureMetadata(request, source));
    }

    private static DocumentCacheTargetMappingException BuildTargetMappingException(
        DocumentCacheMaterializationRequest request,
        DocumentCacheResolvedSourceMetadata source,
        DocumentCacheTargetMappingFailureReason reason
    )
    {
        return new DocumentCacheTargetMappingException(reason, BuildFailureMetadata(request, source));
    }

    private static DocumentCacheMaterializerFailureMetadata BuildFailureMetadata(
        DocumentCacheMaterializationRequest request,
        DocumentCacheResolvedSourceMetadata source
    ) =>
        new(
            request.TargetContext.TargetKey,
            request.TargetContext.MappingSet.Key,
            request.Purpose,
            source.DocumentId
        )
        {
            SelectedRequiredContentVersion = request.SelectedRequiredContentVersion,
            ResourceKeyId = source.ResourceKeyId,
            ProjectName = source.ProjectName,
            ResourceName = source.ResourceName,
            ResourceVersion = source.ResourceVersion,
        };
}
