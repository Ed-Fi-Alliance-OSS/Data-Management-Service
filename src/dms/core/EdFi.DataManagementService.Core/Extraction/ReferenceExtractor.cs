// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Extraction.ReferentialIdCalculator;

namespace EdFi.DataManagementService.Core.Extraction;

/// <summary>
/// Extracts document references for a resource
/// </summary>
internal static class ReferenceExtractor
{
    private enum InvalidReferenceIdentityMemberValueKind
    {
        Null,
        JsonObject,
        JsonArray,
        NonScalar,
    }

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
        ILogger logger,
        ReferenceExtractionMode extractionMode = ReferenceExtractionMode.RelationalWriteValidation
    )
    {
        logger.LogDebug("ReferenceExtractor.Extract");

        List<DocumentReference> documentReferences = [];
        List<DocumentReferenceArray> documentReferenceArrays = [];
        List<WriteValidationFailure> validationFailures = [];

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

            string wildcardParentPath = StripLastJsonPathSegment(identityElements[0].ReferenceJsonPath.Value);
            JsonPathAndNode[] referenceOccurrences = documentBody
                .SelectNodesAndLocationFromArrayPath(wildcardParentPath, logger)
                .ToArray();

            // If no matches, assume an optional reference wasn't there
            if (referenceOccurrences.Length == 0)
            {
                continue;
            }

            var identityElementsByReferencePath = referenceOccurrences.ToDictionary(
                occurrence => occurrence.jsonPath,
                _ => new DocumentIdentityElement?[identityElements.Length],
                StringComparer.Ordinal
            );
            var invalidIdentityElementsByReferencePath = referenceOccurrences.ToDictionary(
                occurrence => occurrence.jsonPath,
                _ => new bool[identityElements.Length],
                StringComparer.Ordinal
            );
            List<DocumentReference> documentReferencesForThisPath = [];

            foreach (JsonPathAndNode referenceOccurrence in referenceOccurrences)
            {
                if (referenceOccurrence.node is JsonObject)
                {
                    continue;
                }

                if (extractionMode == ReferenceExtractionMode.RelationalWriteValidation)
                {
                    validationFailures.Add(
                        new WriteValidationFailure(
                            new JsonPath(referenceOccurrence.jsonPath),
                            $"Reference object '{referenceOccurrence.jsonPath}' for resource '{documentPath.ResourceName.Value}' must be a JSON object."
                        )
                    );
                }
            }

            for (
                var identityElementIndex = 0;
                identityElementIndex < identityElements.Length;
                identityElementIndex++
            )
            {
                var element = identityElements[identityElementIndex];
                JsonPathAndNode[] pathValues = documentBody
                    .SelectNodesAndLocationFromArrayPath(element.ReferenceJsonPath.Value, logger)
                    .ToArray();

                foreach (JsonPathAndNode pathValue in pathValues)
                {
                    string concreteParentPath = StripLastJsonPathSegment(pathValue.jsonPath);

                    if (
                        !identityElementsByReferencePath.TryGetValue(
                            concreteParentPath,
                            out DocumentIdentityElement?[]? collectedIdentityElements
                        )
                    )
                    {
                        throw new InvalidOperationException(
                            $"Reference identity path '{pathValue.jsonPath}' did not match a concrete reference object for resource '{documentPath.ResourceName.Value}'."
                        );
                    }

                    bool[] invalidIdentityElements = invalidIdentityElementsByReferencePath[
                        concreteParentPath
                    ];

                    if (TryGetScalarIdentityValue(pathValue.node, out string? identityValue))
                    {
                        collectedIdentityElements[identityElementIndex] = new DocumentIdentityElement(
                            element.IdentityJsonPath,
                            NormalizeReferenceIdentityValue(element.IdentityJsonPath, identityValue)
                        );
                        continue;
                    }

                    invalidIdentityElements[identityElementIndex] = true;

                    if (extractionMode == ReferenceExtractionMode.RelationalWriteValidation)
                    {
                        validationFailures.Add(
                            new WriteValidationFailure(
                                new JsonPath(pathValue.jsonPath),
                                $"Reference identity member '{pathValue.jsonPath}' for resource '{documentPath.ResourceName.Value}' must be a scalar value when present, but was {DescribeInvalidReferenceIdentityMemberValue(pathValue.node)}."
                            )
                        );
                    }
                }
            }

            // Each reference group must have exactly one value per identity element
            foreach (JsonPathAndNode referenceOccurrence in referenceOccurrences)
            {
                if (referenceOccurrence.node is not JsonObject)
                {
                    continue;
                }

                DocumentIdentityElement?[] collectedIdentityElements = identityElementsByReferencePath[
                    referenceOccurrence.jsonPath
                ];
                bool[] invalidIdentityElements = invalidIdentityElementsByReferencePath[
                    referenceOccurrence.jsonPath
                ];

                if (Array.Exists(invalidIdentityElements, static invalid => invalid))
                {
                    if (extractionMode == ReferenceExtractionMode.LegacyCompatibility)
                    {
                        throw CreateLegacyCompatibilityInvalidReferenceIdentityValueException();
                    }

                    continue;
                }

                string[] missingReferencePaths = identityElements
                    .Select((element, index) => (element, index))
                    .Where(entry =>
                        collectedIdentityElements[entry.index] is null
                        && !invalidIdentityElements[entry.index]
                    )
                    .Select(entry =>
                        BuildConcreteReferenceMemberPath(
                            referenceOccurrence.jsonPath,
                            wildcardParentPath,
                            entry.element.ReferenceJsonPath.Value
                        )
                    )
                    .ToArray();

                if (missingReferencePaths.Length > 0)
                {
                    if (extractionMode == ReferenceExtractionMode.LegacyCompatibility)
                    {
                        throw CreateLegacyCompatibilityMissingIdentityException(
                            documentPath,
                            referenceOccurrence.jsonPath,
                            identityElements.Length,
                            collectedIdentityElements.Count(static identityElement =>
                                identityElement is not null
                            )
                        );
                    }

                    if (extractionMode == ReferenceExtractionMode.RelationalWriteValidation)
                    {
                        validationFailures.Add(
                            new WriteValidationFailure(
                                new JsonPath(referenceOccurrence.jsonPath),
                                $"Reference object '{referenceOccurrence.jsonPath}' for resource '{documentPath.ResourceName.Value}' must include all identifying values when present. Missing: {string.Join(", ", missingReferencePaths.Select(path => $"'{path}'"))}."
                            )
                        );
                    }
                    continue;
                }

                BaseResourceInfo resourceInfo = new(
                    documentPath.ProjectName,
                    documentPath.ResourceName,
                    documentPath.IsDescriptor
                );

                DocumentIdentity documentIdentity = new([
                    .. collectedIdentityElements.Select(identityElement =>
                        identityElement
                        ?? throw new InvalidOperationException(
                            $"Reference '{referenceOccurrence.jsonPath}' for resource '{documentPath.ResourceName.Value}' was missing a collected identity element after validation."
                        )
                    ),
                ]);
                var documentReference = new DocumentReference(
                    resourceInfo,
                    documentIdentity,
                    ReferentialIdFrom(resourceInfo, documentIdentity),
                    new JsonPath(referenceOccurrence.jsonPath)
                );

                documentReferences.Add(documentReference);
                documentReferencesForThisPath.Add(documentReference);
            }

            if (documentReferencesForThisPath.Count > 0)
            {
                documentReferenceArrays.Add(new(new(wildcardParentPath), [.. documentReferencesForThisPath]));
            }
        }

        if (validationFailures.Count > 0)
        {
            throw new ReferenceExtractionValidationException([.. validationFailures.Distinct()]);
        }

        return (documentReferences.ToArray(), documentReferenceArrays.ToArray());
    }

    private static bool TryGetScalarIdentityValue(JsonNode? node, out string identityValue)
    {
        if (node is JsonValue jsonValue)
        {
            identityValue = jsonValue.ToString();
            return true;
        }

        identityValue = string.Empty;
        return false;
    }

    private static string NormalizeReferenceIdentityValue(JsonPath identityJsonPath, string identityValue)
    {
        return IsDescriptorIdentityPath(identityJsonPath) ? identityValue.ToLowerInvariant() : identityValue;
    }

    private static bool IsDescriptorIdentityPath(JsonPath identityJsonPath)
    {
        return identityJsonPath.Value.EndsWith("Descriptor", StringComparison.Ordinal);
    }

    private static string DescribeInvalidReferenceIdentityMemberValue(JsonNode? node)
    {
        return GetInvalidReferenceIdentityMemberValueKind(node) switch
        {
            InvalidReferenceIdentityMemberValueKind.Null => "null",
            InvalidReferenceIdentityMemberValueKind.JsonObject => "a JSON object",
            InvalidReferenceIdentityMemberValueKind.JsonArray => "a JSON array",
            InvalidReferenceIdentityMemberValueKind.NonScalar => "a non-scalar JSON value",
            _ => throw new InvalidOperationException(
                "Unhandled invalid reference identity member value kind."
            ),
        };
    }

    private static InvalidReferenceIdentityMemberValueKind GetInvalidReferenceIdentityMemberValueKind(
        JsonNode? node
    )
    {
        return node switch
        {
            null => InvalidReferenceIdentityMemberValueKind.Null,
            JsonObject => InvalidReferenceIdentityMemberValueKind.JsonObject,
            JsonArray => InvalidReferenceIdentityMemberValueKind.JsonArray,
            _ => InvalidReferenceIdentityMemberValueKind.NonScalar,
        };
    }

    private static string BuildConcreteReferenceMemberPath(
        string concreteReferenceObjectPath,
        string wildcardReferenceObjectPath,
        string wildcardReferenceMemberPath
    )
    {
        if (!wildcardReferenceMemberPath.StartsWith(wildcardReferenceObjectPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Reference member path '{wildcardReferenceMemberPath}' is not under reference object path '{wildcardReferenceObjectPath}'."
            );
        }

        return $"{concreteReferenceObjectPath}{wildcardReferenceMemberPath[wildcardReferenceObjectPath.Length..]}";
    }

    private static InvalidOperationException CreateLegacyCompatibilityMissingIdentityException(
        DocumentPath documentPath,
        string concreteReferenceObjectPath,
        int expectedIdentityElementCount,
        int actualIdentityElementCount
    )
    {
        return new InvalidOperationException(
            $"Reference '{documentPath.ResourceName.Value}' at '{concreteReferenceObjectPath}': expected {expectedIdentityElementCount} identity elements but found {actualIdentityElementCount}"
        );
    }

    private static InvalidOperationException CreateLegacyCompatibilityInvalidReferenceIdentityValueException()
    {
        return new InvalidOperationException("Unexpected JSONPath value error");
    }
}
