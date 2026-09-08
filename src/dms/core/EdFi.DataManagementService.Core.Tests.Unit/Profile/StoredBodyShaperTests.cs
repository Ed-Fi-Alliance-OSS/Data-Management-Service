// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

internal abstract class StoredBodyShaperTests
{
    // -----------------------------------------------------------------------
    //  Shared helper
    // -----------------------------------------------------------------------

    protected static StoredBodyShaper BuildShaper(
        ContentTypeDefinition writeContent,
        IReadOnlyList<CompiledScopeDescriptor> scopes
    )
    {
        var classifier = new ProfileVisibilityClassifier(writeContent, scopes);
        return new StoredBodyShaper(classifier);
    }

    // Shared scope catalogs from ProfileTestFixtures
    protected static IReadOnlyList<CompiledScopeDescriptor> SharedFixtureScopes =>
        ProfileTestFixtures.SharedFixtureScopes;

    protected static IReadOnlyList<CompiledScopeDescriptor> AddressesFixtureScopes =>
        ProfileTestFixtures.AddressesFixtureScopes;

    protected static IReadOnlyList<CompiledScopeDescriptor> ExtensionFixtureScopes =>
        ProfileTestFixtures.ExtensionFixtureScopes;

    // -----------------------------------------------------------------------
    //  Profile builder helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// IncludeAll profile — every scope and member is visible.
    /// </summary>
    protected static ContentTypeDefinition BuildIncludeAllProfile() =>
        ProfileTestFixtures.BuildIncludeAllProfile();

