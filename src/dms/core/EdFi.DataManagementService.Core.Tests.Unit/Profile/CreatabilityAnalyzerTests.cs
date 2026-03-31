// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

public abstract class CreatabilityAnalyzerTests
{
    // -----------------------------------------------------------------------
    //  Test helper: mock IStoredSideExistenceLookup
    // -----------------------------------------------------------------------

    private sealed class TestExistenceLookup(
        HashSet<string>? existingScopeJsonScopes = null,
        HashSet<string>? existingCollectionJsonScopes = null
    ) : IStoredSideExistenceLookup
    {
        public bool VisibleScopeExistsAt(ScopeInstanceAddress address) =>
            existingScopeJsonScopes?.Contains(address.JsonScope) ?? false;

        public bool VisibleCollectionRowExistsAt(CollectionRowAddress address) =>
            existingCollectionJsonScopes?.Contains(address.JsonScope) ?? false;
    }

    // -----------------------------------------------------------------------
    //  Shared helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates a minimal ScopeInstanceAddress for a given JsonScope with no ancestors.
    /// </summary>
    private static ScopeInstanceAddress MakeAddress(string jsonScope) => new(jsonScope, []);

    /// <summary>
    /// Creates a minimal CollectionRowAddress for a given collection JsonScope with
    /// a root parent address and a single semantic identity part.
    /// </summary>
    private static CollectionRowAddress MakeCollectionRowAddress(
        string jsonScope,
        string identityPath,
        string identityValue
    ) =>
        new(
            jsonScope,
            MakeAddress("$"),
            [
                new SemanticIdentityPart(
                    identityPath,
                    System.Text.Json.Nodes.JsonValue.Create(identityValue),
                    true
                ),
            ]
        );

    // -----------------------------------------------------------------------
    //  1. POST with all required members visible → root creatable, no failures
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Create_With_All_Required_Members_Visible : CreatabilityAnalyzerTests
    {
        private CreatabilityResult _result = null!;

        [SetUp]
        public void Setup()
        {
            IReadOnlyList<CompiledScopeDescriptor> scopes = ProfileTestFixtures.SharedFixtureScopes;
            var classifier = new ProfileVisibilityClassifier(
                ProfileTestFixtures.BuildIncludeAllProfile(),
                scopes
            );
            var analyzer = new CreatabilityAnalyzer(
                scopes,
                classifier,
                "TestProfile",
                "StudentSchoolAssociation",
                "POST",
                "Create"
            );

            // Root scope is VisiblePresent
            ImmutableArray<RequestScopeState> scopeStates =
            [
                new(MakeAddress("$"), ProfileVisibilityKind.VisiblePresent, Creatable: false),
                new(
                    MakeAddress("$.calendarReference"),
                    ProfileVisibilityKind.VisibleAbsent,
                    Creatable: false
                ),
            ];

            ImmutableArray<VisibleRequestCollectionItem> items = [];

            var effectiveRequired = new Dictionary<string, IReadOnlyList<string>>
            {
                ["$"] = ["studentReference", "schoolReference", "entryDate"],
            };

            _result = analyzer.Analyze(
                scopeStates,
                items,
                new TestExistenceLookup(),
                isCreate: true,
                effectiveRequired
            );
        }

        [Test]
        public void It_should_report_root_as_creatable()
        {
            _result.RootResourceCreatable.Should().BeTrue();
        }

        [Test]
        public void It_should_have_no_failures()
        {
            _result.Failures.Should().BeEmpty();
        }

        [Test]
        public void It_should_enrich_root_scope_as_creatable()
        {
            _result.EnrichedScopeStates.Should().Contain(s => s.Address.JsonScope == "$" && s.Creatable);
        }
    }

    // -----------------------------------------------------------------------
    //  2. POST with one root required member hidden → non-creatable, one failure
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Create_With_Hidden_Required_Member : CreatabilityAnalyzerTests
    {
        private CreatabilityResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // IncludeOnly profile that only includes studentReference and schoolReference
            // but NOT entryDate (which is required)
            IReadOnlyList<CompiledScopeDescriptor> scopes =
            [
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["studentReference", "schoolReference", "entryDate"]
                ),
            ];

