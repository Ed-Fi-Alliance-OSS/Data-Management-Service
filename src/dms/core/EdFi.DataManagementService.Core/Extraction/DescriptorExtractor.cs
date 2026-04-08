// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Extraction.ReferentialIdCalculator;

namespace EdFi.DataManagementService.Core.Extraction;

/// <summary>
/// Extracts descriptor references for a resource
/// </summary>
internal static class DescriptorExtractor
{
    /// <summary>
    /// Takes a relational write plan and extracts every descriptor reference needed by the
    /// real relational request path, including descriptor-valued members under referenceJsonPaths.
    /// </summary>
    public static DescriptorReference[] ExtractRelationalDescriptors(
        this ResourceSchema resourceSchema,
        ResourceInfo resourceInfo,
        MappingSet mappingSet,
        JsonNode documentBody,
        ILogger logger
    )
    {
        ArgumentNullException.ThrowIfNull(resourceSchema);
        ArgumentNullException.ThrowIfNull(resourceInfo);
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(documentBody);
        ArgumentNullException.ThrowIfNull(logger);

        logger.LogDebug("DescriptorExtractor.ExtractRelationalDescriptors");

        Dictionary<string, DescriptorReference> descriptorReferencesByPath = new(StringComparer.Ordinal);

        AddDescriptorReferences(
            descriptorReferencesByPath,
            resourceSchema.ExtractDescriptors(documentBody, logger)
        );

        if (resourceInfo.IsDescriptor)
        {
            return [.. descriptorReferencesByPath.Values];
        }

        var resource = new QualifiedResourceName(
            resourceInfo.ProjectName.Value,
            resourceInfo.ResourceName.Value
        );
        var writePlan = mappingSet.GetWritePlanOrThrow(resource);

        foreach (var descriptorEdgeSource in writePlan.Model.DescriptorEdgeSources)
        {
            AddDescriptorReferences(
                descriptorReferencesByPath,
                ExtractDescriptorReferences(
                    CreateDescriptorResourceInfo(descriptorEdgeSource.DescriptorResource),
                    descriptorEdgeSource.DescriptorValuePath.Canonical,
                    documentBody,
                    logger
                )
            );
        }

        return [.. descriptorReferencesByPath.Values];
    }

    /// <summary>
    /// Takes an API JSON body for the resource and extracts the descriptor URI reference information from the JSON body.
    /// </summary>
    public static DescriptorReference[] ExtractDescriptors(
        this ResourceSchema resourceSchema,
        JsonNode documentBody,
        ILogger logger
    )
    {
        logger.LogDebug("DescriptorExtractor.ExtractDescriptors");

        List<DescriptorReference> result = [];

        foreach (DocumentPath documentPath in resourceSchema.DocumentPaths)
        {
            if (!documentPath.IsReference)
            {
                continue;
            }

            if (!documentPath.IsDescriptor)
            {
                continue;
            }

            BaseResourceInfo resourceInfo = new(
                documentPath.ProjectName,
                documentPath.ResourceName,
                documentPath.IsDescriptor
            );

            result.AddRange(
                ExtractDescriptorReferences(resourceInfo, documentPath.Path.Value, documentBody, logger)
            );
        }

        return result.ToArray();
    }

    private static IEnumerable<DescriptorReference> ExtractDescriptorReferences(
        BaseResourceInfo resourceInfo,
        string descriptorPath,
        JsonNode documentBody,
        ILogger logger
    )
    {
        foreach (
            JsonPathAndValue descriptorUri in documentBody.SelectNodesAndLocationFromArrayPathCoerceToStrings(
                descriptorPath,
                logger
            )
        )
        {
            yield return CreateDescriptorReference(resourceInfo, descriptorUri.value, descriptorUri.jsonPath);
        }
    }

    private static DescriptorReference CreateDescriptorReference(
        BaseResourceInfo resourceInfo,
        string? descriptorUri,
        string concretePath
    )
    {
        // Normalize the entire URI to lowercase for case-insensitive matching.
        var normalizedDescriptorUriValue = descriptorUri?.ToLowerInvariant() ?? string.Empty;
        DocumentIdentity documentIdentity = new([
            new DocumentIdentityElement(
                DocumentIdentity.DescriptorIdentityJsonPath,
                normalizedDescriptorUriValue
            ),
        ]);

        return new DescriptorReference(
            resourceInfo,
            documentIdentity,
            ReferentialIdFrom(resourceInfo, documentIdentity),
            new JsonPath(concretePath)
        );
    }

    private static BaseResourceInfo CreateDescriptorResourceInfo(QualifiedResourceName descriptorResource) =>
        new(
            new ProjectName(descriptorResource.ProjectName),
            new ResourceName(descriptorResource.ResourceName),
            true
        );

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
