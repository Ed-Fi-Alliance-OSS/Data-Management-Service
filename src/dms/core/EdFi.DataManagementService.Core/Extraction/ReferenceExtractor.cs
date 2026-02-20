// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
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

            // Extract the reference values with concrete paths from the document
            var referenceElements = documentPath
                .ReferenceJsonPathsElements.Select(element => new
                {
                    element.IdentityJsonPath,
                    PathValues = documentBody
                        .SelectNodesAndLocationFromArrayPathCoerceToStrings(
                            element.ReferenceJsonPath.Value,
                            logger
                        )
                        .ToArray(),
                })
                .ToArray();

            int matchCount = referenceElements[0].PathValues.Length;

            // Number of document values from resolved JsonPaths should all be the same, otherwise something is very wrong
            Trace.Assert(
                Array.TrueForAll(referenceElements, x => x.PathValues.Length == matchCount),
                "Length of document value slices are not equal",
                ""
            );

            // If a JsonPath selection had no results, we can assume an optional reference wasn't there
            if (matchCount == 0)
            {
                continue;
            }

            BaseResourceInfo resourceInfo = new(
                documentPath.ProjectName,
                documentPath.ResourceName,
                documentPath.IsDescriptor
            );

            List<DocumentReference> documentReferencesForThisArray = [];

            // Build DocumentReferences with concrete paths including numeric indices
            for (int index = 0; index < matchCount; index += 1)
            {
                // Derive the concrete reference-object path by stripping the final segment
                // e.g. $.classPeriods[0].classPeriodReference.classPeriodName -> $.classPeriods[0].classPeriodReference
                string concreteScalarPath = referenceElements[0].PathValues[index].jsonPath;
                int lastDotIndex = concreteScalarPath.LastIndexOf('.');
                string concreteReferenceObjectPath =
                    lastDotIndex > 0 ? concreteScalarPath[..lastDotIndex] : concreteScalarPath;

                List<DocumentIdentityElement> documentIdentityElements = [];

                foreach (var element in referenceElements)
                {
                    documentIdentityElements.Add(
                        new DocumentIdentityElement(element.IdentityJsonPath, element.PathValues[index].value)
                    );
                }

                DocumentIdentity documentIdentity = new(documentIdentityElements.ToArray());
                documentReferencesForThisArray.Add(
                    new(
                        resourceInfo,
                        documentIdentity,
                        ReferentialIdFrom(resourceInfo, documentIdentity),
                        new JsonPath(concreteReferenceObjectPath)
                    )
                );
            }

            // Get the wildcard parent path from the first ReferenceJsonPathsElement
            string firstReferenceJsonPath = documentPath
                .ReferenceJsonPathsElements.First()
                .ReferenceJsonPath.Value;
            int wildcardLastDotIndex = firstReferenceJsonPath.LastIndexOf('.');
            string parentPath =
                wildcardLastDotIndex > 0
                    ? firstReferenceJsonPath[..wildcardLastDotIndex]
                    : firstReferenceJsonPath;

            documentReferences.AddRange(documentReferencesForThisArray);
            documentReferenceArrays.Add(new(new(parentPath), documentReferencesForThisArray.ToArray()));
        }

        return (documentReferences.ToArray(), documentReferenceArrays.ToArray());
    }
}
