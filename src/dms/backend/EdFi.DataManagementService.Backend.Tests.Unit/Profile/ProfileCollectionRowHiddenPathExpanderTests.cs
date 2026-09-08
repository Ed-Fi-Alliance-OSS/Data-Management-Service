// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

/// <summary>
/// Unit tests for <see cref="ProfileCollectionRowHiddenPathExpander"/>. The expander folds
/// hidden member paths from inlined non-collection descendant scopes onto each
/// <see cref="VisibleStoredCollectionRow.HiddenMemberPaths"/> so the matched-row classifier
/// preserves stored values for members that a descendant scope's restrictive sub-rule
/// hides — closing the data-loss gap left by Slice 5 CP4's fence retirement on
/// collection-descendant inlined non-collection scopes.
/// </summary>
[TestFixture]
public class ProfileCollectionRowHiddenPathExpanderTests
{
    private const string CollectionScope = "$.addresses[*]";

    private static ImmutableArray<SemanticIdentityPart> Identity(string addressType) =>
        [new SemanticIdentityPart("addressType", JsonValue.Create(addressType), IsPresent: true)];

    private static VisibleStoredCollectionRow Row(
        ImmutableArray<SemanticIdentityPart> identity,
        params string[] hiddenPaths
    ) =>
        new(
            new CollectionRowAddress(CollectionScope, new ScopeInstanceAddress("$", []), identity),
            HiddenMemberPaths: [.. hiddenPaths]
        );

    private static StoredScopeState DescendantState(
        string descendantJsonScope,
        ImmutableArray<SemanticIdentityPart> rowIdentity,
        ProfileVisibilityKind visibility,
        params string[] hiddenMemberPaths
    ) =>
        new(
            Address: new ScopeInstanceAddress(
                JsonScope: descendantJsonScope,
                AncestorCollectionInstances: [new AncestorCollectionInstance(CollectionScope, rowIdentity)]
            ),
            Visibility: visibility,
            HiddenMemberPaths: [.. hiddenMemberPaths]
        );

    [TestFixture]
    public class Given_Inlined_NonCollection_Descendant_With_Hidden_Members
        : ProfileCollectionRowHiddenPathExpanderTests
    {
        private ImmutableArray<VisibleStoredCollectionRow> _expanded;

        [SetUp]
        public void Setup()
        {
            // SchoolAddress is the collection table at $.addresses[*]; the write plan registers
            // no separate table for $.addresses[*].period, so ResolveOwnerTablePlan walks to
            // $.addresses[*] and the expander treats period as inlined into SchoolAddress.
            // HiddenMemberPaths=["endDate"] simulates Core's per-scope state for an inlined
            // value type whose IncludeOnly:[beginDate] sub-rule hides endDate from a profile
            // that is otherwise IncludeAll at the collection level.
            var plan = AdapterFactoryTestFixtures.BuildRootAndCollectionPlan();
            var collectionPlan = plan.TablePlansInDependencyOrder[1];
            var rowIdentity = Identity("Home");

            _expanded = ProfileCollectionRowHiddenPathExpander.Expand(
                rows: [Row(rowIdentity, "addressType")],
                storedScopeStates:
                [
                    DescendantState(
                        "$.addresses[*].period",
                        rowIdentity,
                        ProfileVisibilityKind.VisiblePresent,
                        "endDate"
                    ),
                ],
                collectionScope: CollectionScope,
                collectionTablePlan: collectionPlan,
                writePlan: plan
            );
        }

        [Test]
        public void It_preserves_existing_row_hidden_paths()
        {
            _expanded.Should().HaveCount(1);
            _expanded[0].HiddenMemberPaths.Should().Contain("addressType");
        }

        [Test]
        public void It_folds_descendant_path_with_collection_relative_prefix()
        {
            _expanded[0].HiddenMemberPaths.Should().Contain("period.endDate");
        }
    }

    [TestFixture]
    public class Given_Deeper_Inlined_Descendant : ProfileCollectionRowHiddenPathExpanderTests
    {
        private ImmutableArray<VisibleStoredCollectionRow> _expanded;

