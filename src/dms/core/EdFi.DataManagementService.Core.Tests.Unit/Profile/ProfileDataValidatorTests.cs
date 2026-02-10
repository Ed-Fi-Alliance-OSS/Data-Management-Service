// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

[TestFixture]
public class ProfileDataValidatorTests
{
    public class Given_ProfileDataValidator
    {
        private ILogger<ProfileDataValidator> _logger = null!;
        private IEffectiveApiSchemaProvider _effectiveApiSchemaProvider = null!;
        private ApiSchemaDocuments _apiSchemaDocuments = null!;

        private static ApiSchemaDocuments CreateSchemaWithProperties(
            string resourceName,
            params string[] propertyNames
        )
        {
            // Create JSON schema with specified properties
            var properties = new JsonObject();
            foreach (var propName in propertyNames)
            {
                properties[propName] = new JsonObject { ["type"] = "string" };
            }

            var jsonSchemaForInsert = new JsonObject { ["type"] = "object", ["properties"] = properties };

            // Build the resource schema with the JSON schema
            var builder = new ApiSchemaBuilder().WithStartProject().WithStartResource(resourceName);

            // Directly modify the current resource node to add the JSON schema
            var apiSchemaDocuments = builder.WithEndResource().WithEndProject().ToApiSchemaDocuments();

            // Get the project schema node and add the JSON schema
            var projectSchemaNode = apiSchemaDocuments
                .GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new(resourceName));

            if (projectSchemaNode is JsonObject resourceObj)
            {
                resourceObj["jsonSchemaForInsert"] = jsonSchemaForInsert;
                // Set first property as identity member
                if (propertyNames.Length > 0)
                {
                    resourceObj["identityJsonPaths"] = new JsonArray { $"$.{propertyNames[0]}" };
                }
            }

            return apiSchemaDocuments;
        }

