// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Unit.Profile.CollectionRowContextBuilder;
using static EdFi.DataManagementService.Backend.Tests.Unit.Profile.ProfileTestDoubles;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

/// <summary>
/// Verifies that <see cref="ProfileKeyUnificationCore.ResolveForCollectionRow"/> preserves
/// the stored canonical value when the key-unification member is on a hidden path. The
/// request carries a different value ("REQUEST") but because the member is in
/// <see cref="ProfileCollectionRowKeyUnificationContext.HiddenMemberPaths"/>, the core
/// must route to the current-row value ("STORED").
/// </summary>
[TestFixture]
public class Given_row_aware_key_unification_hidden_member_preserves_stored_canonical
{
    private FlattenedWriteValue[] _row = null!;
    private int _canonicalIndex;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, _) = BuildRootPlanWithKeyUnificationMembers([
            new KeyUnificationMemberSpec(
                RelativePath: "$.memberA",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: false
            ),
        ]);
        _canonicalIndex = canonicalIdx;

        var tablePlan = plan.TablePlansInDependencyOrder[0];
        var kuPlan = tablePlan.KeyUnificationPlans[0];
        var resolverOwned = ImmutableHashSet.Create(_canonicalIndex);

        _row = NewInitialRow(tablePlan);
        var valueAssigned = new bool[_row.Length];

        // Member "$.memberA" is hidden — resolver must read from current row, not request.
        // HiddenMemberPaths format is bare (no "$.") — matches CanonicalScopeRelativeMemberPaths.
        var context = CollectionRowContext(
            plan: plan,
            requestItemNode: new JsonObject { ["memberA"] = "REQUEST" },
            currentRow: new Dictionary<DbColumnName, object?>
            {
                [MemberPathColumnFor("$.memberA")] = "STORED",
            },
            hiddenMemberPaths: ImmutableArray.Create("memberA")
        );

        ProfileKeyUnificationCore.ResolveForCollectionRow(
            tablePlan,
            kuPlan,
            context,
            _row,
            valueAssigned,
            resolverOwned
        );
    }

    [Test]
    public void It_writes_canonical_from_stored_hidden_value() =>
        ((FlattenedWriteValue.Literal)_row[_canonicalIndex]).Value.Should().Be("STORED");

    private static FlattenedWriteValue[] NewInitialRow(TableWritePlan tablePlan)
    {
        var row = new FlattenedWriteValue[tablePlan.ColumnBindings.Length];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = new FlattenedWriteValue.Literal(null);
        }
        return row;
    }
}

/// <summary>
/// Verifies that <see cref="ProfileKeyUnificationCore.ResolveForCollectionRow"/> writes
/// synthetic-presence <c>true</c> when the hidden-governed member is present in the stored row
/// (non-null presence column). The request cannot influence this because the member path is in
/// <see cref="ProfileCollectionRowKeyUnificationContext.HiddenMemberPaths"/>.
/// </summary>
[TestFixture]
public class Given_row_aware_key_unification_hidden_member_preserves_synthetic_presence
{
    private FlattenedWriteValue[] _row = null!;
    private int _canonicalIndex;
    private int _presenceIndex;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, presenceIndicesByPath) = BuildRootPlanWithKeyUnificationMembers([
            new KeyUnificationMemberSpec(
                RelativePath: "$.memberA",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: true
            ),
        ]);
        _canonicalIndex = canonicalIdx;
        _presenceIndex = presenceIndicesByPath["$.memberA"];

        var tablePlan = plan.TablePlansInDependencyOrder[0];
        var kuPlan = tablePlan.KeyUnificationPlans[0];
        var resolverOwned = ImmutableHashSet.Create(_canonicalIndex, _presenceIndex);

        _row = NewInitialRow(tablePlan);
        var valueAssigned = new bool[_row.Length];

        // Stored row: presence column is non-null (member is present), value = 42.
        // Member is hidden → core must use stored row, not request.
        // HiddenMemberPaths format is bare (no "$.") — matches CanonicalScopeRelativeMemberPaths.
        var context = CollectionRowContext(
            plan: plan,
            requestItemNode: new JsonObject(),
            currentRow: new Dictionary<DbColumnName, object?>
            {
                [MemberPathColumnFor("$.memberA")] = 42,
                [PresenceColumnFor("$.memberA")] = true,
            },
            hiddenMemberPaths: ImmutableArray.Create("memberA")
        );

        ProfileKeyUnificationCore.ResolveForCollectionRow(
            tablePlan,
            kuPlan,
            context,
            _row,
            valueAssigned,
            resolverOwned
        );
    }

    [Test]
    public void It_writes_canonical_from_stored_hidden_value() =>
        ((FlattenedWriteValue.Literal)_row[_canonicalIndex]).Value.Should().Be(42);

    [Test]
    public void It_writes_synthetic_presence_true_for_stored_present_member() =>
        ((FlattenedWriteValue.Literal)_row[_presenceIndex]).Value.Should().Be(true);

    private static FlattenedWriteValue[] NewInitialRow(TableWritePlan tablePlan)
    {
        var row = new FlattenedWriteValue[tablePlan.ColumnBindings.Length];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = new FlattenedWriteValue.Literal(null);
        }
        return row;
    }
}