    /// <summary>
    /// IncludeOnly profile exposing studentReference, schoolReference at root;
    /// classPeriods with IncludeOnly classPeriodName. Hides entryDate,
    /// entryTypeDescriptor, and classPeriods.officialAttendancePeriod.
    /// </summary>
    protected static ContentTypeDefinition BuildIncludeOnlyProfile() =>
        new(
            MemberSelection: MemberSelection.IncludeOnly,
            Properties: [new PropertyRule("studentReference"), new PropertyRule("schoolReference")],
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
    /// ExcludeOnly profile that excludes entryTypeDescriptor from root.
    /// All other scalars and child scopes are visible.
    /// </summary>
    protected static ContentTypeDefinition BuildExcludeOnlyProfile() =>
        new(
            MemberSelection: MemberSelection.ExcludeOnly,
            Properties: [new PropertyRule("entryTypeDescriptor")],
            Objects: [],
            Collections: [],
            Extensions: []
        );

    /// <summary>
    /// IncludeOnly profile that does not list calendarReference as an object,
    /// making it hidden (not navigable in the profile tree).
    /// </summary>
    protected static ContentTypeDefinition BuildProfileHidingCalendarRef() =>
        new(
            MemberSelection: MemberSelection.IncludeOnly,
            Properties:
            [
                new PropertyRule("studentReference"),
                new PropertyRule("schoolReference"),
                new PropertyRule("entryDate"),
                new PropertyRule("entryTypeDescriptor"),
            ],
            Objects: [],
            Collections:
            [
                new CollectionRule(
                    Name: "classPeriods",
                    MemberSelection: MemberSelection.IncludeAll,
                    LogicalSchema: null,
                    Properties: null,
                    NestedObjects: null,
                    NestedCollections: null,
                    Extensions: null,
                    ItemFilter: null
                ),
            ],
            Extensions: []
        );

    /// <summary>
    /// IncludeAll profile with IncludeOnly value filter on addresses for "Home" type.
    /// </summary>
    protected static ContentTypeDefinition BuildAddressFilterProfile() =>
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
                        Values: ["uri://ed-fi.org/AddressType#Home"]
                    )
                ),
            ],
            Extensions: []
        );

    /// <summary>
    /// Extension profile from shared fixtures.
    /// </summary>
    protected static ContentTypeDefinition BuildExtensionProfile() =>
        ProfileTestFixtures.BuildExtensionProfile();

    // -----------------------------------------------------------------------
    //  1. IncludeAll — all scalars pass through
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_IncludeAll_Profile : StoredBodyShaperTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(BuildIncludeAllProfile(), SharedFixtureScopes);

            JsonNode storedDocument = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "schoolReference": { "schoolId": 100 },
                    "entryDate": "2024-08-01",
                    "entryTypeDescriptor": "uri://ed-fi.org/EntryType#Original",
                    "calendarReference": {
                        "calendarCode": "2024-01",
                        "calendarTypeDescriptor": "uri://ed-fi.org/CalendarType#IEP"
                    },
                    "classPeriods": [
                        {
                            "classPeriodName": "Period1",
                            "officialAttendancePeriod": true
                        }
                    ]
                }
                """
            )!;

            _result = shaper.Shape(storedDocument);
        }

        [Test]
        public void It_should_include_all_root_scalars()
        {
            var body = _result.AsObject();
            body["studentReference"].Should().NotBeNull();
            body["schoolReference"].Should().NotBeNull();
            body["entryDate"]!.GetValue<string>().Should().Be("2024-08-01");
            body["entryTypeDescriptor"]!.GetValue<string>().Should().Be("uri://ed-fi.org/EntryType#Original");
        }

        [Test]
        public void It_should_include_non_collection_child_scope()
        {
            var body = _result.AsObject();
            body["calendarReference"].Should().NotBeNull();
            body["calendarReference"]!["calendarCode"]!.GetValue<string>().Should().Be("2024-01");
        }

        [Test]
        public void It_should_include_all_collection_items()
        {
            var body = _result.AsObject();
            var classPeriods = body["classPeriods"]!.AsArray();
            classPeriods.Should().HaveCount(1);

            var item = classPeriods[0]!.AsObject();
            item["classPeriodName"]!.GetValue<string>().Should().Be("Period1");
            item["officialAttendancePeriod"]!.GetValue<bool>().Should().BeTrue();
        }
    }

    // -----------------------------------------------------------------------
    //  2. IncludeOnly root profile — visible scalars included, hidden stripped
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_IncludeOnly_Root_Profile : StoredBodyShaperTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(BuildIncludeOnlyProfile(), SharedFixtureScopes);

            JsonNode storedDocument = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "schoolReference": { "schoolId": 100 },
                    "entryDate": "2024-08-01",
                    "entryTypeDescriptor": "uri://ed-fi.org/EntryType#Original",
                    "calendarReference": {
                        "calendarCode": "2024-01",
                        "calendarTypeDescriptor": "uri://ed-fi.org/CalendarType#IEP"
                    },
                    "classPeriods": [
                        {
                            "classPeriodName": "Period1",
                            "officialAttendancePeriod": true
                        }
                    ]
                }
                """
            )!;

            _result = shaper.Shape(storedDocument);
        }

        [Test]
        public void It_should_include_visible_root_scalars()
        {
            var body = _result.AsObject();
            body["studentReference"].Should().NotBeNull();
            body["schoolReference"].Should().NotBeNull();
        }

        [Test]
        public void It_should_strip_hidden_root_scalars()
        {
            var body = _result.AsObject();
            body.ContainsKey("entryDate").Should().BeFalse();
            body.ContainsKey("entryTypeDescriptor").Should().BeFalse();
        }

        [Test]
        public void It_should_strip_hidden_collection_item_scalars()
        {
            var body = _result.AsObject();
            var classPeriods = body["classPeriods"]!.AsArray();
            classPeriods.Should().HaveCount(1);

            var item = classPeriods[0]!.AsObject();
            item["classPeriodName"]!.GetValue<string>().Should().Be("Period1");
            item.ContainsKey("officialAttendancePeriod").Should().BeFalse();
        }

        [Test]
        public void It_should_include_visible_child_scope()
        {
            var body = _result.AsObject();
            body["calendarReference"].Should().NotBeNull();
            body["calendarReference"]!["calendarCode"]!.GetValue<string>().Should().Be("2024-01");
        }
    }

    // -----------------------------------------------------------------------
    //  3. ExcludeOnly root profile — excluded scalar stripped, others pass
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_ExcludeOnly_Root_Profile : StoredBodyShaperTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(BuildExcludeOnlyProfile(), SharedFixtureScopes);

            JsonNode storedDocument = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "schoolReference": { "schoolId": 100 },
                    "entryDate": "2024-08-01",
                    "entryTypeDescriptor": "uri://ed-fi.org/EntryType#Original",
                    "calendarReference": {
                        "calendarCode": "2024-01",
                        "calendarTypeDescriptor": "uri://ed-fi.org/CalendarType#IEP"
                    },
                    "classPeriods": [
                        {
                            "classPeriodName": "Period1",
                            "officialAttendancePeriod": true
                        }
                    ]
                }
                """
            )!;

            _result = shaper.Shape(storedDocument);
        }

        [Test]
        public void It_should_include_non_excluded_scalars()
        {
            var body = _result.AsObject();
            body["studentReference"].Should().NotBeNull();
            body["schoolReference"].Should().NotBeNull();
            body["entryDate"]!.GetValue<string>().Should().Be("2024-08-01");
        }

        [Test]
        public void It_should_strip_excluded_scalar()
        {
            var body = _result.AsObject();
            body.ContainsKey("entryTypeDescriptor").Should().BeFalse();
        }

        [Test]
        public void It_should_include_child_scopes()
        {
            var body = _result.AsObject();
            body["calendarReference"].Should().NotBeNull();
            body["classPeriods"]!.AsArray().Should().HaveCount(1);
        }
    }

    // -----------------------------------------------------------------------
    //  4. Hidden non-collection child scope
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Hidden_NonCollection_Child_Scope : StoredBodyShaperTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(BuildProfileHidingCalendarRef(), SharedFixtureScopes);

            JsonNode storedDocument = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "schoolReference": { "schoolId": 100 },
                    "entryDate": "2024-08-01",
                    "entryTypeDescriptor": "uri://ed-fi.org/EntryType#Original",
                    "calendarReference": {
                        "calendarCode": "2024-01",
                        "calendarTypeDescriptor": "uri://ed-fi.org/CalendarType#IEP"
                    },
                    "classPeriods": [
                        {
                            "classPeriodName": "Period1",
                            "officialAttendancePeriod": true
                        }
                    ]
                }
                """
            )!;

            _result = shaper.Shape(storedDocument);
        }

        [Test]
        public void It_should_omit_hidden_non_collection_child()
        {
            var body = _result.AsObject();
            body.ContainsKey("calendarReference").Should().BeFalse();
        }

        [Test]
        public void It_should_include_visible_root_scalars()
        {
            var body = _result.AsObject();
            body["studentReference"].Should().NotBeNull();
            body["schoolReference"].Should().NotBeNull();
            body["entryDate"]!.GetValue<string>().Should().Be("2024-08-01");
            body["entryTypeDescriptor"]!.GetValue<string>().Should().Be("uri://ed-fi.org/EntryType#Original");
        }

        [Test]
        public void It_should_include_visible_collection()
        {
            var body = _result.AsObject();
            body["classPeriods"]!.AsArray().Should().HaveCount(1);
        }
    }

    // -----------------------------------------------------------------------
    //  5. Collection with value filter — passing included, failing excluded
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Collection_With_Value_Filter : StoredBodyShaperTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(BuildAddressFilterProfile(), AddressesFixtureScopes);

            JsonNode storedDocument = JsonNode.Parse(
                """
                {
                    "field1": "value1",
                    "addresses": [
                        {
                            "addressTypeDescriptor": "uri://ed-fi.org/AddressType#Home",
                            "city": "Austin",
                            "stateAbbreviationDescriptor": "uri://ed-fi.org/State#TX"
                        },
                        {
                            "addressTypeDescriptor": "uri://ed-fi.org/AddressType#Mailing",
                            "city": "Dallas",
                            "stateAbbreviationDescriptor": "uri://ed-fi.org/State#TX"
                        },
                        {
                            "addressTypeDescriptor": "uri://ed-fi.org/AddressType#Home",
                            "city": "Houston",
                            "stateAbbreviationDescriptor": "uri://ed-fi.org/State#TX"
                        }
                    ]
                }
                """
            )!;

            _result = shaper.Shape(storedDocument);
        }

        [Test]
        public void It_should_include_passing_items()
        {
            var addresses = _result.AsObject()["addresses"]!.AsArray();
            addresses.Should().HaveCount(2);
        }

        [Test]
        public void It_should_include_first_Home_address()
        {
            var addresses = _result.AsObject()["addresses"]!.AsArray();
            addresses[0]!["city"]!.GetValue<string>().Should().Be("Austin");
        }

        [Test]
        public void It_should_include_second_Home_address()
        {
            var addresses = _result.AsObject()["addresses"]!.AsArray();
            addresses[1]!["city"]!.GetValue<string>().Should().Be("Houston");
        }

        [Test]
        public void It_should_silently_exclude_failing_Mailing_address()
        {
            var addresses = _result.AsObject()["addresses"]!.AsArray();
            addresses.Should().NotContain(a => a!["city"]!.GetValue<string>() == "Dallas");
        }
    }

    // -----------------------------------------------------------------------
    //  6. Extension scopes — visible extensions included, hidden omitted
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Extension_Scopes : StoredBodyShaperTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(BuildExtensionProfile(), ExtensionFixtureScopes);

            JsonNode storedDocument = JsonNode.Parse(
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

            _result = shaper.Shape(storedDocument);
        }

        [Test]
        public void It_should_include_visible_root_extension()
        {
            var body = _result.AsObject();
            body["_ext"].Should().NotBeNull();
            body["_ext"]!["sample"].Should().NotBeNull();
            body["_ext"]!["sample"]!["sampleField"]!.GetValue<string>().Should().Be("rootExtValue");
        }

        [Test]
        public void It_should_include_visible_collection_item_extension()
        {
            var body = _result.AsObject();
            var item = body["classPeriods"]!.AsArray()[0]!.AsObject();
            item["_ext"].Should().NotBeNull();
            item["_ext"]!["sample"].Should().NotBeNull();
            item["_ext"]!["sample"]!["extraField"]!.GetValue<string>().Should().Be("itemExtValue");
        }
    }

    // -----------------------------------------------------------------------
    //  7. DeepClone correctness — mutating output does not affect input
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_DeepClone_Correctness : StoredBodyShaperTests
    {
        private JsonNode _input = null!;
        private JsonNode _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(BuildIncludeAllProfile(), SharedFixtureScopes);

            _input = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "schoolReference": { "schoolId": 100 },
                    "entryDate": "2024-08-01",
                    "entryTypeDescriptor": "uri://ed-fi.org/EntryType#Original",
                    "calendarReference": {
                        "calendarCode": "2024-01",
                        "calendarTypeDescriptor": "uri://ed-fi.org/CalendarType#IEP"
                    },
                    "classPeriods": [
                        {
                            "classPeriodName": "Period1",
                            "officialAttendancePeriod": true
                        }
                    ]
                }
                """
            )!;

            _result = shaper.Shape(_input);
        }

        [Test]
        public void It_should_not_affect_input_when_output_is_mutated()
        {
            // Mutate the output
            _result.AsObject()["entryDate"] = "2025-01-01";

            // Verify original input is unchanged
            _input["entryDate"]!.GetValue<string>().Should().Be("2024-08-01");
        }

        [Test]
        public void It_should_not_affect_input_when_collection_item_is_mutated()
        {
            // Mutate output collection item
            _result.AsObject()["classPeriods"]!.AsArray()[0]!.AsObject()["classPeriodName"] = "Modified";

            // Verify original input is unchanged
            _input["classPeriods"]!.AsArray()[0]!["classPeriodName"]!
                .GetValue<string>()
                .Should()
                .Be("Period1");
        }
    }

    // -----------------------------------------------------------------------
    //  8. Absent collection — absent collection key not included in output
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Absent_Collection : StoredBodyShaperTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(BuildIncludeAllProfile(), SharedFixtureScopes);

            // Stored document with no classPeriods key at all
            JsonNode storedDocument = JsonNode.Parse(
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

            _result = shaper.Shape(storedDocument);
        }

        [Test]
        public void It_should_not_include_absent_collection_key()
        {
            var body = _result.AsObject();
            body.ContainsKey("classPeriods").Should().BeFalse();
        }

        [Test]
        public void It_should_include_present_members()
        {
            var body = _result.AsObject();
            body["studentReference"].Should().NotBeNull();
            body["entryDate"]!.GetValue<string>().Should().Be("2024-08-01");
            body["calendarReference"].Should().NotBeNull();
        }
    }
}
