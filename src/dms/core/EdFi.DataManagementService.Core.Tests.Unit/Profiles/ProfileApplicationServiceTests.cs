// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profiles;
using EdFi.DataManagementService.Core.Profiles.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profiles;

[TestFixture]
[Parallelizable]
public class ProfileApplicationServiceTests
{
    private ProfileApplicationService _service = null!;

    [SetUp]
    public void Setup()
    {
        _service = new ProfileApplicationService(NullLogger<ProfileApplicationService>.Instance);
    }

    [TestFixture]
    [Parallelizable]
    public class ExcludeOnlyForProperties : ProfileApplicationServiceTests
    {
        [Test]
        public void Should_remove_excluded_property()
        {
            // Arrange
            var document = JsonNode.Parse(@"{
                ""firstName"": ""John"",
                ""lastName"": ""Doe"",
                ""birthDate"": ""2000-01-01""
            }");

            var contentType = new ContentType(
                MemberSelection.ExcludeOnly,
                [new ProfileProperty("birthDate")],
                []
            );

            // Act
            var filtered = _service.ApplyFilter(document, contentType);

            // Assert
            var obj = filtered!.AsObject();
            obj.Should().ContainKey("firstName");
            obj.Should().ContainKey("lastName");
            obj.Should().NotContainKey("birthDate");
        }

        [Test]
        public void Should_keep_properties_not_in_exclude_list()
        {
            // Arrange
            var document = JsonNode.Parse(@"{
                ""schoolId"": 123,
                ""nameOfInstitution"": ""Test School"",
                ""webSite"": ""http://test.com"",
                ""shortNameOfInstitution"": ""Test""
            }");

            var contentType = new ContentType(
                MemberSelection.ExcludeOnly,
                [
                    new ProfileProperty("nameOfInstitution"),
                    new ProfileProperty("webSite")
                ],
                []
            );

            // Act
            var filtered = _service.ApplyFilter(document, contentType);

            // Assert
            var obj = filtered!.AsObject();
            obj.Should().ContainKey("schoolId");
            obj.Should().ContainKey("shortNameOfInstitution");
            obj.Should().NotContainKey("nameOfInstitution");
            obj.Should().NotContainKey("webSite");
        }

