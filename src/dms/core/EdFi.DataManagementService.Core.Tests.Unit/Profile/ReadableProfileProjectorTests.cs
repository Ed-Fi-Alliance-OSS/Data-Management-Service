// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

[TestFixture]
[Parallelizable]
public class ReadableProfileProjectorTests
{
    private static readonly IReadableProfileProjector _projector = new ReadableProfileProjector();

    private static ContentTypeDefinition CreateContentType(
        MemberSelection memberSelection,
        IReadOnlyList<PropertyRule>? properties = null,
        IReadOnlyList<ObjectRule>? objects = null,
        IReadOnlyList<CollectionRule>? collections = null,
        IReadOnlyList<ExtensionRule>? extensions = null
    )
    {
        return new ContentTypeDefinition(
            memberSelection,
            properties ?? [],
            objects ?? [],
            collections ?? [],
            extensions ?? []
        );
    }

    private static IReadOnlySet<string> IdentityNames(params string[] names) => new HashSet<string>(names);

    // =======================================================================
    //  AC: Hidden scalar members are removed
    // =======================================================================

    [TestFixture]
    [Parallelizable]
    public class Given_IncludeOnly_Hides_Unlisted_Scalars : ReadableProfileProjectorTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void SetUp()
        {
            var source = new JsonObject
            {
                ["id"] = "abc-123",
                ["schoolId"] = 100,
                ["nameOfInstitution"] = "Test School",
                ["shortNameOfInstitution"] = "TS",
                ["webSite"] = "https://example.com",
            };

            var contentType = CreateContentType(
                MemberSelection.IncludeOnly,
                properties: [new PropertyRule("nameOfInstitution")]
            );

            _result = _projector.Project(source, contentType, IdentityNames("schoolId"));
        }

        [Test]
        public void It_includes_specified_property()
        {
            _result["nameOfInstitution"]?.GetValue<string>().Should().Be("Test School");
        }

        [Test]
        public void It_removes_hidden_scalar_shortName()
        {
            _result["shortNameOfInstitution"].Should().BeNull();
        }

        [Test]
        public void It_removes_hidden_scalar_webSite()
        {
            _result["webSite"].Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_ExcludeOnly_Hides_Listed_Scalars : ReadableProfileProjectorTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void SetUp()
        {
            var source = new JsonObject
            {
                ["id"] = "abc-123",
                ["schoolId"] = 100,
                ["nameOfInstitution"] = "Test School",
                ["shortNameOfInstitution"] = "TS",
                ["webSite"] = "https://example.com",
            };

            var contentType = CreateContentType(
                MemberSelection.ExcludeOnly,
                properties: [new PropertyRule("webSite"), new PropertyRule("shortNameOfInstitution")]
            );

            _result = _projector.Project(source, contentType, IdentityNames("schoolId"));
        }

        [Test]
        public void It_excludes_listed_properties()
        {
            _result["shortNameOfInstitution"].Should().BeNull();
            _result["webSite"].Should().BeNull();
        }

        [Test]
        public void It_preserves_unlisted_properties()
        {
            _result["nameOfInstitution"]?.GetValue<string>().Should().Be("Test School");
        }
    }

    // =======================================================================
    //  AC: Hidden collection scopes are removed
    // =======================================================================

    [TestFixture]
    [Parallelizable]
    public class Given_Collection_Not_In_Profile : ReadableProfileProjectorTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void SetUp()
        {
            var source = new JsonObject
            {
                ["id"] = "abc-123",
                ["schoolId"] = 100,
                ["addresses"] = new JsonArray
                {
                    new JsonObject { ["streetAddress"] = "123 Main St", ["city"] = "Springfield" },
                },
                ["telephones"] = new JsonArray { new JsonObject { ["telephoneNumber"] = "555-1234" } },
            };

            // Only addresses have a collection rule — telephones are not mentioned
            var contentType = CreateContentType(
                MemberSelection.IncludeOnly,
                collections:
                [
                    new CollectionRule(
                        Name: "addresses",
                        MemberSelection: MemberSelection.IncludeAll,
                        LogicalSchema: null,
                        Properties: null,
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: null
                    ),
                ]
            );

            _result = _projector.Project(source, contentType, IdentityNames("schoolId"));
        }

