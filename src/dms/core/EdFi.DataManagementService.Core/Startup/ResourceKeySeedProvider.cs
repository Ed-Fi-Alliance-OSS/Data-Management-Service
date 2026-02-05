// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Provides deterministic resource key seeds derived from the API schema.
/// Seeds are ordered by (ProjectName, ResourceName) using ordinal comparison
/// and assigned sequential ResourceKeyId values from 1..N.
/// </summary>
internal class ResourceKeySeedProvider(ILogger<ResourceKeySeedProvider> logger) : IResourceKeySeedProvider
{
    private readonly ILogger<ResourceKeySeedProvider> _logger = logger;

    /// <summary>
    /// Maximum number of resource keys supported (smallint max value).
    /// </summary>
    private const int MaxResourceKeyCount = 32767;

    /// <inheritdoc />
    public IReadOnlyList<ResourceKeySeed> GetSeeds(ApiSchemaDocumentNodes nodes)
    {
        _logger.LogDebug("Deriving resource key seeds from API schema");

        var apiSchemaDocuments = new ApiSchemaDocuments(nodes, _logger);
        var seedEntries =
            new List<(string ProjectName, string ResourceName, string ResourceVersion, bool IsAbstract)>();

        // Collect resources from all projects
        foreach (var projectSchema in apiSchemaDocuments.GetAllProjectSchemas())
        {
            var projectName = projectSchema.ProjectName.Value;
            var resourceVersion = projectSchema.ResourceVersion.Value;

            // Collect concrete resources (excluding resource extensions)
            foreach (var resourceSchemaNode in projectSchema.GetAllResourceSchemaNodes())
            {
                var resourceSchema = new ResourceSchema(resourceSchemaNode);

                // Skip resource extensions - they extend existing resources, not new resource keys
                if (resourceSchema.IsResourceExtension)
                {
                    continue;
                }

                seedEntries.Add(
                    (projectName, resourceSchema.ResourceName.Value, resourceVersion, IsAbstract: false)
                );
            }

            // Collect abstract resources
            foreach (var abstractResource in projectSchema.AbstractResources)
            {
                seedEntries.Add(
                    (projectName, abstractResource.ResourceName.Value, resourceVersion, IsAbstract: true)
                );
            }
        }

        // Sort by (ProjectName, ResourceName) using ordinal comparison
        seedEntries.Sort(
            (a, b) =>
            {
                var projectCompare = string.Compare(a.ProjectName, b.ProjectName, StringComparison.Ordinal);
                return projectCompare != 0
                    ? projectCompare
                    : string.Compare(a.ResourceName, b.ResourceName, StringComparison.Ordinal);
            }
        );

        // Validate count does not exceed smallint max
        if (seedEntries.Count > MaxResourceKeyCount)
        {
            throw new InvalidOperationException(
                $"Resource key count ({seedEntries.Count}) exceeds maximum allowed ({MaxResourceKeyCount}). "
                    + "The number of resources in the schema exceeds the smallint limit for ResourceKeyId."
            );
        }

        // Assign sequential ResourceKeyId from 1..N
        var seeds = new List<ResourceKeySeed>(seedEntries.Count);
        for (var i = 0; i < seedEntries.Count; i++)
        {
            var entry = seedEntries[i];
            seeds.Add(
                new ResourceKeySeed(
                    ResourceKeyId: (short)(i + 1),
                    ProjectName: entry.ProjectName,
                    ResourceName: entry.ResourceName,
                    ResourceVersion: entry.ResourceVersion,
                    IsAbstract: entry.IsAbstract
                )
            );
        }

        _logger.LogDebug("Derived {Count} resource key seeds from API schema", seeds.Count);

        return seeds.AsReadOnly();
    }

    /// <inheritdoc />
    public byte[] ComputeSeedHash(IReadOnlyList<ResourceKeySeed> seeds)
    {
        // Build manifest string:
        // resource-key-seed-hash:v1
        // {id}|{projectName}|{resourceName}|{resourceVersion}|{isAbstract}
        // ...
        // NOTE: Use explicit '\n' for cross-platform determinism. Do NOT use AppendLine()
        // which uses Environment.NewLine (CRLF on Windows, LF on Linux).
        var manifestBuilder = new StringBuilder();
        manifestBuilder.Append(SchemaHashConstants.ResourceKeySeedHashVersion).Append('\n');

        // Entries must be in ascending ResourceKeyId order. Seeds from GetSeeds are already sorted,
        // but we sort defensively here to guarantee determinism regardless of the caller's source.
        foreach (var seed in seeds.OrderBy(s => s.ResourceKeyId))
        {
            manifestBuilder
                .Append(seed.ResourceKeyId)
                .Append('|')
                .Append(seed.ProjectName)
                .Append('|')
                .Append(seed.ResourceName)
                .Append('|')
                .Append(seed.ResourceVersion)
                .Append('|')
                .Append(seed.IsAbstract ? '1' : '0')
                .Append('\n');
        }

        var manifest = manifestBuilder.ToString();
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(manifest));

        _logger.LogDebug("Computed resource key seed hash over {Count} entries", seeds.Count);

        return hashBytes;
    }
}
