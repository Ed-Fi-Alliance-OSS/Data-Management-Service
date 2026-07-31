// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// Core-side implementation of <see cref="IDocumentLinkSlugResolver"/>. Splits the auxiliary
/// lookup's <c>"{ProjectName}:{ResourceName}"</c> discriminator into its two parts, then
/// resolves the concrete <see cref="ProjectSchema"/> through
/// <see cref="IApiSchemaProvider"/> to produce the <c>(projectEndpointName,
/// endpointName, resourceName)</c> slug triple used by reference-link emission.
/// </summary>
/// <remarks>
/// The per-discriminator cache is held in a <see cref="ConditionalWeakTable{TKey,TValue}"/>
/// keyed by <see cref="MappingSet"/> instance, mirroring
/// <c>RelationalDeleteConstraintResolver</c>. Cache entries are reused for the lifetime of
/// the mapping set and released when the mapping set is collected, so a schema swap (which
/// produces a new <see cref="MappingSet"/> instance) does not leak entries from the prior
/// instance.
/// </remarks>
public sealed class DocumentLinkSlugResolver(
    IApiSchemaProvider apiSchemaProvider,
    ILogger<DocumentLinkSlugResolver> logger
) : IDocumentLinkSlugResolver
{
    private readonly IApiSchemaProvider _apiSchemaProvider = apiSchemaProvider;
    private readonly ILogger<DocumentLinkSlugResolver> _logger = logger;
    private readonly ConditionalWeakTable<
        MappingSet,
        ConcurrentDictionary<string, DocumentLinkSlugTriple>
    > _cacheByMappingSet = new();

    public DocumentLinkSlugTriple Resolve(MappingSet mappingSet, string discriminator)
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(discriminator);

        var cache = _cacheByMappingSet.GetValue(
            mappingSet,
            static _ => new ConcurrentDictionary<string, DocumentLinkSlugTriple>(StringComparer.Ordinal)
        );
        return cache.GetOrAdd(discriminator, key => ResolveCore(mappingSet, key));
    }

    private DocumentLinkSlugTriple ResolveCore(MappingSet mappingSet, string discriminator)
    {
        // The discriminator is "{ProjectName}:{ResourceName}" — the same literal the abstract
        // identity maintenance triggers store and the auxiliary lookup's concrete branches embed.
        // Split on the FIRST ':' so a resource name containing a colon stays intact.
        int separatorIndex = discriminator.IndexOf(':', StringComparison.Ordinal);

        if (separatorIndex <= 0 || separatorIndex == discriminator.Length - 1)
        {
            throw new InvalidOperationException(
                $"Document-reference discriminator '{discriminator}' is not in the expected "
                    + $"'{{ProjectName}}:{{ResourceName}}' form (mapping set "
                    + $"'{mappingSet.Key.EffectiveSchemaHash}', deployment invariant)."
            );
        }

        string projectNameValue = discriminator[..separatorIndex];
        string resourceNameValue = discriminator[(separatorIndex + 1)..];

        ApiSchemaDocuments apiSchemaDocuments = new(_apiSchemaProvider.GetApiSchemaNodes(), _logger);

        ProjectName projectName = new(projectNameValue);
        ProjectSchema? projectSchema = apiSchemaDocuments.FindProjectSchemaForProjectName(projectName);
        if (projectSchema is null)
        {
            throw new InvalidOperationException(
                $"ProjectSchema for ProjectName '{projectName.Value}' was not found while resolving "
                    + $"document-reference discriminator '{discriminator}' (deployment invariant)."
            );
        }

        ResourceName resourceName = new(resourceNameValue);
        EndpointName endpointName = projectSchema.GetEndpointNameFromResourceName(resourceName);

        return new DocumentLinkSlugTriple(
            ProjectEndpointName: projectSchema.ProjectEndpointName.Value,
            EndpointName: endpointName.Value,
            ResourceName: resourceNameValue
        );
    }
}