        [SetUp]
        public void Setup()
        {
            _logger = A.Fake<ILogger<ProfileDataValidator>>();
            _effectiveApiSchemaProvider = A.Fake<IEffectiveApiSchemaProvider>();

            // Create a real ApiSchemaDocuments using ApiSchemaBuilder with a Student resource
            _apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Student")
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);
        }

        [Test]
        public void Should_construct_successfully()
        {
            // Act
            var validator = new ProfileDataValidator(_logger);

            // Assert
            validator.Should().NotBeNull();
        }

        [Test]
        public void Validate_should_return_success_for_empty_profile()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);
            var profileDefinition = new ProfileDefinition("TestProfile", []);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.Should().NotBeNull();
            result.IsValid.Should().BeTrue();
            result.HasErrors.Should().BeFalse();
            result.HasWarnings.Should().BeFalse();
            result.Failures.Should().BeEmpty();
        }

        [Test]
        public void Validate_should_return_success_for_valid_resource()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);
            var resourceProfile = new ResourceProfile("Student", null, null, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeTrue();
            result.HasErrors.Should().BeFalse();
            result.Failures.Should().BeEmpty();
        }

        [Test]
        public void Validate_should_return_error_for_invalid_resource()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);
            var resourceProfile = new ResourceProfile("InvalidResource", null, null, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeFalse();
            result.HasErrors.Should().BeTrue();
            result.Failures.Should().HaveCount(1);
            result.Failures[0].Severity.Should().Be(ValidationSeverity.Error);
            result.Failures[0].ProfileName.Should().Be("TestProfile");
            result.Failures[0].ResourceName.Should().Be("InvalidResource");
            result.Failures[0].Message.Should().Contain("does not exist in the API schema");
        }

        [Test]
        public void Validate_should_check_extension_schemas_for_resource()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create a schema with Student in core and ExtensionResource in an extension
            _apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Student")
                .WithEndResource()
                .WithEndProject()
                .WithStartProject("TestExtension")
                .WithStartResource("ExtensionResource")
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var resourceProfile = new ResourceProfile("ExtensionResource", null, null, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeFalse();
            result.Failures.Should().BeEmpty();
        }

        [Test]
        public void Validate_should_return_success_for_IncludeAll_member_selection()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student having firstName property
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // IncludeAll mode doesn't validate members
            var contentType = new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], []);
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeTrue();
            result.HasErrors.Should().BeFalse();
            result.Failures.Should().BeEmpty();
        }

        [Test]
        public void Validate_should_return_success_for_valid_IncludeOnly_property()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student having firstName property
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile references existing firstName property
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [new PropertyRule("firstName")],
                [],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeTrue();
            result.HasErrors.Should().BeFalse();
            result.Failures.Should().BeEmpty();
        }

        [Test]
        public void Validate_should_return_error_for_invalid_IncludeOnly_property()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student having firstName property
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile references non-existent invalidProperty
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [new PropertyRule("invalidProperty")],
                [],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeFalse();
            result.HasErrors.Should().BeTrue();
            result.Failures.Should().HaveCount(1);
            result.Failures[0].Severity.Should().Be(ValidationSeverity.Error);
            result.Failures[0].ProfileName.Should().Be("TestProfile");
            result.Failures[0].ResourceName.Should().Be("Student");
            result.Failures[0].MemberName.Should().Be("invalidProperty");
            result.Failures[0].Message.Should().Contain("invalidProperty");
            result.Failures[0].Message.Should().Contain("does not exist");
        }

        [Test]
        public void Validate_should_return_error_for_invalid_IncludeOnly_object()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student having firstName property
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile references non-existent object
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [new ObjectRule("invalidObject", MemberSelection.IncludeAll, null, null, null, null, null)],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeFalse();
            result.HasErrors.Should().BeTrue();
            result.Failures.Should().HaveCount(1);
            result.Failures[0].MemberName.Should().Be("invalidObject");
            result.Failures[0].Message.Should().Contain("invalidObject");
        }

        [Test]
        public void Validate_should_return_error_for_invalid_IncludeOnly_collection()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student having firstName property
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile references non-existent collection
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [],
                [
                    new CollectionRule(
                        "invalidCollection",
                        MemberSelection.IncludeAll,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null
                    ),
                ],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeFalse();
            result.HasErrors.Should().BeTrue();
            result.Failures.Should().HaveCount(1);
            result.Failures[0].MemberName.Should().Be("invalidCollection");
            result.Failures[0].Message.Should().Contain("invalidCollection");
        }

        [Test]
        public void Validate_should_succeed_for_valid_ExcludeOnly_with_non_identity_members()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student having firstName and lastName properties, firstName is identity
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName", "lastName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile excludes non-identity member (lastName)
            var contentType = new ContentTypeDefinition(
                MemberSelection.ExcludeOnly,
                [new PropertyRule("lastName")],
                [],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeTrue();
            result.HasErrors.Should().BeFalse();
            result.Failures.Should().BeEmpty();
        }

        [Test]
        public void Validate_should_return_warning_for_ExcludeOnly_excluding_identity_member()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student having firstName property (identity)
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile attempts to exclude identity member
            var contentType = new ContentTypeDefinition(
                MemberSelection.ExcludeOnly,
                [new PropertyRule("firstName")],
                [],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeTrue(); // Warnings don't block
            result.HasErrors.Should().BeFalse();
            result.Failures.Should().HaveCount(1);
            result.Failures[0].Severity.Should().Be(ValidationSeverity.Warning);
            result.Failures[0].ProfileName.Should().Be("TestProfile");
            result.Failures[0].ResourceName.Should().Be("Student");
            result.Failures[0].MemberName.Should().Be("firstName");
            result.Failures[0].Message.Should().Contain("firstName");
            result.Failures[0].Message.Should().Contain("identity member");
            result.Failures[0].Message.Should().Contain("cannot be excluded");
        }

        [Test]
        public void Validate_should_return_warning_for_ExcludeOnly_with_non_existent_member()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student having firstName property
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile excludes non-existent member
            var contentType = new ContentTypeDefinition(
                MemberSelection.ExcludeOnly,
                [new PropertyRule("nonExistentProperty")],
                [],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeTrue(); // Warnings don't block
            result.HasErrors.Should().BeFalse();
            result.Failures.Should().HaveCount(1);
            result.Failures[0].Severity.Should().Be(ValidationSeverity.Warning);
            result.Failures[0].ProfileName.Should().Be("TestProfile");
            result.Failures[0].ResourceName.Should().Be("Student");
            result.Failures[0].MemberName.Should().Be("nonExistentProperty");
            result.Failures[0].Message.Should().Contain("nonExistentProperty");
            result.Failures[0].Message.Should().Contain("does not exist");
        }

        [Test]
        public void Validate_should_return_error_for_IncludeOnly_nested_object_with_nonexistent_property()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student having address object with street and city properties
            var addressProps = new JsonObject
            {
                ["street"] = new JsonObject { ["type"] = "string" },
                ["city"] = new JsonObject { ["type"] = "string" },
            };
            var properties = new JsonObject
            {
                ["firstName"] = new JsonObject { ["type"] = "string" },
                ["address"] = new JsonObject { ["type"] = "object", ["properties"] = addressProps },
            };
            var jsonSchemaForInsert = new JsonObject { ["type"] = "object", ["properties"] = properties };

            var builder = new ApiSchemaBuilder().WithStartProject().WithStartResource("Student");
            _apiSchemaDocuments = builder.WithEndResource().WithEndProject().ToApiSchemaDocuments();
            var projectSchemaNode = _apiSchemaDocuments
                .GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new("Student"));
            if (projectSchemaNode is JsonObject resourceObj)
            {
                resourceObj["jsonSchemaForInsert"] = jsonSchemaForInsert;
                resourceObj["identityJsonPaths"] = new JsonArray { "$.firstName" };
            }
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile includes nested object property that doesn't exist
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [],
                [
                    new ObjectRule(
                        "address",
                        MemberSelection.IncludeOnly,
                        null,
                        [new PropertyRule("nonExistentStreet")],
                        null,
                        null,
                        null
                    ),
                ],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeFalse();
            result.HasErrors.Should().BeTrue();
            result.Failures.Should().HaveCount(1);
            result.Failures[0].Severity.Should().Be(ValidationSeverity.Error);
            result.Failures[0].ProfileName.Should().Be("TestProfile");
            result.Failures[0].ResourceName.Should().Be("Student");
            result.Failures[0].MemberName.Should().Be("address.nonExistentStreet");
            result.Failures[0].Message.Should().Contain("nonExistentStreet");
            result.Failures[0].Message.Should().Contain("does not exist");
        }

        [Test]
        public void Validate_should_return_warning_for_ExcludeOnly_nested_object_with_nonexistent_property()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student having address object with street and city properties
            var addressProps = new JsonObject
            {
                ["street"] = new JsonObject { ["type"] = "string" },
                ["city"] = new JsonObject { ["type"] = "string" },
            };
            var properties = new JsonObject
            {
                ["firstName"] = new JsonObject { ["type"] = "string" },
                ["address"] = new JsonObject { ["type"] = "object", ["properties"] = addressProps },
            };
            var jsonSchemaForInsert = new JsonObject { ["type"] = "object", ["properties"] = properties };

            var builder = new ApiSchemaBuilder().WithStartProject().WithStartResource("Student");
            _apiSchemaDocuments = builder.WithEndResource().WithEndProject().ToApiSchemaDocuments();
            var projectSchemaNode = _apiSchemaDocuments
                .GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new("Student"));
            if (projectSchemaNode is JsonObject resourceObj)
            {
                resourceObj["jsonSchemaForInsert"] = jsonSchemaForInsert;
                resourceObj["identityJsonPaths"] = new JsonArray { "$.firstName" };
            }
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile excludes nested object property that doesn't exist
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [],
                [
                    new ObjectRule(
                        "address",
                        MemberSelection.ExcludeOnly,
                        null,
                        [new PropertyRule("nonExistentStreet")],
                        null,
                        null,
                        null
                    ),
                ],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeTrue(); // Warnings don't block
            result.HasErrors.Should().BeFalse();
            result.Failures.Should().HaveCount(1);
            result.Failures[0].Severity.Should().Be(ValidationSeverity.Warning);
            result.Failures[0].ProfileName.Should().Be("TestProfile");
            result.Failures[0].ResourceName.Should().Be("Student");
            result.Failures[0].MemberName.Should().Be("address.nonExistentStreet");
            result.Failures[0].Message.Should().Contain("nonExistentStreet");
            result.Failures[0].Message.Should().Contain("does not exist");
        }

        [Test]
        public void Validate_should_return_warning_for_ExcludeOnly_nested_object_excluding_identity_member()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student having address object with street and city properties
            var addressProps = new JsonObject
            {
                ["street"] = new JsonObject { ["type"] = "string" },
                ["city"] = new JsonObject { ["type"] = "string" },
            };
            var properties = new JsonObject
            {
                ["firstName"] = new JsonObject { ["type"] = "string" },
                ["address"] = new JsonObject { ["type"] = "object", ["properties"] = addressProps },
            };
            var jsonSchemaForInsert = new JsonObject { ["type"] = "object", ["properties"] = properties };

            var builder = new ApiSchemaBuilder().WithStartProject().WithStartResource("Student");
            _apiSchemaDocuments = builder.WithEndResource().WithEndProject().ToApiSchemaDocuments();
            var projectSchemaNode = _apiSchemaDocuments
                .GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new("Student"));
            if (projectSchemaNode is JsonObject resourceObj)
            {
                resourceObj["jsonSchemaForInsert"] = jsonSchemaForInsert;
                // Make address.street an identity path
                resourceObj["identityJsonPaths"] = new JsonArray { "$.firstName", "$.address.street" };
            }
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile attempts to exclude identity member in nested object
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [],
                [
                    new ObjectRule(
                        "address",
                        MemberSelection.ExcludeOnly,
                        null,
                        [new PropertyRule("street")],
                        null,
                        null,
                        null
                    ),
                ],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeTrue(); // Warnings don't block
            result.HasErrors.Should().BeFalse();
            result.Failures.Should().HaveCount(1);
            result.Failures[0].Severity.Should().Be(ValidationSeverity.Warning);
            result.Failures[0].ProfileName.Should().Be("TestProfile");
            result.Failures[0].ResourceName.Should().Be("Student");
            result.Failures[0].MemberName.Should().Be("address.street");
            result.Failures[0].Message.Should().Contain("street");
            result.Failures[0].Message.Should().Contain("identity member");
            result.Failures[0].Message.Should().Contain("cannot be excluded");
        }

        [Test]
        public void Validate_should_return_error_for_IncludeOnly_nested_collection_with_nonexistent_property()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student having addresses collection
            var addressItemProps = new JsonObject
            {
                ["street"] = new JsonObject { ["type"] = "string" },
                ["city"] = new JsonObject { ["type"] = "string" },
            };
            var properties = new JsonObject
            {
                ["firstName"] = new JsonObject { ["type"] = "string" },
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "object", ["properties"] = addressItemProps },
                },
            };
            var jsonSchemaForInsert = new JsonObject { ["type"] = "object", ["properties"] = properties };

            var builder = new ApiSchemaBuilder().WithStartProject().WithStartResource("Student");
            _apiSchemaDocuments = builder.WithEndResource().WithEndProject().ToApiSchemaDocuments();
            var projectSchemaNode = _apiSchemaDocuments
                .GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new("Student"));
            if (projectSchemaNode is JsonObject resourceObj)
            {
                resourceObj["jsonSchemaForInsert"] = jsonSchemaForInsert;
                resourceObj["identityJsonPaths"] = new JsonArray { "$.firstName" };
            }
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile includes nested collection property that doesn't exist
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [],
                [],
                [
                    new CollectionRule(
                        "addresses",
                        MemberSelection.IncludeOnly,
                        null,
                        [new PropertyRule("nonExistentZipCode")],
                        null,
                        null,
                        null,
                        null
                    ),
                ],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeFalse();
            result.HasErrors.Should().BeTrue();
            result.Failures.Should().HaveCount(1);
            result.Failures[0].Severity.Should().Be(ValidationSeverity.Error);
            result.Failures[0].ProfileName.Should().Be("TestProfile");
            result.Failures[0].ResourceName.Should().Be("Student");
            result.Failures[0].MemberName.Should().Be("addresses[].nonExistentZipCode");
            result.Failures[0].Message.Should().Contain("nonExistentZipCode");
            result.Failures[0].Message.Should().Contain("does not exist");
        }

        [Test]
        public void Validate_should_return_warning_for_ExcludeOnly_nested_collection_with_nonexistent_property()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student having addresses collection
            var addressItemProps = new JsonObject
            {
                ["street"] = new JsonObject { ["type"] = "string" },
                ["city"] = new JsonObject { ["type"] = "string" },
            };
            var properties = new JsonObject
            {
                ["firstName"] = new JsonObject { ["type"] = "string" },
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "object", ["properties"] = addressItemProps },
                },
            };
            var jsonSchemaForInsert = new JsonObject { ["type"] = "object", ["properties"] = properties };

            var builder = new ApiSchemaBuilder().WithStartProject().WithStartResource("Student");
            _apiSchemaDocuments = builder.WithEndResource().WithEndProject().ToApiSchemaDocuments();
            var projectSchemaNode = _apiSchemaDocuments
                .GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new("Student"));
            if (projectSchemaNode is JsonObject resourceObj)
            {
                resourceObj["jsonSchemaForInsert"] = jsonSchemaForInsert;
                resourceObj["identityJsonPaths"] = new JsonArray { "$.firstName" };
            }
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile excludes nested collection property that doesn't exist
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [],
                [],
                [
                    new CollectionRule(
                        "addresses",
                        MemberSelection.ExcludeOnly,
                        null,
                        [new PropertyRule("nonExistentZipCode")],
                        null,
                        null,
                        null,
                        null
                    ),
                ],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeTrue(); // Warnings don't block
            result.HasErrors.Should().BeFalse();
            result.Failures.Should().HaveCount(1);
            result.Failures[0].Severity.Should().Be(ValidationSeverity.Warning);
            result.Failures[0].ProfileName.Should().Be("TestProfile");
            result.Failures[0].ResourceName.Should().Be("Student");
            result.Failures[0].MemberName.Should().Be("addresses[].nonExistentZipCode");
            result.Failures[0].Message.Should().Contain("nonExistentZipCode");
            result.Failures[0].Message.Should().Contain("does not exist");
        }

        [Test]
        public void Validate_should_return_warning_for_ExcludeOnly_nested_collection_excluding_identity_member()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student having addresses collection
            var addressItemProps = new JsonObject
            {
                ["street"] = new JsonObject { ["type"] = "string" },
                ["city"] = new JsonObject { ["type"] = "string" },
            };
            var properties = new JsonObject
            {
                ["firstName"] = new JsonObject { ["type"] = "string" },
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "object", ["properties"] = addressItemProps },
                },
            };
            var jsonSchemaForInsert = new JsonObject { ["type"] = "object", ["properties"] = properties };

            var builder = new ApiSchemaBuilder().WithStartProject().WithStartResource("Student");
            _apiSchemaDocuments = builder.WithEndResource().WithEndProject().ToApiSchemaDocuments();
            var projectSchemaNode = _apiSchemaDocuments
                .GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new("Student"));
            if (projectSchemaNode is JsonObject resourceObj)
            {
                resourceObj["jsonSchemaForInsert"] = jsonSchemaForInsert;
                // Make addresses[].street an identity path
                resourceObj["identityJsonPaths"] = new JsonArray { "$.firstName", "$.addresses[].street" };
            }
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile attempts to exclude identity member in nested collection
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [],
                [],
                [
                    new CollectionRule(
                        "addresses",
                        MemberSelection.ExcludeOnly,
                        null,
                        [new PropertyRule("street")],
                        null,
                        null,
                        null,
                        null
                    ),
                ],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeTrue(); // Warnings don't block
            result.HasErrors.Should().BeFalse();
            result.Failures.Should().HaveCount(1);
            result.Failures[0].Severity.Should().Be(ValidationSeverity.Warning);
            result.Failures[0].ProfileName.Should().Be("TestProfile");
            result.Failures[0].ResourceName.Should().Be("Student");
            result.Failures[0].MemberName.Should().Be("addresses[].street");
            result.Failures[0].Message.Should().Contain("street");
            result.Failures[0].Message.Should().Contain("identity member");
            result.Failures[0].Message.Should().Contain("cannot be excluded");
        }

        [Test]
        public void Validate_should_return_success_for_valid_IncludeOnly_extension_property()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student having _ext with Sample extension
            var extensionProps = new JsonObject { ["customField"] = new JsonObject { ["type"] = "string" } };
            var extProperties = new JsonObject
            {
                ["Sample"] = new JsonObject { ["type"] = "object", ["properties"] = extensionProps },
            };
            var properties = new JsonObject
            {
                ["firstName"] = new JsonObject { ["type"] = "string" },
                ["_ext"] = new JsonObject { ["type"] = "object", ["properties"] = extProperties },
            };
            var jsonSchemaForInsert = new JsonObject { ["type"] = "object", ["properties"] = properties };

            var builder = new ApiSchemaBuilder().WithStartProject().WithStartResource("Student");
            _apiSchemaDocuments = builder.WithEndResource().WithEndProject().ToApiSchemaDocuments();
            var projectSchemaNode = _apiSchemaDocuments
                .GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new("Student"));
            if (projectSchemaNode is JsonObject resourceObj)
            {
                resourceObj["jsonSchemaForInsert"] = jsonSchemaForInsert;
                resourceObj["identityJsonPaths"] = new JsonArray { "$.firstName" };
            }
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile includes valid extension property
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [],
                [],
                [],
                [
                    new ExtensionRule(
                        "Sample",
                        MemberSelection.IncludeOnly,
                        null,
                        [new PropertyRule("customField")],
                        null,
                        null
                    ),
                ]
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeTrue();
            result.HasErrors.Should().BeFalse();
            result.Failures.Should().BeEmpty();
        }

        [Test]
        public void Validate_should_return_error_for_invalid_IncludeOnly_extension_property()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student having _ext with Sample extension
            var extensionProps = new JsonObject { ["customField"] = new JsonObject { ["type"] = "string" } };
            var extProperties = new JsonObject
            {
                ["Sample"] = new JsonObject { ["type"] = "object", ["properties"] = extensionProps },
            };
            var properties = new JsonObject
            {
                ["firstName"] = new JsonObject { ["type"] = "string" },
                ["_ext"] = new JsonObject { ["type"] = "object", ["properties"] = extProperties },
            };
            var jsonSchemaForInsert = new JsonObject { ["type"] = "object", ["properties"] = properties };

            var builder = new ApiSchemaBuilder().WithStartProject().WithStartResource("Student");
            _apiSchemaDocuments = builder.WithEndResource().WithEndProject().ToApiSchemaDocuments();
            var projectSchemaNode = _apiSchemaDocuments
                .GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new("Student"));
            if (projectSchemaNode is JsonObject resourceObj)
            {
                resourceObj["jsonSchemaForInsert"] = jsonSchemaForInsert;
                resourceObj["identityJsonPaths"] = new JsonArray { "$.firstName" };
            }
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile includes non-existent extension property
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [],
                [],
                [],
                [
                    new ExtensionRule(
                        "Sample",
                        MemberSelection.IncludeOnly,
                        null,
                        [new PropertyRule("nonExistentField")],
                        null,
                        null
                    ),
                ]
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeFalse();
            result.HasErrors.Should().BeTrue();
            result.Failures.Should().HaveCount(1);
            result.Failures[0].Severity.Should().Be(ValidationSeverity.Error);
            result.Failures[0].MemberName.Should().Be("_ext.Sample.nonExistentField");
            result.Failures[0].Message.Should().Contain("nonExistentField");
            result.Failures[0].Message.Should().Contain("does not exist");
        }

        [Test]
        public void Validate_should_return_warning_for_ExcludeOnly_extension_excluding_identity_member()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student having _ext with Sample extension
            var extensionProps = new JsonObject
            {
                ["identityField"] = new JsonObject { ["type"] = "string" },
            };
            var extProperties = new JsonObject
            {
                ["Sample"] = new JsonObject { ["type"] = "object", ["properties"] = extensionProps },
            };
            var properties = new JsonObject
            {
                ["firstName"] = new JsonObject { ["type"] = "string" },
                ["_ext"] = new JsonObject { ["type"] = "object", ["properties"] = extProperties },
            };
            var jsonSchemaForInsert = new JsonObject { ["type"] = "object", ["properties"] = properties };

            var builder = new ApiSchemaBuilder().WithStartProject().WithStartResource("Student");
            _apiSchemaDocuments = builder.WithEndResource().WithEndProject().ToApiSchemaDocuments();
            var projectSchemaNode = _apiSchemaDocuments
                .GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new("Student"));
            if (projectSchemaNode is JsonObject resourceObj)
            {
                resourceObj["jsonSchemaForInsert"] = jsonSchemaForInsert;
                // Make extension field an identity member
                resourceObj["identityJsonPaths"] = new JsonArray
                {
                    "$.firstName",
                    "$._ext.Sample.identityField",
                };
            }
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile attempts to exclude identity member in extension
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [],
                [],
                [],
                [
                    new ExtensionRule(
                        "Sample",
                        MemberSelection.ExcludeOnly,
                        null,
                        [new PropertyRule("identityField")],
                        null,
                        null
                    ),
                ]
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeTrue(); // Warnings don't block
            result.HasErrors.Should().BeFalse();
            result.Failures.Should().HaveCount(1);
            result.Failures[0].Severity.Should().Be(ValidationSeverity.Warning);
            result.Failures[0].MemberName.Should().Be("_ext.Sample.identityField");
            result.Failures[0].Message.Should().Contain("identityField");
            result.Failures[0].Message.Should().Contain("identity member");
            result.Failures[0].Message.Should().Contain("cannot be excluded");
        }

        [Test]
        public void Validate_should_validate_multiple_resources_in_profile()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student and School resources
            _apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Student")
                .WithEndResource()
                .WithStartResource("School")
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile with multiple resources, one invalid
            var studentProfile = new ResourceProfile("Student", null, null, null);
            var schoolProfile = new ResourceProfile("School", null, null, null);
            var invalidProfile = new ResourceProfile("InvalidResource", null, null, null);
            var profileDefinition = new ProfileDefinition(
                "TestProfile",
                [studentProfile, schoolProfile, invalidProfile]
            );

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeFalse();
            result.HasErrors.Should().BeTrue();
            result.Failures.Should().HaveCount(1);
            result.Failures[0].Severity.Should().Be(ValidationSeverity.Error);
            result.Failures[0].ResourceName.Should().Be("InvalidResource");
            result.Failures[0].Message.Should().Contain("does not exist in the API schema");
        }

        [Test]
        public void Validate_should_validate_both_ReadContentType_and_WriteContentType()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student having firstName and lastName properties
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName", "lastName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile with both read and write content types, write has invalid property
            var readContentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [new PropertyRule("firstName")],
                [],
                [],
                []
            );
            var writeContentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [new PropertyRule("invalidProperty")],
                [],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, readContentType, writeContentType);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeFalse();
            result.HasErrors.Should().BeTrue();
            result.Failures.Should().HaveCount(1);
            result.Failures[0].Severity.Should().Be(ValidationSeverity.Error);
            result.Failures[0].MemberName.Should().Be("invalidProperty");
            result.Failures[0].Message.Should().Contain("write content type");
        }

        [Test]
        public void Validate_should_handle_extension_not_existing_in_resource()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create schema with Student but no _ext property
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile references an extension that doesn't exist
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [],
                [],
                [
                    new ExtensionRule(
                        "SampleExtension",
                        MemberSelection.IncludeOnly,
                        null,
                        [new PropertyRule("customField")],
                        null,
                        null
                    ),
                ]
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeFalse();
            result.HasErrors.Should().BeTrue();
            result.Failures.Should().HaveCount(1);
            result.Failures[0].Severity.Should().Be(ValidationSeverity.Error);
            result.Failures[0].MemberName.Should().Be("SampleExtension");
            result.Failures[0].Message.Should().Contain("cannot be validated");
            result.Failures[0].Message.Should().Contain("no _ext property");
        }

        [Test]
        public void Validate_should_succeed_for_profile_with_all_member_selection_modes()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);

            // Create comprehensive schema
            var addressProps = new JsonObject
            {
                ["street"] = new JsonObject { ["type"] = "string" },
                ["city"] = new JsonObject { ["type"] = "string" },
            };
            var properties = new JsonObject
            {
                ["firstName"] = new JsonObject { ["type"] = "string" },
                ["lastName"] = new JsonObject { ["type"] = "string" },
                ["email"] = new JsonObject { ["type"] = "string" },
                ["address"] = new JsonObject { ["type"] = "object", ["properties"] = addressProps },
            };
            var jsonSchemaForInsert = new JsonObject { ["type"] = "object", ["properties"] = properties };

            var builder = new ApiSchemaBuilder().WithStartProject().WithStartResource("Student");
            _apiSchemaDocuments = builder.WithEndResource().WithEndProject().ToApiSchemaDocuments();
            var projectSchemaNode = _apiSchemaDocuments
                .GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new("Student"));
            if (projectSchemaNode is JsonObject resourceObj)
            {
                resourceObj["jsonSchemaForInsert"] = jsonSchemaForInsert;
                resourceObj["identityJsonPaths"] = new JsonArray { "$.firstName" };
            }
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            // Profile using all member selection modes correctly
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [new PropertyRule("firstName"), new PropertyRule("lastName")],
                [
                    new ObjectRule(
                        "address",
                        MemberSelection.ExcludeOnly,
                        null,
                        [new PropertyRule("city")], // Exclude city, include street
                        null,
                        null,
                        null
                    ),
                ],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.IsValid.Should().BeTrue();
            result.HasErrors.Should().BeFalse();
            result.Failures.Should().BeEmpty();
        }
    }
}
