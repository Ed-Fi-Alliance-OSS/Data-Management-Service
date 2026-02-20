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
/// Extracts document references for a resource
/// </summary>
internal static class ReferenceExtractor
{
    /// <summary>
    /// Strips the last dot-separated segment from a JSONPath string.
    /// e.g. "$.classPeriods[*].classPeriodReference.classPeriodName" -> "$.classPeriods[*].classPeriodReference"
    /// </summary>
    private static string StripLastJsonPathSegment(string jsonPath)
    {
        int lastDotIndex = jsonPath.LastIndexOf('.');
        return lastDotIndex > 0 ? jsonPath[..lastDotIndex] : jsonPath;
    }

    /// <summary>
    /// Takes an API JSON body for the resource and extracts the document reference information from the JSON body.
    /// </summary>
    public static (DocumentReference[], DocumentReferenceArray[]) ExtractReferences(
        this ResourceSchema resourceSchema,
        JsonNode documentBody,
        ILogger logger
    )
    {
        logger.LogDebug("ReferenceExtractor.Extract");

        List<DocumentReference> documentReferences = [];
        List<DocumentReferenceArray> documentReferenceArrays = [];

        foreach (DocumentPath documentPath in resourceSchema.DocumentPaths)
        {
            if (!documentPath.IsReference)
            {
                continue;
            }

            if (documentPath.IsDescriptor)
            {
                continue;
            }

            var identityElements = documentPath.ReferenceJsonPathsElements.ToArray();

            if (identityElements.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Reference '{documentPath.ResourceName.Value}' has no identity elements in ReferenceJsonPathsElements"
                );
            }

            // Group identity values by their concrete reference-object path.
            var referenceGroups =
                new List<(string parentPath, List<DocumentIdentityElement> identityElements)>();
            var pathToGroupIndex = new Dictionary<string, int>();

            foreach (var element in identityElements)
            {
                var pathValues = documentBody
                    .SelectNodesAndLocationFromArrayPathCoerceToStrings(
                        element.ReferenceJsonPath.Value,
                        logger
                    )
                    .ToArray();

                foreach (var pv in pathValues)
                {
                    string concreteParentPath = StripLastJsonPathSegment(pv.jsonPath);

                    if (!pathToGroupIndex.TryGetValue(concreteParentPath, out int groupIndex))
                    {
                        groupIndex = referenceGroups.Count;
                        pathToGroupIndex[concreteParentPath] = groupIndex;
                        referenceGroups.Add((concreteParentPath, []));
                    }

                    referenceGroups[groupIndex]
                        .identityElements.Add(
                            new DocumentIdentityElement(element.IdentityJsonPath, pv.value)
                        );
                }
            }

            // If no matches, assume an optional reference wasn't there
            if (referenceGroups.Count == 0)
            {
                continue;
            }

            // Each reference group must have exactly one value per identity element
            foreach (var (path, elements) in referenceGroups)
            {
                if (elements.Count != identityElements.Length)
                {
                    throw new InvalidOperationException(
                        $"Reference '{documentPath.ResourceName.Value}' at '{path}': "
                            + $"expected {identityElements.Length} identity elements but found {elements.Count}"
                    );
                }
            }

            BaseResourceInfo resourceInfo = new(
                documentPath.ProjectName,
                documentPath.ResourceName,
                documentPath.IsDescriptor
            );

            List<DocumentReference> documentReferencesForThisArray = [];

            foreach (var (parentPath, identityParts) in referenceGroups)
            {
                DocumentIdentity documentIdentity = new(identityParts.ToArray());
                documentReferencesForThisArray.Add(
                    new(
                        resourceInfo,
                        documentIdentity,
                        ReferentialIdFrom(resourceInfo, documentIdentity),
                        new JsonPath(parentPath)
                    )
                );
            }

            // Derive the wildcard parent path by stripping the last segment from the first identity element's path
            string wildcardParentPath = StripLastJsonPathSegment(identityElements[0].ReferenceJsonPath.Value);

            documentReferences.AddRange(documentReferencesForThisArray);
            documentReferenceArrays.Add(
                new(new(wildcardParentPath), documentReferencesForThisArray.ToArray())
            );
        }

        return (documentReferences.ToArray(), documentReferenceArrays.ToArray());
    }
}
