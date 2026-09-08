// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Resolves the project-endpoint slug, endpoint slug, and concrete resource name for a
/// <see cref="ResourceKeyEntry"/> identified by its <c>ResourceKeyId</c>. Used by the
/// reconstitution reference-writer to render <c>link.rel</c> / <c>link.href</c> values
/// without taking a dependency on the API schema layer from <c>Backend.Plans</c>.
/// </summary>
public interface IDocumentLinkSlugResolver
{
    /// <summary>
    /// Resolves the slug triple for a resource key.
    /// </summary>
    /// <param name="mappingSet">
    /// The mapping set whose <see cref="MappingSet.ResourceKeyById"/> contains the key.
    /// Passed per call rather than captured globally — the request contracts already
    /// carry the mapping set, and the design specifies it as the resolution input.
    /// </param>
    /// <param name="resourceKeyId">The resource key identifier.</param>
    /// <returns>The resolved slug triple.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="resourceKeyId"/> is not present in the mapping set,
    /// or when the resolved project schema cannot be located (deployment invariants).
    /// </exception>
    DocumentLinkSlugTriple Resolve(MappingSet mappingSet, short resourceKeyId);
}

/// <summary>
/// The slug components needed to render a document-reference link.
/// </summary>
/// <param name="ProjectEndpointName">
/// The project endpoint slug (e.g., <c>ed-fi</c>) used as the first path segment.
/// </param>
/// <param name="EndpointName">
/// The resource endpoint slug (e.g., <c>schools</c>) used as the second path segment.
/// </param>
/// <param name="ResourceName">
/// The concrete resource name (e.g., <c>School</c>) used as <c>link.rel</c>. Always the
/// concrete subclass for abstract references — resolved through
/// <see cref="MappingSet.ResourceKeyById"/>, no discriminator parsing.
/// </param>
public sealed record DocumentLinkSlugTriple(
    string ProjectEndpointName,
    string EndpointName,
    string ResourceName
);
