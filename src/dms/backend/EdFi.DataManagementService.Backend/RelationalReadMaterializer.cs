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

public interface IRelationalReadMaterializer
{
    JsonNode Materialize(RelationalReadMaterializationRequest request);
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

        var descriptorUriLookup = BuildDescriptorUriLookup(request.DescriptorRowsInPlanOrder);

        var materializedDocument = DocumentReconstituter.Reconstitute(
            request.DocumentMetadata.DocumentId,
            request.TableRowsInDependencyOrder,
            request.ReadPlan.ReferenceIdentityProjectionPlansInDependencyOrder,
            request.ReadPlan.Model.DescriptorEdgeSources,
            descriptorUriLookup
        );

        return request.ReadMode switch
        {
            RelationalGetRequestReadMode.StoredDocument => materializedDocument,
            RelationalGetRequestReadMode.ExternalResponse => InjectApiMetadata(
                materializedDocument,
                request.DocumentMetadata,
                request.ReadPlan
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.ReadMode,
                "Unsupported relational read materialization mode."
            ),
        };
    }

    private static JsonNode InjectApiMetadata(
        JsonNode materializedDocument,
        DocumentMetadataRow documentMetadata,
        ResourceReadPlan readPlan
    )
    {
        ArgumentNullException.ThrowIfNull(materializedDocument);
        ArgumentNullException.ThrowIfNull(readPlan);

        if (materializedDocument is not JsonObject documentObject)
        {
            throw new InvalidOperationException(
                "Relational external response materialization requires a root JSON object."
            );
        }

        var etag = RelationalApiMetadataFormatter.FormatEtag(materializedDocument, readPlan);
        documentObject[IdPropertyName] = documentMetadata.DocumentUuid.ToString();
        documentObject[EtagPropertyName] = etag;
        documentObject[LastModifiedDatePropertyName] = documentMetadata
            .ContentLastModifiedAt.ToUniversalTime()
            .ToString(LastModifiedDateFormat, CultureInfo.InvariantCulture);

        return documentObject;
    }

    private static IReadOnlyDictionary<long, string> BuildDescriptorUriLookup(
        IReadOnlyList<HydratedDescriptorRows> descriptorRowsInPlanOrder
    )
    {
        ArgumentNullException.ThrowIfNull(descriptorRowsInPlanOrder);

        if (descriptorRowsInPlanOrder.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        Dictionary<long, string> lookup = [];

        foreach (var descriptorRows in descriptorRowsInPlanOrder)
        {
            ArgumentNullException.ThrowIfNull(descriptorRows);

            foreach (var row in descriptorRows.Rows)
            {
                lookup.TryAdd(row.DescriptorId, row.Uri);
            }
        }

        return lookup;
    }
}