            var profile = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties: [new PropertyRule("studentReference"), new PropertyRule("schoolReference")],
                Objects: [],
                Collections: [],
                Extensions: []
            );

            var classifier = new ProfileVisibilityClassifier(profile, scopes);
            var analyzer = new CreatabilityAnalyzer(
                scopes,
                classifier,
                "TestProfile",
                "StudentSchoolAssociation",
                "POST",
                "Create"
            );

            ImmutableArray<RequestScopeState> scopeStates =
            [
                new(MakeAddress("$"), ProfileVisibilityKind.VisiblePresent, Creatable: false),
            ];

            var effectiveRequired = new Dictionary<string, IReadOnlyList<string>>
            {
                ["$"] = ["studentReference", "schoolReference", "entryDate"],
            };

            _result = analyzer.Analyze(
                scopeStates,
                [],
                new TestExistenceLookup(),
                isCreate: true,
                effectiveRequired
            );
        }

        [Test]
        public void It_should_report_root_as_non_creatable()
        {
            _result.RootResourceCreatable.Should().BeFalse();
        }

        [Test]
        public void It_should_have_one_category_4_failure()
        {
            _result.Failures.Should().HaveCount(1);
            _result
                .Failures[0]
                .Should()
                .BeOfType<RootCreateRejectedWhenNonCreatableCreatabilityViolationFailure>();
        }

        [Test]
        public void It_should_report_entryDate_as_hidden_in_failure()
        {
            var failure = (RootCreateRejectedWhenNonCreatableCreatabilityViolationFailure)_result.Failures[0];
            failure.HiddenCreationRequiredMemberPaths.Should().Contain("entryDate");
        }
    }

    // -----------------------------------------------------------------------
    //  3. PUT to existing root (isCreate=false) → non-creatable, no failures
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Update_Existing_Root : CreatabilityAnalyzerTests
    {
        private CreatabilityResult _result = null!;

        [SetUp]
        public void Setup()
        {
            IReadOnlyList<CompiledScopeDescriptor> scopes =
            [
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["studentReference", "schoolReference", "entryDate"]
                ),
            ];

            var classifier = new ProfileVisibilityClassifier(
                ProfileTestFixtures.BuildIncludeAllProfile(),
                scopes
            );
            var analyzer = new CreatabilityAnalyzer(
                scopes,
                classifier,
                "TestProfile",
                "StudentSchoolAssociation",
                "PUT",
                "Update"
            );

            ImmutableArray<RequestScopeState> scopeStates =
            [
                new(MakeAddress("$"), ProfileVisibilityKind.VisiblePresent, Creatable: false),
            ];

            _result = analyzer.Analyze(
                scopeStates,
                [],
                new TestExistenceLookup(),
                isCreate: false,
                new Dictionary<string, IReadOnlyList<string>>()
            );
        }

        [Test]
        public void It_should_report_root_as_non_creatable()
        {
            _result.RootResourceCreatable.Should().BeFalse();
        }

        [Test]
        public void It_should_have_no_failures()
        {
            _result.Failures.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  4. VisiblePresent scope, not in stored state → creatable (all required visible)
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Scope_Create_No_Stored_Scope : CreatabilityAnalyzerTests
    {
        private CreatabilityResult _result = null!;

        [SetUp]
        public void Setup()
        {
            IReadOnlyList<CompiledScopeDescriptor> scopes =
            [
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["field1"]
                ),
                new(
                    JsonScope: "$.calendarReference",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["calendarCode"]
                ),
            ];

            var classifier = new ProfileVisibilityClassifier(
                ProfileTestFixtures.BuildIncludeAllProfile(),
                scopes
            );
            var analyzer = new CreatabilityAnalyzer(
                scopes,
                classifier,
                "TestProfile",
                "Resource",
                "POST",
                "Create"
            );

            ImmutableArray<RequestScopeState> scopeStates =
            [
                new(MakeAddress("$"), ProfileVisibilityKind.VisiblePresent, Creatable: false),
                new(
                    MakeAddress("$.calendarReference"),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
            ];

            // Existence lookup: calendarReference does NOT exist in stored state
            var existenceLookup = new TestExistenceLookup();

            _result = analyzer.Analyze(
                scopeStates,
                [],
                existenceLookup,
                isCreate: true,
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["$"] = ["field1"],
                    ["$.calendarReference"] = ["calendarCode"],
                }
            );
        }

        [Test]
        public void It_should_mark_child_scope_as_creatable()
        {
            _result
                .EnrichedScopeStates.Should()
                .Contain(s => s.Address.JsonScope == "$.calendarReference" && s.Creatable);
        }

        [Test]
        public void It_should_have_no_failures()
        {
            _result.Failures.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  5. VisiblePresent scope, exists in stored state → non-creatable, no failure
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Scope_Update_Existing_Stored_Scope : CreatabilityAnalyzerTests
    {
        private CreatabilityResult _result = null!;

        [SetUp]
        public void Setup()
        {
            IReadOnlyList<CompiledScopeDescriptor> scopes =
            [
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["field1"]
                ),
                new(
                    JsonScope: "$.calendarReference",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["calendarCode"]
                ),
            ];

            var classifier = new ProfileVisibilityClassifier(
                ProfileTestFixtures.BuildIncludeAllProfile(),
                scopes
            );
            var analyzer = new CreatabilityAnalyzer(
                scopes,
                classifier,
                "TestProfile",
                "Resource",
                "PUT",
                "Update"
            );

            ImmutableArray<RequestScopeState> scopeStates =
            [
                new(MakeAddress("$"), ProfileVisibilityKind.VisiblePresent, Creatable: false),
                new(
                    MakeAddress("$.calendarReference"),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
            ];

            // Existence lookup: calendarReference DOES exist in stored state
            var existenceLookup = new TestExistenceLookup(existingScopeJsonScopes: ["$.calendarReference"]);

            _result = analyzer.Analyze(
                scopeStates,
                [],
                existenceLookup,
                isCreate: false,
                new Dictionary<string, IReadOnlyList<string>>()
            );
        }

        [Test]
        public void It_should_mark_child_scope_as_non_creatable()
        {
            _result
                .EnrichedScopeStates.Should()
                .Contain(s => s.Address.JsonScope == "$.calendarReference" && !s.Creatable);
        }

        [Test]
        public void It_should_have_no_failures()
        {
            _result.Failures.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  6. VisibleAbsent scope → non-creatable, no failure
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_VisibleAbsent_Scope : CreatabilityAnalyzerTests
    {
        private CreatabilityResult _result = null!;

        [SetUp]
        public void Setup()
        {
            IReadOnlyList<CompiledScopeDescriptor> scopes =
            [
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["field1"]
                ),
                new(
                    JsonScope: "$.calendarReference",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["calendarCode"]
                ),
            ];

            var classifier = new ProfileVisibilityClassifier(
                ProfileTestFixtures.BuildIncludeAllProfile(),
                scopes
            );
            var analyzer = new CreatabilityAnalyzer(
                scopes,
                classifier,
                "TestProfile",
                "Resource",
                "POST",
                "Create"
            );

            ImmutableArray<RequestScopeState> scopeStates =
            [
                new(MakeAddress("$"), ProfileVisibilityKind.VisiblePresent, Creatable: false),
                new(
                    MakeAddress("$.calendarReference"),
                    ProfileVisibilityKind.VisibleAbsent,
                    Creatable: false
                ),
            ];

            _result = analyzer.Analyze(
                scopeStates,
                [],
                new TestExistenceLookup(),
                isCreate: true,
                new Dictionary<string, IReadOnlyList<string>> { ["$"] = ["field1"] }
            );
        }

        [Test]
        public void It_should_mark_absent_scope_as_non_creatable()
        {
            _result
                .EnrichedScopeStates.Should()
                .Contain(s => s.Address.JsonScope == "$.calendarReference" && !s.Creatable);
        }

        [Test]
        public void It_should_have_no_failures()
        {
            _result.Failures.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  7. Collection item create (not in stored state) → creatable
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Collection_Item_Create : CreatabilityAnalyzerTests
    {
        private CreatabilityResult _result = null!;

        [SetUp]
        public void Setup()
        {
            IReadOnlyList<CompiledScopeDescriptor> scopes =
            [
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["field1"]
                ),
                new(
                    JsonScope: "$.classPeriods[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["classPeriodName"],
                    CanonicalScopeRelativeMemberPaths: ["classPeriodName"]
                ),
            ];

            var classifier = new ProfileVisibilityClassifier(
                ProfileTestFixtures.BuildIncludeAllProfile(),
                scopes
            );
            var analyzer = new CreatabilityAnalyzer(
                scopes,
                classifier,
                "TestProfile",
                "Resource",
                "POST",
                "Create"
            );

            ImmutableArray<RequestScopeState> scopeStates =
            [
                new(MakeAddress("$"), ProfileVisibilityKind.VisiblePresent, Creatable: false),
            ];

            ImmutableArray<VisibleRequestCollectionItem> items =
            [
                new(
                    MakeCollectionRowAddress("$.classPeriods[*]", "classPeriodName", "Period1"),
                    Creatable: false
                ),
            ];

            // Item does NOT exist in stored state
            var existenceLookup = new TestExistenceLookup();

            _result = analyzer.Analyze(
                scopeStates,
                items,
                existenceLookup,
                isCreate: true,
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["$"] = ["field1"],
                    ["$.classPeriods[*]"] = ["classPeriodName"],
                }
            );
        }

        [Test]
        public void It_should_mark_collection_item_as_creatable()
        {
            _result.EnrichedCollectionItems.Should().HaveCount(1);
            _result.EnrichedCollectionItems[0].Creatable.Should().BeTrue();
        }

        [Test]
        public void It_should_have_no_failures()
        {
            _result.Failures.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  8. Collection item update (exists in stored state) → non-creatable, no failure
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Collection_Item_Update : CreatabilityAnalyzerTests
    {
        private CreatabilityResult _result = null!;

        [SetUp]
        public void Setup()
        {
            IReadOnlyList<CompiledScopeDescriptor> scopes =
            [
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["field1"]
                ),
                new(
                    JsonScope: "$.classPeriods[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["classPeriodName"],
                    CanonicalScopeRelativeMemberPaths: ["classPeriodName"]
                ),
            ];

            var classifier = new ProfileVisibilityClassifier(
                ProfileTestFixtures.BuildIncludeAllProfile(),
                scopes
            );
            var analyzer = new CreatabilityAnalyzer(
                scopes,
                classifier,
                "TestProfile",
                "Resource",
                "PUT",
                "Update"
            );

            ImmutableArray<RequestScopeState> scopeStates =
            [
                new(MakeAddress("$"), ProfileVisibilityKind.VisiblePresent, Creatable: false),
            ];

            ImmutableArray<VisibleRequestCollectionItem> items =
            [
                new(
                    MakeCollectionRowAddress("$.classPeriods[*]", "classPeriodName", "Period1"),
                    Creatable: false
                ),
            ];

            // Item DOES exist in stored state
            var existenceLookup = new TestExistenceLookup(
                existingCollectionJsonScopes: ["$.classPeriods[*]"]
            );

            _result = analyzer.Analyze(
                scopeStates,
                items,
                existenceLookup,
                isCreate: false,
                new Dictionary<string, IReadOnlyList<string>>()
            );
        }

        [Test]
        public void It_should_mark_collection_item_as_non_creatable()
        {
            _result.EnrichedCollectionItems.Should().HaveCount(1);
            _result.EnrichedCollectionItems[0].Creatable.Should().BeFalse();
        }

        [Test]
        public void It_should_have_no_failures()
        {
            _result.Failures.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  9. Child non-creatable when parent is not creatable (parent gate)
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Child_NonCreatable_When_Parent_Not_Creatable : CreatabilityAnalyzerTests
    {
        private CreatabilityResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // Parent scope (non-collection) hides a required member so it is non-creatable.
            // Child scope has all members visible but should still be non-creatable
            // because of parent gating.
            IReadOnlyList<CompiledScopeDescriptor> scopes =
            [
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["field1"]
                ),
                new(
                    JsonScope: "$.parentObject",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["parentField", "hiddenField"]
                ),
                new(
                    JsonScope: "$.parentObject.nestedChild",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$.parentObject",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["childField"]
                ),
            ];

            // IncludeOnly profile: root includes field1, parentObject includes parentField
            // but NOT hiddenField (which is required). nestedChild includes childField.
            var profile = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties: [new PropertyRule("field1")],
                Objects:
                [
                    new ObjectRule(
                        Name: "parentObject",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("parentField")],
                        NestedObjects:
                        [
                            new ObjectRule(
                                Name: "nestedChild",
                                MemberSelection: MemberSelection.IncludeOnly,
                                LogicalSchema: null,
                                Properties: [new PropertyRule("childField")],
                                NestedObjects: null,
                                Collections: null,
                                Extensions: null
                            ),
                        ],
                        Collections: null,
                        Extensions: null
                    ),
                ],
                Collections: [],
                Extensions: []
            );

            var classifier = new ProfileVisibilityClassifier(profile, scopes);
            var analyzer = new CreatabilityAnalyzer(
                scopes,
                classifier,
                "TestProfile",
                "Resource",
                "POST",
                "Create"
            );

            ImmutableArray<RequestScopeState> scopeStates =
            [
                new(MakeAddress("$"), ProfileVisibilityKind.VisiblePresent, Creatable: false),
                new(MakeAddress("$.parentObject"), ProfileVisibilityKind.VisiblePresent, Creatable: false),
                new(
                    MakeAddress("$.parentObject.nestedChild"),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
            ];

            // Neither parentObject nor nestedChild exist in stored state (both are new creates)
            var existenceLookup = new TestExistenceLookup();

            var effectiveRequired = new Dictionary<string, IReadOnlyList<string>>
            {
                ["$"] = ["field1"],
                ["$.parentObject"] = ["parentField", "hiddenField"],
                ["$.parentObject.nestedChild"] = ["childField"],
            };

            _result = analyzer.Analyze(scopeStates, [], existenceLookup, isCreate: true, effectiveRequired);
        }

        [Test]
        public void It_should_mark_parent_scope_as_non_creatable()
        {
            _result
                .EnrichedScopeStates.Should()
                .Contain(s => s.Address.JsonScope == "$.parentObject" && !s.Creatable);
        }

        [Test]
        public void It_should_mark_child_scope_as_non_creatable()
        {
            _result
                .EnrichedScopeStates.Should()
                .Contain(s => s.Address.JsonScope == "$.parentObject.nestedChild" && !s.Creatable);
        }

        [Test]
        public void It_should_have_failures_for_both_non_creatable_scopes()
        {
            // Parent fails due to hidden required member, child fails due to parent gate
            _result.Failures.Should().HaveCountGreaterThanOrEqualTo(2);
        }

        [Test]
        public void It_should_include_parent_dependency_in_child_failure()
        {
            var childFailures = _result
                .Failures.OfType<VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure>()
                .Where(f => f.JsonScope == "$.parentObject.nestedChild");

            childFailures.Should().HaveCount(1);
            var childFailure = childFailures.First();
            childFailure
                .Dependencies.Should()
                .Contain(d => d.DependencyKind == ProfileCreatabilityDependencyKind.ImmediateVisibleParent);
        }
    }
}
