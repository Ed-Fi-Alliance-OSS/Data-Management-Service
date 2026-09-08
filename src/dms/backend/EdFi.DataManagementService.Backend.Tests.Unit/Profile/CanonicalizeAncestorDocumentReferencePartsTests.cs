// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

/// <summary>
/// Slice 5 review fix: regression coverage for parent-partitioned ancestor canonicalization
/// of document-reference natural keys. Mirrors the descriptor-side coverage in
/// <see cref="Given_TryResolveAncestorDescriptorIdFromCurrentRows_for_descriptor_only_identity_with_one_current_row"/>'s
/// fixture set, but exercises <c>CanonicalizeAncestorDocumentReferenceParts</c> directly.
/// </summary>
internal static class AncestorDocumentReferencePartsFixtures
{
    public const string ParentsScope = DocumentReferenceBackedNestedTopologyBuilders.ParentsScope;
    public const string ParentReferenceParentIdRelativePath =
        DocumentReferenceBackedNestedTopologyBuilders.ParentReferenceParentIdRelativePath;
    public const string ParentNaturalKey = DocumentReferenceBackedNestedTopologyBuilders.ParentNaturalKey;
    public const long ResolvedParentDocumentId = 999L;
    public const string OuterScope = "$.grandparents[*]";

    public static ScopeInstanceAddress GrandparentAddress(long grandparentId) =>
        new(
            OuterScope,
            [
                new AncestorCollectionInstance(
                    OuterScope,
                    [
                        new SemanticIdentityPart(
                            "$.grandparentId",
                            JsonValue.Create(grandparentId),
                            IsPresent: true
                        ),
                    ]
                ),
            ]
        );

    public static CurrentCollectionRowSnapshot ParentRowSnapshot(
        long stableRowIdentity,
        string parentNaturalKey,
        long parentReferenceDocumentId
    )
    {
        ImmutableArray<SemanticIdentityPart> identity =
        [
            new SemanticIdentityPart(
                ParentReferenceParentIdRelativePath,
                JsonValue.Create(parentReferenceDocumentId),
                IsPresent: true
            ),
        ];

        var projected = new RelationalWriteMergedTableRow(
            ImmutableArray<FlattenedWriteValue>.Empty,
            ImmutableArray<FlattenedWriteValue>.Empty
        );

        var columnValues = new Dictionary<DbColumnName, object?>
        {
            [new DbColumnName("ParentReference_DocumentId")] = parentReferenceDocumentId,
            [new DbColumnName("ParentReference_ParentId")] = parentNaturalKey,
        };

        return new CurrentCollectionRowSnapshot(
            stableRowIdentity,
            identity,
            StoredOrdinal: 1,
            projected,
            columnValues
        );
    }

    public static IReadOnlyDictionary<
        (string JsonScope, ScopeInstanceAddress ParentAddress),
        ImmutableArray<CurrentCollectionRowSnapshot>
    > PartitionMap(
        params (
            string JsonScope,
            ScopeInstanceAddress ParentAddress,
            ImmutableArray<CurrentCollectionRowSnapshot> Rows
        )[] entries
    )
    {
        var result = new Dictionary<
            (string, ScopeInstanceAddress),
            ImmutableArray<CurrentCollectionRowSnapshot>
        >(ProfileCollectionWalker.ChildScopeAndParentComparer.Instance);
        foreach (var entry in entries)
        {
            result[(entry.JsonScope, entry.ParentAddress)] = entry.Rows;
        }
        return result;
    }
}

