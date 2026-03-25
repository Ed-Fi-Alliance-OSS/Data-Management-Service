// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Resolves request-scoped document and descriptor references for relational write prerequisites.
/// </summary>
public interface IReferenceResolver
{
    /// <summary>
    /// Resolves all extracted references for the supplied request context.
    /// </summary>
    Task<ResolvedReferenceSet> ResolveAsync(
        ReferenceResolverRequest request,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Request-scoped inputs for relational reference resolution.
/// </summary>
/// <param name="MappingSet">The resolved runtime mapping set for the active request.</param>
/// <param name="RequestResource">The resource being written.</param>
/// <param name="DocumentReferences">Extracted document references from the request body.</param>
/// <param name="DescriptorReferences">Extracted descriptor references from the request body.</param>
public sealed record ReferenceResolverRequest(
    MappingSet MappingSet,
    QualifiedResourceName RequestResource,
    IReadOnlyList<DocumentReference> DocumentReferences,
    IReadOnlyList<DescriptorReference> DescriptorReferences
);

/// <summary>
/// Narrow adapter seam for resolving deduped referential ids through a dialect-specific backend.
/// </summary>
public interface IReferenceResolverAdapter
{
    /// <summary>
    /// Resolves deduped referential ids for the active request context.
    /// </summary>
    Task<IReadOnlyList<ReferenceLookupResult>> ResolveAsync(
        ReferenceLookupRequest request,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Deduped relational lookup request emitted by <see cref="IReferenceResolver" />.
/// </summary>
/// <param name="MappingSet">The resolved runtime mapping set for the active request.</param>
/// <param name="RequestResource">The resource being written.</param>
/// <param name="ReferentialIds">Deduped referential ids in first-seen request order.</param>
public sealed record ReferenceLookupRequest(
    MappingSet MappingSet,
    QualifiedResourceName RequestResource,
    IReadOnlyList<ReferentialId> ReferentialIds
);

/// <summary>
/// Raw lookup result for one resolved referential id.
/// </summary>
/// <param name="ReferentialId">The resolved referential id.</param>
/// <param name="DocumentId">The matched document id.</param>
/// <param name="ResourceKeyId">The matched resource key id.</param>
/// <param name="IsDescriptor">
/// Whether the matched document is present in <c>dms.Descriptor</c>.
/// </param>
public sealed record ReferenceLookupResult(
    ReferentialId ReferentialId,
    long DocumentId,
    short ResourceKeyId,
    bool IsDescriptor
);

/// <summary>
/// Request-local lookup snapshot keyed by referential id, including memoized misses.
/// </summary>
/// <param name="ReferentialId">The requested referential id.</param>
/// <param name="Result">The resolved lookup result, or <c>null</c> when no row was found.</param>
public sealed record ReferenceLookupSnapshot(ReferentialId ReferentialId, ReferenceLookupResult? Result);

/// <summary>
/// Request-local resolution product consumed by later write-path stages.
/// </summary>
/// <param name="DocumentIdByReferentialId">
/// Successful document-reference resolutions keyed by referential id.
/// </param>
/// <param name="DescriptorIdByKey">
/// Successful descriptor resolutions keyed by normalized descriptor uri and descriptor resource.
/// </param>
/// <param name="LookupsByReferentialId">
/// Raw lookup snapshots keyed by referential id, including memoized misses for later diagnostics/classification.
/// </param>
/// <param name="DocumentReferenceOccurrences">
/// Per-occurrence document-reference lookups in original extraction order.
/// </param>
/// <param name="DescriptorReferenceOccurrences">
/// Per-occurrence descriptor-reference lookups in original extraction order.
/// </param>
public sealed record ResolvedReferenceSet(
    IReadOnlyDictionary<ReferentialId, long> DocumentIdByReferentialId,
    IReadOnlyDictionary<DescriptorReferenceKey, long> DescriptorIdByKey,
    IReadOnlyDictionary<ReferentialId, ReferenceLookupSnapshot> LookupsByReferentialId,
    IReadOnlyList<ResolvedDocumentReferenceOccurrence> DocumentReferenceOccurrences,
    IReadOnlyList<ResolvedDescriptorReferenceOccurrence> DescriptorReferenceOccurrences
);

/// <summary>
/// Successful descriptor resolution key used by write execution.
/// </summary>
/// <param name="NormalizedUri">The normalized descriptor uri.</param>
/// <param name="DescriptorResource">The expected descriptor resource type.</param>
public readonly record struct DescriptorReferenceKey(
    string NormalizedUri,
    QualifiedResourceName DescriptorResource
);

/// <summary>
/// One document-reference occurrence paired with its request-local lookup snapshot.
/// </summary>
/// <param name="Reference">The extracted document-reference occurrence.</param>
/// <param name="Lookup">The memoized lookup snapshot for the occurrence's referential id.</param>
public sealed record ResolvedDocumentReferenceOccurrence(
    DocumentReference Reference,
    ReferenceLookupSnapshot Lookup
);

/// <summary>
/// One descriptor-reference occurrence paired with its request-local lookup snapshot.
/// </summary>
/// <param name="Reference">The extracted descriptor-reference occurrence.</param>
/// <param name="Lookup">The memoized lookup snapshot for the occurrence's referential id.</param>
public sealed record ResolvedDescriptorReferenceOccurrence(
    DescriptorReference Reference,
    ReferenceLookupSnapshot Lookup
);
