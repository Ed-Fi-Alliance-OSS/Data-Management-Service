// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

public abstract class ProfileVisibilityClassifierTests
{
    // Shared scope catalogs from ProfileTestFixtures
    protected static IReadOnlyList<CompiledScopeDescriptor> SharedFixtureScopes =>
        ProfileTestFixtures.SharedFixtureScopes;

    protected static IReadOnlyList<CompiledScopeDescriptor> AddressesFixtureScopes =>
        ProfileTestFixtures.AddressesFixtureScopes;

    // -----------------------------------------------------------------------
    //  Profile builder helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a ContentTypeDefinition covering root + calendarReference object +
    /// classPeriods collection using IncludeOnly throughout.
    /// </summary>
    protected static ContentTypeDefinition BuildIncludeOnlyProfile() =>
        new(
            MemberSelection: MemberSelection.IncludeOnly,
            Properties:
            [
                new PropertyRule("studentReference"),
                new PropertyRule("schoolReference"),
                new PropertyRule("entryDate"),
            ],
            Objects:
            [
                new ObjectRule(
                    Name: "calendarReference",
                    MemberSelection: MemberSelection.IncludeOnly,
                    LogicalSchema: null,
                    Properties:
                    [
                        new PropertyRule("calendarCode"),
                        new PropertyRule("calendarTypeDescriptor"),
                    ],
                    NestedObjects: null,
                    Collections: null,
                    Extensions: null
                ),
            ],
            Collections:
            [
                new CollectionRule(
                    Name: "classPeriods",
                    MemberSelection: MemberSelection.IncludeOnly,
                    LogicalSchema: null,
                    Properties:
                    [
                        new PropertyRule("classPeriodName"),
                        new PropertyRule("officialAttendancePeriod"),
                    ],
                    NestedObjects: null,
                    NestedCollections: null,
                    Extensions: null,
                    ItemFilter: null
                ),
            ],
            Extensions: []
        );

    /// <summary>
    /// Builds a ContentTypeDefinition with IncludeOnly at root that lists only
    /// root-level properties but does NOT include calendarReference or classPeriods.
    /// </summary>
    protected static ContentTypeDefinition BuildIncludeOnlyProfileHidingChildScopes() =>
        new(
            MemberSelection: MemberSelection.IncludeOnly,
            Properties:
            [
                new PropertyRule("studentReference"),
                new PropertyRule("schoolReference"),
                new PropertyRule("entryDate"),
            ],
            Objects: [],
            Collections: [],
            Extensions: []
        );

    /// <summary>
    /// Builds an ExcludeOnly root profile that explicitly excludes calendarReference.
    /// classPeriods is not excluded so it remains visible.
    /// </summary>
    protected static ContentTypeDefinition BuildExcludeOnlyProfileExcludingCalendar() =>
        new(
            MemberSelection: MemberSelection.ExcludeOnly,
            Properties: [new PropertyRule("entryTypeDescriptor")],
            Objects:
            [
                new ObjectRule("calendarReference", MemberSelection.IncludeOnly, null, [], null, null, null),
            ],
            Collections: [],
            Extensions: []
        );

    protected static ContentTypeDefinition BuildIncludeAllProfile() =>
        ProfileTestFixtures.BuildIncludeAllProfile();

    /// <summary>
    /// Builds an IncludeOnly profile for the addresses collection
    /// with an IncludeOnly item filter on addressTypeDescriptor.
    /// </summary>
    protected static ContentTypeDefinition BuildAddressesProfileWithIncludeOnlyFilter() =>
        new(
            MemberSelection: MemberSelection.IncludeOnly,
            Properties: [new PropertyRule("field1")],
            Objects: [],
            Collections:
            [
                new CollectionRule(
                    Name: "addresses",
                    MemberSelection: MemberSelection.IncludeOnly,
                    LogicalSchema: null,
                    Properties:
                    [
                        new PropertyRule("addressTypeDescriptor"),
                        new PropertyRule("city"),
                        new PropertyRule("stateAbbreviationDescriptor"),
                    ],
                    NestedObjects: null,
                    NestedCollections: null,
                    Extensions: null,
                    ItemFilter: new CollectionItemFilter(
                        PropertyName: "addressTypeDescriptor",
                        FilterMode: FilterMode.IncludeOnly,
                        Values: ["uri://ed-fi.org/AddressType#Physical"]
                    )
                ),
            ],
            Extensions: []
        );

