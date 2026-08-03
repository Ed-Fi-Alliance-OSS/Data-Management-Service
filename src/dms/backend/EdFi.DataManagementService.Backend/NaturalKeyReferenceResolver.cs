// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Request-scoped relational reference resolver that resolves each reference by seeking its target's
/// natural-key index, memoizing lookups for the current request.
/// </summary>
/// <remarks>
/// A drop-in replacement for <see cref="ReferenceResolver" />: same <see cref="IReferenceResolver" />
/// contract, same <see cref="ResolvedReferenceSet" /> shape (path-keyed maps, the original
/// <c>DocumentReference</c>/<c>DescriptorReference</c> embedded in each resolution, and
/// <see cref="ResolvedReferenceSet.LookupsByReferentialId" /> still keyed by the references' hashes), and
/// the same one-adapter-round-trip-per-call structure with misses memoized only after the round trip
/// completes.
///
/// <para>
/// What changes is the key: the dedupe/memo key is the reference's own
/// <c>(target resource, ordered identity values)</c> tuple rather than the UUIDv5 hash of it, and the
/// lookup itself is an index seek on the target's <c>UX_&lt;T&gt;_RefKey</c> (or
/// <c>UX_Descriptor_UriLowered_Discriminator</c>) instead of a join through
/// <c>dms.ReferentialIdentity</c> → <c>dms.Document</c>. There is consequently no witness string to
/// compare and no corruption check: the row a probe returns is by construction the row whose natural key
/// the caller asked for.
/// </para>
/// </remarks>
public sealed class NaturalKeyReferenceResolver : IReferenceResolver
{
    private readonly INaturalKeyLookupAdapter _adapter;
    private readonly ILogger? _logger;

    /// <summary>
    /// Request-scoped lookup memo, including misses. Keyed by the identity tuple, not by referential id,
    /// so two references that hash differently but name the same target row share one lookup.
    /// </summary>
    private readonly Dictionary<NaturalKeyLookupKey, NaturalKeyLookupOutcome> _memoizedOutcomes = [];

    /// <summary>
    /// The identity path ordering first seen for each target resource in this request scope. Replaces the
    /// old <c>ReferenceLookupVerificationShapeKey</c> guard: two lookups for the same target that disagree
    /// about the identity shape cannot both be describing the same compiled probe.
    /// </summary>
    private readonly Dictionary<QualifiedResourceName, IReadOnlyList<string>> _identityShapeByResource = [];

