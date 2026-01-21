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
public class ProfileResponseFilterTests
{
    private static readonly IProfileResponseFilter Filter = new ProfileResponseFilter();

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

    [TestFixture]
    [Parallelizable]
    public class Given_IncludeOnly_Member_Selection : ProfileResponseFilterTests
    {
        private JsonNode? _result;
        private JsonObject _source = null!;

        [SetUp]
        public void Setup()
        {
            _source = new JsonObject
            {
                ["id"] = "12345",
                ["schoolId"] = 100,
                ["nameOfInstitution"] = "Test School",
                ["shortNameOfInstitution"] = "TS",
                ["webSite"] = "https://example.com",
            };

            var contentType = CreateContentType(
                MemberSelection.IncludeOnly,
                properties: [new PropertyRule("nameOfInstitution")]
            );

            var identityPropertyNames = Filter.ExtractIdentityPropertyNames([new JsonPath("$.schoolId")]);
            _result = Filter.FilterDocument(_source, contentType, identityPropertyNames);
        }

        [Test]
        public void It_includes_specified_properties()
        {
            _result!["nameOfInstitution"]?.GetValue<string>().Should().Be("Test School");
        }

        [Test]
        public void It_excludes_unspecified_properties()
        {
            _result!["shortNameOfInstitution"].Should().BeNull();
            _result!["webSite"].Should().BeNull();
        }

        [Test]
        public void It_always_includes_id_field()
        {
            _result!["id"]?.GetValue<string>().Should().Be("12345");
        }

