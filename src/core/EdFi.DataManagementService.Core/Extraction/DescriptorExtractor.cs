// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Extensions;
using EdFi.DataManagementService.Core.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Extraction;

/// <summary>
/// Extracts descriptor references for a resource
/// </summary>
internal static class DescriptorExtractor
{
    /// <summary>
    /// Takes an API JSON body for the resource and extracts the descriptor URI reference information from the JSON body.
    /// </summary>
    public static DocumentReference[] ExtractDescriptors(
        this ResourceSchema resourceSchema,
        JsonNode documentBody,
        ILogger logger
    )
    {
        logger.LogDebug("DescriptorExtractor.Extract");

        List<DocumentReference> result = [];

        foreach (DocumentPath documentPath in resourceSchema.DocumentPaths)
        {
            if (!documentPath.IsReference)
                continue;
            if (!documentPath.IsDescriptor)
                continue;

            // Extract the descriptor URIs from the document
            string[] descriptorUris = documentBody
                .SelectNodesFromArrayPathCoerceToStrings(documentPath.Path.Value, logger)
                .ToArray();

            // Path can be empty if descriptor reference is optional
            if (descriptorUris.Length == 0)
                continue;

            BaseResourceInfo resourceInfo =
                new(documentPath.ProjectName, documentPath.ResourceName, documentPath.IsDescriptor);

            foreach (string descriptorUri in descriptorUris)
            {
                // One descriptor reference per Uri
                DocumentIdentityElement documentIdentityElement =
                    new(DocumentIdentity.DescriptorIdentityJsonPath, descriptorUri);
                result.Add(new(resourceInfo, new DocumentIdentity([documentIdentityElement])));
            }
        }

        return result.ToArray();
    }
}
