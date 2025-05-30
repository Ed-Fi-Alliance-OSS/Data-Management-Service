// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
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

            // Extract the descriptor URIs with path from the document
            JsonPathAndValue[] descriptorUrisWithPath = documentBody
                .SelectNodesAndLocationFromArrayPathCoerceToStrings(documentPath.Path.Value, logger)
                .ToArray();

            // Path can be empty if descriptor reference is optional
            if (descriptorUrisWithPath.Length == 0)
            {
                continue;
            }

            BaseResourceInfo resourceInfo = new(
                documentPath.ProjectName,
                documentPath.ResourceName,
                documentPath.IsDescriptor
            );

            foreach (JsonPathAndValue descriptorUri in descriptorUrisWithPath)
            {
                var normalizedDescriptorUriValue = descriptorUri.value;
                if (!string.IsNullOrEmpty(normalizedDescriptorUriValue))
                {
                    int hashIndex = normalizedDescriptorUriValue.IndexOf('#');
                    if (hashIndex >= 0 && hashIndex < normalizedDescriptorUriValue.Length - 1)
                    {
                        string beforeHash = normalizedDescriptorUriValue.Substring(0, hashIndex + 1);
                        string afterHash = normalizedDescriptorUriValue.Substring(hashIndex + 1);

                        normalizedDescriptorUriValue = beforeHash + afterHash.ToLowerInvariant();
                    }
                }

                // One descriptor reference per Uri
                DocumentIdentityElement documentIdentityElement = new(
                    DocumentIdentity.DescriptorIdentityJsonPath,
                    normalizedDescriptorUriValue
                );
                DocumentIdentity documentIdentity = new([documentIdentityElement]);
                result.Add(
                    new(
                        resourceInfo,
                        documentIdentity,
                        ReferentialIdFrom(resourceInfo, documentIdentity),
                        new JsonPath(descriptorUri.jsonPath)
                    )
                );
            }
        }

        return result.ToArray();
    }
}