        [Test]
        public void It_always_includes_identity_fields()
        {
            _result!["schoolId"]?.GetValue<int>().Should().Be(100);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_ExcludeOnly_Member_Selection : ProfileResponseFilterTests
    {
        private JsonNode? _result;
        private JsonObject _source = null!;

        [SetUp]
        public void Setup()
        {
            _source = new JsonObject
            {
                ["id"] = "12345",
                ["schoolId"] = 100,
                ["nameOfInstitution"] = "Test School",
                ["shortNameOfInstitution"] = "TS",
                ["webSite"] = "https://example.com",
            };

            var contentType = CreateContentType(
                MemberSelection.ExcludeOnly,
                properties: [new PropertyRule("webSite"), new PropertyRule("shortNameOfInstitution")]
            );

            var identityPropertyNames = Filter.ExtractIdentityPropertyNames([new JsonPath("$.schoolId")]);
            _result = Filter.FilterDocument(_source, contentType, identityPropertyNames);
        }

        [Test]
        public void It_excludes_specified_properties()
        {
            _result!["shortNameOfInstitution"].Should().BeNull();
            _result!["webSite"].Should().BeNull();
        }

        [Test]
        public void It_includes_unspecified_properties()
        {
            _result!["nameOfInstitution"]?.GetValue<string>().Should().Be("Test School");
        }

        [Test]
        public void It_always_includes_id_field()
        {
            _result!["id"]?.GetValue<string>().Should().Be("12345");
        }

        [Test]
        public void It_always_includes_identity_fields()
        {
            _result!["schoolId"]?.GetValue<int>().Should().Be(100);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_IncludeAll_Member_Selection : ProfileResponseFilterTests
    {
        private JsonNode? _result;
        private JsonObject _source = null!;

        [SetUp]
        public void Setup()
        {
            _source = new JsonObject
            {
                ["id"] = "12345",
                ["schoolId"] = 100,
                ["nameOfInstitution"] = "Test School",
                ["shortNameOfInstitution"] = "TS",
            };

            var contentType = CreateContentType(MemberSelection.IncludeAll);

            var identityPropertyNames = Filter.ExtractIdentityPropertyNames([new JsonPath("$.schoolId")]);
            _result = Filter.FilterDocument(_source, contentType, identityPropertyNames);
        }

        [Test]
        public void It_includes_all_properties()
        {
            _result!["id"]?.GetValue<string>().Should().Be("12345");
            _result!["schoolId"]?.GetValue<int>().Should().Be(100);
            _result!["nameOfInstitution"]?.GetValue<string>().Should().Be("Test School");
            _result!["shortNameOfInstitution"]?.GetValue<string>().Should().Be("TS");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Identity_Field_In_ExcludeOnly_Properties : ProfileResponseFilterTests
    {
        private JsonNode? _result;
        private JsonObject _source = null!;

        [SetUp]
        public void Setup()
        {
            _source = new JsonObject
            {
                ["id"] = "12345",
                ["schoolId"] = 100,
                ["nameOfInstitution"] = "Test School",
            };

            // Attempt to exclude identity field
            var contentType = CreateContentType(
                MemberSelection.ExcludeOnly,
                properties: [new PropertyRule("schoolId")]
            );

            var identityPropertyNames = Filter.ExtractIdentityPropertyNames([new JsonPath("$.schoolId")]);
            _result = Filter.FilterDocument(_source, contentType, identityPropertyNames);
        }

        [Test]
        public void It_never_excludes_identity_fields()
        {
            _result!["schoolId"]?.GetValue<int>().Should().Be(100);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Nested_Object_With_Rules : ProfileResponseFilterTests
    {
        private JsonNode? _result;
        private JsonObject _source = null!;

        [SetUp]
        public void Setup()
        {
            _source = new JsonObject
            {
                ["id"] = "12345",
                ["studentUniqueId"] = "STU001",
                ["firstName"] = "John",
                ["lastName"] = "Doe",
                ["birthDate"] = "2000-01-01",
                ["birthData"] = new JsonObject
                {
                    ["birthCity"] = "New York",
                    ["birthStateAbbreviation"] = "NY",
                    ["birthDate"] = "2000-01-01",
                },
            };

            var contentType = CreateContentType(
                MemberSelection.IncludeOnly,
                properties: [new PropertyRule("firstName"), new PropertyRule("lastName")],
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

            var identityPropertyNames = Filter.ExtractIdentityPropertyNames([
                new JsonPath("$.studentUniqueId"),
            ]);
            _result = Filter.FilterDocument(_source, contentType, identityPropertyNames);
        }

        [Test]
        public void It_filters_top_level_properties()
        {
            _result!["firstName"]?.GetValue<string>().Should().Be("John");
            _result!["lastName"]?.GetValue<string>().Should().Be("Doe");
            _result!["birthDate"].Should().BeNull();
        }

        [Test]
        public void It_filters_nested_object_properties()
        {
            var birthData = _result!["birthData"] as JsonObject;
            birthData.Should().NotBeNull();
            birthData!["birthCity"]?.GetValue<string>().Should().Be("New York");
            birthData["birthStateAbbreviation"].Should().BeNull();
            birthData["birthDate"].Should().BeNull();
        }

        [Test]
        public void It_preserves_identity_field()
        {
            _result!["studentUniqueId"]?.GetValue<string>().Should().Be("STU001");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Collection_With_Member_Selection : ProfileResponseFilterTests
    {
        private JsonNode? _result;
        private JsonObject _source = null!;

        [SetUp]
        public void Setup()
        {
            _source = new JsonObject
            {
                ["id"] = "12345",
                ["schoolId"] = 100,
                ["addresses"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["streetAddress"] = "123 Main St",
                        ["city"] = "Springfield",
                        ["stateAbbreviation"] = "IL",
                        ["postalCode"] = "62701",
                    },
                    new JsonObject
                    {
                        ["streetAddress"] = "456 Oak Ave",
                        ["city"] = "Chicago",
                        ["stateAbbreviation"] = "IL",
                        ["postalCode"] = "60601",
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
                        Properties: [new PropertyRule("streetAddress"), new PropertyRule("city")],
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: null
                    ),
                ]
            );

            var identityPropertyNames = Filter.ExtractIdentityPropertyNames([new JsonPath("$.schoolId")]);
            _result = Filter.FilterDocument(_source, contentType, identityPropertyNames);
        }

        [Test]
        public void It_filters_collection_item_properties()
        {
            var addresses = _result!["addresses"] as JsonArray;
            addresses.Should().NotBeNull();
            addresses!.Count.Should().Be(2);

            var firstAddress = addresses[0] as JsonObject;
            firstAddress!["streetAddress"]?.GetValue<string>().Should().Be("123 Main St");
            firstAddress["city"]?.GetValue<string>().Should().Be("Springfield");
            firstAddress["stateAbbreviation"].Should().BeNull();
            firstAddress["postalCode"].Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Collection_With_ItemFilter_IncludeOnly : ProfileResponseFilterTests
    {
        private JsonNode? _result;
        private JsonObject _source = null!;

        [SetUp]
        public void Setup()
        {
            _source = new JsonObject
            {
                ["id"] = "12345",
                ["schoolId"] = 100,
                ["addresses"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Home",
                        ["streetAddress"] = "123 Main St",
                        ["city"] = "Springfield",
                    },
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
                        ["streetAddress"] = "456 Oak Ave",
                        ["city"] = "Chicago",
                    },
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Work",
                        ["streetAddress"] = "789 Business Rd",
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
                            FilterMode: FilterMode.IncludeOnly,
                            Values: ["uri://ed-fi.org/AddressTypeDescriptor#Home"]
                        )
                    ),
                ]
            );

            var identityPropertyNames = Filter.ExtractIdentityPropertyNames([new JsonPath("$.schoolId")]);
            _result = Filter.FilterDocument(_source, contentType, identityPropertyNames);
        }

        [Test]
        public void It_includes_only_matching_items()
        {
            var addresses = _result!["addresses"] as JsonArray;
            addresses.Should().NotBeNull();
            addresses!.Count.Should().Be(1);

            var address = addresses[0] as JsonObject;
            address!
                ["addressTypeDescriptor"]
                ?.GetValue<string>()
                .Should()
                .Be("uri://ed-fi.org/AddressTypeDescriptor#Home");
            address["city"]?.GetValue<string>().Should().Be("Springfield");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Collection_With_ItemFilter_ExcludeOnly : ProfileResponseFilterTests
    {
        private JsonNode? _result;
        private JsonObject _source = null!;

        [SetUp]
        public void Setup()
        {
            _source = new JsonObject
            {
                ["id"] = "12345",
                ["schoolId"] = 100,
                ["addresses"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Home",
                        ["streetAddress"] = "123 Main St",
                    },
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
                        ["streetAddress"] = "456 Oak Ave",
                    },
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Work",
                        ["streetAddress"] = "789 Business Rd",
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

            var identityPropertyNames = Filter.ExtractIdentityPropertyNames([new JsonPath("$.schoolId")]);
            _result = Filter.FilterDocument(_source, contentType, identityPropertyNames);
        }

        [Test]
        public void It_excludes_matching_items()
        {
            var addresses = _result!["addresses"] as JsonArray;
            addresses.Should().NotBeNull();
            addresses!.Count.Should().Be(2);

            var descriptors = addresses
                .Select(a => (a as JsonObject)?["addressTypeDescriptor"]?.GetValue<string>())
                .ToList();

            descriptors.Should().Contain("uri://ed-fi.org/AddressTypeDescriptor#Home");
            descriptors.Should().Contain("uri://ed-fi.org/AddressTypeDescriptor#Work");
            descriptors.Should().NotContain("uri://ed-fi.org/AddressTypeDescriptor#Mailing");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extensions_With_Rules : ProfileResponseFilterTests
    {
        private JsonNode? _result;
        private JsonObject _source = null!;

        [SetUp]
        public void Setup()
        {
            _source = new JsonObject
            {
                ["id"] = "12345",
                ["schoolId"] = 100,
                ["nameOfInstitution"] = "Test School",
                ["_ext"] = new JsonObject
                {
                    ["sample"] = new JsonObject
                    {
                        ["sampleField1"] = "value1",
                        ["sampleField2"] = "value2",
                        ["sampleField3"] = "value3",
                    },
                    ["tpdm"] = new JsonObject
                    {
                        ["tpdmField1"] = "tpdmValue1",
                        ["tpdmField2"] = "tpdmValue2",
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
                        Properties: [new PropertyRule("sampleField1")],
                        Objects: null,
                        Collections: null
                    ),
                ]
            );

            var identityPropertyNames = Filter.ExtractIdentityPropertyNames([new JsonPath("$.schoolId")]);
            _result = Filter.FilterDocument(_source, contentType, identityPropertyNames);
        }

        [Test]
        public void It_filters_extension_properties_according_to_rules()
        {
            var ext = _result!["_ext"] as JsonObject;
            ext.Should().NotBeNull();

            var sample = ext!["sample"] as JsonObject;
            sample.Should().NotBeNull();
            sample!["sampleField1"]?.GetValue<string>().Should().Be("value1");
            sample["sampleField2"].Should().BeNull();
            sample["sampleField3"].Should().BeNull();
        }

        [Test]
        public void It_excludes_extensions_without_rules_when_IncludeAll()
        {
            var ext = _result!["_ext"] as JsonObject;
            // With IncludeAll at top level, extensions without explicit rules are included
            ext!["tpdm"].Should().NotBeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extensions_With_IncludeOnly_At_Top_Level : ProfileResponseFilterTests
    {
        private JsonNode? _result;
        private JsonObject _source = null!;

        [SetUp]
        public void Setup()
        {
            _source = new JsonObject
            {
                ["id"] = "12345",
                ["schoolId"] = 100,
                ["_ext"] = new JsonObject
                {
                    ["sample"] = new JsonObject { ["sampleField1"] = "value1" },
                    ["tpdm"] = new JsonObject { ["tpdmField1"] = "tpdmValue1" },
                },
            };

            // IncludeOnly at top level with only sample extension rule
            var contentType = CreateContentType(
                MemberSelection.IncludeOnly,
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

            var identityPropertyNames = Filter.ExtractIdentityPropertyNames([new JsonPath("$.schoolId")]);
            _result = Filter.FilterDocument(_source, contentType, identityPropertyNames);
        }

        [Test]
        public void It_includes_only_specified_extension_namespaces()
        {
            var ext = _result!["_ext"] as JsonObject;
            ext.Should().NotBeNull();
            ext!["sample"].Should().NotBeNull();
            ext["tpdm"].Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Empty_Extension_After_Filtering : ProfileResponseFilterTests
    {
        private JsonNode? _result;
        private JsonObject _source = null!;

        [SetUp]
        public void Setup()
        {
            _source = new JsonObject
            {
                ["id"] = "12345",
                ["schoolId"] = 100,
                ["_ext"] = new JsonObject { ["sample"] = new JsonObject { ["sampleField1"] = "value1" } },
            };

            // IncludeOnly at top level with no extension rules
            var contentType = CreateContentType(MemberSelection.IncludeOnly);

            var identityPropertyNames = Filter.ExtractIdentityPropertyNames([new JsonPath("$.schoolId")]);
            _result = Filter.FilterDocument(_source, contentType, identityPropertyNames);
        }

        [Test]
        public void It_omits_ext_field_entirely()
        {
            _result!["_ext"].Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Multiple_Identity_Paths : ProfileResponseFilterTests
    {
        private JsonNode? _result;
        private JsonObject _source = null!;

        [SetUp]
        public void Setup()
        {
            _source = new JsonObject
            {
                ["id"] = "12345",
                ["localCourseCode"] = "MATH101",
                ["schoolId"] = 100,
                ["schoolYear"] = 2024,
                ["sessionName"] = "Fall",
                ["courseTitle"] = "Mathematics",
                ["description"] = "Basic math course",
            };

            var contentType = CreateContentType(
                MemberSelection.IncludeOnly,
                properties: [new PropertyRule("courseTitle")]
            );

            var identityPropertyNames = Filter.ExtractIdentityPropertyNames([
                new JsonPath("$.localCourseCode"),
                new JsonPath("$.schoolId"),
                new JsonPath("$.schoolYear"),
                new JsonPath("$.sessionName"),
            ]);
            _result = Filter.FilterDocument(_source, contentType, identityPropertyNames);
        }

        [Test]
        public void It_preserves_all_identity_fields()
        {
            _result!["localCourseCode"]?.GetValue<string>().Should().Be("MATH101");
            _result!["schoolId"]?.GetValue<int>().Should().Be(100);
            _result!["schoolYear"]?.GetValue<int>().Should().Be(2024);
            _result!["sessionName"]?.GetValue<string>().Should().Be("Fall");
        }

        [Test]
        public void It_includes_specified_properties()
        {
            _result!["courseTitle"]?.GetValue<string>().Should().Be("Mathematics");
        }

        [Test]
        public void It_excludes_non_identity_non_specified_properties()
        {
            _result!["description"].Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Nested_Identity_Paths_Are_Ignored : ProfileResponseFilterTests
    {
        private JsonNode? _result;
        private JsonObject _source = null!;

        [SetUp]
        public void Setup()
        {
            _source = new JsonObject
            {
                ["id"] = "12345",
                ["sectionIdentifier"] = "SEC001",
                ["courseOfferingReference"] = new JsonObject
                {
                    ["localCourseCode"] = "MATH101",
                    ["schoolId"] = 100,
                },
                ["otherField"] = "value",
            };

            var contentType = CreateContentType(MemberSelection.IncludeOnly);

            // Nested paths should not cause top-level property protection
            var identityPropertyNames = Filter.ExtractIdentityPropertyNames([
                new JsonPath("$.sectionIdentifier"),
                new JsonPath("$.courseOfferingReference.localCourseCode"),
                new JsonPath("$.courseOfferingReference.schoolId"),
            ]);
            _result = Filter.FilterDocument(_source, contentType, identityPropertyNames);
        }

        [Test]
        public void It_protects_top_level_identity_fields()
        {
            _result!["sectionIdentifier"]?.GetValue<string>().Should().Be("SEC001");
        }

        [Test]
        public void It_does_not_include_nested_references_as_top_level_identity()
        {
            // courseOfferingReference is not in the IncludeOnly properties list
            // and nested paths don't make the parent protected
            _result!["courseOfferingReference"].Should().BeNull();
        }
    }
}
