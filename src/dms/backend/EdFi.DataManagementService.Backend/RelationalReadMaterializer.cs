// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Backend;

public sealed record RelationalReadMaterializationRequest(
    ResourceReadPlan ReadPlan,
    DocumentMetadataRow DocumentMetadata,
    IReadOnlyList<HydratedTableRows> TableRowsInDependencyOrder,
    IReadOnlyList<HydratedDescriptorRows> DescriptorRowsInPlanOrder,
    RelationalGetRequestReadMode ReadMode
)
{
    /// <summary>
    /// Optional mapping set used to resolve <c>link.rel</c> / <c>link.href</c> slugs during
    /// reconstitution. <see langword="null"/> on call paths that do not have a mapping set
    /// in scope (legacy callers, descriptor materialization). When <see langword="null"/>,
    /// reconstitution falls back to the no-link overload regardless of
    /// <see cref="ResourceLinksOptions.Enabled"/>.
    /// </summary>
    public MappingSet? MappingSet { get; init; }

    /// <summary>
    /// Hydrated rows from the document-reference auxiliary lookup for the originating
    /// <see cref="HydratedPage"/>. Propagated through the single-document path so
    /// reconstitution can emit <c>link.rel</c> / <c>link.href</c> on GET-by-id. <see langword="null"/>
    /// when the resource read plan has no <c>DocumentReferenceLookup</c> or when the caller
    /// does not have one in scope.
    /// </summary>
    public HydratedDocumentReferenceLookup? DocumentReferenceLookup { get; init; }
}

public sealed record RelationalReadPageMaterializationRequest(
    ResourceReadPlan ReadPlan,
    HydratedPage HydratedPage,
    RelationalGetRequestReadMode ReadMode
)
{
    /// <summary>
    /// Optional mapping set used to resolve <c>link.rel</c> / <c>link.href</c> slugs during
    /// reconstitution. <see langword="null"/> on call paths that do not have a mapping set
    /// in scope (legacy callers, descriptor materialization). When <see langword="null"/>,
    /// reconstitution falls back to the no-link overload regardless of
    /// <see cref="ResourceLinksOptions.Enabled"/>.
    /// </summary>
    public MappingSet? MappingSet { get; init; }
}

public sealed record MaterializedDocument(DocumentMetadataRow DocumentMetadata, JsonNode Document);

public interface IRelationalReadMaterializer
{
    JsonNode Materialize(RelationalReadMaterializationRequest request);

    IReadOnlyList<MaterializedDocument> MaterializePage(RelationalReadPageMaterializationRequest request);

    /// <summary>
    /// Final response-shaping pass: strips the <c>link</c> subtree from every document-reference
    /// object when <see cref="ResourceLinksOptions.Enabled"/> is <see langword="false"/>; no-op
    /// otherwise. Invoked by the repository wrapper after readable-profile projection so the
    /// flag governs the served body without affecting intermediate caching or <c>_etag</c>.
    /// Safe to call unconditionally — mutates <paramref name="document"/> in place.
    /// </summary>
    void StripReferenceLinks(JsonNode document, ResourceReadPlan readPlan);
}