        [SetUp]
        public void Setup()
        {
            // Two-level inlined descendant: $.addresses[*].period.detail. The walker emits a
            // StoredScopeState whose AncestorCollectionInstances still ends with the
            // addresses row (period and detail are non-collections, never appear in the
            // collection-ancestor chain). Relative path under the collection scope is
            // "period.detail"; the expander prefixes each hidden member with that path.
            var plan = AdapterFactoryTestFixtures.BuildRootAndCollectionPlan();
            var collectionPlan = plan.TablePlansInDependencyOrder[1];
            var rowIdentity = Identity("Home");

            _expanded = ProfileCollectionRowHiddenPathExpander.Expand(
                rows: [Row(rowIdentity)],
                storedScopeStates:
                [
                    DescendantState(
                        "$.addresses[*].period.detail",
                        rowIdentity,
                        ProfileVisibilityKind.Hidden,
                        "description",
                        "category"
                    ),
                ],
                collectionScope: CollectionScope,
                collectionTablePlan: collectionPlan,
                writePlan: plan
            );
        }

        [Test]
        public void It_prefixes_each_path_with_full_relative_descendant_scope()
        {
            _expanded[0].HiddenMemberPaths.Should().Contain("period.detail.description");
            _expanded[0].HiddenMemberPaths.Should().Contain("period.detail.category");
        }
    }

    [TestFixture]
    public class Given_Descendant_For_A_Different_Row_Identity : ProfileCollectionRowHiddenPathExpanderTests
    {
        private ImmutableArray<VisibleStoredCollectionRow> _expanded;

        [SetUp]
        public void Setup()
        {
            // Two stored rows (Home, Work) with one inlined-descendant scope state pinned to
            // the Work row only. The expander must fold the descendant's hidden paths onto
            // Work without contaminating Home, since Home and Work are independent rows even
            // though they share the same scope and column shape.
            var plan = AdapterFactoryTestFixtures.BuildRootAndCollectionPlan();
            var collectionPlan = plan.TablePlansInDependencyOrder[1];
            var homeIdentity = Identity("Home");
            var workIdentity = Identity("Work");

            _expanded = ProfileCollectionRowHiddenPathExpander.Expand(
                rows: [Row(homeIdentity), Row(workIdentity)],
                storedScopeStates:
                [
                    DescendantState(
                        "$.addresses[*].period",
                        workIdentity,
                        ProfileVisibilityKind.VisiblePresent,
                        "endDate"
                    ),
                ],
                collectionScope: CollectionScope,
                collectionTablePlan: collectionPlan,
                writePlan: plan
            );
        }

        [Test]
        public void It_folds_path_only_onto_matching_row()
        {
            var home = _expanded.Single(r =>
                r.Address.SemanticIdentityInOrder[0].Value!.GetValue<string>() == "Home"
            );
            var work = _expanded.Single(r =>
                r.Address.SemanticIdentityInOrder[0].Value!.GetValue<string>() == "Work"
            );

            home.HiddenMemberPaths.Should().BeEmpty();
            work.HiddenMemberPaths.Should().Contain("period.endDate");
        }
    }

    [TestFixture]
    public class Given_NonDescendant_Scope_State : ProfileCollectionRowHiddenPathExpanderTests
    {
        private ImmutableArray<VisibleStoredCollectionRow> _expanded;

        [SetUp]
        public void Setup()
        {
            // Defensive: a stored scope state whose JsonScope does not start with the
            // collection scope's prefix (e.g. a root extension or a sibling scope) must be
            // ignored. Without this filter, paths from unrelated scopes would pollute the
            // row's HiddenMemberPaths and fail the row-level coverage check.
            var plan = AdapterFactoryTestFixtures.BuildRootAndCollectionPlan();
            var collectionPlan = plan.TablePlansInDependencyOrder[1];
            var rowIdentity = Identity("Home");

            var siblingState = new StoredScopeState(
                Address: new ScopeInstanceAddress("$._ext.sample", []),
                Visibility: ProfileVisibilityKind.Hidden,
                HiddenMemberPaths: ["someField"]
            );

            _expanded = ProfileCollectionRowHiddenPathExpander.Expand(
                rows: [Row(rowIdentity, "addressType")],
                storedScopeStates: [siblingState],
                collectionScope: CollectionScope,
                collectionTablePlan: collectionPlan,
                writePlan: plan
            );
        }