    /// <summary>
    /// Builds an IncludeOnly profile for the addresses collection
    /// with an ExcludeOnly item filter on addressTypeDescriptor.
    /// </summary>
    protected static ContentTypeDefinition BuildAddressesProfileWithExcludeOnlyFilter() =>
        new(
            MemberSelection: MemberSelection.IncludeOnly,
            Properties: [new PropertyRule("field1")],
            Objects: [],
            Collections:
            [
                new CollectionRule(
                    Name: "addresses",
                    MemberSelection: MemberSelection.IncludeOnly,
                    LogicalSchema: null,
                    Properties:
                    [
                        new PropertyRule("addressTypeDescriptor"),
                        new PropertyRule("city"),
                        new PropertyRule("stateAbbreviationDescriptor"),
                    ],
                    NestedObjects: null,
                    NestedCollections: null,
                    Extensions: null,
                    ItemFilter: new CollectionItemFilter(
                        PropertyName: "addressTypeDescriptor",
                        FilterMode: FilterMode.ExcludeOnly,
                        Values: ["uri://ed-fi.org/AddressType#Physical"]
                    )
                ),
            ],
            Extensions: []
        );

    // -----------------------------------------------------------------------
    //  Scope-level visibility: IncludeOnly profile + visible scope with data
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_IncludeOnly_Profile_And_Visible_Scope_With_Data : ProfileVisibilityClassifierTests
    {
        private ProfileVisibilityKind _result;

        [SetUp]
        public void Setup()
        {
            var classifier = new ProfileVisibilityClassifier(BuildIncludeOnlyProfile(), SharedFixtureScopes);
            JsonNode scopeData = JsonNode.Parse(
                """{"classPeriodName":"P1","officialAttendancePeriod":true}"""
            )!;
            _result = classifier.ClassifyScope("$.classPeriods[*]", scopeData);
        }

        [Test]
        public void It_should_return_VisiblePresent()
        {
            _result.Should().Be(ProfileVisibilityKind.VisiblePresent);
        }
    }

    // -----------------------------------------------------------------------
    //  Scope-level visibility: IncludeOnly profile + visible scope without data
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_IncludeOnly_Profile_And_Visible_Scope_Without_Data : ProfileVisibilityClassifierTests
    {
        private ProfileVisibilityKind _result;

        [SetUp]
        public void Setup()
        {
            var classifier = new ProfileVisibilityClassifier(BuildIncludeOnlyProfile(), SharedFixtureScopes);
            _result = classifier.ClassifyScope("$.classPeriods[*]", null);
        }

        [Test]
        public void It_should_return_VisibleAbsent()
        {
            _result.Should().Be(ProfileVisibilityKind.VisibleAbsent);
        }
    }

    // -----------------------------------------------------------------------
    //  Scope-level visibility: IncludeOnly profile + hidden scope
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_IncludeOnly_Profile_And_Hidden_Scope : ProfileVisibilityClassifierTests
    {
        private ProfileVisibilityKind _result;

        [SetUp]
        public void Setup()
        {
            // Profile with IncludeOnly that does NOT list calendarReference
            var classifier = new ProfileVisibilityClassifier(
                BuildIncludeOnlyProfileHidingChildScopes(),
                SharedFixtureScopes
            );
            JsonNode scopeData = JsonNode.Parse(
                """{"calendarCode":"2024-01","calendarTypeDescriptor":"uri://ed-fi.org/CalendarType#IEP"}"""
            )!;
            _result = classifier.ClassifyScope("$.calendarReference", scopeData);
        }

        [Test]
        public void It_should_return_Hidden()
        {
            _result.Should().Be(ProfileVisibilityKind.Hidden);
        }
    }