internal sealed class RelationalReadMaterializer(
    IDocumentLinkSlugResolver slugResolver,
    IOptions<ResourceLinksOptions> linksOptions
) : IRelationalReadMaterializer
{
    private const string IdPropertyName = "id";
    private const string EtagPropertyName = "_etag";
    private const string LastModifiedDatePropertyName = "_lastModifiedDate";
    private const string LastModifiedDateFormat = "yyyy-MM-ddTHH:mm:ss'Z'";

    private readonly IDocumentLinkSlugResolver _slugResolver =
        slugResolver ?? throw new ArgumentNullException(nameof(slugResolver));
    private readonly ResourceLinksOptions _linksOptions =
        linksOptions?.Value ?? throw new ArgumentNullException(nameof(linksOptions));

    public JsonNode Materialize(RelationalReadMaterializationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var materializedDocuments = MaterializePage(
            new RelationalReadPageMaterializationRequest(
                request.ReadPlan,
                new HydratedPage(
                    TotalCount: null,
                    DocumentMetadata: [request.DocumentMetadata],
                    TableRowsInDependencyOrder: request.TableRowsInDependencyOrder,
                    DescriptorRowsInPlanOrder: request.DescriptorRowsInPlanOrder
                )
                {
                    DocumentReferenceLookup = request.DocumentReferenceLookup,
                },
                request.ReadMode
            )
            {
                MappingSet = request.MappingSet,
            }
        );

        if (materializedDocuments.Count != 1)
        {
            throw new InvalidOperationException(
                $"Relational read materialization expected exactly 1 document for DocumentId {request.DocumentMetadata.DocumentId}, "
                    + $"but materialized {materializedDocuments.Count}."
            );
        }

        return materializedDocuments[0].Document;
    }

    public IReadOnlyList<MaterializedDocument> MaterializePage(
        RelationalReadPageMaterializationRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        // The resolver-aware (link-bearing) overload runs only for ExternalResponse reads —
        // StoredDocument-mode reads are internal read-modify-write fetches per
        // RelationalGetRequestContracts.cs:22 and must not carry server-only `link`
        // decorations (otherwise profile write merge can copy them back into the request
        // body via ProfileWriteValidationMiddleware.MergeNestedObjectStrippedFields). Legacy
        // ExternalResponse callers without a MappingSet also fall back to the no-link
        // overload. The ResourceLinksOptions.Enabled flag is honored as the final response-
        // shaping pass via StripReferenceLinks, invoked by the repository wrapper after
        // readable-profile projection.
        var reconstitutedDocuments =
            request.ReadMode == RelationalGetRequestReadMode.ExternalResponse
            && request.MappingSet is { } mappingSet
                ? DocumentReconstituter.ReconstitutePage(
                    request.ReadPlan,
                    request.HydratedPage,
                    mappingSet,
                    _slugResolver
                )
                : DocumentReconstituter.ReconstitutePage(request.ReadPlan, request.HydratedPage);

        if (reconstitutedDocuments.Count != request.HydratedPage.DocumentMetadata.Count)
        {
            throw new InvalidOperationException(
                $"Relational page materialization expected {request.HydratedPage.DocumentMetadata.Count} documents, "
                    + $"but reconstituted {reconstitutedDocuments.Count}."
            );
        }

        return
        [
            .. request.HydratedPage.DocumentMetadata.Select(
                (documentMetadata, index) =>
                    new MaterializedDocument(
                        documentMetadata,
                        ApplyReadMode(reconstitutedDocuments[index], documentMetadata, request.ReadMode)
                    )
            ),
        ];
    }

    private static JsonNode ApplyReadMode(
        JsonNode materializedDocument,
        DocumentMetadataRow documentMetadata,
        RelationalGetRequestReadMode readMode
    )
    {
        return readMode switch
        {
            RelationalGetRequestReadMode.StoredDocument => materializedDocument,
            RelationalGetRequestReadMode.ExternalResponse => InjectApiMetadata(
                materializedDocument,
                documentMetadata
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(readMode),
                readMode,
                "Unsupported relational read materialization mode."
            ),
        };
    }

    public void StripReferenceLinks(JsonNode document, ResourceReadPlan readPlan)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(readPlan);

        DocumentReconstituter.StripReferenceLinks(document, readPlan, _linksOptions);
    }

    private static JsonNode InjectApiMetadata(
        JsonNode materializedDocument,
        DocumentMetadataRow documentMetadata
    )
    {
        ArgumentNullException.ThrowIfNull(materializedDocument);

        if (materializedDocument is not JsonObject documentObject)
        {
            throw new InvalidOperationException(
                "Relational external response materialization requires a root JSON object."
            );
        }

        // ETag is computed over the link-bearing intermediate — the canonical formatter
        // already strips {id, link, _etag, _lastModifiedDate} recursively before hashing
        // (per DMS-1005), so the etag value is link-decoration-independent regardless of
        // whether the response-boundary strip pass runs later in the repository wrapper.
        var etag = RelationalApiMetadataFormatter.FormatEtag(materializedDocument);
        documentObject[IdPropertyName] = documentMetadata.DocumentUuid.ToString();
        documentObject[EtagPropertyName] = etag;
        documentObject[LastModifiedDatePropertyName] = documentMetadata
            .ContentLastModifiedAt.ToUniversalTime()
            .ToString(LastModifiedDateFormat, CultureInfo.InvariantCulture);

        return documentObject;
    }
}