        [Test]
        public void It_does_not_fold_unrelated_scope_paths()
        {
            _expanded[0].HiddenMemberPaths.Should().BeEquivalentTo("addressType");
        }
    }

    [TestFixture]
    public class Given_No_Descendant_Scope_States : ProfileCollectionRowHiddenPathExpanderTests
    {
        [Test]
        public void It_returns_input_rows_unchanged()
        {
            var plan = AdapterFactoryTestFixtures.BuildRootAndCollectionPlan();
            var collectionPlan = plan.TablePlansInDependencyOrder[1];
            var row = Row(Identity("Home"), "addressType");

            var expanded = ProfileCollectionRowHiddenPathExpander.Expand(
                rows: [row],
                storedScopeStates: [],
                collectionScope: CollectionScope,
                collectionTablePlan: collectionPlan,
                writePlan: plan
            );

            expanded.Should().HaveCount(1);
            expanded[0].HiddenMemberPaths.Should().BeEquivalentTo("addressType");
        }
    }

    /// <summary>
    /// Regression: nested-collection identity is only stable within its parent. Two parent
    /// rows P1 and P2 each contain a child row with the same semantic identity ("Shared").
    /// A stored inlined-descendant scope under <c>P2 -> Shared</c> contributes a hidden path,
    /// and the walker dispatches expansion for the P1 child bucket. The expander must not
    /// fold P2's descendant hidden path onto P1's child row, because the row's full
    /// <see cref="CollectionRowAddress"/> (including the parent ancestor chain) differs.
    /// </summary>
    [TestFixture]
    public class Given_Same_Child_Identity_Under_Different_Parents
        : ProfileCollectionRowHiddenPathExpanderTests
    {
        private ImmutableArray<VisibleStoredCollectionRow> _expanded;

        [SetUp]
        public void Setup()
        {
            var (plan, _, childrenPlan) = NestedTopologyBuilders.BuildRootParentsAndChildrenPlan();

            // The walker has already bucketed visible-stored rows for the P1 child scope:
            // only P1's "Shared" child appears in `rows`. The descendant scope under
            // $.parents[*].children[*].period belongs to P2's "Shared" child, evidenced by
            // its full ancestor chain (P2 -> Shared). The expander must compare the full
            // CollectionRowAddress, not just the last ancestor's semantic identity, so the
            // P2-rooted descendant path is rejected for the P1 row.
            var p1ChildRow = NestedTopologyBuilders.BuildChildStoredRow("P1", "Shared");
            var p2DescendantState = new StoredScopeState(
                Address: new ScopeInstanceAddress(
                    JsonScope: "$.parents[*].children[*].period",
                    AncestorCollectionInstances:
                    [
                        new AncestorCollectionInstance(
                            NestedTopologyBuilders.ParentsScope,
                            NestedTopologyBuilders.Identity("P2")
                        ),
                        new AncestorCollectionInstance(
                            NestedTopologyBuilders.ChildrenScope,
                            NestedTopologyBuilders.Identity("Shared")
                        ),
                    ]
                ),
                Visibility: ProfileVisibilityKind.VisiblePresent,
                HiddenMemberPaths: ["endDate"]
            );

            _expanded = ProfileCollectionRowHiddenPathExpander.Expand(
                rows: [p1ChildRow],
                storedScopeStates: [p2DescendantState],
                collectionScope: NestedTopologyBuilders.ChildrenScope,
                collectionTablePlan: childrenPlan,
                writePlan: plan
            );
        }

        [Test]
        public void It_does_not_fold_descendant_path_from_a_different_parent()
        {
            _expanded.Should().HaveCount(1);
            _expanded[0].HiddenMemberPaths.Should().NotContain("period.endDate");
        }
    }

