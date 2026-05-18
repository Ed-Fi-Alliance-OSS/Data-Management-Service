// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

internal sealed record RelationalEdOrgAuthorizationElementResolution(
    EdOrgSecurableElement Element,
    IReadOnlyList<ResolvedEdOrgSecurableElementCandidate> ResolvedCandidates
);

public sealed class RelationalEdOrgAuthorizationElementResolutionCache
{
    private readonly Func<
        ConcreteResourceModel,
        EdOrgSecurableElement,
        IReadOnlyList<ResolvedEdOrgSecurableElementCandidate>
    > _resolveCandidates;

    private readonly ConditionalWeakTable<
        MappingSet,
        ConcurrentDictionary<
            RelationalEdOrgAuthorizationElementCacheKey,
            RelationalEdOrgAuthorizationElementResolution
        >
    > _resolutionsByKeyByMappingSet = new();

    public RelationalEdOrgAuthorizationElementResolutionCache()
        : this(SecurableElementColumnPathResolver.ResolveEducationOrganizationCandidates) { }

    internal RelationalEdOrgAuthorizationElementResolutionCache(
        Func<
            ConcreteResourceModel,
            EdOrgSecurableElement,
            IReadOnlyList<ResolvedEdOrgSecurableElementCandidate>
        > resolveCandidates
    )
    {
        _resolveCandidates = resolveCandidates ?? throw new ArgumentNullException(nameof(resolveCandidates));
    }

    internal IReadOnlyList<RelationalEdOrgAuthorizationElementResolution> GetOrResolveAll(
        MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        var concreteResourceModel = mappingSet.GetConcreteResourceModelOrThrow(resource);
        var resolutionsByKey = _resolutionsByKeyByMappingSet.GetValue(
            mappingSet,
            static _ => new ConcurrentDictionary<
                RelationalEdOrgAuthorizationElementCacheKey,
                RelationalEdOrgAuthorizationElementResolution
            >()
        );
        var elements = concreteResourceModel.SecurableElements.EducationOrganization;
        var resolutions = new RelationalEdOrgAuthorizationElementResolution[elements.Count];

        for (var index = 0; index < elements.Count; index++)
        {
            var element = elements[index];
            var cacheKey = new RelationalEdOrgAuthorizationElementCacheKey(resource, element);

            resolutions[index] = resolutionsByKey.GetOrAdd(
                cacheKey,
                _ => new RelationalEdOrgAuthorizationElementResolution(
                    element,
                    _resolveCandidates(concreteResourceModel, element)
                )
            );
        }

        return resolutions;
    }
}

internal readonly record struct RelationalEdOrgAuthorizationElementCacheKey(
    QualifiedResourceName Resource,
    EdOrgSecurableElement Element
);
