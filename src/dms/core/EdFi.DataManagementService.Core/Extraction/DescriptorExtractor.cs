// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Extraction;

/// <summary>
/// Extracts descriptor references for a resource
/// </summary>
internal static class DescriptorExtractor
{
    /// <summary>
    /// Takes mapping-set relational metadata and extracts every descriptor reference needed by the
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

        // Every raw value from both passes is validated before any DescriptorReference is built, so
        // one request reports all of its invalid descriptor values together.
        List<RawDescriptorValue> rawValues =
        [
            .. CollectSchemaPathValues(resourceSchema, documentBody, logger),
        ];

        if (!resourceInfo.IsDescriptor)
        {
            var resource = new QualifiedResourceName(
                resourceInfo.ProjectName.Value,
                resourceInfo.ResourceName.Value
            );
            var resourceModel = mappingSet.GetConcreteResourceModelOrThrow(resource).RelationalModel;

            foreach (var descriptorEdgeSource in resourceModel.DescriptorEdgeSources)
            {
                rawValues.AddRange(
                    CollectValues(
                        CreateDescriptorResourceInfo(descriptorEdgeSource.DescriptorResource),
                        descriptorEdgeSource.DescriptorValuePath.Canonical,
                        documentBody,
                        logger
                    )
                );
            }
        }

        ThrowIfAnyInvalid(rawValues);

        Dictionary<string, DescriptorReference> descriptorReferencesByPath = new(StringComparer.Ordinal);
        AddDescriptorReferences(descriptorReferencesByPath, rawValues.Select(CreateDescriptorReference));

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

        List<RawDescriptorValue> rawValues =
        [
            .. CollectSchemaPathValues(resourceSchema, documentBody, logger),
        ];
        ThrowIfAnyInvalid(rawValues);

        return [.. rawValues.Select(CreateDescriptorReference)];
    }

    /// <summary>
    /// A descriptor value as found in the body, at its concrete JSON path, before validation.
    /// </summary>
    private sealed record RawDescriptorValue(
        BaseResourceInfo ResourceInfo,
        string? Value,
        string ConcretePath
    );

    private static IEnumerable<RawDescriptorValue> CollectSchemaPathValues(
        ResourceSchema resourceSchema,
        JsonNode documentBody,
        ILogger logger
    )
    {
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

            foreach (
                var rawValue in CollectValues(resourceInfo, documentPath.Path.Value, documentBody, logger)
            )
            {
                yield return rawValue;
            }
        }
    }

    private static IEnumerable<RawDescriptorValue> CollectValues(
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
            yield return new RawDescriptorValue(resourceInfo, descriptorUri.value, descriptorUri.jsonPath);
        }
    }

    /// <summary>
    /// Throws <see cref="ReferenceExtractionValidationException"/> with one failure per concrete path whose
    /// value contains NUL or an unpaired surrogate.
    /// </summary>
    private static void ThrowIfAnyInvalid(IEnumerable<RawDescriptorValue> rawValues)
    {
        WriteValidationFailure[] failures =
        [
            .. rawValues
                .Where(rawValue =>
                    rawValue.Value is not null && !DescriptorText.IsWellFormedWithoutNul(rawValue.Value)
                )
                .Select(rawValue => rawValue.ConcretePath)
                .Distinct(StringComparer.Ordinal)
                .Select(path => new WriteValidationFailure(
                    new JsonPath(path),
                    DescriptorText.InvalidValueMessage
                )),
        ];

        if (failures.Length > 0)
        {
            throw new ReferenceExtractionValidationException(failures);
        }
    }

    private static DescriptorReference CreateDescriptorReference(RawDescriptorValue rawValue)
    {
        var (resourceInfo, descriptorUri, concretePath) = rawValue;

        // Normalize the entire URI to lowercase for case-insensitive matching.
        var normalizedDescriptorUriValue = descriptorUri is null
            ? string.Empty
            : DescriptorText.AssertWellFormedWithoutNul(descriptorUri).ToLowerInvariant();
        DocumentIdentity documentIdentity = new([
            new DocumentIdentityElement(
                DocumentIdentity.DescriptorIdentityJsonPath,
                normalizedDescriptorUriValue
            ),
        ]);

        return new DescriptorReference(resourceInfo, documentIdentity, new JsonPath(concretePath));
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
                    || !DocumentIdentityComparer.Instance.Equals(
                        existingReference.DocumentIdentity,
                        descriptorReference.DocumentIdentity
                    )
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