    // -----------------------------------------------------------------------
    //  Scope-level visibility: IncludeOnly profile hiding an unlisted child scope
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_IncludeOnly_Profile_Hiding_Unlisted_Child_Scope : ProfileVisibilityClassifierTests
    {
        private ProfileVisibilityKind _result;

        [SetUp]
        public void Setup()
        {
            // IncludeOnly root that does not list classPeriods → Navigator returns null → Hidden
            var classifier = new ProfileVisibilityClassifier(
                BuildIncludeOnlyProfileHidingChildScopes(),
                SharedFixtureScopes
            );
            _result = classifier.ClassifyScope("$.classPeriods[*]", null);
        }

        [Test]
        public void It_should_return_Hidden()
        {
            _result.Should().Be(ProfileVisibilityKind.Hidden);
        }
    }

    // -----------------------------------------------------------------------
    //  Scope-level visibility: ExcludeOnly profile + non-excluded scope with data
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_ExcludeOnly_Profile_And_Non_Excluded_Scope_With_Data : ProfileVisibilityClassifierTests
    {
        private ProfileVisibilityKind _result;

        [SetUp]
        public void Setup()
        {
            // ExcludeOnly root. calendarReference is not in exclusion list → visible.
            ContentTypeDefinition writeContent = new(
                MemberSelection: MemberSelection.ExcludeOnly,
                Properties: [new PropertyRule("entryTypeDescriptor")],
                Objects: [],
                Collections: [],
                Extensions: []
            );
            var classifier = new ProfileVisibilityClassifier(writeContent, SharedFixtureScopes);
            JsonNode scopeData = JsonNode.Parse(
                """{"calendarCode":"2024-01","calendarTypeDescriptor":"uri://ed-fi.org/CalendarType#IEP"}"""
            )!;
            _result = classifier.ClassifyScope("$.calendarReference", scopeData);
        }

        [Test]
        public void It_should_return_VisiblePresent()
        {
            _result.Should().Be(ProfileVisibilityKind.VisiblePresent);
        }
    }

    // -----------------------------------------------------------------------
    //  Scope-level visibility: IncludeAll profile → all scopes visible
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_IncludeAll_Profile : ProfileVisibilityClassifierTests
    {
        private ProfileVisibilityKind _rootResult;
        private ProfileVisibilityKind _childResult;
        private ProfileVisibilityKind _collectionResult;

        [SetUp]
        public void Setup()
        {
            var classifier = new ProfileVisibilityClassifier(BuildIncludeAllProfile(), SharedFixtureScopes);
            JsonNode rootData = JsonNode.Parse("""{"entryDate":"2024-08-01"}""")!;
            JsonNode childData = JsonNode.Parse("""{"calendarCode":"2024-01"}""")!;
            JsonNode collectionData = JsonNode.Parse("""{"classPeriodName":"P1"}""")!;
            _rootResult = classifier.ClassifyScope("$", rootData);
            _childResult = classifier.ClassifyScope("$.calendarReference", childData);
            _collectionResult = classifier.ClassifyScope("$.classPeriods[*]", collectionData);
        }

        [Test]
        public void It_should_return_VisiblePresent_for_root()
        {
            _rootResult.Should().Be(ProfileVisibilityKind.VisiblePresent);
        }

        [Test]
        public void It_should_return_VisiblePresent_for_child()
        {
            _childResult.Should().Be(ProfileVisibilityKind.VisiblePresent);
        }

        [Test]
        public void It_should_return_VisiblePresent_for_collection()
        {
            _collectionResult.Should().Be(ProfileVisibilityKind.VisiblePresent);
        }
    }

    // -----------------------------------------------------------------------
    //  Scope-level visibility: Hidden parent scope → child also Hidden
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Hidden_Parent_Scope : ProfileVisibilityClassifierTests
    {
        private ProfileVisibilityKind _result;

        [SetUp]
        public void Setup()
        {
            // IncludeOnly root with no collections listed → classPeriods is hidden
            var classifier = new ProfileVisibilityClassifier(
                BuildIncludeOnlyProfileHidingChildScopes(),
                SharedFixtureScopes
            );
            // The classPeriods collection is not listed in the IncludeOnly root
            _result = classifier.ClassifyScope("$.classPeriods[*]", null);
        }

        [Test]
        public void It_should_return_Hidden()
        {
            _result.Should().Be(ProfileVisibilityKind.Hidden);
        }
    }

