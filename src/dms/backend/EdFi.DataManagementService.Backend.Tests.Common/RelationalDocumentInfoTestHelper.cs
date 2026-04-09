// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

internal sealed record RelationalDocumentInfoExtractionSource(
    ResourceInfo ResourceInfo,
    ResourceSchema ResourceSchema,
    bool UseReferenceExtraction = true,
    bool UseRelationalDescriptorExtraction = true
);

internal sealed record RelationalDocumentInfoSupplement(
    IReadOnlyList<DocumentReference> DocumentReferences,
    IReadOnlyList<DocumentReferenceArray> DocumentReferenceArrays,
    IReadOnlyList<DescriptorReference> DescriptorReferences
)
{
    public static RelationalDocumentInfoSupplement Empty { get; } = new([], [], []);
}

internal static class RelationalDocumentInfoTestHelper
{
    public static DocumentInfo CreateDocumentInfo(
        JsonNode requestBody,
        ResourceInfo resourceInfo,
        ResourceSchema resourceSchema,
        MappingSet mappingSet,
        IReadOnlyList<RelationalDocumentInfoExtractionSource>? additionalSources = null,
        RelationalDocumentInfoSupplement? supplement = null,
        ReferentialId? referentialIdOverride = null,
        ILogger? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(requestBody);
        ArgumentNullException.ThrowIfNull(resourceInfo);
        ArgumentNullException.ThrowIfNull(resourceSchema);
        ArgumentNullException.ThrowIfNull(mappingSet);

        var activeLogger = logger ?? NullLogger.Instance;
        var activeSupplement = supplement ?? RelationalDocumentInfoSupplement.Empty;

        var (documentIdentity, superclassIdentity) = resourceSchema.ExtractIdentities(
            requestBody,
            activeLogger
        );

        List<DocumentReference> documentReferences = [];
        List<DocumentReferenceArray> documentReferenceArrays = [];
        Dictionary<string, DescriptorReference> descriptorReferencesByPath = new(StringComparer.Ordinal);

        AddSource(
            new RelationalDocumentInfoExtractionSource(resourceInfo, resourceSchema),
            requestBody,
            mappingSet,
            activeLogger,
            documentReferences,
            documentReferenceArrays,
            descriptorReferencesByPath
        );

        if (additionalSources is not null)
        {
            foreach (var additionalSource in additionalSources)
            {
                AddSource(
                    additionalSource,
                    requestBody,
                    mappingSet,
                    activeLogger,
                    documentReferences,
                    documentReferenceArrays,
                    descriptorReferencesByPath
                );
            }
        }

        documentReferences.AddRange(activeSupplement.DocumentReferences);
        documentReferenceArrays.AddRange(activeSupplement.DocumentReferenceArrays);
        AddDescriptorReferences(descriptorReferencesByPath, activeSupplement.DescriptorReferences);

        return new(
            DocumentIdentity: documentIdentity,
            ReferentialId: referentialIdOverride
                ?? ReferentialIdCalculator.ReferentialIdFrom(resourceInfo, documentIdentity),
            DocumentReferences: [.. documentReferences],
            DocumentReferenceArrays: [.. documentReferenceArrays],
            DescriptorReferences: [.. descriptorReferencesByPath.Values],
            SuperclassIdentity: superclassIdentity
        );
    }

    private static void AddSource(
        RelationalDocumentInfoExtractionSource source,
        JsonNode requestBody,
        MappingSet mappingSet,
        ILogger logger,
        List<DocumentReference> documentReferences,
        List<DocumentReferenceArray> documentReferenceArrays,
        IDictionary<string, DescriptorReference> descriptorReferencesByPath
    )
    {
        if (source.UseReferenceExtraction)
        {
            var (extractedDocumentReferences, extractedDocumentReferenceArrays) =
                source.ResourceSchema.ExtractReferences(
                    requestBody,
                    logger,
                    ReferenceExtractionMode.RelationalWriteValidation
                );

            documentReferences.AddRange(extractedDocumentReferences);
            documentReferenceArrays.AddRange(extractedDocumentReferenceArrays);
        }

        AddDescriptorReferences(
            descriptorReferencesByPath,
            source.UseRelationalDescriptorExtraction
                ? source.ResourceSchema.ExtractRelationalDescriptors(
                    source.ResourceInfo,
                    mappingSet,
                    requestBody,
                    logger
                )
                : source.ResourceSchema.ExtractDescriptors(requestBody, logger)
        );
    }

    private static void AddDescriptorReferences(
        IDictionary<string, DescriptorReference> descriptorReferencesByPath,
        IEnumerable<DescriptorReference> descriptorReferences
    )
    {
        foreach (var descriptorReference in descriptorReferences)
        {
            if (
                descriptorReferencesByPath.TryGetValue(
                    descriptorReference.Path.Value,
                    out var existingReference
                )
            )
            {
                if (
                    existingReference.ResourceInfo != descriptorReference.ResourceInfo
                    || existingReference.ReferentialId != descriptorReference.ReferentialId
                )
                {
                    throw new InvalidOperationException(
                        $"Descriptor path '{descriptorReference.Path.Value}' resolved to conflicting descriptor references."
                    );
                }

                continue;
            }

            descriptorReferencesByPath[descriptorReference.Path.Value] = descriptorReference;
        }
    }
}
