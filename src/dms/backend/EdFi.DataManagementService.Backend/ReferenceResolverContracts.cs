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
/// Request-local resolution product consumed by later write-path stages.
/// </summary>
/// <param name="SuccessfulDocumentReferencesByPath">
/// Successful document-reference resolutions keyed by concrete extracted JSON path.
/// </param>
/// <param name="SuccessfulDescriptorReferencesByPath">
/// Successful descriptor resolutions keyed by concrete extracted JSON path.
/// </param>
/// <param name="DocumentReferences">
/// Successful document-reference resolutions looked up by natural key, independent of which identity array
/// instance the caller holds.
/// </param>
/// <param name="InvalidDocumentReferences">
/// Classified document-reference failures keyed by concrete extracted JSON path occurrence.
/// </param>
/// <param name="InvalidDescriptorReferences">
/// Classified descriptor-reference failures keyed by concrete extracted JSON path occurrence.
/// </param>
public sealed record ResolvedReferenceSet(
    IReadOnlyDictionary<JsonPath, ResolvedDocumentReference> SuccessfulDocumentReferencesByPath,
    IReadOnlyDictionary<JsonPath, ResolvedDescriptorReference> SuccessfulDescriptorReferencesByPath,
    IResolvedDocumentReferenceMap DocumentReferences,
    IReadOnlyList<DocumentReferenceFailure> InvalidDocumentReferences,
    IReadOnlyList<DescriptorReferenceFailure> InvalidDescriptorReferences
)
{
    public bool HasFailures => InvalidDocumentReferences.Count > 0 || InvalidDescriptorReferences.Count > 0;

    /// <summary>
    /// The document-reference failures whose referenced document does not exist. These are the only
    /// failures that can be deferred so authorization is evaluated before the missing-reference conflict.
    /// </summary>
    public IEnumerable<DocumentReferenceFailure> MissingDocumentReferences =>
        InvalidDocumentReferences.Where(static failure =>
            failure.Reason is DocumentReferenceFailureReason.Missing
        );
}

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
/// <param name="DescriptorId">The resolved compact descriptor id for stored references.</param>
/// <param name="DocumentId">The resolved descriptor document id for this concrete occurrence.</param>
/// <param name="ResourceKeyId">The resolved resource key id for this concrete occurrence.</param>
public sealed record ResolvedDescriptorReference(
    DescriptorReference Reference,
    int DescriptorId,
    long DocumentId,
    short ResourceKeyId
);