        [Test]
        public void It_includes_collection_with_rule()
        {
            var addresses = _result["addresses"] as JsonArray;
            addresses.Should().NotBeNull();
            addresses!.Count.Should().Be(1);
        }

        [Test]
        public void It_removes_collection_without_rule()
        {
            _result["telephones"].Should().BeNull();
        }
    }

    // =======================================================================
    //  AC: Hidden _ext data is removed
    // =======================================================================

    [TestFixture]
    [Parallelizable]
    public class Given_IncludeOnly_Hides_Unmentioned_Extensions : ReadableProfileProjectorTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void SetUp()
        {
            var source = new JsonObject
            {
                ["id"] = "abc-123",
                ["schoolId"] = 100,
                ["_ext"] = new JsonObject
                {
                    ["sample"] = new JsonObject { ["sampleField1"] = "value1", ["sampleField2"] = "value2" },
                    ["tpdm"] = new JsonObject { ["tpdmField1"] = "tpdmValue1" },
                },
            };

            var contentType = CreateContentType(
                MemberSelection.IncludeOnly,
                extensions:
                [
                    new ExtensionRule(
                        Name: "sample",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("sampleField1")],
                        Objects: null,
                        Collections: null
                    ),
                ]
            );

            _result = _projector.Project(source, contentType, IdentityNames("schoolId"));
        }

        [Test]
        public void It_includes_extension_matching_rule()
        {
            var ext = _result["_ext"] as JsonObject;
            ext.Should().NotBeNull();
            var sample = ext!["sample"] as JsonObject;
            sample.Should().NotBeNull();
            sample!["sampleField1"]?.GetValue<string>().Should().Be("value1");
        }

        [Test]
        public void It_filters_extension_scalars_per_rule()
        {
            var sample = (_result["_ext"] as JsonObject)!["sample"] as JsonObject;
            sample!["sampleField2"].Should().BeNull();
        }

        [Test]
        public void It_removes_extension_namespace_without_rule()
        {
            var ext = _result["_ext"] as JsonObject;
            ext!["tpdm"].Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_ExcludeOnly_Includes_Unmentioned_Extensions : ReadableProfileProjectorTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void SetUp()
        {
            var source = new JsonObject
            {
                ["id"] = "abc-123",
                ["schoolId"] = 100,
                ["_ext"] = new JsonObject
                {
                    ["sample"] = new JsonObject { ["sampleField1"] = "value1" },
                    ["tpdm"] = new JsonObject { ["tpdmField1"] = "tpdmValue1" },
                },
            };

            var contentType = CreateContentType(
                MemberSelection.ExcludeOnly,
                extensions:
                [
                    new ExtensionRule(
                        Name: "sample",
                        MemberSelection: MemberSelection.IncludeAll,
                        LogicalSchema: null,
                        Properties: null,
                        Objects: null,
                        Collections: null
                    ),
                ]
            );

            _result = _projector.Project(source, contentType, IdentityNames("schoolId"));
        }

        [Test]
        public void It_includes_extension_with_rule()
        {
            var sample = (_result["_ext"] as JsonObject)!["sample"] as JsonObject;
            sample.Should().NotBeNull();
            sample!["sampleField1"]?.GetValue<string>().Should().Be("value1");
        }

        [Test]
        public void It_includes_extension_without_rule_under_ExcludeOnly()
        {
            var tpdm = (_result["_ext"] as JsonObject)!["tpdm"] as JsonObject;
            tpdm.Should().NotBeNull();
            tpdm!["tpdmField1"]?.GetValue<string>().Should().Be("tpdmValue1");
        }
    }

    // =======================================================================
    //  AC: Present members are preserved intact
    // =======================================================================

    [TestFixture]
    [Parallelizable]
    public class Given_IncludeAll_Preserves_Everything : ReadableProfileProjectorTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void SetUp()
        {
            var source = new JsonObject
            {
                ["id"] = "abc-123",
                ["schoolId"] = 100,
                ["nameOfInstitution"] = "Test School",
                ["shortNameOfInstitution"] = "TS",
            };

            var contentType = CreateContentType(MemberSelection.IncludeAll);

            _result = _projector.Project(source, contentType, IdentityNames("schoolId"));
        }

        [Test]
        public void It_preserves_all_properties()
        {
            _result["id"]?.GetValue<string>().Should().Be("abc-123");
            _result["schoolId"]?.GetValue<int>().Should().Be(100);
            _result["nameOfInstitution"]?.GetValue<string>().Should().Be("Test School");
            _result["shortNameOfInstitution"]?.GetValue<string>().Should().Be("TS");
        }
    }

    // =======================================================================
    //  AC: Absent sections produce no output (not empty objects/arrays)
    // =======================================================================

    [TestFixture]
    [Parallelizable]
    public class Given_All_Ext_Namespaces_Hidden : ReadableProfileProjectorTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void SetUp()
        {
            var source = new JsonObject
            {
                ["id"] = "abc-123",
                ["schoolId"] = 100,
                ["_ext"] = new JsonObject { ["sample"] = new JsonObject { ["sampleField1"] = "value1" } },
            };

            // IncludeOnly with no extension rules — all extensions hidden
            var contentType = CreateContentType(MemberSelection.IncludeOnly);

            _result = _projector.Project(source, contentType, IdentityNames("schoolId"));
        }

        [Test]
        public void It_omits_ext_entirely_instead_of_empty_object()
        {
            _result["_ext"].Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Collection_All_Items_Filtered_Out : ReadableProfileProjectorTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void SetUp()
        {
            var source = new JsonObject
            {
                ["id"] = "abc-123",
                ["schoolId"] = 100,
                ["addresses"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
                        ["streetAddress"] = "123 Main St",
                    },
                },
            };

            var contentType = CreateContentType(
                MemberSelection.IncludeAll,
                collections:
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
                            Values: ["uri://ed-fi.org/AddressTypeDescriptor#Home"]
                        )
                    ),
                ]
            );

            _result = _projector.Project(source, contentType, IdentityNames("schoolId"));
        }

        [Test]
        public void It_omits_collection_instead_of_empty_array()
        {
            _result["addresses"].Should().BeNull();
        }
    }

    // =======================================================================
    //  AC: Extension data follows base-data filtering rules
    // =======================================================================

    [TestFixture]
    [Parallelizable]
    public class Given_Extension_With_Collection_And_Object_Rules : ReadableProfileProjectorTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void SetUp()
        {
            var source = new JsonObject
            {
                ["id"] = "abc-123",
                ["schoolId"] = 100,
                ["_ext"] = new JsonObject
                {
                    ["sample"] = new JsonObject
                    {
                        ["scalarField"] = "keep",
                        ["hiddenField"] = "drop",
                        ["nestedObj"] = new JsonObject { ["innerKeep"] = "yes", ["innerDrop"] = "no" },
                        ["items"] = new JsonArray
                        {
                            new JsonObject { ["itemName"] = "A", ["itemDetail"] = "detail-A" },
                        },
                    },
                },
            };

            var contentType = CreateContentType(
                MemberSelection.IncludeAll,
                extensions:
                [
                    new ExtensionRule(
                        Name: "sample",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("scalarField")],
                        Objects:
                        [
                            new ObjectRule(
                                Name: "nestedObj",
                                MemberSelection: MemberSelection.IncludeOnly,
                                LogicalSchema: null,
                                Properties: [new PropertyRule("innerKeep")],
                                NestedObjects: null,
                                Collections: null,
                                Extensions: null
                            ),
                        ],
                        Collections:
                        [
                            new CollectionRule(
                                Name: "items",
                                MemberSelection: MemberSelection.IncludeOnly,
                                LogicalSchema: null,
                                Properties: [new PropertyRule("itemName")],
                                NestedObjects: null,
                                NestedCollections: null,
                                Extensions: null,
                                ItemFilter: null
                            ),
                        ]
                    ),
                ]
            );

            _result = _projector.Project(source, contentType, IdentityNames("schoolId"));
        }

        [Test]
        public void It_filters_extension_scalar_members()
        {
            var sample = (_result["_ext"] as JsonObject)!["sample"] as JsonObject;
            sample!["scalarField"]?.GetValue<string>().Should().Be("keep");
            sample["hiddenField"].Should().BeNull();
        }

        [Test]
        public void It_filters_extension_nested_object()
        {
            var nested =
                ((_result["_ext"] as JsonObject)!["sample"] as JsonObject)!["nestedObj"] as JsonObject;
            nested!["innerKeep"]?.GetValue<string>().Should().Be("yes");
            nested["innerDrop"].Should().BeNull();
        }

        [Test]
        public void It_filters_extension_collection_items()
        {
            var items = ((_result["_ext"] as JsonObject)!["sample"] as JsonObject)!["items"] as JsonArray;
            items.Should().NotBeNull();
            items!.Count.Should().Be(1);
            var item = items[0] as JsonObject;
            item!["itemName"]?.GetValue<string>().Should().Be("A");
            item["itemDetail"].Should().BeNull();
        }
    }

    // =======================================================================
    //  AC: Collection item value filtering
    // =======================================================================

    [TestFixture]
    [Parallelizable]
    public class Given_Collection_ItemFilter_IncludeOnly : ReadableProfileProjectorTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void SetUp()
        {
            var source = new JsonObject
            {
                ["id"] = "abc-123",
                ["schoolId"] = 100,
                ["addresses"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Home",
                        ["city"] = "Springfield",
                    },
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Work",
                        ["city"] = "Chicago",
                    },
                },
            };

            var contentType = CreateContentType(
                MemberSelection.IncludeAll,
                collections:
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
                            Values: ["uri://ed-fi.org/AddressTypeDescriptor#Home"]
                        )
                    ),
                ]
            );

            _result = _projector.Project(source, contentType, IdentityNames("schoolId"));
        }

        [Test]
        public void It_includes_only_matching_items()
        {
            var addresses = _result["addresses"] as JsonArray;
            addresses!.Count.Should().Be(1);
            (addresses[0] as JsonObject)!["city"]?.GetValue<string>().Should().Be("Springfield");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Collection_ItemFilter_ExcludeOnly : ReadableProfileProjectorTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void SetUp()
        {
            var source = new JsonObject
            {
                ["id"] = "abc-123",
                ["schoolId"] = 100,
                ["addresses"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Home",
                        ["city"] = "Springfield",
                    },
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
                        ["city"] = "Chicago",
                    },
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Work",
                        ["city"] = "Peoria",
                    },
                },
            };

            var contentType = CreateContentType(
                MemberSelection.IncludeAll,
                collections:
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
                            FilterMode: FilterMode.ExcludeOnly,
                            Values: ["uri://ed-fi.org/AddressTypeDescriptor#Mailing"]
                        )
                    ),
                ]
            );

            _result = _projector.Project(source, contentType, IdentityNames("schoolId"));
        }

        [Test]
        public void It_excludes_matching_items_and_keeps_rest()
        {
            var addresses = _result["addresses"] as JsonArray;
            addresses!.Count.Should().Be(2);

            var cities = addresses.Select(a => (a as JsonObject)!["city"]?.GetValue<string>()).ToList();
            cities.Should().Contain("Springfield");
            cities.Should().Contain("Peoria");
            cities.Should().NotContain("Chicago");
        }
    }

    // =======================================================================
    //  AC: Nested object filtering
    // =======================================================================

    [TestFixture]
    [Parallelizable]
    public class Given_Nested_Object_With_IncludeOnly : ReadableProfileProjectorTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void SetUp()
        {
            var source = new JsonObject
            {
                ["id"] = "abc-123",
                ["studentUniqueId"] = "STU001",
                ["firstName"] = "John",
                ["birthData"] = new JsonObject
                {
                    ["birthCity"] = "New York",
                    ["birthStateAbbreviation"] = "NY",
                    ["birthDate"] = "2000-01-01",
                },
            };

            var contentType = CreateContentType(
                MemberSelection.IncludeOnly,
                properties: [new PropertyRule("firstName")],
                objects:
                [
                    new ObjectRule(
                        Name: "birthData",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("birthCity")],
                        NestedObjects: null,
                        Collections: null,
                        Extensions: null
                    ),
                ]
            );

            _result = _projector.Project(source, contentType, IdentityNames("studentUniqueId"));
        }

        [Test]
        public void It_preserves_included_nested_property()
        {
            var birthData = _result["birthData"] as JsonObject;
            birthData!["birthCity"]?.GetValue<string>().Should().Be("New York");
        }

        [Test]
        public void It_removes_excluded_nested_properties()
        {
            var birthData = _result["birthData"] as JsonObject;
            birthData!["birthStateAbbreviation"].Should().BeNull();
            birthData["birthDate"].Should().BeNull();
        }
    }

    // =======================================================================
    //  AC: Nested collection filtering
    // =======================================================================

    [TestFixture]
    [Parallelizable]
    public class Given_Nested_Collection_Within_Collection : ReadableProfileProjectorTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void SetUp()
        {
            var source = new JsonObject
            {
                ["id"] = "abc-123",
                ["schoolId"] = 100,
                ["addresses"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["streetAddress"] = "123 Main St",
                        ["city"] = "Springfield",
                        ["periods"] = new JsonArray
                        {
                            new JsonObject { ["beginDate"] = "2024-01-01", ["endDate"] = "2024-12-31" },
                        },
                    },
                },
            };

            var contentType = CreateContentType(
                MemberSelection.IncludeAll,
                collections:
                [
                    new CollectionRule(
                        Name: "addresses",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("streetAddress")],
                        NestedObjects: null,
                        NestedCollections:
                        [
                            new CollectionRule(
                                Name: "periods",
                                MemberSelection: MemberSelection.IncludeOnly,
                                LogicalSchema: null,
                                Properties: [new PropertyRule("beginDate")],
                                NestedObjects: null,
                                NestedCollections: null,
                                Extensions: null,
                                ItemFilter: null
                            ),
                        ],
                        Extensions: null,
                        ItemFilter: null
                    ),
                ]
            );

            _result = _projector.Project(source, contentType, IdentityNames("schoolId"));
        }

        [Test]
        public void It_filters_parent_collection_item_scalars()
        {
            var address = (_result["addresses"] as JsonArray)![0] as JsonObject;
            address!["streetAddress"]?.GetValue<string>().Should().Be("123 Main St");
            address["city"].Should().BeNull();
        }

        [Test]
        public void It_filters_nested_collection_item_scalars()
        {
            var period =
                (((_result["addresses"] as JsonArray)![0] as JsonObject)!["periods"] as JsonArray)![0]
                as JsonObject;
            period!["beginDate"]?.GetValue<string>().Should().Be("2024-01-01");
            period["endDate"].Should().BeNull();
        }
    }

    // =======================================================================
    //  AC: Identity properties always preserved
    // =======================================================================

    [TestFixture]
    [Parallelizable]
    public class Given_Identity_Fields_Not_In_Profile : ReadableProfileProjectorTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void SetUp()
        {
            var source = new JsonObject
            {
                ["id"] = "abc-123",
                ["schoolId"] = 100,
                ["nameOfInstitution"] = "Test School",
            };

            // IncludeOnly with only nameOfInstitution — schoolId is identity, not listed
            var contentType = CreateContentType(
                MemberSelection.IncludeOnly,
                properties: [new PropertyRule("nameOfInstitution")]
            );

            _result = _projector.Project(source, contentType, IdentityNames("schoolId"));
        }

        [Test]
        public void It_always_preserves_id_field()
        {
            _result["id"]?.GetValue<string>().Should().Be("abc-123");
        }

        [Test]
        public void It_always_preserves_identity_field()
        {
            _result["schoolId"]?.GetValue<int>().Should().Be(100);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Nested_Identity_Paths : ReadableProfileProjectorTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void SetUp()
        {
            var source = new JsonObject
            {
                ["id"] = "abc-123",
                ["calendarCode"] = "CAL001",
                ["schoolReference"] = new JsonObject { ["schoolId"] = 100 },
                ["schoolYearTypeReference"] = new JsonObject { ["schoolYear"] = 2030 },
                ["nonIdentityField"] = "should be stripped",
            };

            var contentType = CreateContentType(
                MemberSelection.IncludeOnly,
                properties: [new PropertyRule("calendarCode")]
            );

            var identityNames = ReadableProfileProjector.ExtractIdentityPropertyNames([
                new JsonPath("$.calendarCode"),
                new JsonPath("$.schoolReference.schoolId"),
                new JsonPath("$.schoolYearTypeReference.schoolYear"),
            ]);

            _result = _projector.Project(source, contentType, identityNames);
        }

        [Test]
        public void It_preserves_reference_objects_containing_identity()
        {
            var schoolRef = _result["schoolReference"] as JsonObject;
            schoolRef.Should().NotBeNull();
            schoolRef!["schoolId"]?.GetValue<int>().Should().Be(100);

            var yearRef = _result["schoolYearTypeReference"] as JsonObject;
            yearRef.Should().NotBeNull();
            yearRef!["schoolYear"]?.GetValue<int>().Should().Be(2030);
        }

        [Test]
        public void It_strips_non_identity_fields()
        {
            _result["nonIdentityField"].Should().BeNull();
        }
    }

    // =======================================================================
    //  AC: Projector does not alter the input document
    // =======================================================================

    [TestFixture]
    [Parallelizable]
    public class Given_Projection_Preserves_Input_Immutability : ReadableProfileProjectorTests
    {
        private JsonObject _source = null!;
        private string _sourceBeforeProjection = null!;

        [SetUp]
        public void SetUp()
        {
            _source = new JsonObject
            {
                ["id"] = "abc-123",
                ["schoolId"] = 100,
                ["nameOfInstitution"] = "Test School",
                ["webSite"] = "https://example.com",
                ["addresses"] = new JsonArray
                {
                    new JsonObject { ["streetAddress"] = "123 Main St", ["city"] = "Springfield" },
                },
                ["_ext"] = new JsonObject { ["sample"] = new JsonObject { ["field1"] = "value1" } },
            };

            _sourceBeforeProjection = _source.ToJsonString();

            var contentType = CreateContentType(
                MemberSelection.IncludeOnly,
                properties: [new PropertyRule("nameOfInstitution")]
            );

            _ = _projector.Project(_source, contentType, IdentityNames("schoolId"));
        }

        [Test]
        public void It_does_not_modify_the_source_document()
        {
            _source.ToJsonString().Should().Be(_sourceBeforeProjection);
        }
    }

    // =======================================================================
    //  AC: Full round-trip correctness
    // =======================================================================

    [TestFixture]
    [Parallelizable]
    public class Given_Complex_Reconstituted_Document : ReadableProfileProjectorTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void SetUp()
        {
            var source = new JsonObject
            {
                ["id"] = "doc-uuid-001",
                ["studentUniqueId"] = "STU-12345",
                ["firstName"] = "Jane",
                ["lastSurname"] = "Doe",
                ["birthDate"] = "2010-05-15",
                ["citizenshipStatusDescriptor"] = "uri://ed-fi.org/CitizenshipStatusDescriptor#US",
                ["schoolReference"] = new JsonObject { ["schoolId"] = 255901 },
                ["birthData"] = new JsonObject
                {
                    ["birthCity"] = "Austin",
                    ["birthStateAbbreviation"] = "TX",
                    ["birthCountryDescriptor"] = "uri://ed-fi.org/CountryDescriptor#US",
                },
                ["addresses"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Home",
                        ["streetAddress"] = "100 Elm St",
                        ["city"] = "Austin",
                        ["stateAbbreviation"] = "TX",
                        ["postalCode"] = "78701",
                    },
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
                        ["streetAddress"] = "PO Box 999",
                        ["city"] = "Austin",
                        ["stateAbbreviation"] = "TX",
                        ["postalCode"] = "78702",
                    },
                },
                ["telephones"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["telephoneNumber"] = "555-0100",
                        ["telephoneNumberTypeDescriptor"] =
                            "uri://ed-fi.org/TelephoneNumberTypeDescriptor#Home",
                    },
                },
                ["_ext"] = new JsonObject
                {
                    ["sample"] = new JsonObject { ["favoriteColor"] = "blue", ["nickname"] = "JD" },
                    ["tpdm"] = new JsonObject
                    {
                        ["genderIdentity"] = "Female",
                        ["economicDisadvantaged"] = false,
                    },
                },
            };

            var contentType = CreateContentType(
                MemberSelection.IncludeOnly,
                properties: [new PropertyRule("firstName"), new PropertyRule("lastSurname")],
                objects:
                [
                    new ObjectRule(
                        Name: "birthData",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("birthCity")],
                        NestedObjects: null,
                        Collections: null,
                        Extensions: null
                    ),
                ],
                collections:
                [
                    new CollectionRule(
                        Name: "addresses",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties:
                        [
                            new PropertyRule("addressTypeDescriptor"),
                            new PropertyRule("streetAddress"),
                            new PropertyRule("city"),
                        ],
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: new CollectionItemFilter(
                            PropertyName: "addressTypeDescriptor",
                            FilterMode: FilterMode.IncludeOnly,
                            Values: ["uri://ed-fi.org/AddressTypeDescriptor#Home"]
                        )
                    ),
                ],
                extensions:
                [
                    new ExtensionRule(
                        Name: "sample",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("favoriteColor")],
                        Objects: null,
                        Collections: null
                    ),
                ]
            );

            var identityNames = ReadableProfileProjector.ExtractIdentityPropertyNames([
                new JsonPath("$.studentUniqueId"),
                new JsonPath("$.schoolReference.schoolId"),
            ]);

            _result = _projector.Project(source, contentType, identityNames);
        }

        [Test]
        public void It_preserves_id_and_identity_fields()
        {
            _result["id"]?.GetValue<string>().Should().Be("doc-uuid-001");
            _result["studentUniqueId"]?.GetValue<string>().Should().Be("STU-12345");
            var schoolRef = _result["schoolReference"] as JsonObject;
            schoolRef!["schoolId"]?.GetValue<int>().Should().Be(255901);
        }

        [Test]
        public void It_includes_profiled_scalars()
        {
            _result["firstName"]?.GetValue<string>().Should().Be("Jane");
            _result["lastSurname"]?.GetValue<string>().Should().Be("Doe");
        }

        [Test]
        public void It_removes_hidden_scalars()
        {
            _result["birthDate"].Should().BeNull();
            _result["citizenshipStatusDescriptor"].Should().BeNull();
        }

        [Test]
        public void It_filters_nested_object()
        {
            var birthData = _result["birthData"] as JsonObject;
            birthData!["birthCity"]?.GetValue<string>().Should().Be("Austin");
            birthData["birthStateAbbreviation"].Should().BeNull();
            birthData["birthCountryDescriptor"].Should().BeNull();
        }

        [Test]
        public void It_filters_collection_by_item_filter_and_member_selection()
        {
            var addresses = _result["addresses"] as JsonArray;
            addresses!.Count.Should().Be(1);

            var home = addresses[0] as JsonObject;
            home!
                ["addressTypeDescriptor"]
                ?.GetValue<string>()
                .Should()
                .Be("uri://ed-fi.org/AddressTypeDescriptor#Home");
            home["streetAddress"]?.GetValue<string>().Should().Be("100 Elm St");
            home["city"]?.GetValue<string>().Should().Be("Austin");
            home["stateAbbreviation"].Should().BeNull();
            home["postalCode"].Should().BeNull();
        }

        [Test]
        public void It_removes_hidden_collection()
        {
            _result["telephones"].Should().BeNull();
        }

        [Test]
        public void It_filters_extension_per_rule()
        {
            var ext = _result["_ext"] as JsonObject;
            ext.Should().NotBeNull();

            var sample = ext!["sample"] as JsonObject;
            sample!["favoriteColor"]?.GetValue<string>().Should().Be("blue");
            sample["nickname"].Should().BeNull();
        }

        [Test]
        public void It_removes_hidden_extension_namespace()
        {
            var ext = _result["_ext"] as JsonObject;
            ext!["tpdm"].Should().BeNull();
        }
    }

    // =======================================================================
    //  ExtractIdentityPropertyNames helper
    // =======================================================================

    [TestFixture]
    [Parallelizable]
    public class Given_ExtractIdentityPropertyNames_With_Mixed_Paths : ReadableProfileProjectorTests
    {
        private IReadOnlySet<string> _result = null!;

        [SetUp]
        public void SetUp()
        {
            _result = ReadableProfileProjector.ExtractIdentityPropertyNames([
                new JsonPath("$.schoolId"),
                new JsonPath("$.courseOfferingReference.localCourseCode"),
                new JsonPath("$.sessionReference.sessionName"),
            ]);
        }

        [Test]
        public void It_extracts_simple_property_name()
        {
            _result.Should().Contain("schoolId");
        }

        [Test]
        public void It_extracts_root_segment_from_nested_paths()
        {
            _result.Should().Contain("courseOfferingReference");
            _result.Should().Contain("sessionReference");
        }

        [Test]
        public void It_returns_exactly_three_names()
        {
            _result.Count.Should().Be(3);
        }
    }
}
