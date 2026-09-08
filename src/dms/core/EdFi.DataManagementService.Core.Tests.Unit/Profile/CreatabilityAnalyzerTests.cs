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
        HashSet<ScopeInstanceAddress>? existingScopes = null,
        HashSet<CollectionRowAddress>? existingCollectionRows = null
    ) : IStoredSideExistenceLookup
    {
        public bool VisibleScopeExistsAt(ScopeInstanceAddress address) =>
            existingScopes?.Contains(address) ?? false;

        public bool VisibleCollectionRowExistsAt(CollectionRowAddress address) =>
            existingCollectionRows?.Contains(address) ?? false;
    }

    /// <summary>
    /// Creates a HashSet of ScopeInstanceAddresses using structural equality.
    /// </summary>
    private static HashSet<ScopeInstanceAddress> ScopeAddressSet(params ScopeInstanceAddress[] addresses) =>
        new(addresses, ScopeInstanceAddressComparer.Instance);

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
            var existenceLookup = new TestExistenceLookup(
                existingScopes: ScopeAddressSet(MakeAddress("$.calendarReference"))
            );

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
                    Creatable: false,
                    "$.classPeriods[0]"
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
                    Creatable: false,
                    "$.classPeriods[0]"
                ),
            ];

            // Item DOES exist in stored state
            var existenceLookup = new TestExistenceLookup(
                existingCollectionRows: new(
                    [MakeCollectionRowAddress("$.classPeriods[*]", "classPeriodName", "Period1")],
                    CollectionRowAddressComparer.Instance
                )
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

    // -----------------------------------------------------------------------
    //  10. Three-level chain creatability: root → parent scope → child scope → nested child scope
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Three_Level_Chain_With_All_New_Descendants : CreatabilityAnalyzerTests
    {
        private CreatabilityResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // Four non-collection scopes forming a three-level chain:
            //   $                               (Root)
            //   $.parentObject                  (NonCollection, parent: $)
            //   $.parentObject._ext.project     (NonCollection, parent: $.parentObject)
            //   $.parentObject._ext.project.detail (NonCollection, parent: $.parentObject._ext.project)
            IReadOnlyList<CompiledScopeDescriptor> scopes =
            [
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["schoolReference"]
                ),
                new(
                    JsonScope: "$.parentObject",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["parentField"]
                ),
                new(
                    JsonScope: "$.parentObject._ext.project",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$.parentObject",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["projectCode"]
                ),
                new(
                    JsonScope: "$.parentObject._ext.project.detail",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$.parentObject._ext.project",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["detailField"]
                ),
            ];

            // IncludeAll profile: all members visible at every scope
            var profile = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeAll,
                Properties: [],
                Objects:
                [
                    new ObjectRule(
                        Name: "parentObject",
                        MemberSelection: MemberSelection.IncludeAll,
                        LogicalSchema: null,
                        Properties: [],
                        NestedObjects:
                        [
                            new ObjectRule(
                                Name: "detail",
                                MemberSelection: MemberSelection.IncludeAll,
                                LogicalSchema: null,
                                Properties: [],
                                NestedObjects: null,
                                Collections: null,
                                Extensions: null
                            ),
                        ],
                        Collections: null,
                        Extensions:
                        [
                            new ExtensionRule(
                                Name: "project",
                                MemberSelection: MemberSelection.IncludeAll,
                                LogicalSchema: null,
                                Properties: [],
                                Objects:
                                [
                                    new ObjectRule(
                                        Name: "detail",
                                        MemberSelection: MemberSelection.IncludeAll,
                                        LogicalSchema: null,
                                        Properties: [],
                                        NestedObjects: null,
                                        Collections: null,
                                        Extensions: null
                                    ),
                                ],
                                Collections: null
                            ),
                        ]
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
                "PUT",
                "Update"
            );

            // Root scope state (existing resource, update path). All child scopes are new.
            ImmutableArray<RequestScopeState> scopeStates =
            [
                new(MakeAddress("$"), ProfileVisibilityKind.VisiblePresent, Creatable: false),
                new(MakeAddress("$.parentObject"), ProfileVisibilityKind.VisiblePresent, Creatable: false),
                new(
                    MakeAddress("$.parentObject._ext.project"),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
                new(
                    MakeAddress("$.parentObject._ext.project.detail"),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
            ];

            // No stored scopes for any child → all are new creates
            var existenceLookup = new TestExistenceLookup();

            var effectiveRequired = new Dictionary<string, IReadOnlyList<string>>
            {
                ["$"] = ["schoolReference"],
                ["$.parentObject"] = ["parentField"],
                ["$.parentObject._ext.project"] = ["projectCode"],
                ["$.parentObject._ext.project.detail"] = ["detailField"],
            };

            _result = analyzer.Analyze(scopeStates, [], existenceLookup, isCreate: false, effectiveRequired);
        }

        [Test]
        public void It_should_report_root_as_non_creatable()
        {
            // Root is existing (isCreate=false) so it is on the update path
            _result.RootResourceCreatable.Should().BeFalse();
        }

        [Test]
        public void It_should_mark_parent_scope_as_creatable()
        {
            // New non-collection scope with all required members visible, parent root exists
            _result
                .EnrichedScopeStates.Should()
                .Contain(s => s.Address.JsonScope == "$.parentObject" && s.Creatable);
        }

        [Test]
        public void It_should_mark_extension_scope_as_creatable()
        {
            // New extension scope with all required visible, parent scope is creatable
            _result
                .EnrichedScopeStates.Should()
                .Contain(s => s.Address.JsonScope == "$.parentObject._ext.project" && s.Creatable);
        }

        [Test]
        public void It_should_mark_nested_child_scope_as_creatable()
        {
            // New nested scope with all required visible, parent ext scope is creatable
            _result
                .EnrichedScopeStates.Should()
                .Contain(s => s.Address.JsonScope == "$.parentObject._ext.project.detail" && s.Creatable);
        }

        [Test]
        public void It_should_have_no_failures()
        {
            _result.Failures.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  10b. Three-level chain with collections: existing root → middle
    //       common-type scope → descendant extension child collection.
    //       Proves creatability gates across three levels including collections.
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Three_Level_Chain_With_Collection_Scopes : CreatabilityAnalyzerTests
    {
        private CreatabilityResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // Three-level chain:
            //   $                                        (Root, existing)
            //   $.commonType                             (NonCollection, parent: $, new)
            //   $.commonType._ext.project                (NonCollection extension, parent: $.commonType, new)
            //   $.commonType._ext.project.services[*]    (Collection, parent: $.commonType._ext.project)
            IReadOnlyList<CompiledScopeDescriptor> scopes =
            [
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["schoolReference"]
                ),
                new(
                    JsonScope: "$.commonType",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["commonField"]
                ),
                new(
                    JsonScope: "$.commonType._ext.project",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$.commonType",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["projectCode"]
                ),
                new(
                    JsonScope: "$.commonType._ext.project.services[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$.commonType._ext.project",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["serviceDescriptor"],
                    CanonicalScopeRelativeMemberPaths: ["serviceDescriptor"]
                ),
            ];

            // IncludeAll profile: all members visible at every scope
            var profile = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeAll,
                Properties: [],
                Objects:
                [
                    new ObjectRule(
                        Name: "commonType",
                        MemberSelection: MemberSelection.IncludeAll,
                        LogicalSchema: null,
                        Properties: [],
                        NestedObjects: null,
                        Collections: null,
                        Extensions:
                        [
                            new ExtensionRule(
                                Name: "project",
                                MemberSelection: MemberSelection.IncludeAll,
                                LogicalSchema: null,
                                Properties: [],
                                Objects: null,
                                Collections:
                                [
                                    new CollectionRule(
                                        Name: "services",
                                        MemberSelection: MemberSelection.IncludeAll,
                                        LogicalSchema: null,
                                        Properties: null,
                                        NestedObjects: null,
                                        NestedCollections: null,
                                        Extensions: null,
                                        ItemFilter: null
                                    ),
                                ]
                            ),
                        ]
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
                "PUT",
                "Update"
            );

            // Root exists (update path). Middle and extension scopes are new.
            ImmutableArray<RequestScopeState> scopeStates =
            [
                new(MakeAddress("$"), ProfileVisibilityKind.VisiblePresent, Creatable: false),
                new(MakeAddress("$.commonType"), ProfileVisibilityKind.VisiblePresent, Creatable: false),
                new(
                    MakeAddress("$.commonType._ext.project"),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
            ];

            // Two collection items: one new (Coaching), one matched in stored (Tutoring)
            var coachingRowAddress = MakeCollectionRowAddress(
                "$.commonType._ext.project.services[*]",
                "serviceDescriptor",
                "Coaching"
            );
            var tutoringRowAddress = MakeCollectionRowAddress(
                "$.commonType._ext.project.services[*]",
                "serviceDescriptor",
                "Tutoring"
            );

            ImmutableArray<VisibleRequestCollectionItem> items =
            [
                new(coachingRowAddress, Creatable: false, "$.studentProgramAssociationServices[0]"),
                new(tutoringRowAddress, Creatable: false, "$.studentProgramAssociationServices[1]"),
            ];

            // Root exists in stored state; Tutoring item exists (matched)
            var existenceLookup = new TestExistenceLookup(
                existingScopes: ScopeAddressSet(MakeAddress("$")),
                existingCollectionRows: new HashSet<CollectionRowAddress>(
                    CollectionRowAddressComparer.Instance
                )
                {
                    tutoringRowAddress,
                }
            );

            var effectiveRequired = new Dictionary<string, IReadOnlyList<string>>
            {
                ["$"] = ["schoolReference"],
                ["$.commonType"] = ["commonField"],
                ["$.commonType._ext.project"] = ["projectCode"],
                ["$.commonType._ext.project.services[*]"] = ["serviceDescriptor"],
            };

            _result = analyzer.Analyze(
                scopeStates,
                items,
                existenceLookup,
                isCreate: false,
                effectiveRequired
            );
        }

        [Test]
        public void It_should_report_root_as_non_creatable()
        {
            // Root is existing (isCreate=false) so it is on the update path
            _result.RootResourceCreatable.Should().BeFalse();
        }

        [Test]
        public void It_should_mark_middle_common_type_scope_as_creatable()
        {
            // New non-collection scope with all required members visible, parent root exists
            _result
                .EnrichedScopeStates.Should()
                .Contain(s => s.Address.JsonScope == "$.commonType" && s.Creatable);
        }

        [Test]
        public void It_should_mark_extension_scope_as_creatable()
        {
            // New extension scope with all required visible, parent scope is creatable
            _result
                .EnrichedScopeStates.Should()
                .Contain(s => s.Address.JsonScope == "$.commonType._ext.project" && s.Creatable);
        }

        [Test]
        public void It_should_mark_new_collection_item_as_creatable()
        {
            // Coaching item is new and parent extension scope is creatable
            _result
                .EnrichedCollectionItems.Should()
                .Contain(i =>
                    i.Address.JsonScope == "$.commonType._ext.project.services[*]"
                    && i.Address.SemanticIdentityInOrder[0].Value!.GetValue<string>() == "Coaching"
                    && i.Creatable
                );
        }

        [Test]
        public void It_should_mark_matched_collection_item_as_non_creatable()
        {
            // Tutoring item is matched in stored state → update, not creatable
            _result
                .EnrichedCollectionItems.Should()
                .Contain(i =>
                    i.Address.JsonScope == "$.commonType._ext.project.services[*]"
                    && i.Address.SemanticIdentityInOrder[0].Value!.GetValue<string>() == "Tutoring"
                    && !i.Creatable
                );
        }

        [Test]
        public void It_should_have_no_failures()
        {
            _result.Failures.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  11. Update-allowed: existing scope matched in stored state → non-creatable, no failure
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Update_Allowed_For_Existing_Scope : CreatabilityAnalyzerTests
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
                    CanonicalScopeRelativeMemberPaths: ["calendarCode", "schoolYear"]
                ),
            ];

            // IncludeOnly profile that exposes calendarCode but NOT schoolYear (required)
            var profile = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties: [new PropertyRule("field1")],
                Objects:
                [
                    new ObjectRule(
                        Name: "calendarReference",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("calendarCode")],
                        NestedObjects: null,
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

            // Scope EXISTS in stored state → update path, not a create
            var existenceLookup = new TestExistenceLookup(
                existingScopes: ScopeAddressSet(MakeAddress("$.calendarReference"))
            );

            _result = analyzer.Analyze(
                scopeStates,
                [],
                existenceLookup,
                isCreate: false,
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["$"] = ["field1"],
                    ["$.calendarReference"] = ["calendarCode", "schoolYear"],
                }
            );
        }

        [Test]
        public void It_should_mark_scope_as_non_creatable()
        {
            _result
                .EnrichedScopeStates.Should()
                .Contain(s => s.Address.JsonScope == "$.calendarReference" && !s.Creatable);
        }

        [Test]
        public void It_should_have_no_failures()
        {
            // Existing scope → update path → no creatability failure even though
            // the profile hides a required member (schoolYear)
            _result.Failures.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  12. Create-denied: new scope with hidden required member → non-creatable, failure
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Create_Denied_For_New_Scope_With_Hidden_Required : CreatabilityAnalyzerTests
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
                    CanonicalScopeRelativeMemberPaths: ["calendarCode", "schoolYear"]
                ),
            ];

            // IncludeOnly profile that exposes calendarCode but NOT schoolYear (required)
            var profile = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties: [new PropertyRule("field1")],
                Objects:
                [
                    new ObjectRule(
                        Name: "calendarReference",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("calendarCode")],
                        NestedObjects: null,
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

            // Scope does NOT exist in stored state → attempted create
            var existenceLookup = new TestExistenceLookup();

            _result = analyzer.Analyze(
                scopeStates,
                [],
                existenceLookup,
                isCreate: false,
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["$"] = ["field1"],
                    ["$.calendarReference"] = ["calendarCode", "schoolYear"],
                }
            );
        }

        [Test]
        public void It_should_mark_scope_as_non_creatable()
        {
            _result
                .EnrichedScopeStates.Should()
                .Contain(s => s.Address.JsonScope == "$.calendarReference" && !s.Creatable);
        }

        [Test]
        public void It_should_have_one_category_4_failure()
        {
            _result.Failures.Should().HaveCount(1);
            _result
                .Failures[0]
                .Should()
                .BeOfType<VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure>();
        }

        [Test]
        public void It_should_report_schoolYear_as_hidden_in_failure()
        {
            var failure = (VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure)
                _result.Failures[0];
            failure.HiddenCreationRequiredMemberPaths.Should().Contain("schoolYear");
        }
    }

    // -----------------------------------------------------------------------
    //  13. Bottom-up descendant blocks parent (not yet implemented)
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Descendant_Blocks_Parent_Bottom_Up : CreatabilityAnalyzerTests
    {
        private CreatabilityResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // Parent non-collection scope is new, child extension scope is also new
            // but has hidden required members. Bottom-up rule says parent should also
            // be non-creatable because its required descendant can't be created.
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
                    CanonicalScopeRelativeMemberPaths: ["parentField"]
                ),
                new(
                    JsonScope: "$.parentObject._ext.sample",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$.parentObject",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["extField", "hiddenExtField"]
                ),
            ];

            // Parent has all members visible; child extension hides hiddenExtField
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
                        NestedObjects: null,
                        Collections: null,
                        Extensions:
                        [
                            new ExtensionRule(
                                Name: "sample",
                                MemberSelection: MemberSelection.IncludeOnly,
                                LogicalSchema: null,
                                Properties: [new PropertyRule("extField")],
                                Objects: null,
                                Collections: null
                            ),
                        ]
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
                    MakeAddress("$.parentObject._ext.sample"),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
            ];

            // Neither scope exists in stored state (both are new creates)
            var existenceLookup = new TestExistenceLookup();

            var effectiveRequired = new Dictionary<string, IReadOnlyList<string>>
            {
                ["$"] = ["field1"],
                ["$.parentObject"] = ["parentField"],
                ["$.parentObject._ext.sample"] = ["extField", "hiddenExtField"],
            };

            _result = analyzer.Analyze(scopeStates, [], existenceLookup, isCreate: true, effectiveRequired);
        }

        [Test]
        public void It_should_mark_child_extension_scope_as_non_creatable()
        {
            // Child has hidden required member → non-creatable
            _result
                .EnrichedScopeStates.Should()
                .Contain(s => s.Address.JsonScope == "$.parentObject._ext.sample" && !s.Creatable);
        }

        [Test]
        public void It_should_mark_parent_scope_as_non_creatable_due_to_descendant()
        {
            // Bottom-up propagation: parent demoted because required descendant is non-creatable
            _result
                .EnrichedScopeStates.Should()
                .Contain(s => s.Address.JsonScope == "$.parentObject" && !s.Creatable);
        }

        [Test]
        public void It_should_include_descendant_dependency_in_parent_failure()
        {
            var parentFailures = _result
                .Failures.OfType<VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure>()
                .Where(f => f.JsonScope == "$.parentObject");

            parentFailures.Should().HaveCount(1);
            var parentFailure = parentFailures.First();
            parentFailure
                .Dependencies.Should()
                .Contain(d =>
                    d.DependencyKind == ProfileCreatabilityDependencyKind.RequiredVisibleDescendant
                );
        }
    }

    // -----------------------------------------------------------------------
    //  13b. Root blocked by non-creatable descendant (bottom-up propagation
    //       reaches root per profiles.md:443-446 and :476)
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Root_Blocked_By_NonCreatable_Descendant : CreatabilityAnalyzerTests
    {
        private CreatabilityResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // Root + one child non-collection scope. Child has a hidden required member,
            // making it non-creatable. Bottom-up propagation should demote root.
            IReadOnlyList<CompiledScopeDescriptor> scopes =
            [
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["schoolReference"]
                ),
                new(
                    JsonScope: "$.childObject",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["visibleField", "hiddenField"]
                ),
            ];

            // Root has all members visible; child hides hiddenField
            var profile = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties: [new PropertyRule("schoolReference")],
                Objects:
                [
                    new ObjectRule(
                        Name: "childObject",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("visibleField")],
                        NestedObjects: null,
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
                new(MakeAddress("$.childObject"), ProfileVisibilityKind.VisiblePresent, Creatable: false),
            ];

            // Neither scope exists in stored state (both are new creates)
            var existenceLookup = new TestExistenceLookup();

            var effectiveRequired = new Dictionary<string, IReadOnlyList<string>>
            {
                ["$"] = ["schoolReference"],
                ["$.childObject"] = ["visibleField", "hiddenField"],
            };

            _result = analyzer.Analyze(scopeStates, [], existenceLookup, isCreate: true, effectiveRequired);
        }

        [Test]
        public void It_should_mark_child_as_non_creatable()
        {
            _result
                .EnrichedScopeStates.Should()
                .Contain(s => s.Address.JsonScope == "$.childObject" && !s.Creatable);
        }

        [Test]
        public void It_should_mark_root_as_non_creatable_due_to_descendant()
        {
            _result.EnrichedScopeStates.Should().Contain(s => s.Address.JsonScope == "$" && !s.Creatable);
        }

        [Test]
        public void It_should_set_RootResourceCreatable_to_false()
        {
            _result.RootResourceCreatable.Should().BeFalse();
        }

        [Test]
        public void It_should_emit_failure_for_the_non_creatable_child_only()
        {
            // Root demotion is expressed through RootResourceCreatable=false, not a
            // scope-level failure. Only the child's own hidden-required failure is emitted.
            _result.Failures.Should().HaveCount(1);
            _result
                .Failures.OfType<VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure>()
                .Should()
                .Contain(f => f.JsonScope == "$.childObject");
        }
    }

    // -----------------------------------------------------------------------
    //  13c. Multi-instance parent gate: only the specific parent matching the
    //       child's ancestor context is checked, not all instances of the same scope.
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Multi_Instance_Parent_Gate_Matches_Ancestor_Context : CreatabilityAnalyzerTests
    {
        private CreatabilityResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // $.a has two instances (different ancestor contexts, simulating
            // being inside different collection items). Instance 1 exists in
            // stored state (update path). Instance 2 is a new create with a
            // hidden required member (non-creatable).
            //
            // $.a.child also has two instances. Child 1's parent gate should
            // be satisfied (parent instance 1 exists). Child 2's parent gate
            // should NOT be satisfied (parent instance 2 is non-creatable).
            //
            // Without ancestor-aware matching, IsParentGateSatisfied("$.a")
            // would return true for BOTH children because instance 1 exists.
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
                    JsonScope: "$.a",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["visibleField", "hiddenField"]
                ),
                new(
                    JsonScope: "$.a.child",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$.a",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["childField"]
                ),
            ];

            // Profile hides hiddenField on $.a, all members visible on $.a.child
            var profile = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties: [new PropertyRule("field1")],
                Objects:
                [
                    new ObjectRule(
                        Name: "a",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("visibleField")],
                        NestedObjects:
                        [
                            new ObjectRule(
                                Name: "child",
                                MemberSelection: MemberSelection.IncludeAll,
                                LogicalSchema: null,
                                Properties: [],
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
                "PUT",
                "Update"
            );

            // Ancestor contexts representing two different collection items
            var ancestorsAlpha = ImmutableArray.Create(
                new AncestorCollectionInstance(
                    "$.coll[*]",
                    [new SemanticIdentityPart("id", System.Text.Json.Nodes.JsonValue.Create("Alpha"), true)]
                )
            );
            var ancestorsBeta = ImmutableArray.Create(
                new AncestorCollectionInstance(
                    "$.coll[*]",
                    [new SemanticIdentityPart("id", System.Text.Json.Nodes.JsonValue.Create("Beta"), true)]
                )
            );

            ImmutableArray<RequestScopeState> scopeStates =
            [
                // Root: existing
                new(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
                // $.a instance 1 (Alpha context): will exist in stored state
                new(
                    new ScopeInstanceAddress("$.a", ancestorsAlpha),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
                // $.a instance 2 (Beta context): NOT in stored state → new create, non-creatable
                new(
                    new ScopeInstanceAddress("$.a", ancestorsBeta),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
                // $.a.child instance 1 (Alpha context): new create
                new(
                    new ScopeInstanceAddress("$.a.child", ancestorsAlpha),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
                // $.a.child instance 2 (Beta context): new create
                new(
                    new ScopeInstanceAddress("$.a.child", ancestorsBeta),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
            ];

            // $.a instance 1 (Alpha) exists; everything else is new
            var existenceLookup = new TestExistenceLookup(
                existingScopes: ScopeAddressSet(
                    new ScopeInstanceAddress("$", []),
                    new ScopeInstanceAddress("$.a", ancestorsAlpha)
                )
            );

            var effectiveRequired = new Dictionary<string, IReadOnlyList<string>>
            {
                ["$"] = ["field1"],
                ["$.a"] = ["visibleField", "hiddenField"],
                ["$.a.child"] = ["childField"],
            };

            _result = analyzer.Analyze(scopeStates, [], existenceLookup, isCreate: false, effectiveRequired);
        }

        [Test]
        public void It_should_mark_parent_instance_1_as_not_new_create()
        {
            // Instance 1 (Alpha) exists in stored state → not a new create, Creatable=false
            _result.EnrichedScopeStates[1].Creatable.Should().BeFalse();
        }

        [Test]
        public void It_should_mark_parent_instance_2_as_non_creatable()
        {
            // Instance 2 (Beta) is a new create with hidden required member → non-creatable
            _result.EnrichedScopeStates[2].Creatable.Should().BeFalse();
        }

        [Test]
        public void It_should_mark_child_instance_1_as_creatable()
        {
            // Child 1 (Alpha context): parent instance 1 exists (not new create) → gate satisfied
            _result.EnrichedScopeStates[3].Creatable.Should().BeTrue();
        }

        [Test]
        public void It_should_mark_child_instance_2_as_non_creatable()
        {
            // Child 2 (Beta context): parent instance 2 is non-creatable new create → gate fails
            _result.EnrichedScopeStates[4].Creatable.Should().BeFalse();
        }

        [Test]
        public void It_should_emit_failure_for_parent_instance_2()
        {
            // $.a instance 2 has hidden required member
            _result
                .Failures.OfType<VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure>()
                .Should()
                .Contain(f => f.JsonScope == "$.a");
        }

        [Test]
        public void It_should_emit_failure_for_child_instance_2()
        {
            // $.a.child instance 2 fails parent gate
            _result
                .Failures.OfType<VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure>()
                .Should()
                .Contain(f => f.JsonScope == "$.a.child");
        }
    }

    // -----------------------------------------------------------------------
    //  13b. Collection items under different parent instances (address-scoped gate)
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Collection_Items_Under_Different_Parent_Instances : CreatabilityAnalyzerTests
    {
        private CreatabilityResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // $.parent has two instances (different ancestor contexts, simulating
            // being inside different collection items). Instance 1 (Alpha) exists
            // in stored state (update path). Instance 2 (Beta) is a new create with
            // a hidden required member (non-creatable).
            //
            // $.parent.items[*] has two collection items, one under each parent instance.
            // The Alpha item's parent gate should be satisfied (parent exists).
            // The Beta item's parent gate should NOT be satisfied (parent is non-creatable).
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
                    JsonScope: "$.parent",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["visibleField", "hiddenField"]
                ),
                new(
                    JsonScope: "$.parent.items[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$.parent",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["itemName"],
                    CanonicalScopeRelativeMemberPaths: ["itemName"]
                ),
            ];

            // Profile hides hiddenField on $.parent, all members visible on collection
            var profile = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties: [new PropertyRule("field1")],
                Objects:
                [
                    new ObjectRule(
                        Name: "parent",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("visibleField")],
                        NestedObjects: null,
                        Collections:
                        [
                            new CollectionRule(
                                Name: "items",
                                MemberSelection: MemberSelection.IncludeAll,
                                LogicalSchema: null,
                                Properties: [],
                                NestedObjects: null,
                                NestedCollections: null,
                                Extensions: null,
                                ItemFilter: null
                            ),
                        ],
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
                "PUT",
                "Update"
            );

            // Ancestor contexts representing two different collection items
            var ancestorsAlpha = ImmutableArray.Create(
                new AncestorCollectionInstance(
                    "$.coll[*]",
                    [new SemanticIdentityPart("id", System.Text.Json.Nodes.JsonValue.Create("Alpha"), true)]
                )
            );
            var ancestorsBeta = ImmutableArray.Create(
                new AncestorCollectionInstance(
                    "$.coll[*]",
                    [new SemanticIdentityPart("id", System.Text.Json.Nodes.JsonValue.Create("Beta"), true)]
                )
            );

            ImmutableArray<RequestScopeState> scopeStates =
            [
                // Root: existing
                new(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
                // $.parent instance 1 (Alpha context): will exist in stored state
                new(
                    new ScopeInstanceAddress("$.parent", ancestorsAlpha),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
                // $.parent instance 2 (Beta context): NOT in stored state -> new create, non-creatable
                new(
                    new ScopeInstanceAddress("$.parent", ancestorsBeta),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
            ];

            // Collection items under each parent instance
            var alphaItem = new VisibleRequestCollectionItem(
                new CollectionRowAddress(
                    "$.parent.items[*]",
                    new ScopeInstanceAddress("$.parent", ancestorsAlpha),
                    [
                        new SemanticIdentityPart(
                            "itemName",
                            System.Text.Json.Nodes.JsonValue.Create("ItemA"),
                            true
                        ),
                    ]
                ),
                Creatable: false,
                "$.parent[0].items[0]"
            );
            var betaItem = new VisibleRequestCollectionItem(
                new CollectionRowAddress(
                    "$.parent.items[*]",
                    new ScopeInstanceAddress("$.parent", ancestorsBeta),
                    [
                        new SemanticIdentityPart(
                            "itemName",
                            System.Text.Json.Nodes.JsonValue.Create("ItemB"),
                            true
                        ),
                    ]
                ),
                Creatable: false,
                "$.parent[1].items[0]"
            );
            ImmutableArray<VisibleRequestCollectionItem> items = [alphaItem, betaItem];

            // $.parent instance 1 (Alpha) exists; everything else is new
            var existenceLookup = new TestExistenceLookup(
                existingScopes: ScopeAddressSet(
                    new ScopeInstanceAddress("$", []),
                    new ScopeInstanceAddress("$.parent", ancestorsAlpha)
                )
            );

            var effectiveRequired = new Dictionary<string, IReadOnlyList<string>>
            {
                ["$"] = ["field1"],
                ["$.parent"] = ["visibleField", "hiddenField"],
                ["$.parent.items[*]"] = ["itemName"],
            };

            _result = analyzer.Analyze(
                scopeStates,
                items,
                existenceLookup,
                isCreate: false,
                effectiveRequired
            );
        }

        [Test]
        public void It_should_mark_alpha_collection_item_as_creatable()
        {
            // Alpha item: parent exists (not new create) -> gate satisfied, all members visible
            _result.EnrichedCollectionItems[0].Creatable.Should().BeTrue();
        }

        [Test]
        public void It_should_mark_beta_collection_item_as_non_creatable()
        {
            // Beta item: parent is non-creatable new create -> gate fails
            _result.EnrichedCollectionItems[1].Creatable.Should().BeFalse();
        }

        [Test]
        public void It_should_emit_failure_for_beta_parent()
        {
            // $.parent instance 2 (Beta) has hidden required member
            _result
                .Failures.OfType<VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure>()
                .Should()
                .Contain(f => f.JsonScope == "$.parent");
        }

        [Test]
        public void It_should_emit_failure_for_beta_collection_item()
        {
            // Beta collection item fails parent gate
            _result
                .Failures.OfType<VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure>()
                .Should()
                .Contain(f => f.JsonScope == "$.parent.items[*]");
        }
    }

    // -----------------------------------------------------------------------
    //  14. Storage-managed values excluded from creatability checks
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Create_With_Storage_Managed_Values_In_Required : CreatabilityAnalyzerTests
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
                    CanonicalScopeRelativeMemberPaths: ["id", "studentReference", "_etag"]
                ),
            ];

            // IncludeOnly profile that only includes studentReference
            // (id and _etag are NOT in the profile, but they are storage-managed)
            var profile = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties: [new PropertyRule("studentReference")],
                Objects: [],
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
            ];

            // effectiveSchemaRequired includes id, studentReference, and _etag
            var effectiveRequired = new Dictionary<string, IReadOnlyList<string>>
            {
                ["$"] = ["id", "studentReference", "_etag"],
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
        public void It_should_report_root_as_creatable()
        {
            // id and _etag are storage-managed, excluded from creation-required
            // studentReference is visible → root is creatable
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
    //  15. Duplicate detection integration with CreatabilityAnalyzer output
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Duplicate_Items_From_Analyzer_Output : CreatabilityAnalyzerTests
    {
        private CreatabilityResult _analyzerResult = null!;
        private ImmutableArray<WritableProfileValidationFailure> _duplicateResult;

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

            // Two items with the SAME semantic identity in the same collection
            var item1 = new VisibleRequestCollectionItem(
                MakeCollectionRowAddress("$.classPeriods[*]", "classPeriodName", "Period1"),
                Creatable: false,
                "$.classPeriods[0]"
            );
            var item2 = new VisibleRequestCollectionItem(
                MakeCollectionRowAddress("$.classPeriods[*]", "classPeriodName", "Period1"),
                Creatable: false,
                "$.classPeriods[1]"
            );

            ImmutableArray<RequestScopeState> scopeStates =
            [
                new(MakeAddress("$"), ProfileVisibilityKind.VisiblePresent, Creatable: false),
            ];

            ImmutableArray<VisibleRequestCollectionItem> items = [item1, item2];

            var existenceLookup = new TestExistenceLookup();

            var effectiveRequired = new Dictionary<string, IReadOnlyList<string>>
            {
                ["$"] = ["field1"],
                ["$.classPeriods[*]"] = ["classPeriodName"],
            };

            _analyzerResult = analyzer.Analyze(
                scopeStates,
                items,
                existenceLookup,
                isCreate: true,
                effectiveRequired
            );

            // Run DuplicateCollectionItemDetector on the analyzer's enriched output
            _duplicateResult = DuplicateCollectionItemDetector.Detect(
                _analyzerResult.EnrichedCollectionItems,
                profileName: "TestProfile",
                resourceName: "Resource",
                method: "POST",
                operation: "Create"
            );
        }

        [Test]
        public void It_should_produce_enriched_collection_items()
        {
            _analyzerResult.EnrichedCollectionItems.Should().HaveCount(2);
        }

        [Test]
        public void It_should_detect_one_duplicate_collision()
        {
            _duplicateResult.Should().HaveCount(1);
        }

        [Test]
        public void It_should_return_correct_failure_type()
        {
            _duplicateResult[0]
                .Should()
                .BeOfType<DuplicateVisibleCollectionItemCollisionWritableProfileValidationFailure>();
        }

        [Test]
        public void It_should_report_correct_scope_in_duplicate_failure()
        {
            var failure = (DuplicateVisibleCollectionItemCollisionWritableProfileValidationFailure)
                _duplicateResult[0];
            failure.JsonScope.Should().Be("$.classPeriods[*]");
        }
    }

    // -----------------------------------------------------------------------
    //  16. Non-collection scope nested under existing collection item resolves parent gate
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Scope_Nested_Under_Existing_Collection_Item : CreatabilityAnalyzerTests
    {
        private CreatabilityResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // Hierarchy: $ -> $.items[*] -> $.items[*].detail
            // The collection item exists in stored state.
            // The detail scope is new (not in stored state) and should be creatable
            // because its parent (the collection item) exists.
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
                    JsonScope: "$.items[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["itemId"],
                    CanonicalScopeRelativeMemberPaths: ["itemId"]
                ),
                new(
                    JsonScope: "$.items[*].detail",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$.items[*]",
                    CollectionAncestorsInOrder: ["$.items[*]"],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["detailField"]
                ),
            ];

            // IncludeAll profile — all members visible everywhere
            var profile = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeAll,
                Properties: [],
                Objects: [],
                Collections:
                [
                    new CollectionRule(
                        Name: "items",
                        MemberSelection: MemberSelection.IncludeAll,
                        LogicalSchema: null,
                        Properties: [],
                        NestedObjects:
                        [
                            new ObjectRule(
                                Name: "detail",
                                MemberSelection: MemberSelection.IncludeAll,
                                LogicalSchema: null,
                                Properties: [],
                                NestedObjects: null,
                                Collections: null,
                                Extensions: null
                            ),
                        ],
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: null
                    ),
                ],
                Extensions: []
            );

            var classifier = new ProfileVisibilityClassifier(profile, scopes);
            var analyzer = new CreatabilityAnalyzer(
                scopes,
                classifier,
                "TestProfile",
                "Resource",
                "PUT",
                "Update"
            );

            // The collection item's semantic identity
            var itemIdentity = ImmutableArray.Create(
                new SemanticIdentityPart("itemId", System.Text.Json.Nodes.JsonValue.Create("Item1"), true)
            );

            // Ancestor context for the detail scope: the containing collection item
            var detailAncestors = ImmutableArray.Create(
                new AncestorCollectionInstance("$.items[*]", itemIdentity)
            );

            // The collection item with root as parent
            var collectionItem = new VisibleRequestCollectionItem(
                new CollectionRowAddress("$.items[*]", new ScopeInstanceAddress("$", []), itemIdentity),
                Creatable: false,
                "$.items[0]"
            );

            ImmutableArray<RequestScopeState> scopeStates =
            [
                // Root: existing
                new(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
                // Detail scope under the collection item: new (not in stored state)
                new(
                    new ScopeInstanceAddress("$.items[*].detail", detailAncestors),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
            ];

            ImmutableArray<VisibleRequestCollectionItem> items = [collectionItem];

            // Root and the collection item both exist in stored state
            var existenceLookup = new TestExistenceLookup(
                existingScopes: ScopeAddressSet(new ScopeInstanceAddress("$", [])),
                existingCollectionRows: new HashSet<CollectionRowAddress>(
                    CollectionRowAddressComparer.Instance
                )
                {
                    new CollectionRowAddress("$.items[*]", new ScopeInstanceAddress("$", []), itemIdentity),
                }
            );

            var effectiveRequired = new Dictionary<string, IReadOnlyList<string>>
            {
                ["$"] = ["field1"],
                ["$.items[*]"] = ["itemId"],
                ["$.items[*].detail"] = ["detailField"],
            };

            _result = analyzer.Analyze(
                scopeStates,
                items,
                existenceLookup,
                isCreate: false,
                effectiveRequired
            );
        }

        [Test]
        public void It_should_mark_detail_scope_as_creatable()
        {
            // The detail scope is new (not in stored state), its parent collection item
            // exists, so parent gate should be satisfied and it should be creatable.
            _result
                .EnrichedScopeStates.Should()
                .Contain(s => s.Address.JsonScope == "$.items[*].detail" && s.Creatable);
        }

        [Test]
        public void It_should_mark_collection_item_as_non_creatable()
        {
            // The collection item exists in stored state, so it's an update, not a new create
            _result.EnrichedCollectionItems[0].Creatable.Should().BeFalse();
        }

        [Test]
        public void It_should_have_no_failures()
        {
            _result.Failures.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  17. Collection item demoted by non-creatable descendant scope (bottom-up)
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Collection_Item_Demoted_By_NonCreatable_Descendant_Scope : CreatabilityAnalyzerTests
    {
        private CreatabilityResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // Hierarchy: $ -> $.items[*] -> $.items[*].detail
            // The collection item is new (not in stored state).
            // The detail scope is new and has a hidden required member -> non-creatable.
            // The collection item should be demoted to non-creatable by bottom-up propagation.
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
                    JsonScope: "$.items[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["itemId"],
                    CanonicalScopeRelativeMemberPaths: ["itemId"]
                ),
                new(
                    JsonScope: "$.items[*].detail",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$.items[*]",
                    CollectionAncestorsInOrder: ["$.items[*]"],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["detailField", "hiddenField"]
                ),
            ];

            // Profile hides hiddenField on the detail scope
            var profile = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeAll,
                Properties: [],
                Objects: [],
                Collections:
                [
                    new CollectionRule(
                        Name: "items",
                        MemberSelection: MemberSelection.IncludeAll,
                        LogicalSchema: null,
                        Properties: [],
                        NestedObjects:
                        [
                            new ObjectRule(
                                Name: "detail",
                                MemberSelection: MemberSelection.IncludeOnly,
                                LogicalSchema: null,
                                Properties: [new PropertyRule("detailField")],
                                NestedObjects: null,
                                Collections: null,
                                Extensions: null
                            ),
                        ],
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: null
                    ),
                ],
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

            // The collection item's semantic identity
            var itemIdentity = ImmutableArray.Create(
                new SemanticIdentityPart("itemId", System.Text.Json.Nodes.JsonValue.Create("Item1"), true)
            );

            // Ancestor context for the detail scope: the containing collection item
            var detailAncestors = ImmutableArray.Create(
                new AncestorCollectionInstance("$.items[*]", itemIdentity)
            );

            // The collection item with root as parent — new (not in stored state)
            var collectionItem = new VisibleRequestCollectionItem(
                new CollectionRowAddress("$.items[*]", new ScopeInstanceAddress("$", []), itemIdentity),
                Creatable: false,
                "$.items[0]"
            );

            ImmutableArray<RequestScopeState> scopeStates =
            [
                // Root: new create
                new(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
                // Detail scope under the collection item: new (not in stored state)
                new(
                    new ScopeInstanceAddress("$.items[*].detail", detailAncestors),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
            ];

            ImmutableArray<VisibleRequestCollectionItem> items = [collectionItem];

            // Nothing exists in stored state
            var existenceLookup = new TestExistenceLookup();

            var effectiveRequired = new Dictionary<string, IReadOnlyList<string>>
            {
                ["$"] = ["field1"],
                ["$.items[*]"] = ["itemId"],
                ["$.items[*].detail"] = ["detailField", "hiddenField"],
            };

            _result = analyzer.Analyze(
                scopeStates,
                items,
                existenceLookup,
                isCreate: true,
                effectiveRequired
            );
        }

        [Test]
        public void It_should_mark_detail_scope_as_non_creatable()
        {
            // The detail scope has a hidden required member -> non-creatable
            _result
                .EnrichedScopeStates.Should()
                .Contain(s => s.Address.JsonScope == "$.items[*].detail" && !s.Creatable);
        }

        [Test]
        public void It_should_demote_collection_item_to_non_creatable()
        {
            // The collection item should be demoted by bottom-up propagation because
            // its descendant detail scope is non-creatable
            _result.EnrichedCollectionItems[0].Creatable.Should().BeFalse();
        }

        [Test]
        public void It_should_emit_failure_for_detail_scope()
        {
            _result
                .Failures.OfType<VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure>()
                .Should()
                .Contain(f => f.JsonScope == "$.items[*].detail");
        }

        [Test]
        public void It_should_emit_failure_for_demoted_collection_item()
        {
            // Bottom-up demotion should emit a failure for the collection item
            // with a RequiredVisibleDescendant dependency pointing to the detail scope
            _result
                .Failures.OfType<VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure>()
                .Should()
                .Contain(f => f.JsonScope == "$.items[*]");
        }
    }

    // -----------------------------------------------------------------------
    //  18. Collection item demoted by non-creatable nested collection item (bottom-up)
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Collection_Item_Demoted_By_NonCreatable_Nested_Collection_Item
        : CreatabilityAnalyzerTests
    {
        private CreatabilityResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // Hierarchy: $ -> $.outer[*] -> $.outer[*].inner[*]
            // The outer collection item is new (not in stored state) and all its own
            // members are visible -> creatable on its own merits.
            // The inner collection item is new and has a hidden required member -> non-creatable.
            // The outer item should be demoted to non-creatable by bottom-up propagation
            // through the nested collection chain.
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
                    JsonScope: "$.outer[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["outerId"],
                    CanonicalScopeRelativeMemberPaths: ["outerId", "outerField"]
                ),
                new(
                    JsonScope: "$.outer[*].inner[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$.outer[*]",
                    CollectionAncestorsInOrder: ["$.outer[*]"],
                    SemanticIdentityRelativePathsInOrder: ["innerId"],
                    CanonicalScopeRelativeMemberPaths: ["innerId", "innerField", "hiddenInnerField"]
                ),
            ];

            // Profile includes all members of outer, but hides hiddenInnerField on inner
            var profile = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeAll,
                Properties: [],
                Objects: [],
                Collections:
                [
                    new CollectionRule(
                        Name: "outer",
                        MemberSelection: MemberSelection.IncludeAll,
                        LogicalSchema: null,
                        Properties: [],
                        NestedObjects: null,
                        NestedCollections:
                        [
                            new CollectionRule(
                                Name: "inner",
                                MemberSelection: MemberSelection.IncludeOnly,
                                LogicalSchema: null,
                                Properties: [new PropertyRule("innerId"), new PropertyRule("innerField")],
                                NestedObjects: null,
                                NestedCollections: null,
                                Extensions: null,
                                ItemFilter: null
                            ),
                        ],
                        Extensions: null,
                        ItemFilter: null
                    ),
                ],
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

            // The outer collection item's semantic identity
            var outerIdentity = ImmutableArray.Create(
                new SemanticIdentityPart("outerId", System.Text.Json.Nodes.JsonValue.Create("Outer1"), true)
            );

            // The inner collection item's semantic identity
            var innerIdentity = ImmutableArray.Create(
                new SemanticIdentityPart("innerId", System.Text.Json.Nodes.JsonValue.Create("Inner1"), true)
            );

            // Ancestor context for the inner item: includes the outer collection item
            var innerAncestors = ImmutableArray.Create(
                new AncestorCollectionInstance("$.outer[*]", outerIdentity)
            );

            // The outer collection item with root as parent — new (not in stored state)
            var outerItem = new VisibleRequestCollectionItem(
                new CollectionRowAddress("$.outer[*]", new ScopeInstanceAddress("$", []), outerIdentity),
                Creatable: false,
                "$.outer[0]"
            );

            // The inner collection item with outer as parent — new (not in stored state)
            var innerItem = new VisibleRequestCollectionItem(
                new CollectionRowAddress(
                    "$.outer[*].inner[*]",
                    new ScopeInstanceAddress("$.outer[*]", innerAncestors),
                    innerIdentity
                ),
                Creatable: false,
                "$.outer[0].inner[0]"
            );

            ImmutableArray<RequestScopeState> scopeStates =
            [
                // Root: new create
                new(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
            ];

            ImmutableArray<VisibleRequestCollectionItem> items = [outerItem, innerItem];

            // Nothing exists in stored state — both items are new creates
            var existenceLookup = new TestExistenceLookup();

            var effectiveRequired = new Dictionary<string, IReadOnlyList<string>>
            {
                ["$"] = ["field1"],
                ["$.outer[*]"] = ["outerId"],
                ["$.outer[*].inner[*]"] = ["innerId", "hiddenInnerField"],
            };

            _result = analyzer.Analyze(
                scopeStates,
                items,
                existenceLookup,
                isCreate: true,
                effectiveRequired
            );
        }

        [Test]
        public void It_should_mark_inner_collection_item_as_non_creatable()
        {
            // The inner item has a hidden required member -> non-creatable
            _result
                .EnrichedCollectionItems.Should()
                .Contain(item => item.Address.JsonScope == "$.outer[*].inner[*]" && !item.Creatable);
        }

        [Test]
        public void It_should_demote_outer_collection_item_to_non_creatable()
        {
            // The outer item should be demoted by bottom-up propagation because
            // its nested inner collection item is non-creatable
            _result
                .EnrichedCollectionItems.Should()
                .Contain(item => item.Address.JsonScope == "$.outer[*]" && !item.Creatable);
        }

        [Test]
        public void It_should_emit_failure_for_inner_collection_item()
        {
            _result
                .Failures.OfType<VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure>()
                .Should()
                .Contain(f => f.JsonScope == "$.outer[*].inner[*]");
        }

        [Test]
        public void It_should_emit_failure_for_demoted_outer_collection_item()
        {
            // Bottom-up demotion should emit a failure for the outer item
            // with a RequiredVisibleDescendant dependency pointing to the inner collection
            _result
                .Failures.OfType<VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure>()
                .Should()
                .Contain(f =>
                    f.JsonScope == "$.outer[*]"
                    && f.Dependencies.Any(d =>
                        d.DependencyKind == ProfileCreatabilityDependencyKind.RequiredVisibleDescendant
                        && d.JsonScope == "$.outer[*].inner[*]"
                    )
                );
        }
    }

    // -----------------------------------------------------------------------
    //  Slice 5 Findings carryover: missing scope metadata must fail closed.
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_A_Creating_Scope_Whose_Required_Members_Metadata_Is_Missing : CreatabilityAnalyzerTests
    {
        [Test]
        public void It_throws_invalid_operation_naming_the_scope()
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

            ImmutableArray<RequestScopeState> scopeStates =
            [
                new(MakeAddress("$"), ProfileVisibilityKind.VisiblePresent, Creatable: false),
            ];
            ImmutableArray<VisibleRequestCollectionItem> items = [];

            // Intentionally missing the "$" entry the analyzer needs for a creating root scope.
            var emptyMetadata = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

            Action act = () =>
                analyzer.Analyze(
                    scopeStates,
                    items,
                    new TestExistenceLookup(),
                    isCreate: true,
                    emptyMetadata
                );

            act.Should().Throw<InvalidOperationException>().WithMessage("*scope '$'*");
        }
    }

    [TestFixture]
    public class Given_A_Creating_Collection_Item_Whose_Required_Members_Metadata_Is_Missing
        : CreatabilityAnalyzerTests
    {
        [Test]
        public void It_throws_invalid_operation_naming_the_collection_item_scope()
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

            ImmutableArray<RequestScopeState> scopeStates =
            [
                new(MakeAddress("$"), ProfileVisibilityKind.VisiblePresent, Creatable: false),
            ];

            // A creating collection item drives the AnalyzeCollectionItem path.
            ImmutableArray<VisibleRequestCollectionItem> items =
            [
                new(
                    MakeCollectionRowAddress("$.classPeriods[*]", "classPeriodName", "Period1"),
                    Creatable: false,
                    "$.classPeriods[0]"
                ),
            ];

            // Provide root metadata so the root scope passes; intentionally omit
            // the collection-item scope's JsonScope so the per-item creatability
            // evaluation has no entry.
            var partialMetadata = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["$"] = ["field1"],
            };

            Action act = () =>
                analyzer.Analyze(
                    scopeStates,
                    items,
                    new TestExistenceLookup(),
                    isCreate: true,
                    partialMetadata
                );

            act.Should().Throw<InvalidOperationException>().WithMessage("*$.classPeriods[*]*");
        }
    }

    [TestFixture]
    public class Given_A_Parent_Gated_Creating_Scope_Whose_Required_Members_Metadata_Is_Missing
        : CreatabilityAnalyzerTests
    {
        [Test]
        public void It_throws_invalid_operation_naming_the_child_scope()
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

            // Root scope and a non-root NonCollection child scope are both
            // VisiblePresent and absent from the stored side: the child scope
            // is the parent-gated creating scope whose metadata is missing.
            ImmutableArray<RequestScopeState> scopeStates =
            [
                new(MakeAddress("$"), ProfileVisibilityKind.VisiblePresent, Creatable: false),
                new(
                    MakeAddress("$.calendarReference"),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
            ];
            ImmutableArray<VisibleRequestCollectionItem> items = [];

            // Provide root and classPeriods metadata so root creatability
            // succeeds (parent-creatable gate passes); intentionally omit
            // "$.calendarReference" so the parent-gated child evaluation has
            // no entry in effectiveSchemaRequiredMembersByScope.
            var partialMetadata = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["$"] = ["field1"],
                ["$.classPeriods[*]"] = ["classPeriodName"],
            };

            Action act = () =>
                analyzer.Analyze(
                    scopeStates,
                    items,
                    new TestExistenceLookup(),
                    isCreate: true,
                    partialMetadata
                );

            act.Should().Throw<InvalidOperationException>().WithMessage("*$.calendarReference*");
        }
    }
}
