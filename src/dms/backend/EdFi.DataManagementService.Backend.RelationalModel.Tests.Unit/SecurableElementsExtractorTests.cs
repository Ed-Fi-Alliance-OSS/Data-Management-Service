// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_SecurableElementsExtractor
{
    private static readonly QualifiedResourceName _resource = new("Ed-Fi", "TestResource");

    [TestFixture]
    public class Given_no_securableElements_node
    {
        [Test]
        public void It_should_return_empty()
        {
            var schema = new JsonObject { ["otherProperty"] = "value" };

            var result = SecurableElementsExtractor.ExtractSecurableElements(schema, _resource);

            result.Should().Be(ResourceSecurableElements.Empty);
        }
    }

    [TestFixture]
    public class Given_valid_securableElements
    {
        [Test]
        public void It_should_extract_all_element_types()
        {
            var schema = new JsonObject
            {
                ["securableElements"] = new JsonObject
                {
                    ["EducationOrganization"] = new JsonArray(
                        new JsonObject
                        {
                            ["jsonPath"] = "$.schoolReference.schoolId",
                            ["metaEdName"] = "SchoolId",
                        }
                    ),
                    ["Namespace"] = new JsonArray(JsonValue.Create("$.namespace")),
                    ["Student"] = new JsonArray(JsonValue.Create("$.studentReference.studentUniqueId")),
                    ["Contact"] = new JsonArray(JsonValue.Create("$.contactReference.contactUniqueId")),
                    ["Staff"] = new JsonArray(JsonValue.Create("$.staffReference.staffUniqueId")),
                },
            };

            var result = SecurableElementsExtractor.ExtractSecurableElements(schema, _resource);

            result.EducationOrganization.Should().HaveCount(1);
            result.EducationOrganization[0].JsonPath.Should().Be("$.schoolReference.schoolId");
            result.EducationOrganization[0].MetaEdName.Should().Be("SchoolId");
            result.Namespace.Should().Equal("$.namespace");
            result.Student.Should().Equal("$.studentReference.studentUniqueId");
            result.Contact.Should().Equal("$.contactReference.contactUniqueId");
            result.Staff.Should().Equal("$.staffReference.staffUniqueId");
        }
    }

    [TestFixture]
    public class Given_null_EducationOrganization_array_item
    {
        [Test]
        public void It_should_throw_with_descriptive_message()
        {
            var schema = new JsonObject
            {
                ["securableElements"] = new JsonObject
                {
                    ["EducationOrganization"] = new JsonArray((JsonNode?)null),
                },
            };

            var act = () => SecurableElementsExtractor.ExtractSecurableElements(schema, _resource);

            act.Should().Throw<InvalidOperationException>().WithMessage("*EducationOrganization[0] is null*");
        }
    }

    [TestFixture]
    public class Given_missing_jsonPath_in_EdOrg_item
    {
        [Test]
        public void It_should_throw_with_descriptive_message()
        {
            var schema = new JsonObject
            {
                ["securableElements"] = new JsonObject
                {
                    ["EducationOrganization"] = new JsonArray(new JsonObject { ["metaEdName"] = "SchoolId" }),
                },
            };

            var act = () => SecurableElementsExtractor.ExtractSecurableElements(schema, _resource);

            act.Should().Throw<InvalidOperationException>().WithMessage("*jsonPath is missing*");
        }
    }

    [TestFixture]
    public class Given_missing_metaEdName_in_EdOrg_item
    {
        [Test]
        public void It_should_throw_with_descriptive_message()
        {
            var schema = new JsonObject
            {
                ["securableElements"] = new JsonObject
                {
                    ["EducationOrganization"] = new JsonArray(
                        new JsonObject { ["jsonPath"] = "$.schoolReference.schoolId" }
                    ),
                },
            };

            var act = () => SecurableElementsExtractor.ExtractSecurableElements(schema, _resource);

            act.Should().Throw<InvalidOperationException>().WithMessage("*metaEdName is missing*");
        }
    }

    [TestFixture]
    public class Given_null_string_path_item
    {
        [Test]
        public void It_should_throw_for_null_Student_path()
        {
            var schema = new JsonObject
            {
                ["securableElements"] = new JsonObject { ["Student"] = new JsonArray((JsonNode?)null) },
            };

            var act = () => SecurableElementsExtractor.ExtractSecurableElements(schema, _resource);

            act.Should().Throw<InvalidOperationException>().WithMessage("*Student[0] is null*");
        }

        [Test]
        public void It_should_throw_for_null_Namespace_path()
        {
            var schema = new JsonObject
            {
                ["securableElements"] = new JsonObject { ["Namespace"] = new JsonArray((JsonNode?)null) },
            };

            var act = () => SecurableElementsExtractor.ExtractSecurableElements(schema, _resource);

            act.Should().Throw<InvalidOperationException>().WithMessage("*Namespace[0] is null*");
        }
    }
}
