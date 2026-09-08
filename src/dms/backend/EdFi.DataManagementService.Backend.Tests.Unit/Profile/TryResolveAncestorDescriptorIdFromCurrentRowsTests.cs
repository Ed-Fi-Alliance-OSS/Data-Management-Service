// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

internal static class AncestorDescriptorIdFixtures
{
    public const string AncestorScope = "$.zones[*]";

    public static ScopeInstanceAddress RootParent =>
        new("$", ImmutableArray<AncestorCollectionInstance>.Empty);

    public static CurrentCollectionRowSnapshot Snapshot(
        long stableRowIdentity,
        params (string RelativePath, JsonNode? Value)[] identityParts
    )
    {
        var parts = identityParts
            .Select(p => new SemanticIdentityPart(p.RelativePath, p.Value, IsPresent: true))
            .ToImmutableArray();
        var projected = new RelationalWriteMergedTableRow(
            ImmutableArray<FlattenedWriteValue>.Empty,
            ImmutableArray<FlattenedWriteValue>.Empty
        );
        return new CurrentCollectionRowSnapshot(
            stableRowIdentity,
            parts,
            StoredOrdinal: 1,
            projected,
            new Dictionary<DbColumnName, object?>()
        );
    }

    public static ImmutableArray<SemanticIdentityPart> Identity(
        params (string RelativePath, JsonNode? Value)[] parts
    ) =>
        parts
            .Select(p => new SemanticIdentityPart(p.RelativePath, p.Value, IsPresent: true))
            .ToImmutableArray();

    public static VisibleStoredCollectionRow StoredRow(
        ScopeInstanceAddress parentAddress,
        params (string RelativePath, JsonNode? Value)[] identityParts
    ) =>
        new(
            new CollectionRowAddress(AncestorScope, parentAddress, Identity(identityParts)),
            ImmutableArray<string>.Empty
        );

    public static ScopeInstanceAddress NestedParent(string outerScope, long outerId) =>
        new(
            outerScope,
            [
                new AncestorCollectionInstance(
                    outerScope,
                    [new SemanticIdentityPart("$.outerId", JsonValue.Create(outerId), IsPresent: true)]
                ),
            ]
        );

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

[TestFixture]
public class Given_TryResolveAncestorDescriptorIdFromCurrentRows_for_descriptor_only_identity_with_one_current_row
{
    private long? _result;

    [SetUp]
    public void Setup()
    {
        var identity = AncestorDescriptorIdFixtures.Identity(
            ("$.zoneDescriptor", JsonValue.Create("uri://example.org/Zone#Urban"))
        );
        ImmutableArray<CurrentCollectionRowSnapshot> currentRows =
        [
            AncestorDescriptorIdFixtures.Snapshot(
                stableRowIdentity: 1L,
                ("$.zoneDescriptor", JsonValue.Create(7777L))
            ),
        ];
        ImmutableArray<VisibleStoredCollectionRow> storedRows =
        [
            AncestorDescriptorIdFixtures.StoredRow(
                AncestorDescriptorIdFixtures.RootParent,
                ("$.zoneDescriptor", JsonValue.Create("uri://example.org/Zone#Urban"))
            ),
        ];
        var partitionMap = AncestorDescriptorIdFixtures.PartitionMap(
            (AncestorDescriptorIdFixtures.AncestorScope, AncestorDescriptorIdFixtures.RootParent, currentRows)
        );

        _result = RelationalWriteProfileMergeSynthesizer.TryResolveAncestorDescriptorIdFromCurrentRows(
            identity,
            descriptorIndices: [0],
            descriptorIdx: 0,
            currentRows,
            storedRows,
            rawTargetParentAddress: AncestorDescriptorIdFixtures.RootParent,
            canonicalTargetParentAddress: AncestorDescriptorIdFixtures.RootParent,
            ancestorJsonScope: AncestorDescriptorIdFixtures.AncestorScope,
            currentRowsByJsonScopeAndParent: partitionMap
        );
    }

    [Test]
    public void It_returns_the_descriptor_id_via_count_equal_positional_fallback() =>
        _result.Should().Be(7777L);
}

[TestFixture]
public class Given_TryResolveAncestorDescriptorIdFromCurrentRows_for_descriptor_only_identity_with_no_current_rows
{
    private long? _result;

