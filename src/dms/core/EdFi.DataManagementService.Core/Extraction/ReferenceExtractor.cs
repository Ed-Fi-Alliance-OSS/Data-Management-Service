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

            // Extract the reference values from the document
            IntermediateReferenceElement[] intermediateReferenceElements = documentPath
                .ReferenceJsonPathsElements.Select(
                    referenceJsonPathsElement => new IntermediateReferenceElement(
                        referenceJsonPathsElement.IdentityJsonPath,
                        documentBody
                            .SelectNodesFromArrayPathCoerceToStrings(
                                referenceJsonPathsElement.ReferenceJsonPath.Value,
                                logger
                            )
                            .ToArray()
                    )
                )
                .ToArray();

            int valueSliceLength = intermediateReferenceElements[0].ValueSlice.Length;

            // Number of document values from resolved JsonPaths should all be the same, otherwise something is very wrong
            Trace.Assert(
                Array.TrueForAll(intermediateReferenceElements, x => x.ValueSlice.Length == valueSliceLength),
                "Length of document value slices are not equal"
            );

            // If a JsonPath selection had no results, we can assume an optional reference wasn't there
            if (valueSliceLength == 0)
            {
                continue;
            }

            BaseResourceInfo resourceInfo = new(
                documentPath.ProjectName,
                documentPath.ResourceName,
                documentPath.IsDescriptor
            );

            List<DocumentReference> documentReferencesForThisArray = [];

            // Reorient intermediateReferenceElements into actual DocumentReferences
            for (int index = 0; index < valueSliceLength; index += 1)
            {
                List<DocumentIdentityElement> documentIdentityElements = [];

                foreach (
                    IntermediateReferenceElement intermediateReferenceElement in intermediateReferenceElements
                )
                {
                    documentIdentityElements.Add(
                        new DocumentIdentityElement(
                            intermediateReferenceElement.IdentityJsonPath,
                            intermediateReferenceElement.ValueSlice[index]
                        )
                    );
                }

                DocumentIdentity documentIdentity = new(documentIdentityElements.ToArray());
                documentReferencesForThisArray.Add(
                    new(resourceInfo, documentIdentity, ReferentialIdFrom(resourceInfo, documentIdentity))
                );
            }

            // Get the parent path from the first ReferenceJsonPathsElement
            string firstReferenceJsonPath = documentPath
                .ReferenceJsonPathsElements.First()
                .ReferenceJsonPath.Value;
            int lastDotIndex = firstReferenceJsonPath.LastIndexOf('.');
            string parentPath =
                lastDotIndex > 0 ? firstReferenceJsonPath.Substring(0, lastDotIndex) : firstReferenceJsonPath;

            documentReferences.AddRange(documentReferencesForThisArray);
            documentReferenceArrays.Add(new(new(parentPath), documentReferencesForThisArray.ToArray()));
        }

        return (documentReferences.ToArray(), documentReferenceArrays.ToArray());
    }
}