    public NaturalKeyReferenceResolver(
        INaturalKeyLookupAdapter adapter,
        ILogger<NaturalKeyReferenceResolver>? logger = null
    )
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _logger = logger;
    }

    public async Task<ResolvedReferenceSet> ResolveAsync(
        ReferenceResolverRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.MappingSet);
        ArgumentNullException.ThrowIfNull(request.DocumentReferences);
        ArgumentNullException.ThrowIfNull(request.DescriptorReferences);

        var mappingSet = request.MappingSet;

        var documentPlans = new ReferenceLookupPlan[request.DocumentReferences.Count];
        for (var index = 0; index < documentPlans.Length; index++)
        {
            documentPlans[index] = CreateDocumentPlan(mappingSet, request.DocumentReferences[index]);
        }

        var descriptorPlans = new ReferenceLookupPlan[request.DescriptorReferences.Count];
        for (var index = 0; index < descriptorPlans.Length; index++)
        {
            descriptorPlans[index] = CreateDescriptorPlan(mappingSet, request.DescriptorReferences[index]);
        }

        var pendingBatch = BuildPendingBatch(mappingSet, documentPlans, descriptorPlans);

        if (pendingBatch is not null)
        {
            var rows = await _adapter
                .ResolveAsync(pendingBatch.Batch, cancellationToken)
                .ConfigureAwait(false);

            // Cached only here, after the round trip completed: an adapter failure must leave the request
            // scope unpoisoned so a retry re-issues the same lookups.
            CacheOutcomes(mappingSet, pendingBatch, rows);
        }

        return BuildResolvedReferenceSet(mappingSet, request, documentPlans, descriptorPlans);
    }

    // ── Plan construction ────────────────────────────────────────────────────────────────────────

    private ReferenceLookupPlan CreateDocumentPlan(MappingSet mappingSet, DocumentReference reference)
    {
        var target = ToQualifiedResourceName(reference.ResourceInfo);
        var probe = GetProbeTargetOrThrow(mappingSet, target);
        var valueByIdentityPath = BuildIdentityValueByPath(reference.DocumentIdentity);

        EnsureIdentityShape(mappingSet, target, reference.DocumentIdentity, probe);

        var values = new object[probe.Columns.Count];
        StringBuilder identityKeyBuilder = new();

        for (var columnIndex = 0; columnIndex < probe.Columns.Count; columnIndex++)
        {
            var column = probe.Columns[columnIndex];
            var identityPath = column.SourceIdentityJsonPath.Canonical;
            var literal = valueByIdentityPath[identityPath];

            if (column.DescriptorResource is not null)
            {
                // Descriptor-valued identity parts bind the persisted lower-cased URI; the builder resolves
                // it to a descriptor document id inline.
                literal = literal.ToLowerInvariant();
                values[columnIndex] = literal;
            }
            else if (
                RelationalScalarLiteralParser.TryParse(literal, column.ScalarType, out var convertedValue)
                && convertedValue is not null
            )
            {
                values[columnIndex] = convertedValue;
            }
            else
            {
                // A value that cannot be typed to its target column cannot match any stored row. Reporting
                // it as a missing reference (with a logged reason) keeps it a 400 with a usable path,
                // rather than a 500 from deep inside the write path.
                _logger?.LogWarning(
                    "Reference at path '{ReferencePath}' targeting '{TargetResource}' could not be resolved: "
                        + "identity value at '{IdentityJsonPath}' is not a valid {ScalarKind} literal for column "
                        + "'{StorageColumn}'. The reference is reported as missing.",
                    reference.Path.Value,
                    RelationalWriteSupport.FormatResource(target),
                    identityPath,
                    column.ScalarType.Kind,
                    column.StorageColumn.Value
                );

                return ReferenceLookupPlan.Unresolvable(target);
            }

            AppendIdentityKeyPart(identityKeyBuilder, identityPath, literal);
        }

        return new ReferenceLookupPlan(
            Target: target,
            Key: new NaturalKeyLookupKey(target, IsDescriptor: false, identityKeyBuilder.ToString()),
            Values: values,
            Probe: probe
        );
    }

    private static ReferenceLookupPlan CreateDescriptorPlan(
        MappingSet mappingSet,
        DescriptorReference reference
    )
    {
        var target = ToQualifiedResourceName(reference.ResourceInfo);

        // Fail fast when the mapping set never compiled a discriminator literal for the target: the
        // alternative is a statement that silently matches nothing.
        _ = NaturalKeyLookupCommandSupport.DescriptorDiscriminatorLiteralOrThrow(mappingSet, target);

        var descriptorIdentity =
            reference.DocumentIdentity.DocumentIdentityElements.SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"Descriptor reference at path '{reference.Path.Value}' is missing its descriptor identity value."
            );

        // Descriptor matching is case-insensitive by Ed-Fi contract; the probe binds the persisted
        // lower-cased URI column, so the request value is lower-cased here rather than in SQL.
        var loweredUri = descriptorIdentity.IdentityValue.ToLowerInvariant();
        StringBuilder identityKeyBuilder = new();
        AppendIdentityKeyPart(identityKeyBuilder, descriptorIdentity.IdentityJsonPath.Value, loweredUri);

        return new ReferenceLookupPlan(
            Target: target,
            Key: new NaturalKeyLookupKey(target, IsDescriptor: true, identityKeyBuilder.ToString()),
            Values: [loweredUri],
            Probe: null
        );
    }

    private static NaturalKeyProbeTarget GetProbeTargetOrThrow(
        MappingSet mappingSet,
        QualifiedResourceName target
    )
    {
        if (mappingSet.NaturalKeyProbeTargets.TryGetValue(target, out var probe))
        {
            return probe;
        }

        throw new InvalidOperationException(
            $"Mapping set '{RelationalWriteSupport.FormatMappingSetKey(mappingSet.Key)}' "
                + $"is missing a compiled natural-key probe target for document-reference target "
                + $"'{RelationalWriteSupport.FormatResource(target)}'."
        );
    }

    private static Dictionary<string, string> BuildIdentityValueByPath(DocumentIdentity documentIdentity)
    {
        Dictionary<string, string> valueByPath = new(StringComparer.Ordinal);

        foreach (var identityElement in documentIdentity.DocumentIdentityElements)
        {
            valueByPath[identityElement.IdentityJsonPath.Value] = identityElement.IdentityValue;
        }

        return valueByPath;
    }

    private void EnsureIdentityShape(
        MappingSet mappingSet,
        QualifiedResourceName target,
        DocumentIdentity documentIdentity,
        NaturalKeyProbeTarget probe
    )
    {
        var identityPaths = documentIdentity
            .DocumentIdentityElements.Select(static element => element.IdentityJsonPath.Value)
            .ToArray();

        if (_identityShapeByResource.TryGetValue(target, out var firstSeenIdentityPaths))
        {
            if (!identityPaths.SequenceEqual(firstSeenIdentityPaths, StringComparer.Ordinal))
            {
                throw CreateIdentityShapeMismatchException(
                    mappingSet,
                    target,
                    $"multiple lookup entries for the same resource used different identity path orderings "
                        + $"('{string.Join("', '", firstSeenIdentityPaths)}' then '{string.Join("', '", identityPaths)}')."
                );
            }

            return;
        }

        HashSet<string> identityPathSet = new(identityPaths, StringComparer.Ordinal);

        foreach (var column in probe.Columns)
        {
            if (identityPathSet.Contains(column.SourceIdentityJsonPath.Canonical))
            {
                continue;
            }

            throw CreateIdentityShapeMismatchException(
                mappingSet,
                target,
                $"the compiled natural-key probe binds identity path '{column.SourceIdentityJsonPath.Canonical}' "
                    + $"for column '{column.StorageColumn.Value}', but the request identity supplies only "
                    + $"'{string.Join("', '", identityPaths)}'."
            );
        }

        _identityShapeByResource[target] = identityPaths;
    }

    private static Exception CreateIdentityShapeMismatchException(
        MappingSet mappingSet,
        QualifiedResourceName target,
        string detail
    ) =>
        new InvalidOperationException(
            $"Natural-key reference resolution failed for target "
                + $"'{RelationalWriteSupport.FormatResource(target)}' in mapping set "
                + $"'{RelationalWriteSupport.FormatMappingSetKey(mappingSet.Key)}': {detail}"
        );

    /// <summary>
    /// Appends one <c>path=value</c> pair to the dedupe key. Structurally the pre-hash string
    /// <c>ReferentialIdFactory</c> builds, minus the hash — the unit separator keeps a value that contains
    /// the delimiter from colliding with a different path/value split.
    /// </summary>
    private static void AppendIdentityKeyPart(StringBuilder builder, string identityPath, string value)
    {
        if (builder.Length > 0)
        {
            builder.Append(IdentityKeySeparator);
        }

        builder.Append(identityPath).Append('=').Append(value);
    }

    /// <summary>
    /// ASCII unit separator: a control character no JSON identity value carries, so a value containing the
    /// delimiter cannot collide with a different path/value split.
    /// </summary>
    private const char IdentityKeySeparator = '';

    // ── Batch construction ───────────────────────────────────────────────────────────────────────

    private PendingBatch? BuildPendingBatch(
        MappingSet mappingSet,
        IReadOnlyList<ReferenceLookupPlan> documentPlans,
        IReadOnlyList<ReferenceLookupPlan> descriptorPlans
    )
    {
        Dictionary<(QualifiedResourceName Target, bool IsDescriptor), PendingGroup> groupByTarget = [];
        HashSet<NaturalKeyLookupKey> requestedKeys = [];
        List<PendingGroup> pendingGroups = [];

        // Document references first, then descriptor references, matching the extraction order the old
        // resolver batched in.
        foreach (var plan in documentPlans.Concat(descriptorPlans))
        {
            if (plan.Key is not { } key || !requestedKeys.Add(key))
            {
                continue;
            }

            if (_memoizedOutcomes.ContainsKey(key))
            {
                continue;
            }

            var groupKey = (plan.Target, key.IsDescriptor);

            if (!groupByTarget.TryGetValue(groupKey, out var pendingGroup))
            {
                pendingGroup = new PendingGroup(plan.Target, plan.Probe);
                groupByTarget[groupKey] = pendingGroup;
                pendingGroups.Add(pendingGroup);
            }

            pendingGroup.Keys.Add(key);
            pendingGroup.Entries.Add(new NaturalKeyLookupEntry(pendingGroup.Entries.Count + 1, plan.Values!));
        }

        if (pendingGroups.Count == 0)
        {
            return null;
        }

        NaturalKeyLookupGroup[] groups =
        [
            .. pendingGroups.Select(pendingGroup =>
                pendingGroup.Probe is { } probe
                    ? (NaturalKeyLookupGroup)
                        new NaturalKeyProbeLookupGroup(pendingGroup.Target, probe, pendingGroup.Entries)
                    : new DescriptorLookupGroup(pendingGroup.Target, pendingGroup.Entries)
            ),
        ];

        return new PendingBatch(
            new NaturalKeyLookupBatch(mappingSet, groups),
            [.. pendingGroups.Select(pendingGroup => (IReadOnlyList<NaturalKeyLookupKey>)pendingGroup.Keys)]
        );
    }

    // ── Result caching ───────────────────────────────────────────────────────────────────────────

    private void CacheOutcomes(
        MappingSet mappingSet,
        PendingBatch pendingBatch,
        IReadOnlyList<NaturalKeyLookupRow> rows
    )
    {
        ArgumentNullException.ThrowIfNull(rows);

        Dictionary<NaturalKeyLookupKey, NaturalKeyLookupOutcome> outcomeByKey = [];

        foreach (var row in rows)
        {
            var key = ResolveRowKey(pendingBatch, row);
            var group = pendingBatch.Batch.Groups[row.GroupIndex];

            if (group is DescriptorLookupGroup)
            {
                outcomeByKey[key] = MergeDescriptorRow(
                    mappingSet,
                    group.Target,
                    outcomeByKey.GetValueOrDefault(key, NaturalKeyLookupOutcome.Miss),
                    row
                );

                continue;
            }

            var probeGroup = (NaturalKeyProbeLookupGroup)group;
            var resourceKeyId = probeGroup.Probe.IsAbstract
                ? ResolveAbstractResourceKeyIdOrThrow(mappingSet, group.Target, row)
                : GetResourceKeyIdOrThrow(mappingSet, group.Target);

            var hit = new NaturalKeyLookupOutcome(
                row.DocumentId,
                resourceKeyId,
                IsRequestedType: true,
                HasAnyRow: true
            );

            if (outcomeByKey.TryGetValue(key, out var existingOutcome) && existingOutcome != hit)
            {
                throw new InvalidOperationException(
                    $"Natural-key lookup for target '{RelationalWriteSupport.FormatResource(group.Target)}' "
                        + $"returned multiple rows for one entry (document ids "
                        + $"'{existingOutcome.DocumentId}' and '{row.DocumentId}'); the target's natural key is not unique."
                );
            }

            outcomeByKey[key] = hit;
        }

        foreach (var groupKeys in pendingBatch.KeysByGroup)
        {
            foreach (var key in groupKeys)
            {
                _memoizedOutcomes[key] = outcomeByKey.GetValueOrDefault(key, NaturalKeyLookupOutcome.Miss);
            }
        }
    }

    private static NaturalKeyLookupKey ResolveRowKey(PendingBatch pendingBatch, NaturalKeyLookupRow row)
    {
        if (row.GroupIndex < 0 || row.GroupIndex >= pendingBatch.KeysByGroup.Count)
        {
            throw new InvalidOperationException(
                $"Natural-key lookup adapter returned a row for group index '{row.GroupIndex}', "
                    + $"but the batch has {pendingBatch.KeysByGroup.Count} groups."
            );
        }

        var groupKeys = pendingBatch.KeysByGroup[row.GroupIndex];

        // Rows arrive in unspecified order; the projected ordinal is the only attribution.
        if (row.Ordinal < 1 || row.Ordinal > groupKeys.Count)
        {
            throw new InvalidOperationException(
                $"Natural-key lookup adapter returned ordinal '{row.Ordinal}' for group index "
                    + $"'{row.GroupIndex}', which has {groupKeys.Count} entries."
            );
        }

        return groupKeys[row.Ordinal - 1];
    }

    private static NaturalKeyLookupOutcome MergeDescriptorRow(
        MappingSet mappingSet,
        QualifiedResourceName target,
        NaturalKeyLookupOutcome existingOutcome,
        NaturalKeyLookupRow row
    )
    {
        var expectedDiscriminator = NaturalKeyLookupCommandSupport.DescriptorDiscriminatorLiteralOrThrow(
            mappingSet,
            target
        );
        var expectedResourceKeyId = GetResourceKeyIdOrThrow(mappingSet, target);

        // The descriptor statement deliberately seeks by URI alone, so a URI that names a descriptor of
        // another type still returns a row. Picking the (Discriminator, ResourceKeyId)-matching row here is
        // what preserves the DescriptorTypeMismatch-vs-Missing distinction.
        var isRequestedType =
            string.Equals(row.Discriminator, expectedDiscriminator, StringComparison.Ordinal)
            && row.ResourceKeyId == expectedResourceKeyId;

        if (isRequestedType)
        {
            return new NaturalKeyLookupOutcome(
                row.DocumentId,
                expectedResourceKeyId,
                IsRequestedType: true,
                HasAnyRow: true
            );
        }

        if (existingOutcome.IsRequestedType && existingOutcome.DocumentId is not null)
        {
            return existingOutcome;
        }

        // A wrong-type row is still a row the lookup found: it is recorded in the snapshot exactly as the
        // hash resolver recorded the dms.Document row it resolved, and only the classification rejects it.
        // A row whose mirrored ResourceKeyId was never stamped cannot be reported that way, so it counts
        // only towards the type-mismatch verdict.
        var hasUsableRow = row.ResourceKeyId is not null;

        return new NaturalKeyLookupOutcome(
            hasUsableRow ? row.DocumentId : existingOutcome.DocumentId,
            hasUsableRow ? row.ResourceKeyId : existingOutcome.ResourceKeyId,
            IsRequestedType: false,
            HasAnyRow: true
        );
    }

    private static short ResolveAbstractResourceKeyIdOrThrow(
        MappingSet mappingSet,
        QualifiedResourceName target,
        NaturalKeyLookupRow row
    )
    {
        // The abstract identity table names the matched concrete subtype as "{Project}:{Resource}"; the
        // resolved reference must report that concrete resource key id, exactly as the old resolver
        // reported dms.Document.ResourceKeyId for an alias row.
        if (row.Discriminator is not { } discriminator)
        {
            throw new InvalidOperationException(
                $"Abstract natural-key lookup for target '{RelationalWriteSupport.FormatResource(target)}' "
                    + $"returned a row for document id '{row.DocumentId}' without a discriminator."
            );
        }

        var separatorIndex = discriminator.IndexOf(':', StringComparison.Ordinal);

        if (separatorIndex > 0 && separatorIndex < discriminator.Length - 1)
        {
            var memberResource = new QualifiedResourceName(
                discriminator[..separatorIndex],
                discriminator[(separatorIndex + 1)..]
            );

            if (mappingSet.ResourceKeyIdByResource.TryGetValue(memberResource, out var resourceKeyId))
            {
                return resourceKeyId;
            }
        }

        throw new InvalidOperationException(
            $"Abstract natural-key lookup for target '{RelationalWriteSupport.FormatResource(target)}' "
                + $"returned discriminator '{discriminator}' for document id '{row.DocumentId}', which does not "
                + $"name a resource in mapping set '{RelationalWriteSupport.FormatMappingSetKey(mappingSet.Key)}'."
        );
    }

    private static short GetResourceKeyIdOrThrow(MappingSet mappingSet, QualifiedResourceName resource)
    {
        if (mappingSet.ResourceKeyIdByResource.TryGetValue(resource, out var resourceKeyId))
        {
            return resourceKeyId;
        }

        throw new InvalidOperationException(
            $"Mapping set '{RelationalWriteSupport.FormatMappingSetKey(mappingSet.Key)}' "
                + $"does not contain a resource key id for resource '{RelationalWriteSupport.FormatResource(resource)}'."
        );
    }

    // ── Materialization ──────────────────────────────────────────────────────────────────────────

    private ResolvedReferenceSet BuildResolvedReferenceSet(
        MappingSet mappingSet,
        ReferenceResolverRequest request,
        IReadOnlyList<ReferenceLookupPlan> documentPlans,
        IReadOnlyList<ReferenceLookupPlan> descriptorPlans
    )
    {
        Dictionary<ReferentialId, ReferenceLookupSnapshot> lookupsByReferentialId = [];

        var documentReferenceOccurrences = new ResolvedDocumentReferenceOccurrence[documentPlans.Count];
        for (var index = 0; index < documentPlans.Count; index++)
        {
            var reference = request.DocumentReferences[index];
            documentReferenceOccurrences[index] = new ResolvedDocumentReferenceOccurrence(
                reference,
                GetOrCreateSnapshot(
                    mappingSet,
                    lookupsByReferentialId,
                    reference.ReferentialId,
                    documentPlans[index],
                    isDescriptor: false
                )
            );
        }

        var descriptorReferenceOccurrences = new ResolvedDescriptorReferenceOccurrence[descriptorPlans.Count];
        for (var index = 0; index < descriptorPlans.Count; index++)
        {
            var reference = request.DescriptorReferences[index];
            descriptorReferenceOccurrences[index] = new ResolvedDescriptorReferenceOccurrence(
                reference,
                GetOrCreateSnapshot(
                    mappingSet,
                    lookupsByReferentialId,
                    reference.ReferentialId,
                    descriptorPlans[index],
                    isDescriptor: true
                )
            );
        }

        Dictionary<JsonPath, ResolvedDocumentReference> successfulDocumentReferencesByPath = [];
        List<DocumentReferenceFailure> invalidDocumentReferences = [];

        for (var index = 0; index < documentReferenceOccurrences.Length; index++)
        {
            var occurrence = documentReferenceOccurrences[index];
            var outcome = GetOutcome(documentPlans[index]);

            if (
                !outcome.IsRequestedType
                || outcome.DocumentId is not { } documentId
                || outcome.ResourceKeyId is not { } resourceKeyId
            )
            {
                invalidDocumentReferences.Add(
                    DocumentReferenceFailure.From(
                        occurrence.Reference,
                        DocumentReferenceFailureReason.Missing
                    )
                );
                continue;
            }

            // Preserved from the old resolver even though a probe hit is a member of the requested target
            // by construction: the acceptance rule stays in one place and stays asserted.
            var targetMetadata = mappingSet.GetDocumentReferenceTargetMetadataOrThrow(
                occurrence.Reference.ResourceInfo
            );

            if (!targetMetadata.AllowsResourceKeyId(resourceKeyId))
            {
                invalidDocumentReferences.Add(
                    DocumentReferenceFailure.From(
                        occurrence.Reference,
                        DocumentReferenceFailureReason.IncompatibleTargetType
                    )
                );
                continue;
            }

            AddSuccessfulDocumentReference(
                successfulDocumentReferencesByPath,
                occurrence.Reference,
                documentId,
                resourceKeyId
            );
        }

        Dictionary<JsonPath, ResolvedDescriptorReference> successfulDescriptorReferencesByPath = [];
        List<DescriptorReferenceFailure> invalidDescriptorReferences = [];

        for (var index = 0; index < descriptorReferenceOccurrences.Length; index++)
        {
            var occurrence = descriptorReferenceOccurrences[index];
            var outcome = GetOutcome(descriptorPlans[index]);

            if (
                !outcome.IsRequestedType
                || outcome.DocumentId is not { } documentId
                || outcome.ResourceKeyId is not { } resourceKeyId
            )
            {
                invalidDescriptorReferences.Add(
                    DescriptorReferenceFailure.From(
                        occurrence.Reference,
                        outcome.HasAnyRow
                            ? DescriptorReferenceFailureReason.DescriptorTypeMismatch
                            : DescriptorReferenceFailureReason.Missing
                    )
                );
                continue;
            }

            AddSuccessfulDescriptorReference(
                successfulDescriptorReferencesByPath,
                occurrence.Reference,
                documentId,
                resourceKeyId
            );
        }

        return new ResolvedReferenceSet(
            SuccessfulDocumentReferencesByPath: successfulDocumentReferencesByPath,
            SuccessfulDescriptorReferencesByPath: successfulDescriptorReferencesByPath,
            LookupsByReferentialId: lookupsByReferentialId,
            InvalidDocumentReferences: invalidDocumentReferences,
            InvalidDescriptorReferences: invalidDescriptorReferences,
            DocumentReferenceOccurrences: documentReferenceOccurrences,
            DescriptorReferenceOccurrences: descriptorReferenceOccurrences
        );
    }

    private ReferenceLookupSnapshot GetOrCreateSnapshot(
        MappingSet mappingSet,
        IDictionary<ReferentialId, ReferenceLookupSnapshot> lookupsByReferentialId,
        ReferentialId referentialId,
        ReferenceLookupPlan plan,
        bool isDescriptor
    )
    {
        if (lookupsByReferentialId.TryGetValue(referentialId, out var existingSnapshot))
        {
            return existingSnapshot;
        }

        var outcome = GetOutcome(plan);

        // LookupsByReferentialId stays keyed by the Core-computed hash: it is a public
        // ResolvedReferenceSet member with live consumers, and the key is request-scoped -- nothing
        // persists a ReferentialId. RequestedTargetResourceKeyId is the key id of the resource the
        // reference addressed, which differs from ResourceKeyId when that target is abstract.
        var result =
            outcome.DocumentId is { } documentId && outcome.ResourceKeyId is { } resourceKeyId
                ? new ReferenceLookupResult(
                    ReferentialId: referentialId,
                    DocumentId: documentId,
                    ResourceKeyId: resourceKeyId,
                    RequestedTargetResourceKeyId: GetResourceKeyIdOrThrow(mappingSet, plan.Target),
                    IsDescriptor: isDescriptor
                )
                : null;

        var snapshot = new ReferenceLookupSnapshot(referentialId, result);
        lookupsByReferentialId[referentialId] = snapshot;

        return snapshot;
    }

    private NaturalKeyLookupOutcome GetOutcome(ReferenceLookupPlan plan)
    {
        if (plan.Key is not { } key)
        {
            // An identity value that could not be typed never reached the database.
            return NaturalKeyLookupOutcome.Miss;
        }

        if (_memoizedOutcomes.TryGetValue(key, out var outcome))
        {
            return outcome;
        }

        throw new InvalidOperationException(
            $"Natural-key reference resolver did not cache a lookup for target "
                + $"'{RelationalWriteSupport.FormatResource(plan.Target)}' before materializing the result set."
        );
    }

    private static void AddSuccessfulDocumentReference(
        IDictionary<JsonPath, ResolvedDocumentReference> successfulReferencesByPath,
        DocumentReference documentReference,
        long documentId,
        short resourceKeyId
    )
    {
        if (
            !successfulReferencesByPath.TryAdd(
                documentReference.Path,
                new ResolvedDocumentReference(documentReference, documentId, resourceKeyId)
            )
        )
        {
            throw new InvalidOperationException(
                $"Document reference path '{documentReference.Path.Value}' was extracted more than once within the same request."
            );
        }
    }

    private static void AddSuccessfulDescriptorReference(
        IDictionary<JsonPath, ResolvedDescriptorReference> successfulReferencesByPath,
        DescriptorReference descriptorReference,
        long documentId,
        short resourceKeyId
    )
    {
        if (
            !successfulReferencesByPath.TryAdd(
                descriptorReference.Path,
                new ResolvedDescriptorReference(descriptorReference, documentId, resourceKeyId)
            )
        )
        {
            throw new InvalidOperationException(
                $"Descriptor reference path '{descriptorReference.Path.Value}' was extracted more than once within the same request."
            );
        }
    }

    private static QualifiedResourceName ToQualifiedResourceName(BaseResourceInfo resourceInfo) =>
        new(resourceInfo.ProjectName.Value, resourceInfo.ResourceName.Value);

    // ── Request-local records ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The dedupe/memo key for one reference: its target plus its ordered identity values.
    /// </summary>
    private sealed record NaturalKeyLookupKey(
        QualifiedResourceName Target,
        bool IsDescriptor,
        string IdentityKey
    );

    /// <summary>
    /// What one deduped lookup resolved to, including memoized misses.
    /// </summary>
    private sealed record NaturalKeyLookupOutcome(
        long? DocumentId,
        short? ResourceKeyId,
        bool IsRequestedType,
        bool HasAnyRow
    )
    {
        public static readonly NaturalKeyLookupOutcome Miss = new(null, null, false, false);
    }

    /// <summary>
    /// One reference occurrence reduced to what the batch needs, or an unresolvable value.
    /// </summary>
    private sealed record ReferenceLookupPlan(
        QualifiedResourceName Target,
        NaturalKeyLookupKey? Key,
        IReadOnlyList<object>? Values,
        NaturalKeyProbeTarget? Probe
    )
    {
        public static ReferenceLookupPlan Unresolvable(QualifiedResourceName target) =>
            new(target, null, null, null);
    }

    private sealed class PendingGroup(QualifiedResourceName target, NaturalKeyProbeTarget? probe)
    {
        public QualifiedResourceName Target { get; } = target;

        public NaturalKeyProbeTarget? Probe { get; } = probe;

        public List<NaturalKeyLookupKey> Keys { get; } = [];

        public List<NaturalKeyLookupEntry> Entries { get; } = [];
    }

    private sealed record PendingBatch(
        NaturalKeyLookupBatch Batch,
        IReadOnlyList<IReadOnlyList<NaturalKeyLookupKey>> KeysByGroup
    );
}