    [SetUp]
    public void Setup()
    {
        var identity = AncestorDescriptorIdFixtures.Identity(
            ("$.zoneDescriptor", JsonValue.Create("uri://example.org/Zone#Urban"))
        );

        _result = RelationalWriteProfileMergeSynthesizer.TryResolveAncestorDescriptorIdFromCurrentRows(
            identity,
            descriptorIndices: [0],
            descriptorIdx: 0,
            currentRows: ImmutableArray<CurrentCollectionRowSnapshot>.Empty,
            ancestorVisibleStoredRows: ImmutableArray<VisibleStoredCollectionRow>.Empty,
            rawTargetParentAddress: AncestorDescriptorIdFixtures.RootParent,
            canonicalTargetParentAddress: AncestorDescriptorIdFixtures.RootParent,
            ancestorJsonScope: AncestorDescriptorIdFixtures.AncestorScope,
            currentRowsByJsonScopeAndParent: AncestorDescriptorIdFixtures.PartitionMap()
        );
    }

    [Test]
    public void It_returns_null() => _result.Should().BeNull();
}

[TestFixture]
public class Given_TryResolveAncestorDescriptorIdFromCurrentRows_for_descriptor_only_identity_with_multiple_count_equal_rows
{
    private long? _result;

    [SetUp]
    public void Setup()
    {
        var identity = AncestorDescriptorIdFixtures.Identity(
            ("$.zoneDescriptor", JsonValue.Create("uri://example.org/Zone#Rural"))
        );
        ImmutableArray<CurrentCollectionRowSnapshot> currentRows =
        [
            AncestorDescriptorIdFixtures.Snapshot(
                stableRowIdentity: 1L,
                ("$.zoneDescriptor", JsonValue.Create(7777L))
            ),
            AncestorDescriptorIdFixtures.Snapshot(
                stableRowIdentity: 2L,
                ("$.zoneDescriptor", JsonValue.Create(8888L))
            ),
        ];
        ImmutableArray<VisibleStoredCollectionRow> storedRows =
        [
            AncestorDescriptorIdFixtures.StoredRow(
                AncestorDescriptorIdFixtures.RootParent,
                ("$.zoneDescriptor", JsonValue.Create("uri://example.org/Zone#Urban"))
            ),
            AncestorDescriptorIdFixtures.StoredRow(
                AncestorDescriptorIdFixtures.RootParent,
                ("$.zoneDescriptor", JsonValue.Create("uri://example.org/Zone#Rural"))
            ),
        ];
        var partitionMap = AncestorDescriptorIdFixtures.PartitionMap(
            (AncestorDescriptorIdFixtures.AncestorScope, AncestorDescriptorIdFixtures.RootParent, currentRows)
        );

        _result = RelationalWriteProfileMergeSynthesizer.TryResolveAncestorDescriptorIdFromCurrentRows(
            identity,
            descriptorIndices: [0],
            descriptorIdx: 0,
            currentRows,
            storedRows,
            rawTargetParentAddress: AncestorDescriptorIdFixtures.RootParent,
            canonicalTargetParentAddress: AncestorDescriptorIdFixtures.RootParent,
            ancestorJsonScope: AncestorDescriptorIdFixtures.AncestorScope,
            currentRowsByJsonScopeAndParent: partitionMap
        );
    }

    [Test]
    public void It_resolves_the_second_row_positionally_to_the_canonical_id() => _result.Should().Be(8888L);
}

[TestFixture]
public class Given_TryResolveAncestorDescriptorIdFromCurrentRows_when_stored_count_diverges_from_current_count
{
    private long? _result;

