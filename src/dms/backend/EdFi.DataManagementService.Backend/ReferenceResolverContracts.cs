// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
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
/// Creates the request-scoped dialect adapter used by <see cref="IReferenceResolver" />.
/// </summary>
public interface IReferenceResolverAdapterFactory
{
    /// <summary>
    /// Creates the adapter for the current request scope.
    /// </summary>
    IReferenceResolverAdapter CreateAdapter();
}

/// <summary>
/// Deduped relational lookup request emitted by <see cref="IReferenceResolver" />.
/// </summary>
/// <param name="MappingSet">The resolved runtime mapping set for the active request.</param>
/// <param name="RequestResource">The resource being written.</param>
/// <param name="Lookups">Deduped lookups in first-seen request order.</param>
public sealed record ReferenceLookupRequest(
    MappingSet MappingSet,
    QualifiedResourceName RequestResource,
    IReadOnlyList<ReferenceLookupRequestEntry> Lookups
)
{
    public IReadOnlyList<ReferentialId> ReferentialIds { get; } =
        Lookups.Select(static lookup => lookup.ReferentialId).ToArray();
}

/// <summary>
/// One deduped reference lookup request carrying the target identity needed to verify a resolved hit.
/// </summary>
/// <param name="ReferentialId">The deduped requested referential id.</param>
/// <param name="RequestedResource">The target resource requested by the caller.</param>
/// <param name="RequestedIdentity">
/// The ordered requested natural-key identity for the target resource.
/// </param>
/// <param name="ExpectedVerificationIdentityKey">
/// The normalized expected identity key derived from <paramref name="RequestedIdentity" />.
/// </param>
public sealed record ReferenceLookupRequestEntry(
    ReferentialId ReferentialId,
    QualifiedResourceName RequestedResource,
    DocumentIdentity RequestedIdentity,
    string ExpectedVerificationIdentityKey
);

/// <summary>
/// Raw lookup result for one resolved referential id.
/// </summary>
/// <param name="ReferentialId">The resolved referential id.</param>
/// <param name="DocumentId">The matched document id.</param>
/// <param name="ResourceKeyId">
/// The matched document resource key id from <c>dms.Document</c>.
/// </param>
/// <param name="ReferentialIdentityResourceKeyId">
/// The resource key id from the matched <c>dms.ReferentialIdentity</c> row.
/// This may differ from <paramref name="ResourceKeyId" /> for alias rows.
/// </param>
/// <param name="IsDescriptor">
/// Whether the matched document is present in <c>dms.Descriptor</c>.
/// </param>
/// <param name="VerificationIdentityKey">
/// The authoritative natural-key witness projected from the matched document or descriptor.
/// </param>
public sealed record ReferenceLookupResult(
    ReferentialId ReferentialId,
    long DocumentId,
    short ResourceKeyId,
    short ReferentialIdentityResourceKeyId,
    bool IsDescriptor,
    string? VerificationIdentityKey = null
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
/// <param name="SuccessfulDocumentReferencesByPath">
/// Successful document-reference resolutions keyed by concrete extracted JSON path.
/// </param>
/// <param name="SuccessfulDescriptorReferencesByPath">
/// Successful descriptor resolutions keyed by concrete extracted JSON path.
/// </param>
/// <param name="LookupsByReferentialId">
/// Raw lookup snapshots keyed by referential id, including memoized misses for later diagnostics/classification.
/// </param>
/// <param name="InvalidDocumentReferences">
/// Classified document-reference failures keyed by concrete extracted JSON path occurrence.
/// </param>
/// <param name="InvalidDescriptorReferences">
/// Classified descriptor-reference failures keyed by concrete extracted JSON path occurrence.
/// </param>
/// <param name="DocumentReferenceOccurrences">
/// Per-occurrence document-reference lookups in original extraction order.
/// </param>
/// <param name="DescriptorReferenceOccurrences">
/// Per-occurrence descriptor-reference lookups in original extraction order.
/// </param>
public sealed record ResolvedReferenceSet(
    IReadOnlyDictionary<JsonPath, ResolvedDocumentReference> SuccessfulDocumentReferencesByPath,
    IReadOnlyDictionary<JsonPath, ResolvedDescriptorReference> SuccessfulDescriptorReferencesByPath,
    IReadOnlyDictionary<ReferentialId, ReferenceLookupSnapshot> LookupsByReferentialId,
    IReadOnlyList<DocumentReferenceFailure> InvalidDocumentReferences,
    IReadOnlyList<DescriptorReferenceFailure> InvalidDescriptorReferences,
    IReadOnlyList<ResolvedDocumentReferenceOccurrence> DocumentReferenceOccurrences,
    IReadOnlyList<ResolvedDescriptorReferenceOccurrence> DescriptorReferenceOccurrences
)
{
    public bool HasFailures => InvalidDocumentReferences.Count > 0 || InvalidDescriptorReferences.Count > 0;
}

/// <summary>
/// Normalized descriptor identity key used to sanity-check path-keyed successful resolutions.
/// </summary>
internal readonly record struct DescriptorReferenceKey(
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

/// <summary>
/// Successful document-reference resolution keyed by its concrete extracted JSON path.
/// </summary>
/// <param name="Reference">The extracted document-reference occurrence.</param>
/// <param name="DocumentId">The resolved document id for this concrete occurrence.</param>
/// <param name="ResourceKeyId">The resolved resource key id for this concrete occurrence.</param>
public sealed record ResolvedDocumentReference(
    DocumentReference Reference,
    long DocumentId,
    short ResourceKeyId
);

/// <summary>
/// Successful descriptor resolution keyed by its concrete extracted JSON path.
/// </summary>
/// <param name="Reference">The extracted descriptor-reference occurrence.</param>
/// <param name="DocumentId">The resolved descriptor document id for this concrete occurrence.</param>
/// <param name="ResourceKeyId">The resolved resource key id for this concrete occurrence.</param>
public sealed record ResolvedDescriptorReference(
    DescriptorReference Reference,
    long DocumentId,
    short ResourceKeyId
);
