// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Backend;

internal sealed class DocumentCacheReadResponseShaper(
    IRelationalReadMaterializer readMaterializer,
    IReadableProfileProjector readableProfileProjector,
    IServedEtagComposer servedEtagComposer,
    IOptions<ResourceLinksOptions> linksOptions,
    ILogger<DocumentCacheReadResponseShaper>? logger = null
) : IDocumentCacheReadResponseShaper
{
    private const string IdPropertyName = "id";
    private const string EtagPropertyName = "_etag";
    private const string LastModifiedDatePropertyName = "_lastModifiedDate";
    private const string LastModifiedDateFormat = "yyyy-MM-ddTHH:mm:ss'Z'";

    private readonly IRelationalReadMaterializer _readMaterializer =
        readMaterializer ?? throw new ArgumentNullException(nameof(readMaterializer));
    private readonly IReadableProfileProjector _readableProfileProjector =
        readableProfileProjector ?? throw new ArgumentNullException(nameof(readableProfileProjector));
    private readonly IServedEtagComposer _servedEtagComposer =
        servedEtagComposer ?? throw new ArgumentNullException(nameof(servedEtagComposer));
    private readonly ResourceLinksOptions _linksOptions =
        linksOptions?.Value ?? throw new ArgumentNullException(nameof(linksOptions));
    private readonly ILogger<DocumentCacheReadResponseShaper> _logger =
        logger ?? NullLogger<DocumentCacheReadResponseShaper>.Instance;

    public DocumentCacheReadLookupResult<GetResult> ShapeGetById(
        DocumentCacheReadAccelerationGetByIdRequest request,
        DocumentCacheReadDocumentLookupResult.FreshHit hit
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hit);

        return TryShape(() =>
        {
            JsonNode edfiDoc = ShapeDocument(
                request.MappingSet,
                request.Resource,
                request.ResourceKind,
                request.ReadableProfileProjectionContext,
                request.ResponseContentCoding,
                hit.Candidate,
                hit.DocumentJson
            );

            return DocumentCacheReadLookupResult<GetResult>.Hit(
                new GetResult.GetSuccess(
                    hit.Candidate.DocumentUuid,
                    edfiDoc,
                    hit.Candidate.ContentLastModifiedAt.UtcDateTime,
                    LastModifiedTraceId: null
                )
            );
        });
    }

    public DocumentCacheReadLookupResult<QueryResult> ShapeQuery(
        DocumentCacheReadAccelerationQueryRequest request,
        DocumentCacheReadAccelerationCandidatePage authorizedCandidatePage,
        DocumentCacheReadBatchLookupResult hitPage
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorizedCandidatePage);
        ArgumentNullException.ThrowIfNull(hitPage);

        return TryShape(() =>
        {
            JsonArray edfiDocs = [];
            IReadOnlyList<DocumentCacheReadAccelerationCandidate> authorizedCandidates =
                authorizedCandidatePage.Candidates;

            if (hitPage.Documents.Count != authorizedCandidates.Count)
            {
                throw new DocumentCacheReadResponseShapingException(
                    DocumentCacheReadResponseShapingFailureReason.QueryHitCandidateMismatch
                );
            }

            for (var index = 0; index < hitPage.Documents.Count; index++)
            {
                DocumentCacheReadDocumentLookupResult documentLookupResult = hitPage.Documents[index];

                if (documentLookupResult is not DocumentCacheReadDocumentLookupResult.FreshHit hit)
                {
                    return DocumentCacheReadLookupResult<QueryResult>.FallbackFromLookupOutcome(
                        documentLookupResult.Outcome
                    );
                }

                if (hit.Candidate != authorizedCandidates[index])
                {
                    throw new DocumentCacheReadResponseShapingException(
                        DocumentCacheReadResponseShapingFailureReason.QueryHitCandidateMismatch
                    );
                }

                edfiDocs.Add(
                    ShapeDocument(
                        request.MappingSet,
                        request.Resource,
                        request.ResourceKind,
                        request.ReadableProfileProjectionContext,
                        request.ResponseContentCoding,
                        hit.Candidate,
                        hit.DocumentJson
                    )
                );
            }

            return DocumentCacheReadLookupResult<QueryResult>.Hit(
                new QueryResult.QuerySuccess(
                    edfiDocs,
                    authorizedCandidatePage.TotalCount is null
                        ? null
                        : RelationalReadGuardrails.ConvertTotalCountOrThrow(
                            request.Resource,
                            authorizedCandidatePage.TotalCount,
                            "cache query response shaping"
                        ),
                    authorizedCandidatePage.HighestSelectedDocumentId
                )
            );
        });
    }

    private DocumentCacheReadLookupResult<TResult> TryShape<TResult>(
        Func<DocumentCacheReadLookupResult<TResult>> shape
    )
        where TResult : class
    {
        try
        {
            return shape();
        }
        catch (DocumentCacheReadResponseShapingException exception)
        {
            LogShapingFailure(exception.Reason);
            return DocumentCacheReadLookupResult<TResult>.Fallback(
                DocumentCacheReadAccelerationFallbackReason.CacheLookupInvariantFailure,
                invariantDiagnostic: DocumentCacheReadInvariantDiagnostic.CacheHitResponseShaping(
                    exception.Reason
                ),
                rawLookupOutcome: DocumentCacheReadLookupOutcome.DeterministicInvariantFailure
            );
        }
    }

    private JsonNode ShapeDocument(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        ReadableProfileProjectionContext? readableProfileProjectionContext,
        ResponseContentCoding responseContentCoding,
        DocumentCacheReadAccelerationCandidate candidate,
        string documentJson
    )
    {
        JsonObject documentObject = ParseCachedDocumentObject(documentJson);
        ValidateCachedDocument(candidate, documentObject);

        documentObject[EtagPropertyName] = ComposeServedEtag(
            mappingSet,
            resourceKind,
            readableProfileProjectionContext,
            responseContentCoding,
            candidate.ContentVersion
        );

        JsonNode shapedDocument = documentObject;

        if (readableProfileProjectionContext is not null)
        {
            shapedDocument = _readableProfileProjector.Project(
                shapedDocument,
                readableProfileProjectionContext.ContentTypeDefinition,
                readableProfileProjectionContext.IdentityPropertyNames
            );
        }

        if (resourceKind == DocumentCacheReadAccelerationResourceKind.Resource)
        {
            ResourceReadPlan readPlan = mappingSet.GetReadPlanOrThrow(resource);
            _readMaterializer.StripReferenceLinks(shapedDocument, readPlan);
        }

        return shapedDocument;
    }

    private string ComposeServedEtag(
        MappingSet mappingSet,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        ReadableProfileProjectionContext? readableProfileProjectionContext,
        ResponseContentCoding responseContentCoding,
        long contentVersion
    ) =>
        _servedEtagComposer.Compose(
            new ServedEtagContext(
                mappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                readableProfileProjectionContext?.ProfileName,
                LinksEnabled: resourceKind == DocumentCacheReadAccelerationResourceKind.Resource
                    && _linksOptions.Enabled,
                contentVersion,
                responseContentCoding
            )
        );

    private static JsonObject ParseCachedDocumentObject(string documentJson)
    {
        JsonNode? documentNode;

        try
        {
            documentNode = JsonNode.Parse(documentJson);
        }
        catch (JsonException exception)
        {
            throw new DocumentCacheReadResponseShapingException(
                DocumentCacheReadResponseShapingFailureReason.InvalidDocumentJson,
                exception
            );
        }

        if (documentNode is not JsonObject documentObject)
        {
            throw new DocumentCacheReadResponseShapingException(
                DocumentCacheReadResponseShapingFailureReason.DocumentJsonNotObject
            );
        }

        return documentObject;
    }

    private static void ValidateCachedDocument(
        DocumentCacheReadAccelerationCandidate candidate,
        JsonObject documentObject
    )
    {
        if (documentObject.ContainsKey(EtagPropertyName))
        {
            throw new DocumentCacheReadResponseShapingException(
                DocumentCacheReadResponseShapingFailureReason.DocumentJsonContainsEtag
            );
        }

        if (
            !TryGetStringProperty(documentObject, IdPropertyName, out string documentJsonId)
            || !string.Equals(
                documentJsonId,
                candidate.DocumentUuid.Value.ToString(),
                StringComparison.Ordinal
            )
        )
        {
            throw new DocumentCacheReadResponseShapingException(
                DocumentCacheReadResponseShapingFailureReason.DocumentJsonIdMismatch
            );
        }

        if (
            !TryGetStringProperty(
                documentObject,
                LastModifiedDatePropertyName,
                out string documentJsonLastModifiedDate
            )
            || !string.Equals(
                documentJsonLastModifiedDate,
                FormatLastModifiedDate(candidate.ContentLastModifiedAt),
                StringComparison.Ordinal
            )
        )
        {
            throw new DocumentCacheReadResponseShapingException(
                DocumentCacheReadResponseShapingFailureReason.DocumentJsonLastModifiedDateMismatch
            );
        }
    }

    private static bool TryGetStringProperty(JsonObject documentJson, string propertyName, out string value)
    {
        if (
            documentJson.TryGetPropertyValue(propertyName, out JsonNode? propertyValue)
            && propertyValue is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out string? stringValue)
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

    private void LogShapingFailure(DocumentCacheReadResponseShapingFailureReason reason)
    {
        _logger.LogWarning(
            "DocumentCache cache-hit response shaping failed with {Reason}; falling back relationally.",
            reason
        );
    }
}

public sealed class DocumentCacheReadResponseShapingException : Exception
{
    public DocumentCacheReadResponseShapingException(
        DocumentCacheReadResponseShapingFailureReason reason,
        Exception? innerException = null
    )
        : base($"DocumentCache cache-hit response shaping failed with reason '{reason}'.", innerException)
    {
        Reason = reason;
    }

    public DocumentCacheReadResponseShapingFailureReason Reason { get; }
}

public enum DocumentCacheReadResponseShapingFailureReason
{
    InvalidDocumentJson,
    DocumentJsonNotObject,
    DocumentJsonContainsEtag,
    DocumentJsonIdMismatch,
    DocumentJsonLastModifiedDateMismatch,
    QueryHitCandidateMismatch,
    StreamEtagMismatch,
}