    /// <summary>
    /// Companion to <see cref="Given_Same_Child_Identity_Under_Different_Parents"/>: when the
    /// descendant's full ancestor chain matches the row being walked (same parent <c>P1</c>
    /// and same child identity <c>Shared</c>), the expander folds <c>period.endDate</c> onto
    /// that row. This proves the structural address match accepts the legitimate case it
    /// must accept while rejecting the cross-parent case above.
    /// </summary>
    [TestFixture]
    public class Given_Same_Child_Identity_Under_Same_Parent : ProfileCollectionRowHiddenPathExpanderTests
    {
        private ImmutableArray<VisibleStoredCollectionRow> _expanded;

        [SetUp]
        public void Setup()
        {
            var (plan, _, childrenPlan) = NestedTopologyBuilders.BuildRootParentsAndChildrenPlan();

            var p1ChildRow = NestedTopologyBuilders.BuildChildStoredRow("P1", "Shared");
            var p1DescendantState = new StoredScopeState(
                Address: new ScopeInstanceAddress(
                    JsonScope: "$.parents[*].children[*].period",
                    AncestorCollectionInstances:
                    [
                        new AncestorCollectionInstance(
                            NestedTopologyBuilders.ParentsScope,
                            NestedTopologyBuilders.Identity("P1")
                        ),
                        new AncestorCollectionInstance(
                            NestedTopologyBuilders.ChildrenScope,
                            NestedTopologyBuilders.Identity("Shared")
                        ),
                    ]
                ),
                Visibility: ProfileVisibilityKind.VisiblePresent,
                HiddenMemberPaths: ["endDate"]
            );

            _expanded = ProfileCollectionRowHiddenPathExpander.Expand(
                rows: [p1ChildRow],
                storedScopeStates: [p1DescendantState],
                collectionScope: NestedTopologyBuilders.ChildrenScope,
                collectionTablePlan: childrenPlan,
                writePlan: plan
            );
        }

        [Test]
        public void It_folds_descendant_path_when_full_ancestor_chain_matches()
        {
            _expanded.Should().HaveCount(1);
            _expanded[0].HiddenMemberPaths.Should().Contain("period.endDate");
        }
    }

    /// <summary>
    /// Two rows in the same collection differ only in <see cref="SemanticIdentityPart.IsPresent"/>:
    /// one row has the identity property missing entirely, the other has it as explicit JSON
    /// null. A descendant <see cref="StoredScopeState"/> belongs to only the explicit-null
    /// row. Under the presence-aware key the descendant's hidden paths must fold onto that
    /// row alone — never onto the missing-identity row.
    /// </summary>
    [TestFixture]
    public class Given_Sibling_Rows_Differing_Only_By_Identity_Presence
        : ProfileCollectionRowHiddenPathExpanderTests
    {
        private ImmutableArray<VisibleStoredCollectionRow> _expanded;

        [SetUp]
        public void Setup()
        {
            var plan = AdapterFactoryTestFixtures.BuildRootAndCollectionPlan();
            var collectionPlan = plan.TablePlansInDependencyOrder[1];

            var missingIdentity = ImmutableArray.Create(
                new SemanticIdentityPart("addressType", null, IsPresent: false)
            );
            var explicitNullIdentity = ImmutableArray.Create(
                new SemanticIdentityPart("addressType", null, IsPresent: true)
            );

            _expanded = ProfileCollectionRowHiddenPathExpander.Expand(
                rows: [Row(missingIdentity, "addressType"), Row(explicitNullIdentity, "addressType")],
                storedScopeStates:
                [
                    DescendantState(
                        "$.addresses[*].period",
                        explicitNullIdentity,
                        ProfileVisibilityKind.VisiblePresent,
                        "endDate"
                    ),
                ],
                collectionScope: CollectionScope,
                collectionTablePlan: collectionPlan,
                writePlan: plan
            );
        }

        [Test]
        public void It_folds_hidden_paths_onto_the_explicit_null_row_only()
        {
            _expanded.Should().HaveCount(2);

            var missingRow = _expanded[0];
            missingRow.Address.SemanticIdentityInOrder[0].IsPresent.Should().BeFalse();
            missingRow.HiddenMemberPaths.Should().NotContain("period.endDate");

            var explicitNullRow = _expanded[1];
            explicitNullRow.Address.SemanticIdentityInOrder[0].IsPresent.Should().BeTrue();
            explicitNullRow.HiddenMemberPaths.Should().Contain("period.endDate");
        }
    }
}
