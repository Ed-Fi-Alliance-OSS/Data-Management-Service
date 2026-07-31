// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Resolves the project-endpoint slug, endpoint slug, and concrete resource name for a resource
/// identified by its <c>"{ProjectName}:{ResourceName}"</c> discriminator. Used by the
/// reconstitution reference-writer to render <c>link.rel</c> / <c>link.href</c> values
/// without taking a dependency on the API schema layer from <c>Backend.Plans</c>.
/// </summary>
public interface IDocumentLinkSlugResolver
{
    /// <summary>
    /// Resolves the slug triple for a resource discriminator.
    /// </summary>
    /// <param name="mappingSet">
    /// The mapping set the resolution is scoped to. Passed per call rather than captured
    /// globally — the request contracts already carry the mapping set, and it keys the
    /// implementation's per-schema resolution cache.
    /// </param>
    /// <param name="discriminator">
    /// The referenced document's <c>"{ProjectName}:{ResourceName}"</c> discriminator (e.g.,
    /// <c>Ed-Fi:School</c>) as returned by the document-reference auxiliary lookup. Always the
    /// concrete resource, including for abstract references.
    /// </param>
    /// <returns>The resolved slug triple.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="discriminator"/> is not well-formed, or when the resolved
    /// project schema / resource cannot be located (deployment invariants).
    /// </exception>
    DocumentLinkSlugTriple Resolve(MappingSet mappingSet, string discriminator);
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
/// concrete subclass for abstract references — the auxiliary lookup's discriminator already
/// names the concrete resource, so no subclass inference happens here.
/// </param>
public sealed record DocumentLinkSlugTriple(
    string ProjectEndpointName,
    string EndpointName,
    string ResourceName
);