    [SetUp]
    public void Setup()
    {
        var identity = AncestorDescriptorIdFixtures.Identity(
            ("$.zoneDescriptor", JsonValue.Create("uri://example.org/Zone#Urban"))
        );
        ImmutableArray<CurrentCollectionRowSnapshot> currentRows =
        [
            AncestorDescriptorIdFixtures.Snapshot(
                stableRowIdentity: 1L,
                ("$.zoneDescriptor", JsonValue.Create(7777L))
            ),
            AncestorDescriptorIdFixtures.Snapshot(
                stableRowIdentity: 2L,
                ("$.zoneDescriptor", JsonValue.Create(8888L))
            ),
        ];
        ImmutableArray<VisibleStoredCollectionRow> storedRows =
        [
            AncestorDescriptorIdFixtures.StoredRow(
                AncestorDescriptorIdFixtures.RootParent,
                ("$.zoneDescriptor", JsonValue.Create("uri://example.org/Zone#Urban"))
            ),
        ];
        var partitionMap = AncestorDescriptorIdFixtures.PartitionMap(
            (AncestorDescriptorIdFixtures.AncestorScope, AncestorDescriptorIdFixtures.RootParent, currentRows)
        );

        _result = RelationalWriteProfileMergeSynthesizer.TryResolveAncestorDescriptorIdFromCurrentRows(
            identity,
            descriptorIndices: [0],
            descriptorIdx: 0,
            currentRows,
            storedRows,
            rawTargetParentAddress: AncestorDescriptorIdFixtures.RootParent,
            canonicalTargetParentAddress: AncestorDescriptorIdFixtures.RootParent,
            ancestorJsonScope: AncestorDescriptorIdFixtures.AncestorScope,
            currentRowsByJsonScopeAndParent: partitionMap
        );
    }

    [Test]
    public void It_returns_null_so_caller_fails_closed_per_design() => _result.Should().BeNull();
}

[TestFixture]
public class Given_TryResolveAncestorDescriptorIdFromCurrentRows_for_descriptor_only_identity_in_two_partitions_with_partition_map
{
    private long? _result;

    [SetUp]
    public void Setup()
    {
        const string outerScope = "$.outer[*]";
        var partitionA = AncestorDescriptorIdFixtures.NestedParent(outerScope, 1L);
        var partitionB = AncestorDescriptorIdFixtures.NestedParent(outerScope, 2L);

        var identity = AncestorDescriptorIdFixtures.Identity(
            ("$.zoneDescriptor", JsonValue.Create("uri://example.org/Zone#Rural"))
        );
        ImmutableArray<CurrentCollectionRowSnapshot> partitionACurrent =
        [
            AncestorDescriptorIdFixtures.Snapshot(
                stableRowIdentity: 11L,
                ("$.zoneDescriptor", JsonValue.Create(1111L))
            ),
        ];
        ImmutableArray<CurrentCollectionRowSnapshot> partitionBCurrent =
        [
            AncestorDescriptorIdFixtures.Snapshot(
                stableRowIdentity: 22L,
                ("$.zoneDescriptor", JsonValue.Create(2222L))
            ),
        ];
        ImmutableArray<CurrentCollectionRowSnapshot> scopeWideCurrent =
        [
            .. partitionACurrent,
            .. partitionBCurrent,
        ];
        ImmutableArray<VisibleStoredCollectionRow> storedRows =
        [
            AncestorDescriptorIdFixtures.StoredRow(
                partitionA,
                ("$.zoneDescriptor", JsonValue.Create("uri://example.org/Zone#Urban"))
            ),
            AncestorDescriptorIdFixtures.StoredRow(
                partitionB,
                ("$.zoneDescriptor", JsonValue.Create("uri://example.org/Zone#Rural"))
            ),
        ];
        var partitionMap = AncestorDescriptorIdFixtures.PartitionMap(
            (AncestorDescriptorIdFixtures.AncestorScope, partitionA, partitionACurrent),
            (AncestorDescriptorIdFixtures.AncestorScope, partitionB, partitionBCurrent)
        );

        _result = RelationalWriteProfileMergeSynthesizer.TryResolveAncestorDescriptorIdFromCurrentRows(
            identity,
            descriptorIndices: [0],
            descriptorIdx: 0,
            scopeWideCurrent,
            storedRows,
            rawTargetParentAddress: partitionB,
            canonicalTargetParentAddress: partitionB,
            ancestorJsonScope: AncestorDescriptorIdFixtures.AncestorScope,
            currentRowsByJsonScopeAndParent: partitionMap
        );
    }

    [Test]
    public void It_resolves_via_target_partition_positional_pairing() => _result.Should().Be(2222L);
}

[TestFixture]
public class Given_TryResolveAncestorDescriptorIdFromCurrentRows_for_mixed_identity_with_unique_scalar_match
{
    private long? _result;

