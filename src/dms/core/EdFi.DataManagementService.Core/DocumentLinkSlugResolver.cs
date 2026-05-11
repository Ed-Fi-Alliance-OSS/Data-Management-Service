// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// Core-side implementation of <see cref="IDocumentLinkSlugResolver"/>. Walks
/// <see cref="MappingSet.ResourceKeyById"/> to the <see cref="ResourceKeyEntry"/>, then
/// resolves the concrete <see cref="ProjectSchema"/> through
/// <see cref="IApiSchemaProvider"/> to produce the <c>(projectEndpointName,
/// endpointName, resourceName)</c> slug triple used by reference-link emission.
/// </summary>
public sealed class DocumentLinkSlugResolver(
    IApiSchemaProvider apiSchemaProvider,
    ILogger<DocumentLinkSlugResolver> logger
) : IDocumentLinkSlugResolver
{
    private readonly IApiSchemaProvider _apiSchemaProvider = apiSchemaProvider;
    private readonly ILogger<DocumentLinkSlugResolver> _logger = logger;
    private readonly ConcurrentDictionary<(MappingSetKey, short), DocumentLinkSlugTriple> _cache = new();

    public DocumentLinkSlugTriple Resolve(MappingSet mappingSet, short resourceKeyId)
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        return _cache.GetOrAdd((mappingSet.Key, resourceKeyId), _ => ResolveCore(mappingSet, resourceKeyId));
    }

    private DocumentLinkSlugTriple ResolveCore(MappingSet mappingSet, short resourceKeyId)
    {
        if (!mappingSet.ResourceKeyById.TryGetValue(resourceKeyId, out ResourceKeyEntry? entry))
        {
            throw new InvalidOperationException(
                $"ResourceKeyId {resourceKeyId} is not present in mapping set "
                    + $"'{mappingSet.Key.EffectiveSchemaHash}' (deployment invariant)."
            );
        }

        ApiSchemaDocuments apiSchemaDocuments = new(_apiSchemaProvider.GetApiSchemaNodes(), _logger);

        ProjectName projectName = new(entry.Resource.ProjectName);
        ProjectSchema? projectSchema = apiSchemaDocuments.FindProjectSchemaForProjectName(projectName);
        if (projectSchema is null)
        {
            throw new InvalidOperationException(
                $"ProjectSchema for ProjectName '{projectName.Value}' was not found while resolving "
                    + $"ResourceKeyId {resourceKeyId} (deployment invariant)."
            );
        }

        ResourceName resourceName = new(entry.Resource.ResourceName);
        EndpointName endpointName = projectSchema.GetEndpointNameFromResourceName(resourceName);

        return new DocumentLinkSlugTriple(
            ProjectEndpointName: projectSchema.ProjectEndpointName.Value,
            EndpointName: endpointName.Value,
            ResourceName: entry.Resource.ResourceName
        );
    }
}
