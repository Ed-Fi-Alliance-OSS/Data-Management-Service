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
    IDocumentCacheDescriptorHydrator descriptorHydrator,
    IDocumentCacheMaterializationDataStore materializationDataStore,
    IRelationalReadMaterializer readMaterializer,
    IServedEtagComposer servedEtagComposer
) : IDocumentCacheMaterializer
{
    private const string IdPropertyName = "id";
    private const string EtagPropertyName = "_etag";
    private const string LastModifiedDatePropertyName = "_lastModifiedDate";
    private const string LastModifiedDateFormat = "yyyy-MM-ddTHH:mm:ss'Z'";

    private readonly IDocumentCacheSourceMetadataReader _sourceMetadataReader =
        sourceMetadataReader ?? throw new ArgumentNullException(nameof(sourceMetadataReader));
    private readonly IDocumentCacheDescriptorHydrator _descriptorHydrator =
        descriptorHydrator ?? throw new ArgumentNullException(nameof(descriptorHydrator));
    private readonly IDocumentCacheMaterializationDataStore _materializationDataStore =
        materializationDataStore ?? throw new ArgumentNullException(nameof(materializationDataStore));
    private readonly IRelationalReadMaterializer _readMaterializer =
        readMaterializer ?? throw new ArgumentNullException(nameof(readMaterializer));
    private readonly IServedEtagComposer _servedEtagComposer =
        servedEtagComposer ?? throw new ArgumentNullException(nameof(servedEtagComposer));

    public async Task<DocumentCacheMaterializationResult> MaterializeAsync(
        DocumentCacheMaterializationRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var boundRequest = _materializationDataStore.BindToTargetDataStore(request);

        var sourceReadResult = await _sourceMetadataReader
            .ReadAsync(boundRequest, boundRequest.CancellationToken)
            .ConfigureAwait(false);

        return sourceReadResult switch
        {
            DocumentCacheSourceMetadataReadResult.MissingSource => DocumentCacheMaterializationResult
                .MissingSource
                .Instance,
            DocumentCacheSourceMetadataReadResult.Found
            {
                Metadata: DocumentCacheResolvedSourceMetadata.OrdinaryResource ordinaryResource,
            } => await MaterializeOrdinaryResourceAsync(boundRequest, ordinaryResource).ConfigureAwait(false),
            DocumentCacheSourceMetadataReadResult.Found
            {
                Metadata: DocumentCacheResolvedSourceMetadata.DescriptorResource descriptorResource,
            } => await MaterializeDescriptorResourceAsync(boundRequest, descriptorResource)
                .ConfigureAwait(false),
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
        var hydratedPage = await _materializationDataStore
            .HydrateAsync(
                request,
                source.ReadPlan,
                new PageKeysetSpec.Single(source.DocumentId),
                new HydrationExecutionOptions(UseSingleDocumentFastPath: true),
                request.CancellationToken
            )
            .ConfigureAwait(false);

        if (await CheckSourceCoherenceAsync(request, source).ConfigureAwait(false) is { } nonSuccessResult)
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

        var documentJson = RequireDocumentJsonObject(request, source, documentJsonNode);

        var streamEtag = DocumentCacheMaterializerStreamEtagComposer.ComposeForResource(
            _servedEtagComposer,
            request.TargetContext.MappingSet,
            source.ContentVersion
        );

        ValidateCandidate(request, source, documentJson);

        return CreateSuccess(source, streamEtag, documentJson);
    }

    private async Task<DocumentCacheMaterializationResult> MaterializeDescriptorResourceAsync(
        DocumentCacheMaterializationRequest request,
        DocumentCacheResolvedSourceMetadata.DescriptorResource source
    )
    {
        var hydrationResult = await _descriptorHydrator
            .HydrateAsync(request, source, request.CancellationToken)
            .ConfigureAwait(false);

        return hydrationResult switch
        {
            DocumentCacheDescriptorHydrationResult.StableDescriptorBodyMissing =>
                await ClassifyDescriptorBodyMissingAsync(request, source).ConfigureAwait(false),
            DocumentCacheDescriptorHydrationResult.Found found => await MaterializeDescriptorCandidateAsync(
                    request,
                    source,
                    found.DescriptorRow
                )
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"DocumentCache descriptor hydrator returned unsupported result type '{hydrationResult.GetType().Name}'."
            ),
        };
    }

    private async Task<DocumentCacheMaterializationResult> ClassifyDescriptorBodyMissingAsync(
        DocumentCacheMaterializationRequest request,
        DocumentCacheResolvedSourceMetadata.DescriptorResource source
    )
    {
        if (await CheckSourceCoherenceAsync(request, source).ConfigureAwait(false) is { } nonSuccessResult)
        {
            return nonSuccessResult;
        }

        throw BuildProjectionProcessingException(
            request,
            source,
            DocumentCacheProjectionProcessingFailureReason.StableSourceBodyMissing
        );
    }

    private async Task<DocumentCacheMaterializationResult> MaterializeDescriptorCandidateAsync(
        DocumentCacheMaterializationRequest request,
        DocumentCacheResolvedSourceMetadata.DescriptorResource source,
        DescriptorReadRow descriptorRow
    )
    {
        if (await CheckSourceCoherenceAsync(request, source).ConfigureAwait(false) is { } nonSuccessResult)
        {
            return nonSuccessResult;
        }

        var documentJson = DescriptorDocumentMaterializer.Materialize(
            descriptorRow,
            RelationalReadMaterializationMode.CacheProjection,
            composedEtag: null
        );

        var streamEtag = DocumentCacheMaterializerStreamEtagComposer.ComposeForDescriptor(
            _servedEtagComposer,
            request.TargetContext.MappingSet,
            source.ContentVersion
        );

        ValidateCandidate(request, source, documentJson);

        return CreateSuccess(source, streamEtag, documentJson);
    }

    private async Task<DocumentCacheMaterializationResult?> CheckSourceCoherenceAsync(
        DocumentCacheMaterializationRequest request,
        DocumentCacheResolvedSourceMetadata source
    )
    {
        var finalReadResult = await _sourceMetadataReader
            .ReadCurrentAsync(request, request.CancellationToken)
            .ConfigureAwait(false);

        return finalReadResult switch
        {
            DocumentCacheCurrentSourceMetadataReadResult.MissingSource => DocumentCacheMaterializationResult
                .MissingSource
                .Instance,
            DocumentCacheCurrentSourceMetadataReadResult.Found found
                when source.HasSameCanonicalMetadata(found.Metadata) => null,
            DocumentCacheCurrentSourceMetadataReadResult.Found => DocumentCacheMaterializationResult
                .SourceChangedDuringHydration
                .Instance,
            _ => throw new InvalidOperationException(
                $"DocumentCache current source metadata reader returned unsupported result type '{finalReadResult.GetType().Name}'."
            ),
        };
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

    private static DocumentCacheMaterializationResult.Success CreateSuccess(
        DocumentCacheResolvedSourceMetadata source,
        string streamEtag,
        JsonObject documentJson
    ) =>
        new(
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

    private static JsonObject RequireDocumentJsonObject(
        DocumentCacheMaterializationRequest request,
        DocumentCacheResolvedSourceMetadata source,
        JsonNode documentJsonNode
    )
    {
        if (documentJsonNode is JsonObject documentJson)
        {
            return documentJson;
        }

        throw BuildProjectionProcessingException(
            request,
            source,
            DocumentCacheProjectionProcessingFailureReason.DocumentJsonNotObject
        );
    }

    private static void ValidateCandidate(
        DocumentCacheMaterializationRequest request,
        DocumentCacheResolvedSourceMetadata source,
        JsonObject documentJson
    )
    {
        if (
            !TryGetStringProperty(documentJson, IdPropertyName, out var documentJsonId)
            || !string.Equals(documentJsonId, source.DocumentUuid.Value.ToString(), StringComparison.Ordinal)
        )
        {
            throw BuildProjectionProcessingException(
                request,
                source,
                DocumentCacheProjectionProcessingFailureReason.DocumentJsonIdMismatch
            );
        }

        if (
            !TryGetStringProperty(
                documentJson,
                LastModifiedDatePropertyName,
                out var documentJsonLastModifiedDate
            )
            || !string.Equals(
                documentJsonLastModifiedDate,
                FormatLastModifiedDate(source.ContentLastModifiedAt),
                StringComparison.Ordinal
            )
        )
        {
            throw BuildProjectionProcessingException(
                request,
                source,
                DocumentCacheProjectionProcessingFailureReason.DocumentJsonLastModifiedDateMismatch
            );
        }

        if (documentJson.ContainsKey(EtagPropertyName))
        {
            throw BuildProjectionProcessingException(
                request,
                source,
                DocumentCacheProjectionProcessingFailureReason.DocumentJsonContainsEtag
            );
        }
    }

    private static bool TryGetStringProperty(JsonObject documentJson, string propertyName, out string value)
    {
        if (
            documentJson.TryGetPropertyValue(propertyName, out var propertyValue)
            && propertyValue is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out var stringValue)
        )
        {
            value = stringValue;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string FormatLastModifiedDate(DateTimeOffset lastModifiedAt) =>
        lastModifiedAt.UtcDateTime.ToString(LastModifiedDateFormat, CultureInfo.InvariantCulture);

    private static DocumentCacheProjectionProcessingException BuildProjectionProcessingException(
        DocumentCacheMaterializationRequest request,
        DocumentCacheResolvedSourceMetadata source,
        DocumentCacheProjectionProcessingFailureReason reason
    )
    {
        return new DocumentCacheProjectionProcessingException(reason, BuildFailureMetadata(request, source));
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