    [SetUp]
    public void Setup()
    {
        var identity = AncestorDescriptorIdFixtures.Identity(
            ("$.programId", JsonValue.Create("ProgA")),
            ("$.programTypeDescriptor", JsonValue.Create("uri://example.org/ProgramType#Reg"))
        );
        ImmutableArray<CurrentCollectionRowSnapshot> currentRows =
        [
            AncestorDescriptorIdFixtures.Snapshot(
                stableRowIdentity: 1L,
                ("$.programId", JsonValue.Create("ProgA")),
                ("$.programTypeDescriptor", JsonValue.Create(4242L))
            ),
            AncestorDescriptorIdFixtures.Snapshot(
                stableRowIdentity: 2L,
                ("$.programId", JsonValue.Create("ProgB")),
                ("$.programTypeDescriptor", JsonValue.Create(5252L))
            ),
        ];
        var partitionMap = AncestorDescriptorIdFixtures.PartitionMap(
            (AncestorDescriptorIdFixtures.AncestorScope, AncestorDescriptorIdFixtures.RootParent, currentRows)
        );

        _result = RelationalWriteProfileMergeSynthesizer.TryResolveAncestorDescriptorIdFromCurrentRows(
            identity,
            descriptorIndices: [1],
            descriptorIdx: 1,
            currentRows,
            ancestorVisibleStoredRows: ImmutableArray<VisibleStoredCollectionRow>.Empty,
            rawTargetParentAddress: AncestorDescriptorIdFixtures.RootParent,
            canonicalTargetParentAddress: AncestorDescriptorIdFixtures.RootParent,
            ancestorJsonScope: AncestorDescriptorIdFixtures.AncestorScope,
            currentRowsByJsonScopeAndParent: partitionMap
        );
    }

    [Test]
    public void It_returns_the_descriptor_id_from_the_uniquely_matched_row() => _result.Should().Be(4242L);
}

[TestFixture]
public class Given_TryResolveAncestorDescriptorIdFromCurrentRows_for_mixed_identity_with_partition_scoped_scalar_match
{
    private long? _result;

    [SetUp]
    public void Setup()
    {
        const string outerScope = "$.outer[*]";
        var partitionA = AncestorDescriptorIdFixtures.NestedParent(outerScope, 1L);
        var partitionB = AncestorDescriptorIdFixtures.NestedParent(outerScope, 2L);

        // Identity targets partition B, where code "A" uniquely identifies a row.
        // Scope-wide there are TWO rows with code "A" (one per partition); a non-partitioned
        // scalar match would see the duplicate and fail. Per-partition scalar match looks
        // only inside partition B and resolves uniquely.
        var identity = AncestorDescriptorIdFixtures.Identity(
            ("$.code", JsonValue.Create("A")),
            ("$.kindDescriptor", JsonValue.Create("uri://example.org/Kind#Alpha"))
        );

        ImmutableArray<CurrentCollectionRowSnapshot> partitionACurrent =
        [
            AncestorDescriptorIdFixtures.Snapshot(
                stableRowIdentity: 11L,
                ("$.code", JsonValue.Create("A")),
                ("$.kindDescriptor", JsonValue.Create(101L))
            ),
        ];
        ImmutableArray<CurrentCollectionRowSnapshot> partitionBCurrent =
        [
            AncestorDescriptorIdFixtures.Snapshot(
                stableRowIdentity: 22L,
                ("$.code", JsonValue.Create("A")),
                ("$.kindDescriptor", JsonValue.Create(202L))
            ),
        ];
        ImmutableArray<CurrentCollectionRowSnapshot> scopeWideCurrent =
        [
            .. partitionACurrent,
            .. partitionBCurrent,
        ];

        var partitionMap = AncestorDescriptorIdFixtures.PartitionMap(
            (AncestorDescriptorIdFixtures.AncestorScope, partitionA, partitionACurrent),
            (AncestorDescriptorIdFixtures.AncestorScope, partitionB, partitionBCurrent)
        );

        _result = RelationalWriteProfileMergeSynthesizer.TryResolveAncestorDescriptorIdFromCurrentRows(
            identity,
            descriptorIndices: [1],
            descriptorIdx: 1,
            scopeWideCurrent,
            ancestorVisibleStoredRows: ImmutableArray<VisibleStoredCollectionRow>.Empty,
            rawTargetParentAddress: partitionB,
            canonicalTargetParentAddress: partitionB,
            ancestorJsonScope: AncestorDescriptorIdFixtures.AncestorScope,
            currentRowsByJsonScopeAndParent: partitionMap
        );
    }

    [Test]
    public void It_returns_the_descriptor_id_from_the_target_partition() => _result.Should().Be(202L);
}