        [Test]
        public void Should_be_case_insensitive()
        {
            // Arrange
            var document = JsonNode.Parse(@"{
                ""BirthDate"": ""2000-01-01"",
                ""firstName"": ""John""
            }");

            var contentType = new ContentType(
                MemberSelection.ExcludeOnly,
                [new ProfileProperty("birthdate")],
                []
            );

            // Act
            var filtered = _service.ApplyFilter(document, contentType);

            // Assert
            var obj = filtered!.AsObject();
            obj.Should().ContainKey("firstName");
            obj.Should().NotContainKey("BirthDate");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class IncludeOnlyForProperties : ProfileApplicationServiceTests
    {
        [Test]
        public void Should_keep_only_included_properties()
        {
            // Arrange
            var document = JsonNode.Parse(@"{
                ""firstName"": ""John"",
                ""lastName"": ""Doe"",
                ""birthDate"": ""2000-01-01"",
                ""email"": ""john@example.com""
            }");

            var contentType = new ContentType(
                MemberSelection.IncludeOnly,
                [
                    new ProfileProperty("firstName"),
                    new ProfileProperty("lastName")
                ],
                []
            );

            // Act
            var filtered = _service.ApplyFilter(document, contentType);

            // Assert
            var obj = filtered!.AsObject();
            obj.Should().ContainKey("firstName");
            obj.Should().ContainKey("lastName");
            obj.Should().NotContainKey("birthDate");
            obj.Should().NotContainKey("email");
        }

        [Test]
        public void Should_remove_all_if_none_included()
        {
            // Arrange
            var document = JsonNode.Parse(@"{
                ""firstName"": ""John"",
                ""lastName"": ""Doe""
            }");

            var contentType = new ContentType(
                MemberSelection.IncludeOnly,
                [],
                []
            );

            // Act
            var filtered = _service.ApplyFilter(document, contentType);

            // Assert
            var obj = filtered!.AsObject();
            obj.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class ExcludeOnlyForCollections : ProfileApplicationServiceTests
    {
        [Test]
        public void Should_remove_excluded_collections()
        {
            // Arrange
            var document = JsonNode.Parse(@"{
                ""schoolId"": 123,
                ""addresses"": [
                    {""street"": ""123 Main St""}
                ],
                ""gradeLevels"": [
                    {""gradeLevel"": ""1""}
                ],
                ""programs"": [
                    {""name"": ""Math""}
                ]
            }");

            var contentType = new ContentType(
                MemberSelection.ExcludeOnly,
                [],
                [
                    new ProfileCollection("addresses", MemberSelection.IncludeAll),
                    new ProfileCollection("programs", MemberSelection.IncludeAll)
                ]
            );

            // Act
            var filtered = _service.ApplyFilter(document, contentType);

            // Assert
            var obj = filtered!.AsObject();
            obj.Should().ContainKey("schoolId");
            obj.Should().ContainKey("gradeLevels");
            obj.Should().NotContainKey("addresses");
            obj.Should().NotContainKey("programs");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class IncludeOnlyForCollections : ProfileApplicationServiceTests
    {
        [Test]
        public void Should_keep_only_included_collections()
        {
            // Arrange
            var document = JsonNode.Parse(@"{
                ""schoolId"": 123,
                ""addresses"": [
                    {""street"": ""123 Main St""}
                ],
                ""gradeLevels"": [
                    {""gradeLevel"": ""1""}
                ],
                ""programs"": [
                    {""name"": ""Math""}
                ]
            }");

            var contentType = new ContentType(
                MemberSelection.IncludeOnly,
                [],
                [
                    new ProfileCollection("addresses", MemberSelection.IncludeAll),
                    new ProfileCollection("gradeLevels", MemberSelection.IncludeAll)
                ]
            );

            // Act
            var filtered = _service.ApplyFilter(document, contentType);

            // Assert
            var obj = filtered!.AsObject();
            obj.Should().NotContainKey("schoolId");
            obj.Should().ContainKey("addresses");
            obj.Should().ContainKey("gradeLevels");
            obj.Should().NotContainKey("programs");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class MixedPropertiesAndCollections : ProfileApplicationServiceTests
    {
        [Test]
        public void Should_apply_include_only_to_both_properties_and_collections()
        {
            // Arrange
            var document = JsonNode.Parse(@"{
                ""schoolId"": 123,
                ""name"": ""Test School"",
                ""addresses"": [
                    {""street"": ""123 Main St""}
                ],
                ""gradeLevels"": [
                    {""gradeLevel"": ""1""}
                ]
            }");

            var contentType = new ContentType(
                MemberSelection.IncludeOnly,
                [new ProfileProperty("name")],
                [new ProfileCollection("addresses", MemberSelection.IncludeAll)]
            );

            // Act
            var filtered = _service.ApplyFilter(document, contentType);

            // Assert
            var obj = filtered!.AsObject();
            obj.Should().NotContainKey("schoolId");
            obj.Should().ContainKey("name");
            obj.Should().ContainKey("addresses");
            obj.Should().NotContainKey("gradeLevels");
        }

        [Test]
        public void Should_apply_exclude_only_to_both_properties_and_collections()
        {
            // Arrange
            var document = JsonNode.Parse(@"{
                ""schoolId"": 123,
                ""name"": ""Test School"",
                ""addresses"": [
                    {""street"": ""123 Main St""}
                ],
                ""gradeLevels"": [
                    {""gradeLevel"": ""1""}
                ]
            }");

            var contentType = new ContentType(
                MemberSelection.ExcludeOnly,
                [new ProfileProperty("name")],
                [new ProfileCollection("gradeLevels", MemberSelection.IncludeAll)]
            );

            // Act
            var filtered = _service.ApplyFilter(document, contentType);

            // Assert
            var obj = filtered!.AsObject();
            obj.Should().ContainKey("schoolId");
            obj.Should().NotContainKey("name");
            obj.Should().ContainKey("addresses");
            obj.Should().NotContainKey("gradeLevels");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class EdgeCases : ProfileApplicationServiceTests
    {
        [Test]
        public void Should_return_null_for_null_document()
        {
            // Arrange
            var contentType = new ContentType(
                MemberSelection.ExcludeOnly,
                [],
                []
            );

            // Act
            var filtered = _service.ApplyFilter(null, contentType);

            // Assert
            filtered.Should().BeNull();
        }

        [Test]
        public void Should_not_modify_original_document()
        {
            // Arrange
            var document = JsonNode.Parse(@"{
                ""firstName"": ""John"",
                ""birthDate"": ""2000-01-01""
            }");

            var contentType = new ContentType(
                MemberSelection.ExcludeOnly,
                [new ProfileProperty("birthDate")],
                []
            );

            // Act
            _service.ApplyFilter(document, contentType);

            // Assert - original should still have birthDate
            document!.AsObject().Should().ContainKey("birthDate");
        }
    }
}