    // -----------------------------------------------------------------------
    //  Scope-level visibility: Extension scope with IncludeOnly parent not listing it
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Extension_Scope_With_IncludeOnly_Parent_Not_Listed : ProfileVisibilityClassifierTests
    {
        private ProfileVisibilityKind _result;

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
                    JsonScope: "$._ext.sample",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["sampleField"]
                ),
            ];

            // IncludeOnly at root, no extensions listed → extension scope is hidden
            ContentTypeDefinition writeContent = new(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties: [new PropertyRule("field1")],
                Objects: [],
                Collections: [],
                Extensions: []
            );

            var classifier = new ProfileVisibilityClassifier(writeContent, scopes);
            _result = classifier.ClassifyScope("$._ext.sample", null);
        }

        [Test]
        public void It_should_return_Hidden()
        {
            _result.Should().Be(ProfileVisibilityKind.Hidden);
        }
    }

    // -----------------------------------------------------------------------
    //  Scope-level visibility: Extension scope with IncludeAll parent → visible
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Extension_Scope_With_IncludeAll_Parent : ProfileVisibilityClassifierTests
    {
        private ProfileVisibilityKind _result;

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
                    JsonScope: "$._ext.sample",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["sampleField"]
                ),
            ];

            // IncludeAll at root → extension scope is visible
            var classifier = new ProfileVisibilityClassifier(BuildIncludeAllProfile(), scopes);
            JsonNode scopeData = JsonNode.Parse("""{"sampleField":"value"}""")!;
            _result = classifier.ClassifyScope("$._ext.sample", scopeData);
        }

        [Test]
        public void It_should_return_VisiblePresent()
        {
            _result.Should().Be(ProfileVisibilityKind.VisiblePresent);
        }
    }

    // -----------------------------------------------------------------------
    //  Collection item value filtering: No filter → passes
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_No_Item_Filter : ProfileVisibilityClassifierTests
    {
        private bool _result;

        [SetUp]
        public void Setup()
        {
            var classifier = new ProfileVisibilityClassifier(BuildIncludeOnlyProfile(), SharedFixtureScopes);
            JsonNode item = JsonNode.Parse("""{"classPeriodName":"P1","officialAttendancePeriod":true}""")!;
            _result = classifier.PassesCollectionItemFilter("$.classPeriods[*]", item);
        }

        [Test]
        public void It_should_return_true()
        {
            _result.Should().BeTrue();
        }
    }

    // -----------------------------------------------------------------------
    //  Collection item value filtering: IncludeOnly filter + matching item → passes
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Include_Filter_And_Matching_Item : ProfileVisibilityClassifierTests
    {
        private bool _result;

        [SetUp]
        public void Setup()
        {
            var classifier = new ProfileVisibilityClassifier(
                BuildAddressesProfileWithIncludeOnlyFilter(),
                AddressesFixtureScopes
            );
            JsonNode item = JsonNode.Parse(
                """{"addressTypeDescriptor":"uri://ed-fi.org/AddressType#Physical","city":"Austin"}"""
            )!;
            _result = classifier.PassesCollectionItemFilter("$.addresses[*]", item);
        }

        [Test]
        public void It_should_return_true()
        {
            _result.Should().BeTrue();
        }
    }

    // -----------------------------------------------------------------------
    //  Collection item value filtering: IncludeOnly filter + non-matching item → fails
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Include_Filter_And_Non_Matching_Item : ProfileVisibilityClassifierTests
    {
        private bool _result;

        [SetUp]
        public void Setup()
        {
            var classifier = new ProfileVisibilityClassifier(
                BuildAddressesProfileWithIncludeOnlyFilter(),
                AddressesFixtureScopes
            );
            JsonNode item = JsonNode.Parse(
                """{"addressTypeDescriptor":"uri://ed-fi.org/AddressType#Mailing","city":"Austin"}"""
            )!;
            _result = classifier.PassesCollectionItemFilter("$.addresses[*]", item);
        }

        [Test]
        public void It_should_return_false()
        {
            _result.Should().BeFalse();
        }
    }

    // -----------------------------------------------------------------------
    //  Collection item value filtering: ExcludeOnly filter + matching item → fails
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Exclude_Filter_And_Matching_Item : ProfileVisibilityClassifierTests
    {
        private bool _result;

        [SetUp]
        public void Setup()
        {
            var classifier = new ProfileVisibilityClassifier(
                BuildAddressesProfileWithExcludeOnlyFilter(),
                AddressesFixtureScopes
            );
            JsonNode item = JsonNode.Parse(
                """{"addressTypeDescriptor":"uri://ed-fi.org/AddressType#Physical","city":"Austin"}"""
            )!;
            _result = classifier.PassesCollectionItemFilter("$.addresses[*]", item);
        }

        [Test]
        public void It_should_return_false()
        {
            _result.Should().BeFalse();
        }
    }

    // -----------------------------------------------------------------------
    //  Collection item value filtering: ExcludeOnly filter + non-matching item → passes
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Exclude_Filter_And_Non_Matching_Item : ProfileVisibilityClassifierTests
    {
        private bool _result;

        [SetUp]
        public void Setup()
        {
            var classifier = new ProfileVisibilityClassifier(
                BuildAddressesProfileWithExcludeOnlyFilter(),
                AddressesFixtureScopes
            );
            JsonNode item = JsonNode.Parse(
                """{"addressTypeDescriptor":"uri://ed-fi.org/AddressType#Mailing","city":"Austin"}"""
            )!;
            _result = classifier.PassesCollectionItemFilter("$.addresses[*]", item);
        }

        [Test]
        public void It_should_return_true()
        {
            _result.Should().BeTrue();
        }
    }

    // -----------------------------------------------------------------------
    //  Member filtering: IncludeOnly scope → mode=IncludeOnly, names=[included props]
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_IncludeOnly_Scope_Member_Filter : ProfileVisibilityClassifierTests
    {
        private ScopeMemberFilter _result;

        [SetUp]
        public void Setup()
        {
            var classifier = new ProfileVisibilityClassifier(BuildIncludeOnlyProfile(), SharedFixtureScopes);
            _result = classifier.GetMemberFilter("$.classPeriods[*]");
        }

        [Test]
        public void It_should_have_IncludeOnly_mode()
        {
            _result.Mode.Should().Be(MemberSelection.IncludeOnly);
        }

        [Test]
        public void It_should_contain_included_property_names()
        {
            _result.ExplicitNames.Should().Contain("classPeriodName");
            _result.ExplicitNames.Should().Contain("officialAttendancePeriod");
        }
    }

    // -----------------------------------------------------------------------
    //  Member filtering: IncludeAll scope → mode=IncludeAll, names=empty
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_IncludeAll_Scope_Member_Filter : ProfileVisibilityClassifierTests
    {
        private ScopeMemberFilter _result;

        [SetUp]
        public void Setup()
        {
            var classifier = new ProfileVisibilityClassifier(BuildIncludeAllProfile(), SharedFixtureScopes);
            _result = classifier.GetMemberFilter("$.classPeriods[*]");
        }

        [Test]
        public void It_should_have_IncludeAll_mode()
        {
            _result.Mode.Should().Be(MemberSelection.IncludeAll);
        }

        [Test]
        public void It_should_have_empty_explicit_names()
        {
            _result.ExplicitNames.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  Member filtering: ExcludeOnly scope → mode=ExcludeOnly, names=[excluded props]
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_ExcludeOnly_Scope_Member_Filter : ProfileVisibilityClassifierTests
    {
        private ScopeMemberFilter _result;

        [SetUp]
        public void Setup()
        {
            // ExcludeOnly root with entryTypeDescriptor explicitly excluded
            var classifier = new ProfileVisibilityClassifier(
                BuildExcludeOnlyProfileExcludingCalendar(),
                SharedFixtureScopes
            );
            _result = classifier.GetMemberFilter("$");
        }

        [Test]
        public void It_should_have_ExcludeOnly_mode()
        {
            _result.Mode.Should().Be(MemberSelection.ExcludeOnly);
        }

        [Test]
        public void It_should_contain_excluded_property_names()
        {
            _result.ExplicitNames.Should().Contain("entryTypeDescriptor");
        }
    }
}
