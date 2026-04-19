// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

public sealed record RelationalReadMaterializationRequest(
    ResourceReadPlan ReadPlan,
    DocumentMetadataRow DocumentMetadata,
    IReadOnlyList<HydratedTableRows> TableRowsInDependencyOrder,
    IReadOnlyList<HydratedDescriptorRows> DescriptorRowsInPlanOrder,
    RelationalGetRequestReadMode ReadMode
);

public sealed record RelationalReadPageMaterializationRequest(
    ResourceReadPlan ReadPlan,
    HydratedPage HydratedPage,
    RelationalGetRequestReadMode ReadMode
);

public sealed record MaterializedDocument(DocumentMetadataRow DocumentMetadata, JsonNode Document);

public interface IRelationalReadMaterializer
{
    JsonNode Materialize(RelationalReadMaterializationRequest request);

    IReadOnlyList<MaterializedDocument> MaterializePage(RelationalReadPageMaterializationRequest request);
}

internal sealed class RelationalReadMaterializer : IRelationalReadMaterializer
{
    private const string IdPropertyName = "id";
    private const string EtagPropertyName = "_etag";
    private const string LastModifiedDatePropertyName = "_lastModifiedDate";
    private const string LastModifiedDateFormat = "yyyy-MM-ddTHH:mm:ss'Z'";

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
                ),
                request.ReadMode
            )
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

        var reconstitutedDocuments = DocumentReconstituter.ReconstitutePage(
            request.ReadPlan,
            request.HydratedPage
        );

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

        var etag = RelationalApiMetadataFormatter.FormatEtag(materializedDocument);
        documentObject[IdPropertyName] = documentMetadata.DocumentUuid.ToString();
        documentObject[EtagPropertyName] = etag;
        documentObject[LastModifiedDatePropertyName] = documentMetadata
            .ContentLastModifiedAt.ToUniversalTime()
            .ToString(LastModifiedDateFormat, CultureInfo.InvariantCulture);

        return documentObject;
    }
}