/// <summary>
/// Verifies that <see cref="ProfileKeyUnificationCore.ResolveForCollectionRow"/> still raises
/// <see cref="RelationalWriteRequestValidationException"/> on canonical disagreement when both
/// members are visible (empty <see cref="ProfileCollectionRowKeyUnificationContext.HiddenMemberPaths"/>).
/// Member A and member B are both visible-present and resolve to different values from the
/// request item node.
/// </summary>
[TestFixture]
public class Given_row_aware_key_unification_visible_members_disagreement_raises_validation_exception
{
    private Exception? _thrown;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, _) = BuildRootPlanWithKeyUnificationMembers([
            new KeyUnificationMemberSpec(
                RelativePath: "$.memberA",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: false
            ),
            new KeyUnificationMemberSpec(
                RelativePath: "$.memberB",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: false
            ),
        ]);

        var tablePlan = plan.TablePlansInDependencyOrder[0];
        var kuPlan = tablePlan.KeyUnificationPlans[0];
        var resolverOwned = ImmutableHashSet.Create(canonicalIdx);

        var row = NewInitialRow(tablePlan);
        var valueAssigned = new bool[row.Length];

        // Both members visible (no hidden paths). MemberA = 1, memberB = 2 → disagreement.
        var context = CollectionRowContext(
            plan: plan,
            requestItemNode: new JsonObject { ["memberA"] = 1, ["memberB"] = 2 },
            currentRow: new Dictionary<DbColumnName, object?>(),
            hiddenMemberPaths: ImmutableArray<string>.Empty
        );

        try
        {
            ProfileKeyUnificationCore.ResolveForCollectionRow(
                tablePlan,
                kuPlan,
                context,
                row,
                valueAssigned,
                resolverOwned
            );
        }
        catch (Exception ex)
        {
            _thrown = ex;
        }
    }

    [Test]
    public void It_raises_RelationalWriteRequestValidationException() =>
        _thrown.Should().BeOfType<RelationalWriteRequestValidationException>();

    [Test]
    public void It_includes_key_unification_conflict_in_message() =>
        ((RelationalWriteRequestValidationException)_thrown!)
            .ValidationFailures[0]
            .Message.Should()
            .Contain("Key-unification conflict");

    private static FlattenedWriteValue[] NewInitialRow(TableWritePlan tablePlan)
    {
        var row = new FlattenedWriteValue[tablePlan.ColumnBindings.Length];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = new FlattenedWriteValue.Literal(null);
        }
        return row;
    }
}

