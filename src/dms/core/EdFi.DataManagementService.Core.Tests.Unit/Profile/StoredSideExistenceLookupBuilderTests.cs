// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

public abstract class StoredSideExistenceLookupBuilderTests
{
    // -----------------------------------------------------------------------
    //  Shared scope catalogs
    // -----------------------------------------------------------------------

    protected static IReadOnlyList<CompiledScopeDescriptor> SharedFixtureScopes =>
        ProfileTestFixtures.SharedFixtureScopes;

    protected static IReadOnlyList<CompiledScopeDescriptor> AddressesFixtureScopes =>
        ProfileTestFixtures.AddressesFixtureScopes;

    // -----------------------------------------------------------------------
    //  Test helper: wraps Build with classifier/engine construction
    // -----------------------------------------------------------------------

    protected static StoredSideExistenceLookupResult BuildLookup(
        JsonNode? storedDocument,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
        ContentTypeDefinition writeContentType
    )
    {
        var classifier = new ProfileVisibilityClassifier(writeContentType, scopeCatalog);
        var addressEngine = new AddressDerivationEngine(scopeCatalog);
        return StoredSideExistenceLookupBuilder.Build(
            storedDocument,
            scopeCatalog,
            classifier,
            addressEngine
        );
    }

    // -----------------------------------------------------------------------
    //  Profile builder helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// IncludeOnly profile exposing calendarReference (with both members)
    /// and classPeriods (classPeriodName only). Hides entryTypeDescriptor
    /// and classPeriods.officialAttendancePeriod.
    /// </summary>
    protected static ContentTypeDefinition BuildIncludeOnlyWithCalendarReference() =>
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
                    Properties: [new PropertyRule("classPeriodName")],
                    NestedObjects: null,
                    NestedCollections: null,
                    Extensions: null,
                    ItemFilter: null
                ),
            ],
            Extensions: []
        );

    /// <summary>
    /// IncludeOnly profile that does NOT include calendarReference (hides it).
    /// Only exposes root scalars and classPeriods.
    /// </summary>
    protected static ContentTypeDefinition BuildIncludeOnlyWithoutCalendarReference() =>
        new(
            MemberSelection: MemberSelection.IncludeOnly,
            Properties:
            [
                new PropertyRule("studentReference"),
                new PropertyRule("schoolReference"),
                new PropertyRule("entryDate"),
            ],
            Objects: [],
            Collections:
            [
                new CollectionRule(
                    Name: "classPeriods",
                    MemberSelection: MemberSelection.IncludeOnly,
                    LogicalSchema: null,
                    Properties: [new PropertyRule("classPeriodName")],
                    NestedObjects: null,
                    NestedCollections: null,
                    Extensions: null,
                    ItemFilter: null
                ),
            ],
            Extensions: []
        );

    /// <summary>
    /// IncludeAll profile for addresses with an IncludeOnly item filter
    /// on addressTypeDescriptor for "Physical" only.
    /// </summary>
    protected static ContentTypeDefinition BuildAddressesWithPhysicalFilter() =>
        new(
            MemberSelection: MemberSelection.IncludeAll,
            Properties: [],
            Objects: [],
            Collections:
            [
                new CollectionRule(
                    Name: "addresses",
                    MemberSelection: MemberSelection.IncludeAll,
                    LogicalSchema: null,
                    Properties: null,
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
    /// IncludeOnly profile for addresses that includes only addressTypeDescriptor
    /// and city (hides stateAbbreviationDescriptor), with Physical item filter.
    /// Used for HiddenMemberPaths assertions on collection rows.
    /// </summary>
    protected static ContentTypeDefinition BuildAddressesIncludeOnlyWithPhysicalFilter() =>
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
                    Properties: [new PropertyRule("addressTypeDescriptor"), new PropertyRule("city")],
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

    // -----------------------------------------------------------------------
    //  1. Visible scope exists at address
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Stored_Document_With_Visible_Scope : StoredSideExistenceLookupBuilderTests
    {
        private StoredSideExistenceLookupResult _result = null!;

        [SetUp]
        public void Setup()
        {
            JsonNode storedDoc = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "schoolReference": { "schoolId": 100 },
                    "entryDate": "2024-08-01",
                    "calendarReference": {
                        "calendarCode": "2024-01",
                        "calendarTypeDescriptor": "uri://ed-fi.org/CalendarType#IEP"
                    },
                    "classPeriods": [
                        { "classPeriodName": "Period1", "officialAttendancePeriod": true }
                    ]
                }
                """
            )!;

            _result = BuildLookup(storedDoc, SharedFixtureScopes, BuildIncludeOnlyWithCalendarReference());
        }

        [Test]
        public void It_should_report_visible_scope_exists_at_calendarReference()
        {
            var address = new ScopeInstanceAddress("$.calendarReference", []);
            _result.Lookup.VisibleScopeExistsAt(address).Should().BeTrue();
        }

        [Test]
        public void It_should_report_visible_scope_exists_at_root()
        {
            var address = new ScopeInstanceAddress("$", []);
            _result.Lookup.VisibleScopeExistsAt(address).Should().BeTrue();
        }

        [Test]
        public void It_should_include_calendarReference_in_classified_scopes()
        {
            _result
                .ClassifiedStoredScopes.Should()
                .Contain(s =>
                    s.Address.JsonScope == "$.calendarReference"
                    && s.Visibility == ProfileVisibilityKind.VisiblePresent
                );
        }
    }

    // -----------------------------------------------------------------------
    //  2. Hidden scope not in lookup
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Stored_Document_With_Hidden_Scope : StoredSideExistenceLookupBuilderTests
    {
        private StoredSideExistenceLookupResult _result = null!;

        [SetUp]
        public void Setup()
        {
            JsonNode storedDoc = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "schoolReference": { "schoolId": 100 },
                    "entryDate": "2024-08-01",
                    "calendarReference": {
                        "calendarCode": "2024-01",
                        "calendarTypeDescriptor": "uri://ed-fi.org/CalendarType#IEP"
                    }
                }
                """
            )!;

            // Profile does NOT include calendarReference
            _result = BuildLookup(storedDoc, SharedFixtureScopes, BuildIncludeOnlyWithoutCalendarReference());
        }

        [Test]
        public void It_should_not_report_visible_scope_at_calendarReference()
        {
            var address = new ScopeInstanceAddress("$.calendarReference", []);
            _result.Lookup.VisibleScopeExistsAt(address).Should().BeFalse();
        }

        [Test]
        public void It_should_classify_calendarReference_as_hidden()
        {
            _result
                .ClassifiedStoredScopes.Should()
                .Contain(s =>
                    s.Address.JsonScope == "$.calendarReference"
                    && s.Visibility == ProfileVisibilityKind.Hidden
                );
        }
    }

    // -----------------------------------------------------------------------
    //  3. Collection with passing item
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Stored_Collection_With_Passing_Item : StoredSideExistenceLookupBuilderTests
    {
        private StoredSideExistenceLookupResult _result = null!;

        [SetUp]
        public void Setup()
        {
            JsonNode storedDoc = JsonNode.Parse(
                """
                {
                    "field1": "value1",
                    "addresses": [
                        {
                            "addressTypeDescriptor": "uri://ed-fi.org/AddressType#Physical",
                            "city": "Austin",
                            "stateAbbreviationDescriptor": "uri://ed-fi.org/State#TX"
                        }
                    ]
                }
                """
            )!;

            _result = BuildLookup(storedDoc, AddressesFixtureScopes, BuildAddressesWithPhysicalFilter());
        }

        [Test]
        public void It_should_have_one_visible_collection_row()
        {
            _result.ClassifiedStoredCollectionRows.Should().HaveCount(1);
        }

        [Test]
        public void It_should_report_physical_row_exists_in_lookup()
        {
            var row = _result.ClassifiedStoredCollectionRows[0];
            _result.Lookup.VisibleCollectionRowExistsAt(row.Address).Should().BeTrue();
        }

        [Test]
        public void It_should_have_correct_json_scope_on_row()
        {
            _result.ClassifiedStoredCollectionRows[0].Address.JsonScope.Should().Be("$.addresses[*]");
        }
    }

    // -----------------------------------------------------------------------
    //  4. Collection with failing item
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Stored_Collection_With_Failing_Item : StoredSideExistenceLookupBuilderTests
    {
        private StoredSideExistenceLookupResult _result = null!;

        [SetUp]
        public void Setup()
        {
            JsonNode storedDoc = JsonNode.Parse(
                """
                {
                    "field1": "value1",
                    "addresses": [
                        {
                            "addressTypeDescriptor": "uri://ed-fi.org/AddressType#Mailing",
                            "city": "Dallas",
                            "stateAbbreviationDescriptor": "uri://ed-fi.org/State#TX"
                        }
                    ]
                }
                """
            )!;

            // Physical filter — Mailing fails
            _result = BuildLookup(storedDoc, AddressesFixtureScopes, BuildAddressesWithPhysicalFilter());
        }

        [Test]
        public void It_should_have_no_visible_collection_rows()
        {
            _result.ClassifiedStoredCollectionRows.Should().BeEmpty();
        }

        [Test]
        public void It_should_not_report_any_row_in_lookup()
        {
            // Construct a Mailing row address to verify it's not in the lookup
            var addressEngine = new AddressDerivationEngine(AddressesFixtureScopes);
            JsonNode mailingItem = JsonNode.Parse(
                """
                {
                    "addressTypeDescriptor": "uri://ed-fi.org/AddressType#Mailing",
                    "city": "Dallas",
                    "stateAbbreviationDescriptor": "uri://ed-fi.org/State#TX"
                }
                """
            )!;
            var rowAddress = addressEngine.DeriveCollectionRowAddress("$.addresses[*]", mailingItem, []);
            _result.Lookup.VisibleCollectionRowExistsAt(rowAddress).Should().BeFalse();
        }
    }

    // -----------------------------------------------------------------------
    //  5. Null stored document (create flow)
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_No_Stored_Document : StoredSideExistenceLookupBuilderTests
    {
        private StoredSideExistenceLookupResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = BuildLookup(null, SharedFixtureScopes, BuildIncludeOnlyWithCalendarReference());
        }

        [Test]
        public void It_should_return_empty_classified_scopes()
        {
            _result.ClassifiedStoredScopes.Should().BeEmpty();
        }

        [Test]
        public void It_should_return_empty_classified_collection_rows()
        {
            _result.ClassifiedStoredCollectionRows.Should().BeEmpty();
        }

        [Test]
        public void It_should_report_no_scope_exists_at_root()
        {
            var address = new ScopeInstanceAddress("$", []);
            _result.Lookup.VisibleScopeExistsAt(address).Should().BeFalse();
        }

        [Test]
        public void It_should_report_no_scope_exists_at_calendarReference()
        {
            var address = new ScopeInstanceAddress("$.calendarReference", []);
            _result.Lookup.VisibleScopeExistsAt(address).Should().BeFalse();
        }

        [Test]
        public void It_should_report_no_collection_row_exists()
        {
            var addressEngine = new AddressDerivationEngine(SharedFixtureScopes);
            JsonNode item = JsonNode.Parse("""{ "classPeriodName": "Period1" }""")!;
            var rowAddress = addressEngine.DeriveCollectionRowAddress("$.classPeriods[*]", item, []);
            _result.Lookup.VisibleCollectionRowExistsAt(rowAddress).Should().BeFalse();
        }
    }

    // -----------------------------------------------------------------------
    //  6. HiddenMemberPaths for non-collection scope
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Stored_Document_Reports_Hidden_Member_Paths : StoredSideExistenceLookupBuilderTests
    {
        private StoredSideExistenceLookupResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // SharedFixtureScopes root has: studentReference.studentUniqueId, schoolReference.schoolId,
            // entryDate, entryTypeDescriptor
            // The IncludeOnly profile includes studentReference, schoolReference, entryDate
            // but NOT entryTypeDescriptor — so entryTypeDescriptor is hidden.
            JsonNode storedDoc = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "schoolReference": { "schoolId": 100 },
                    "entryDate": "2024-08-01",
                    "entryTypeDescriptor": "uri://ed-fi.org/EntryType#Original",
                    "calendarReference": {
                        "calendarCode": "2024-01",
                        "calendarTypeDescriptor": "uri://ed-fi.org/CalendarType#IEP"
                    }
                }
                """
            )!;

            _result = BuildLookup(storedDoc, SharedFixtureScopes, BuildIncludeOnlyWithCalendarReference());
        }

        [Test]
        public void It_should_include_hidden_entryTypeDescriptor_in_root_scope_hidden_paths()
        {
            var rootScope = _result.ClassifiedStoredScopes.FirstOrDefault(s => s.Address.JsonScope == "$");
            rootScope.Should().NotBeNull();
            rootScope!.HiddenMemberPaths.Should().Contain("entryTypeDescriptor");
        }

        [Test]
        public void It_should_not_include_explicitly_included_members_in_hidden_paths()
        {
            // The IncludeOnly profile names are "studentReference", "schoolReference", "entryDate".
            // The canonical member path "entryDate" matches — so it should NOT be hidden.
            var rootScope = _result.ClassifiedStoredScopes.First(s => s.Address.JsonScope == "$");
            rootScope.HiddenMemberPaths.Should().NotContain("entryDate");
        }

        [Test]
        public void It_should_not_hide_reference_sub_paths_when_top_level_member_is_included()
        {
            // Canonical paths "studentReference.studentUniqueId" and "schoolReference.schoolId"
            // have top-level members "studentReference" and "schoolReference" which are in the
            // IncludeOnly list. Top-level inclusion transitively includes all descendants.
            var rootScope = _result.ClassifiedStoredScopes.First(s => s.Address.JsonScope == "$");
            rootScope.HiddenMemberPaths.Should().NotContain("studentReference.studentUniqueId");
            rootScope.HiddenMemberPaths.Should().NotContain("schoolReference.schoolId");
        }

        [Test]
        public void It_should_have_empty_hidden_paths_for_calendarReference()
        {
            // calendarReference IncludeOnly includes both calendarCode and calendarTypeDescriptor
            // which are the only canonical members — so nothing is hidden
            var calRef = _result.ClassifiedStoredScopes.First(s =>
                s.Address.JsonScope == "$.calendarReference"
            );
            calRef.HiddenMemberPaths.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  6b. Dotted canonical paths are visible when top-level member is included
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Dotted_Canonical_Path_With_Included_Top_Level_Member
        : StoredSideExistenceLookupBuilderTests
    {
        private StoredSideExistenceLookupResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // SharedFixtureScopes root has canonical paths:
            //   "studentReference.studentUniqueId", "schoolReference.schoolId",
            //   "entryDate", "entryTypeDescriptor"
            // The IncludeOnly profile includes "studentReference", "schoolReference", "entryDate"
            // — so the dotted paths should be VISIBLE (top-level member is included).
            JsonNode storedDoc = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "schoolReference": { "schoolId": 100 },
                    "entryDate": "2024-08-01",
                    "entryTypeDescriptor": "uri://ed-fi.org/EntryType#Original"
                }
                """
            )!;

            _result = BuildLookup(storedDoc, SharedFixtureScopes, BuildIncludeOnlyWithCalendarReference());
        }

        [Test]
        public void It_should_not_hide_dotted_path_when_top_level_member_is_included()
        {
            // "studentReference.studentUniqueId" should NOT be hidden because
            // the profile includes "studentReference" — top-level inclusion
            // transitively includes all descendants.
            var rootScope = _result.ClassifiedStoredScopes.First(s => s.Address.JsonScope == "$");
            rootScope.HiddenMemberPaths.Should().NotContain("studentReference.studentUniqueId");
            rootScope.HiddenMemberPaths.Should().NotContain("schoolReference.schoolId");
        }

        [Test]
        public void It_should_still_hide_excluded_flat_member()
        {
            // "entryTypeDescriptor" is NOT in the IncludeOnly list — it should be hidden.
            var rootScope = _result.ClassifiedStoredScopes.First(s => s.Address.JsonScope == "$");
            rootScope.HiddenMemberPaths.Should().Contain("entryTypeDescriptor");
        }

        [Test]
        public void It_should_only_hide_entryTypeDescriptor_for_root_scope()
        {
            // Only entryTypeDescriptor should be hidden — the three included top-level
            // members (and their descendants) should all be visible.
            var rootScope = _result.ClassifiedStoredScopes.First(s => s.Address.JsonScope == "$");
            rootScope.HiddenMemberPaths.Should().HaveCount(1);
            rootScope.HiddenMemberPaths.Should().Contain("entryTypeDescriptor");
        }
    }

    // -----------------------------------------------------------------------
    //  7. HiddenMemberPaths for collection rows
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Stored_Collection_Reports_Hidden_Member_Paths : StoredSideExistenceLookupBuilderTests
    {
        private StoredSideExistenceLookupResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // AddressesFixtureScopes addresses canonical members:
            //   addressTypeDescriptor, city, stateAbbreviationDescriptor
            // IncludeOnly profile includes addressTypeDescriptor and city
            // — hides stateAbbreviationDescriptor
            JsonNode storedDoc = JsonNode.Parse(
                """
                {
                    "field1": "value1",
                    "addresses": [
                        {
                            "addressTypeDescriptor": "uri://ed-fi.org/AddressType#Physical",
                            "city": "Austin",
                            "stateAbbreviationDescriptor": "uri://ed-fi.org/State#TX"
                        }
                    ]
                }
                """
            )!;

            _result = BuildLookup(
                storedDoc,
                AddressesFixtureScopes,
                BuildAddressesIncludeOnlyWithPhysicalFilter()
            );
        }

        [Test]
        public void It_should_have_one_visible_collection_row()
        {
            _result.ClassifiedStoredCollectionRows.Should().HaveCount(1);
        }

        [Test]
        public void It_should_include_stateAbbreviationDescriptor_in_hidden_member_paths()
        {
            _result
                .ClassifiedStoredCollectionRows[0]
                .HiddenMemberPaths.Should()
                .Contain("stateAbbreviationDescriptor");
        }

        [Test]
        public void It_should_not_include_visible_members_in_hidden_paths()
        {
            var row = _result.ClassifiedStoredCollectionRows[0];
            row.HiddenMemberPaths.Should().NotContain("addressTypeDescriptor");
            row.HiddenMemberPaths.Should().NotContain("city");
        }
    }

    // -----------------------------------------------------------------------
    //  8. HiddenMemberPaths for hidden scopes — all members are hidden
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Hidden_Scope_Reports_All_Members_As_Hidden : StoredSideExistenceLookupBuilderTests
    {
        private StoredSideExistenceLookupResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // Use a profile that hides calendarReference entirely
            JsonNode storedDoc = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "schoolReference": { "schoolId": 100 },
                    "entryDate": "2024-08-01",
                    "calendarReference": {
                        "calendarCode": "2024-01",
                        "calendarTypeDescriptor": "uri://ed-fi.org/CalendarType#IEP"
                    }
                }
                """
            )!;

            _result = BuildLookup(storedDoc, SharedFixtureScopes, BuildIncludeOnlyWithoutCalendarReference());
        }

        [Test]
        public void It_should_classify_calendarReference_as_hidden()
        {
            var calRef = _result.ClassifiedStoredScopes.First(s =>
                s.Address.JsonScope == "$.calendarReference"
            );
            calRef.Visibility.Should().Be(ProfileVisibilityKind.Hidden);
        }

        [Test]
        public void It_should_include_all_canonical_members_in_hidden_paths()
        {
            var calRef = _result.ClassifiedStoredScopes.First(s =>
                s.Address.JsonScope == "$.calendarReference"
            );
            calRef.HiddenMemberPaths.Should().Contain("calendarCode");
            calRef.HiddenMemberPaths.Should().Contain("calendarTypeDescriptor");
        }

        [Test]
        public void It_should_have_exactly_all_canonical_members_as_hidden()
        {
            var calRef = _result.ClassifiedStoredScopes.First(s =>
                s.Address.JsonScope == "$.calendarReference"
            );
            calRef.HiddenMemberPaths.Should().HaveCount(2);
        }
    }

    // -----------------------------------------------------------------------
    //  9. HiddenMemberPaths for extension scope under collection item
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Collection_Item_With_Hidden_Extension_Reports_Hidden_Member_Paths
        : StoredSideExistenceLookupBuilderTests
    {
        private StoredSideExistenceLookupResult _result = null!;

        /// <summary>
        /// IncludeOnly profile that includes root extension (sampleField) and
        /// classPeriods (classPeriodName), but does NOT include the collection-item
        /// extension — making $.classPeriods[*]._ext.sample hidden.
        /// </summary>
        private static ContentTypeDefinition BuildExtensionProfileWithHiddenItemExtension() =>
            new(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties: [new PropertyRule("studentReference"), new PropertyRule("entryDate")],
                Objects: [],
                Collections:
                [
                    new CollectionRule(
                        Name: "classPeriods",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("classPeriodName")],
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: null
                    ),
                ],
                Extensions:
                [
                    new ExtensionRule(
                        Name: "sample",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("sampleField")],
                        Objects: null,
                        Collections: null
                    ),
                ]
            );

        [SetUp]
        public void Setup()
        {
            // Stored document has a collection item with extension data
            JsonNode storedDoc = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "entryDate": "2024-08-01",
                    "_ext": {
                        "sample": {
                            "sampleField": "rootExtValue"
                        }
                    },
                    "classPeriods": [
                        {
                            "classPeriodName": "Period1",
                            "_ext": {
                                "sample": {
                                    "extraField": "itemExtValue"
                                }
                            }
                        }
                    ]
                }
                """
            )!;

            _result = BuildLookup(
                storedDoc,
                ProfileTestFixtures.ExtensionFixtureScopes,
                BuildExtensionProfileWithHiddenItemExtension()
            );
        }

        [Test]
        public void It_should_classify_collection_item_extension_scope()
        {
            _result
                .ClassifiedStoredScopes.Where(s => s.Address.JsonScope == "$.classPeriods[*]._ext.sample")
                .Should()
                .HaveCount(1);
        }

        [Test]
        public void It_should_classify_collection_item_extension_as_hidden()
        {
            var extScope = _result.ClassifiedStoredScopes.First(s =>
                s.Address.JsonScope == "$.classPeriods[*]._ext.sample"
            );
            extScope.Visibility.Should().Be(ProfileVisibilityKind.Hidden);
        }

        [Test]
        public void It_should_include_extension_member_in_hidden_member_paths()
        {
            var extScope = _result.ClassifiedStoredScopes.First(s =>
                s.Address.JsonScope == "$.classPeriods[*]._ext.sample"
            );
            extScope.HiddenMemberPaths.Should().Contain("extraField");
        }

        [Test]
        public void It_should_classify_root_extension_as_visible()
        {
            var rootExt = _result.ClassifiedStoredScopes.First(s => s.Address.JsonScope == "$._ext.sample");
            rootExt.Visibility.Should().Be(ProfileVisibilityKind.VisiblePresent);
        }

        [Test]
        public void It_should_have_empty_hidden_paths_for_visible_root_extension()
        {
            var rootExt = _result.ClassifiedStoredScopes.First(s => s.Address.JsonScope == "$._ext.sample");
            rootExt.HiddenMemberPaths.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  10. Per-item absent child scope states under collection ancestry
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Absent_Child_Scope_Under_Collection_Item : StoredSideExistenceLookupBuilderTests
    {
        private StoredSideExistenceLookupResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // NestedNonCollectionInsideCollectionScopes has:
            //   $                      (Root)
            //   $.addresses[*]         (Collection, identity: addressTypeDescriptor)
            //   $.addresses[*].period  (NonCollection, members: beginDate, endDate)
            //
            // The stored document has an address item WITHOUT the nested "period" scope.
            JsonNode storedDoc = JsonNode.Parse(
                """
                {
                    "field1": "value1",
                    "addresses": [
                        {
                            "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Home",
                            "city": "Austin"
                        }
                    ]
                }
                """
            )!;

            _result = BuildLookup(
                storedDoc,
                ProfileTestFixtures.NestedNonCollectionInsideCollectionScopes,
                ProfileTestFixtures.BuildIncludeAllProfile()
            );
        }

        [Test]
        public void It_should_emit_absent_scope_state_for_missing_period()
        {
            var periodScopes = _result.ClassifiedStoredScopes.Where(s =>
                s.Address.JsonScope == "$.addresses[*].period"
            );
            periodScopes.Should().HaveCount(1);
        }

        [Test]
        public void It_should_classify_absent_period_as_visible_absent()
        {
            var period = _result.ClassifiedStoredScopes.First(s =>
                s.Address.JsonScope == "$.addresses[*].period"
            );
            period.Visibility.Should().Be(ProfileVisibilityKind.VisibleAbsent);
        }

        [Test]
        public void It_should_derive_correct_ancestor_address_for_absent_period()
        {
            var period = _result.ClassifiedStoredScopes.First(s =>
                s.Address.JsonScope == "$.addresses[*].period"
            );
            // Should have one ancestor collection instance for $.addresses[*]
            period.Address.AncestorCollectionInstances.Should().HaveCount(1);
            period.Address.AncestorCollectionInstances[0].JsonScope.Should().Be("$.addresses[*]");
        }
    }
}