/// <summary>
/// Slice 5 review fix: when the same referenced document natural key appears under two
/// different parent instances (a valid nested shape), ancestor canonicalization must
/// resolve each occurrence within its own parent partition. A scope-wide scan would treat
/// the two rows as ambiguous and fail closed even though each partition has exactly one
/// match. This fixture seeds two partitions sharing the same natural key and verifies
/// canonicalization succeeds within each.
/// </summary>
[TestFixture]
public class Given_CanonicalizeAncestorDocumentReferenceParts_for_same_natural_key_under_two_parent_instances
{
    private ResourceWritePlan _resourcePlan = null!;
    private TableWritePlan _parentsPlan = null!;
    private IReadOnlyList<RelationalWriteProfileMergeSynthesizer.DocumentReferenceIdentityPart> _docRefParts =
        null!;
    private FlatteningResolvedReferenceLookupSet _lookups = null!;
    private IReadOnlyDictionary<
        (string JsonScope, ScopeInstanceAddress ParentAddress),
        ImmutableArray<CurrentCollectionRowSnapshot>
    > _partitionMap = null!;
    private ScopeInstanceAddress _grandparentA = null!;
    private ScopeInstanceAddress _grandparentB = null!;
    private ImmutableArray<SemanticIdentityPart> _identity;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, _) =
            DocumentReferenceBackedNestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        _resourcePlan = plan;
        _parentsPlan = parentsPlan;

        _docRefParts = RelationalWriteProfileMergeSynthesizer.ResolveDocumentReferenceIdentityParts(
            _resourcePlan,
            _parentsPlan
        );

        var resolvedRefs = DocumentReferenceBackedNestedTopologyBuilders.BuildResolvedReferenceSet(
            AncestorDocumentReferencePartsFixtures.ParentNaturalKey,
            AncestorDocumentReferencePartsFixtures.ResolvedParentDocumentId
        );
        _lookups = FlatteningResolvedReferenceLookupSet.Create(_resourcePlan, resolvedRefs);

        _grandparentA = AncestorDocumentReferencePartsFixtures.GrandparentAddress(1L);
        _grandparentB = AncestorDocumentReferencePartsFixtures.GrandparentAddress(2L);

        // Two parent rows in two partitions, sharing the same natural key and resolving to
        // the same backend document id. A scope-wide scan would see both and report
        // ambiguity; the fix requires the canonicalization to consult only the partition
        // belonging to the target parent address.
        var rowInA = AncestorDocumentReferencePartsFixtures.ParentRowSnapshot(
            stableRowIdentity: 100L,
            AncestorDocumentReferencePartsFixtures.ParentNaturalKey,
            AncestorDocumentReferencePartsFixtures.ResolvedParentDocumentId
        );
        var rowInB = AncestorDocumentReferencePartsFixtures.ParentRowSnapshot(
            stableRowIdentity: 200L,
            AncestorDocumentReferencePartsFixtures.ParentNaturalKey,
            AncestorDocumentReferencePartsFixtures.ResolvedParentDocumentId
        );

        _partitionMap = AncestorDocumentReferencePartsFixtures.PartitionMap(
            (AncestorDocumentReferencePartsFixtures.ParentsScope, _grandparentA, [rowInA]),
            (AncestorDocumentReferencePartsFixtures.ParentsScope, _grandparentB, [rowInB])
        );

        // The Core-emitted ancestor identity carries the natural-key form; canonicalization
        // must rewrite it to the resolved backend document id (Int64).
        _identity =
        [
            new SemanticIdentityPart(
                AncestorDocumentReferencePartsFixtures.ParentReferenceParentIdRelativePath,
                JsonValue.Create(AncestorDocumentReferencePartsFixtures.ParentNaturalKey),
                IsPresent: true
            ),
        ];
    }

    [Test]
    public void It_resolves_the_natural_key_within_partition_A()
    {
        var result = RelationalWriteProfileMergeSynthesizer.CanonicalizeAncestorDocumentReferenceParts(
            _identity,
            _parentsPlan,
            _docRefParts,
            AncestorDocumentReferencePartsFixtures.ParentsScope,
            _lookups,
            canonicalTargetParentAddress: _grandparentA,
            currentRowsByJsonScopeAndParent: _partitionMap,
            ancestorRequestOrdinalPath: default,
            hasRequestOrdinalPath: false
        );

        result[0]
            .Value!.GetValue<long>()
            .Should()
            .Be(
                AncestorDocumentReferencePartsFixtures.ResolvedParentDocumentId,
                "the partition for grandparent A holds exactly one matching row; "
                    + "without partition narrowing the scope-wide scan would fail closed "
                    + "even though the per-partition lookup is unambiguous"
            );
    }

    [Test]
    public void It_resolves_the_natural_key_within_partition_B()
    {
        var result = RelationalWriteProfileMergeSynthesizer.CanonicalizeAncestorDocumentReferenceParts(
            _identity,
            _parentsPlan,
            _docRefParts,
            AncestorDocumentReferencePartsFixtures.ParentsScope,
            _lookups,
            canonicalTargetParentAddress: _grandparentB,
            currentRowsByJsonScopeAndParent: _partitionMap,
            ancestorRequestOrdinalPath: default,
            hasRequestOrdinalPath: false
        );

        result[0]
            .Value!.GetValue<long>()
            .Should()
            .Be(
                AncestorDocumentReferencePartsFixtures.ResolvedParentDocumentId,
                "the partition for grandparent B is independent of grandparent A; "
                    + "both partitions resolve their own copy of the same natural key"
            );
    }

    [Test]
    public void It_fails_closed_when_target_partition_has_no_entry_and_request_cache_misses()
    {
        var unknownPartition = AncestorDocumentReferencePartsFixtures.GrandparentAddress(99L);

        var act = () =>
            RelationalWriteProfileMergeSynthesizer.CanonicalizeAncestorDocumentReferenceParts(
                _identity,
                _parentsPlan,
                _docRefParts,
                AncestorDocumentReferencePartsFixtures.ParentsScope,
                _lookups,
                canonicalTargetParentAddress: unknownPartition,
                currentRowsByJsonScopeAndParent: _partitionMap,
                ancestorRequestOrdinalPath: default,
                hasRequestOrdinalPath: false
            );

        act.Should()
            .Throw<InvalidOperationException>(
                "with no partition entry and no request-side ordinal path to consult the "
                    + "reference cache, the helper preserves the Slice 4 fail-closed shape"
            )
            .WithMessage("*target parent partition*");
    }
}
