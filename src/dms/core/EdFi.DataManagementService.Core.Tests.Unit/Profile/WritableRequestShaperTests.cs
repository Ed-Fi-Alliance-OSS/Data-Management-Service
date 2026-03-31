// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

public abstract class WritableRequestShaperTests
{
    // -----------------------------------------------------------------------
    //  Shared helper
    // -----------------------------------------------------------------------

    protected static WritableRequestShaper BuildShaper(
        ContentTypeDefinition writeContent,
        IReadOnlyList<CompiledScopeDescriptor> scopes
    )
    {
        var classifier = new ProfileVisibilityClassifier(writeContent, scopes);
        var addressEngine = new AddressDerivationEngine(scopes);
        return new WritableRequestShaper(
            classifier,
            addressEngine,
            profileName: "TestProfile",
            resourceName: "TestResource",
            method: "POST",
            operation: "write"
        );
    }

    // Shared scope catalogs from ProfileTestFixtures
    protected static IReadOnlyList<CompiledScopeDescriptor> SharedFixtureScopes =>
        ProfileTestFixtures.SharedFixtureScopes;

    protected static IReadOnlyList<CompiledScopeDescriptor> AddressesFixtureScopes =>
        ProfileTestFixtures.AddressesFixtureScopes;

    // -----------------------------------------------------------------------
    //  Profile builder helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds an IncludeOnly profile exposing studentReference, schoolReference,
    /// entryDate, calendarReference, and classPeriods.classPeriodName.
    /// Hides entryTypeDescriptor and classPeriods.officialAttendancePeriod.
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
                    Properties: [new PropertyRule("classPeriodName")],
                    NestedObjects: null,
                    NestedCollections: null,
                    Extensions: null,
                    ItemFilter: null
                ),
            ],
            Extensions: []
        );

    protected static ContentTypeDefinition BuildIncludeAllProfile() =>
        ProfileTestFixtures.BuildIncludeAllProfile();

    /// <summary>
    /// Builds an IncludeAll profile for addresses with an IncludeOnly item filter
    /// on addressTypeDescriptor for "Physical".
    /// </summary>
    protected static ContentTypeDefinition BuildAddressesIncludeAllWithPhysicalFilter() =>
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

    // -----------------------------------------------------------------------
    //  1. Shared reference fixture (StudentSchoolAssociation IncludeOnly)
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Shared_Reference_Fixture_Request : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(BuildIncludeOnlyProfile(), SharedFixtureScopes);

            // Request body with all fields, including hidden ones
            JsonNode requestBody = JsonNode.Parse(
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

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_should_include_visible_root_members_in_shaped_body()
        {
            var body = _result.WritableRequestBody.AsObject();
            body["studentReference"].Should().NotBeNull();
            body["schoolReference"].Should().NotBeNull();
            body["entryDate"].Should().NotBeNull();
        }

        [Test]
        public void It_should_exclude_hidden_root_members_from_shaped_body()
        {
            var body = _result.WritableRequestBody.AsObject();
            body.ContainsKey("entryTypeDescriptor").Should().BeFalse();
        }

        [Test]
        public void It_should_include_visible_collection_items_with_filtered_members()
        {
            var body = _result.WritableRequestBody.AsObject();
            var classPeriods = body["classPeriods"]!.AsArray();
            classPeriods.Should().HaveCount(1);

            var item = classPeriods[0]!.AsObject();
            item["classPeriodName"]!.GetValue<string>().Should().Be("Period1");
            item.ContainsKey("officialAttendancePeriod").Should().BeFalse();
        }

        [Test]
        public void It_should_include_visible_child_scope_in_shaped_body()
        {
            var body = _result.WritableRequestBody.AsObject();
            body["calendarReference"].Should().NotBeNull();
        }

        [Test]
        public void It_should_emit_root_scope_state_as_VisiblePresent()
        {
            _result
                .RequestScopeStates.Should()
                .Contain(s =>
                    s.Address.JsonScope == "$" && s.Visibility == ProfileVisibilityKind.VisiblePresent
                );
        }

        [Test]
        public void It_should_emit_calendar_scope_state_as_VisiblePresent()
        {
            _result
                .RequestScopeStates.Should()
                .Contain(s =>
                    s.Address.JsonScope == "$.calendarReference"
                    && s.Visibility == ProfileVisibilityKind.VisiblePresent
                );
        }

        [Test]
        public void It_should_emit_all_scope_states_with_Creatable_false()
        {
            _result.RequestScopeStates.Should().AllSatisfy(s => s.Creatable.Should().BeFalse());
        }

        [Test]
        public void It_should_emit_one_visible_collection_item()
        {
            _result.VisibleRequestCollectionItems.Should().HaveCount(1);
            _result.VisibleRequestCollectionItems[0].Address.JsonScope.Should().Be("$.classPeriods[*]");
            _result.VisibleRequestCollectionItems[0].Creatable.Should().BeFalse();
        }

        [Test]
        public void It_should_emit_validation_failure_for_hidden_submitted_scalar()
        {
            _result
                .ValidationFailures.OfType<ForbiddenSubmittedDataWritableProfileValidationFailure>()
                .Should()
                .Contain(f => f.RequestJsonPaths.Contains("$.entryTypeDescriptor"));
        }

        [Test]
        public void It_should_emit_validation_failure_for_hidden_submitted_collection_item_scalar()
        {
            _result
                .ValidationFailures.OfType<ForbiddenSubmittedDataWritableProfileValidationFailure>()
                .Should()
                .Contain(f => f.RequestJsonPaths.Contains("$.classPeriods[0].officialAttendancePeriod"));
        }
    }

    // -----------------------------------------------------------------------
    //  2. Absent visible scope (IncludeAll, missing calendarReference)
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Absent_Visible_Scope : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(BuildIncludeAllProfile(), SharedFixtureScopes);

            // Request body missing calendarReference entirely
            JsonNode requestBody = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "schoolReference": { "schoolId": 100 },
                    "entryDate": "2024-08-01",
                    "entryTypeDescriptor": "uri://ed-fi.org/EntryType#Original"
                }
                """
            )!;

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_should_emit_calendar_scope_state_as_VisibleAbsent()
        {
            _result
                .RequestScopeStates.Should()
                .Contain(s =>
                    s.Address.JsonScope == "$.calendarReference"
                    && s.Visibility == ProfileVisibilityKind.VisibleAbsent
                );
        }

        [Test]
        public void It_should_not_include_calendarReference_in_shaped_body()
        {
            var body = _result.WritableRequestBody.AsObject();
            body.ContainsKey("calendarReference").Should().BeFalse();
        }

        [Test]
        public void It_should_not_emit_scope_state_for_collection_scopes()
        {
            _result.RequestScopeStates.Should().NotContain(s => s.Address.JsonScope == "$.classPeriods[*]");
        }
    }

    // -----------------------------------------------------------------------
    //  3. Collection item failing value filter (single item)
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Collection_Item_Failing_Value_Filter : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(BuildAddressesIncludeAllWithPhysicalFilter(), AddressesFixtureScopes);

            // Request with a Mailing address (fails Physical IncludeOnly filter)
            JsonNode requestBody = JsonNode.Parse(
                """
                {
                    "field1": "value1",
                    "addresses": [
                        {
                            "addressTypeDescriptor": "uri://ed-fi.org/AddressType#Mailing",
                            "city": "Austin",
                            "stateAbbreviationDescriptor": "uri://ed-fi.org/State#TX"
                        }
                    ]
                }
                """
            )!;

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_should_collect_one_validation_failure()
        {
            _result.ValidationFailures.Should().HaveCount(1);
        }

        [Test]
        public void It_should_have_failure_for_addresses_scope()
        {
            _result
                .ValidationFailures[0]
                .Should()
                .BeOfType<ForbiddenSubmittedDataWritableProfileValidationFailure>();
            var failure = (ForbiddenSubmittedDataWritableProfileValidationFailure)
                _result.ValidationFailures[0];
            failure.JsonScope.Should().Be("$.addresses[*]");
        }

        [Test]
        public void It_should_have_rooted_request_json_path_in_failure()
        {
            var failure = (ForbiddenSubmittedDataWritableProfileValidationFailure)
                _result.ValidationFailures[0];
            failure.RequestJsonPaths.Should().ContainSingle().Which.Should().Be("$.addresses[0]");
        }

        [Test]
        public void It_should_exclude_failing_item_from_output()
        {
            var body = _result.WritableRequestBody.AsObject();
            var addresses = body["addresses"]!.AsArray();
            addresses.Should().BeEmpty();
        }

        [Test]
        public void It_should_not_emit_visible_collection_item_for_failing_entry()
        {
            _result.VisibleRequestCollectionItems.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  4. Multiple failing items
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Multiple_Failing_Items : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(BuildAddressesIncludeAllWithPhysicalFilter(), AddressesFixtureScopes);

            // Request with two items, both failing the filter
            JsonNode requestBody = JsonNode.Parse(
                """
                {
                    "field1": "value1",
                    "addresses": [
                        {
                            "addressTypeDescriptor": "uri://ed-fi.org/AddressType#Mailing",
                            "city": "Austin",
                            "stateAbbreviationDescriptor": "uri://ed-fi.org/State#TX"
                        },
                        {
                            "addressTypeDescriptor": "uri://ed-fi.org/AddressType#Billing",
                            "city": "Dallas",
                            "stateAbbreviationDescriptor": "uri://ed-fi.org/State#TX"
                        }
                    ]
                }
                """
            )!;

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_should_collect_two_validation_failures()
        {
            _result.ValidationFailures.Should().HaveCount(2);
        }

        [Test]
        public void It_should_exclude_all_failing_items_from_output()
        {
            var body = _result.WritableRequestBody.AsObject();
            var addresses = body["addresses"]!.AsArray();
            addresses.Should().BeEmpty();
        }

        [Test]
        public void It_should_not_emit_any_visible_collection_items()
        {
            _result.VisibleRequestCollectionItems.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  5. Items passing value filter
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Items_Passing_Value_Filter : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(BuildAddressesIncludeAllWithPhysicalFilter(), AddressesFixtureScopes);

            // Request with a Physical address (passes IncludeOnly filter)
            JsonNode requestBody = JsonNode.Parse(
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

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_should_have_no_validation_failures()
        {
            _result.ValidationFailures.Should().BeEmpty();
        }

        [Test]
        public void It_should_include_passing_item_in_output()
        {
            var body = _result.WritableRequestBody.AsObject();
            var addresses = body["addresses"]!.AsArray();
            addresses.Should().HaveCount(1);
            addresses[0]!["city"]!.GetValue<string>().Should().Be("Austin");
        }

        [Test]
        public void It_should_emit_one_visible_collection_item()
        {
            _result.VisibleRequestCollectionItems.Should().HaveCount(1);
            _result.VisibleRequestCollectionItems[0].Address.JsonScope.Should().Be("$.addresses[*]");
            _result.VisibleRequestCollectionItems[0].Creatable.Should().BeFalse();
        }
    }

    // -----------------------------------------------------------------------
    //  6. Root-level extension scope shaping
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Root_Extension_Scope : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(
                ProfileTestFixtures.BuildExtensionProfile(),
                ProfileTestFixtures.ExtensionFixtureScopes
            );

            JsonNode requestBody = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "entryDate": "2024-08-01",
                    "_ext": {
                        "sample": {
                            "sampleField": "hello",
                            "hiddenField": "should be stripped"
                        }
                    },
                    "classPeriods": [
                        {
                            "classPeriodName": "Period1"
                        }
                    ]
                }
                """
            )!;

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_should_include_ext_in_shaped_body()
        {
            var body = _result.WritableRequestBody.AsObject();
            body.ContainsKey("_ext").Should().BeTrue();
        }

        [Test]
        public void It_should_include_visible_extension_members()
        {
            var ext = _result.WritableRequestBody["_ext"]!["sample"]!.AsObject();
            ext["sampleField"]!.GetValue<string>().Should().Be("hello");
        }

        [Test]
        public void It_should_exclude_hidden_extension_members()
        {
            var ext = _result.WritableRequestBody["_ext"]!["sample"]!.AsObject();
            ext.ContainsKey("hiddenField").Should().BeFalse();
        }

        [Test]
        public void It_should_emit_extension_scope_state_as_VisiblePresent()
        {
            _result
                .RequestScopeStates.Should()
                .Contain(s =>
                    s.Address.JsonScope == "$._ext.sample"
                    && s.Visibility == ProfileVisibilityKind.VisiblePresent
                );
        }
    }

    // -----------------------------------------------------------------------
    //  7. Extension scope within collection item
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Extension_Within_Collection_Item : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(
                ProfileTestFixtures.BuildExtensionProfile(),
                ProfileTestFixtures.ExtensionFixtureScopes
            );

            JsonNode requestBody = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "entryDate": "2024-08-01",
                    "classPeriods": [
                        {
                            "classPeriodName": "Period1",
                            "_ext": {
                                "sample": {
                                    "extraField": "ext-value",
                                    "hiddenExtField": "should be stripped"
                                }
                            }
                        }
                    ]
                }
                """
            )!;

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_should_include_ext_in_collection_item()
        {
            var body = _result.WritableRequestBody.AsObject();
            var classPeriods = body["classPeriods"]!.AsArray();
            classPeriods.Should().HaveCount(1);
            var item = classPeriods[0]!.AsObject();
            item.ContainsKey("_ext").Should().BeTrue();
        }

        [Test]
        public void It_should_include_visible_extension_members_in_collection_item()
        {
            var item = _result.WritableRequestBody["classPeriods"]![0]!;
            var ext = item["_ext"]!["sample"]!.AsObject();
            ext["extraField"]!.GetValue<string>().Should().Be("ext-value");
        }

        [Test]
        public void It_should_exclude_hidden_extension_members_in_collection_item()
        {
            var item = _result.WritableRequestBody["classPeriods"]![0]!;
            var ext = item["_ext"]!["sample"]!.AsObject();
            ext.ContainsKey("hiddenExtField").Should().BeFalse();
        }

        [Test]
        public void It_should_emit_collection_ext_scope_state_as_VisiblePresent()
        {
            _result
                .RequestScopeStates.Should()
                .Contain(s =>
                    s.Address.JsonScope == "$.classPeriods[*]._ext.sample"
                    && s.Visibility == ProfileVisibilityKind.VisiblePresent
                );
        }
    }

    // -----------------------------------------------------------------------
    //  8. Nested non-collection scope inside collection does not throw when
    //     collection is absent (regression test for EmitMissingScopeStates)
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Absent_Collection_With_Nested_Non_Collection_Scope : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(
                ProfileTestFixtures.BuildIncludeAllProfile(),
                ProfileTestFixtures.NestedNonCollectionInsideCollectionScopes
            );

            // Request body does not include the addresses collection at all
            JsonNode requestBody = JsonNode.Parse(
                """
                {
                    "field1": "value1"
                }
                """
            )!;

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_should_not_throw()
        {
            // The fact that Setup completed without throwing is the assertion.
            // EmitMissingScopeStates must skip $.addresses[*].period since it
            // has collection ancestors and no ancestor context is available.
            _result.Should().NotBeNull();
        }

        [Test]
        public void It_should_not_emit_scope_state_for_nested_non_collection_inside_collection()
        {
            _result
                .RequestScopeStates.Should()
                .NotContain(s => s.Address.JsonScope == "$.addresses[*].period");
        }

        [Test]
        public void It_should_emit_root_scope_state()
        {
            _result
                .RequestScopeStates.Should()
                .Contain(s =>
                    s.Address.JsonScope == "$" && s.Visibility == ProfileVisibilityKind.VisiblePresent
                );
        }
    }

    // -----------------------------------------------------------------------
    //  9. Collection inside extension scope
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Collection_Inside_Extension_Scope : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(
                ProfileTestFixtures.BuildExtensionWithCollectionProfile(),
                ProfileTestFixtures.ExtensionWithCollectionFixtureScopes
            );

            JsonNode requestBody = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "entryDate": "2024-08-01",
                    "_ext": {
                        "sample": {
                            "sampleField": "hello",
                            "extActivities": [
                                {
                                    "activityName": "Activity1",
                                    "activityDescription": "should be stripped"
                                }
                            ]
                        }
                    }
                }
                """
            )!;

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_should_include_extension_collection_in_shaped_body()
        {
            var ext = _result.WritableRequestBody["_ext"]!["sample"]!.AsObject();
            ext.ContainsKey("extActivities").Should().BeTrue();
            ext["extActivities"]!.AsArray().Should().HaveCount(1);
        }

        [Test]
        public void It_should_filter_extension_collection_item_members()
        {
            var item = _result.WritableRequestBody["_ext"]!["sample"]!["extActivities"]![0]!.AsObject();
            item["activityName"]!.GetValue<string>().Should().Be("Activity1");
            item.ContainsKey("activityDescription").Should().BeFalse();
        }

        [Test]
        public void It_should_emit_visible_collection_item_for_extension_collection()
        {
            _result.VisibleRequestCollectionItems.Should().HaveCount(1);
            _result
                .VisibleRequestCollectionItems[0]
                .Address.JsonScope.Should()
                .Be("$._ext.sample.extActivities[*]");
        }

        [Test]
        public void It_should_emit_extension_scope_state()
        {
            _result
                .RequestScopeStates.Should()
                .Contain(s =>
                    s.Address.JsonScope == "$._ext.sample"
                    && s.Visibility == ProfileVisibilityKind.VisiblePresent
                );
        }
    }

    // -----------------------------------------------------------------------
    //  10. Hidden non-collection scope emits Hidden RequestScopeState
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Hidden_Non_Collection_Scope : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // IncludeOnly profile that does NOT include calendarReference → Hidden
            var profile = new ContentTypeDefinition(
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

            var shaper = BuildShaper(profile, SharedFixtureScopes);

            JsonNode requestBody = JsonNode.Parse(
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
                        { "classPeriodName": "Period1" }
                    ]
                }
                """
            )!;

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_should_emit_hidden_scope_state_for_calendarReference()
        {
            _result
                .RequestScopeStates.Should()
                .Contain(s =>
                    s.Address.JsonScope == "$.calendarReference"
                    && s.Visibility == ProfileVisibilityKind.Hidden
                );
        }

        [Test]
        public void It_should_not_include_calendarReference_in_shaped_body()
        {
            var body = _result.WritableRequestBody.AsObject();
            body.ContainsKey("calendarReference").Should().BeFalse();
        }

        [Test]
        public void It_should_emit_validation_failure_for_hidden_submitted_scope()
        {
            _result
                .ValidationFailures.OfType<ForbiddenSubmittedDataWritableProfileValidationFailure>()
                .Should()
                .Contain(f => f.RequestJsonPaths.Contains("$.calendarReference"));
        }
    }

    // -----------------------------------------------------------------------
    //  11. Recursive shaping of nested non-collection scope inside non-collection scope
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Nested_NonCollection_Inside_NonCollection_Scope : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(
                ProfileTestFixtures.BuildNestedNonCollectionProfile(),
                ProfileTestFixtures.NestedNonCollectionInNonCollectionScopes
            );

            JsonNode requestBody = JsonNode.Parse(
                """
                {
                    "field1": "value1",
                    "parentObject": {
                        "parentField": "pValue",
                        "sharedField": "should-be-hidden",
                        "nestedCommonType": {
                            "nestedField": "nValue",
                            "hiddenNestedField": "should-be-hidden"
                        }
                    }
                }
                """
            )!;

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_should_include_nested_common_type_in_shaped_body()
        {
            var parent = _result.WritableRequestBody["parentObject"]!.AsObject();
            parent.ContainsKey("nestedCommonType").Should().BeTrue();
        }

        [Test]
        public void It_should_include_visible_nested_members()
        {
            var nested = _result.WritableRequestBody["parentObject"]!["nestedCommonType"]!.AsObject();
            nested["nestedField"]!.GetValue<string>().Should().Be("nValue");
        }

        [Test]
        public void It_should_exclude_hidden_nested_members()
        {
            var nested = _result.WritableRequestBody["parentObject"]!["nestedCommonType"]!.AsObject();
            nested.ContainsKey("hiddenNestedField").Should().BeFalse();
        }

        [Test]
        public void It_should_exclude_hidden_parent_members()
        {
            var parent = _result.WritableRequestBody["parentObject"]!.AsObject();
            parent.ContainsKey("sharedField").Should().BeFalse();
        }

        [Test]
        public void It_should_emit_scope_state_for_nested_common_type()
        {
            _result
                .RequestScopeStates.Should()
                .Contain(s =>
                    s.Address.JsonScope == "$.parentObject.nestedCommonType"
                    && s.Visibility == ProfileVisibilityKind.VisiblePresent
                );
        }

        [Test]
        public void It_should_emit_scope_state_for_parent_object()
        {
            _result
                .RequestScopeStates.Should()
                .Contain(s =>
                    s.Address.JsonScope == "$.parentObject"
                    && s.Visibility == ProfileVisibilityKind.VisiblePresent
                );
        }
    }

    // -----------------------------------------------------------------------
    //  12. Absent nested non-collection scope inside non-collection scope
    //      emits VisibleAbsent state
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Absent_Nested_NonCollection_Inside_Present_NonCollection : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(
                ProfileTestFixtures.BuildNestedNonCollectionProfile(),
                ProfileTestFixtures.NestedNonCollectionInNonCollectionScopes
            );

            // parentObject is present but nestedCommonType is absent
            JsonNode requestBody = JsonNode.Parse(
                """
                {
                    "field1": "value1",
                    "parentObject": {
                        "parentField": "pValue"
                    }
                }
                """
            )!;

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_should_emit_VisibleAbsent_for_nested_common_type()
        {
            _result
                .RequestScopeStates.Should()
                .Contain(s =>
                    s.Address.JsonScope == "$.parentObject.nestedCommonType"
                    && s.Visibility == ProfileVisibilityKind.VisibleAbsent
                );
        }

        [Test]
        public void It_should_emit_VisiblePresent_for_parent_object()
        {
            _result
                .RequestScopeStates.Should()
                .Contain(s =>
                    s.Address.JsonScope == "$.parentObject"
                    && s.Visibility == ProfileVisibilityKind.VisiblePresent
                );
        }
    }

    // -----------------------------------------------------------------------
    //  13. Absent nested non-collection scope inside a present collection item
    //      emits VisibleAbsent state per item (Finding 3)
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Present_Collection_Item_With_Absent_Nested_Scope : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(
                ProfileTestFixtures.BuildIncludeAllProfile(),
                ProfileTestFixtures.NestedNonCollectionInsideCollectionScopes
            );

            // Two address items: first has period, second does not
            JsonNode requestBody = JsonNode.Parse(
                """
                {
                    "field1": "value1",
                    "addresses": [
                        {
                            "addressTypeDescriptor": "Physical",
                            "city": "Austin",
                            "period": {
                                "beginDate": "2024-01-01",
                                "endDate": "2024-12-31"
                            }
                        },
                        {
                            "addressTypeDescriptor": "Mailing",
                            "city": "Houston"
                        }
                    ]
                }
                """
            )!;

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_should_emit_VisiblePresent_for_first_item_period()
        {
            _result
                .RequestScopeStates.Should()
                .Contain(s =>
                    s.Address.JsonScope == "$.addresses[*].period"
                    && s.Visibility == ProfileVisibilityKind.VisiblePresent
                );
        }

        [Test]
        public void It_should_emit_VisibleAbsent_for_second_item_period()
        {
            // There should be two scope states for $.addresses[*].period:
            // one VisiblePresent (first item) and one VisibleAbsent (second item)
            var periodStates = _result
                .RequestScopeStates.Where(s => s.Address.JsonScope == "$.addresses[*].period")
                .ToList();

            periodStates.Should().HaveCount(2);
            periodStates.Should().Contain(s => s.Visibility == ProfileVisibilityKind.VisiblePresent);
            periodStates.Should().Contain(s => s.Visibility == ProfileVisibilityKind.VisibleAbsent);
        }

        [Test]
        public void It_should_include_period_in_first_item_shaped_body()
        {
            var firstItem = _result.WritableRequestBody["addresses"]![0]!.AsObject();
            firstItem.ContainsKey("period").Should().BeTrue();
            firstItem["period"]!["beginDate"]!.GetValue<string>().Should().Be("2024-01-01");
        }

        [Test]
        public void It_should_not_include_period_in_second_item_shaped_body()
        {
            var secondItem = _result.WritableRequestBody["addresses"]![1]!.AsObject();
            secondItem.ContainsKey("period").Should().BeFalse();
        }
    }

    // -----------------------------------------------------------------------
    //  14. Absent extension scope inside a present collection item
    //      emits VisibleAbsent state
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Present_Collection_Item_Without_Extension : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(
                ProfileTestFixtures.BuildExtensionProfile(),
                ProfileTestFixtures.ExtensionFixtureScopes
            );

            // Collection item present but _ext is absent
            JsonNode requestBody = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "entryDate": "2024-08-01",
                    "_ext": {
                        "sample": { "sampleField": "hello" }
                    },
                    "classPeriods": [
                        { "classPeriodName": "Period1" }
                    ]
                }
                """
            )!;

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_should_emit_VisibleAbsent_for_collection_item_ext_scope()
        {
            _result
                .RequestScopeStates.Should()
                .Contain(s =>
                    s.Address.JsonScope == "$.classPeriods[*]._ext.sample"
                    && s.Visibility == ProfileVisibilityKind.VisibleAbsent
                );
        }

        [Test]
        public void It_should_emit_VisiblePresent_for_root_ext_scope()
        {
            _result
                .RequestScopeStates.Should()
                .Contain(s =>
                    s.Address.JsonScope == "$._ext.sample"
                    && s.Visibility == ProfileVisibilityKind.VisiblePresent
                );
        }
    }

    // -----------------------------------------------------------------------
    //  15. ExcludeOnly filter mode
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_ExcludeOnly_Profile : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // ExcludeOnly root that explicitly excludes entryTypeDescriptor
            var profile = new ContentTypeDefinition(
                MemberSelection: MemberSelection.ExcludeOnly,
                Properties: [new PropertyRule("entryTypeDescriptor")],
                Objects: [],
                Collections: [],
                Extensions: []
            );

            var shaper = BuildShaper(profile, SharedFixtureScopes);

            JsonNode requestBody = JsonNode.Parse(
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

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_should_exclude_explicitly_excluded_root_members()
        {
            var body = _result.WritableRequestBody.AsObject();
            body.ContainsKey("entryTypeDescriptor").Should().BeFalse();
        }

        [Test]
        public void It_should_include_non_excluded_root_members()
        {
            var body = _result.WritableRequestBody.AsObject();
            body["studentReference"].Should().NotBeNull();
            body["schoolReference"].Should().NotBeNull();
            body["entryDate"].Should().NotBeNull();
        }

        [Test]
        public void It_should_include_child_scopes_not_in_exclude_list()
        {
            var body = _result.WritableRequestBody.AsObject();
            body["calendarReference"].Should().NotBeNull();
            body["classPeriods"].Should().NotBeNull();
        }

        [Test]
        public void It_should_include_all_members_in_implicitly_visible_collection()
        {
            var body = _result.WritableRequestBody.AsObject();
            var item = body["classPeriods"]![0]!.AsObject();
            item["classPeriodName"].Should().NotBeNull();
            item["officialAttendancePeriod"].Should().NotBeNull();
        }

        [Test]
        public void It_should_emit_validation_failure_for_excluded_submitted_scalar()
        {
            _result
                .ValidationFailures.OfType<ForbiddenSubmittedDataWritableProfileValidationFailure>()
                .Should()
                .Contain(f => f.RequestJsonPaths.Contains("$.entryTypeDescriptor"));
        }
    }

    // -----------------------------------------------------------------------
    //  16. Hidden member NOT submitted — no failure expected
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Hidden_Member_Not_Submitted : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // IncludeOnly profile that hides entryTypeDescriptor
            // Request body does NOT include entryTypeDescriptor
            var shaper = BuildShaper(BuildIncludeOnlyProfile(), SharedFixtureScopes);

            JsonNode requestBody = JsonNode.Parse(
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
                        { "classPeriodName": "Period1" }
                    ]
                }
                """
            )!;

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_should_not_emit_forbidden_data_failure_for_absent_hidden_member()
        {
            _result
                .ValidationFailures.OfType<ForbiddenSubmittedDataWritableProfileValidationFailure>()
                .Should()
                .BeEmpty();
        }
    }
}