/// <summary>
/// Regression test for the path-domain mismatch bug in
/// <see cref="ProfileKeyUnificationCore.ClassifyMemberVisibilityFromHiddenSet"/>.
/// When the governing table has a non-root scope (e.g. "$.classPeriods[*]"),
/// <see cref="ProfileCollectionRowKeyUnificationContext.HiddenMemberPaths"/> is scope-relative
/// (e.g. "$.memberA"), but the buggy code compared the absolute path
/// "$.classPeriods[*].memberA" against the hidden set and found no match, so the
/// member was misclassified as <c>VisiblePresent</c> and the stored value was overwritten.
/// </summary>
[TestFixture]
public class Given_row_aware_key_unification_hidden_member_on_non_root_scope_preserves_stored_canonical
{
    private FlattenedWriteValue[] _row = null!;
    private int _canonicalIndex;

    [SetUp]
    public void Setup()
    {
        // Use a collection-scoped table: "$.classPeriods[*]".
        // Member RelativePath is scope-relative: "$.memberA".
        // ToAbsoluteBindingPath("$.classPeriods[*]", "$.memberA") = "$.classPeriods[*].memberA".
        // HiddenMemberPaths format is bare (CanonicalScopeRelativeMemberPaths convention): ["memberA"].
        // Bug: Contains("$.classPeriods[*].memberA") = false → VisiblePresent → REQUEST wins.
        // Fix: StripScopePrefix("$.classPeriods[*].memberA", "$.classPeriods[*]") = "memberA"
        //      → Contains("memberA") = true → HiddenGoverned → STORED wins.
        var (plan, canonicalIdx, _) = BuildRootPlusRootExtensionPlanWithKeyUnification(
            [
                new KeyUnificationMemberSpec(
                    RelativePath: "$.memberA",
                    SourceKind: KeyUnificationMemberSourceKind.Scalar,
                    PresenceSynthetic: false
                ),
            ],
            extensionJsonScope: "$.classPeriods[*]"
        );
        _canonicalIndex = canonicalIdx;

        var tablePlan = plan.TablePlansInDependencyOrder[1]; // collection-scoped table
        var kuPlan = tablePlan.KeyUnificationPlans[0];
        var resolverOwned = ImmutableHashSet.Create(_canonicalIndex);

        _row = NewInitialRow(tablePlan);
        var valueAssigned = new bool[_row.Length];

        // HiddenMemberPaths format is bare (CanonicalScopeRelativeMemberPaths convention) — "memberA",
        // not absolute "$.classPeriods[*].memberA" and not scope-relative "$.memberA".
        var context = CollectionRowContext(
            plan: plan,
            requestItemNode: new JsonObject { ["memberA"] = "REQUEST" },
            currentRow: new Dictionary<DbColumnName, object?>
            {
                [MemberPathColumnFor("$.memberA")] = "STORED",
            },
            hiddenMemberPaths: ImmutableArray.Create("memberA")
        );

        ProfileKeyUnificationCore.ResolveForCollectionRow(
            tablePlan,
            kuPlan,
            context,
            _row,
            valueAssigned,
            resolverOwned
        );
    }

    [Test]
    public void It_preserves_stored_canonical_on_non_root_scope() =>
        ((FlattenedWriteValue.Literal)_row[_canonicalIndex]).Value.Should().Be("STORED");

    private static FlattenedWriteValue[] NewInitialRow(TableWritePlan tablePlan)
    {
        var row = new FlattenedWriteValue[tablePlan.ColumnBindings.Length];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = new FlattenedWriteValue.Literal(null);
        }
        return row;
    }
}

internal static class CollectionRowContextBuilder
{
    internal static ProfileCollectionRowKeyUnificationContext CollectionRowContext(
        ResourceWritePlan plan,
        JsonNode requestItemNode,
        IReadOnlyDictionary<DbColumnName, object?> currentRow,
        ImmutableArray<string> hiddenMemberPaths
    ) =>
        new(
            RequestItemNode: requestItemNode,
            CurrentRowByColumnName: currentRow,
            HiddenMemberPaths: hiddenMemberPaths,
            ResolvedReferenceLookups: EmptyResolvedReferenceLookups(plan)
        );
}
