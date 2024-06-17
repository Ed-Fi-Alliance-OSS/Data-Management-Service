// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Extensions;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Extraction;

/// <summary>
/// Extracts document references for a resource
/// </summary>
internal static class ReferenceExtractor
{
    /// <summary>
    /// Takes an API JSON body for the resource and extracts the document reference information from the JSON body.
    /// </summary>
    public static DocumentReference[] ExtractReferences(this ResourceSchema resourceSchema, JsonNode documentBody, ILogger _logger)
    {
        _logger.LogDebug("ReferenceExtractor.Extract");

        List<DocumentReference> result = [];

        foreach (DocumentPath documentPath in resourceSchema.DocumentPaths)
        {
            if (!documentPath.IsReference)
                continue;
            if (documentPath.IsDescriptor)
                continue;

            // Extract the reference values from the document
            IntermediateReferenceElement[] intermediateReferenceElements = documentPath
                .ReferenceJsonPathsElements.Select(
                    referenceJsonPathsElement => new IntermediateReferenceElement(
                        referenceJsonPathsElement.IdentityJsonPath,
                        documentBody
                            .SelectNodesFromArrayPathCoerceToStrings(
                                referenceJsonPathsElement.ReferenceJsonPath.Value,
                                _logger
                            )
                            .ToArray()
                    )
                )
                .ToArray();

            // If a JsonPath selection had no results, we can assume an optional reference wasn't there
            if (Array.Exists(intermediateReferenceElements, x => x.ValueSlice.Length == 0))
                continue;

            int valueSliceLength = intermediateReferenceElements[0].ValueSlice.Length;

            // Number of document values from resolved JsonPaths should all be the same, otherwise something is very wrong
            Trace.Assert(
                Array.TrueForAll(intermediateReferenceElements, x => x.ValueSlice.Length == valueSliceLength),
                "Length of document value slices are not equal"
            );

            BaseResourceInfo resourceInfo =
                new(documentPath.ProjectName, documentPath.ResourceName, documentPath.IsDescriptor);

            // Reorient intermediateReferenceElements into actual DocumentReferences
            for (int index = 0; index < valueSliceLength; index += 1)
            {
                List<IDocumentIdentityElement> documentIdentityElements = [];

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

                result.Add(new(resourceInfo, new DocumentIdentity(documentIdentityElements)));
            }
        }

        return result.ToArray();
    }
}
