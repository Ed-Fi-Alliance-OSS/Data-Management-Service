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

        private static ApiSchemaDocuments CreateSchemaWithReferenceObject(
            string resourceName,
            string referenceName,
            params string[] referencePropertyNames
        )
        {
            var refProperties = new JsonObject();
            foreach (var name in referencePropertyNames)
            {
                refProperties[name] = new JsonObject { ["type"] = "string" };
            }

            var referenceObject = new JsonObject { ["type"] = "object", ["properties"] = refProperties };

            var jsonSchemaForInsert = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { [referenceName] = referenceObject },
            };

            var apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource(resourceName)
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            var resourceNode = apiSchemaDocuments
                .GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new(resourceName));

            if (resourceNode is JsonObject resourceObj)
            {
                resourceObj["jsonSchemaForInsert"] = jsonSchemaForInsert;
                resourceObj["identityJsonPaths"] = new JsonArray
                {
                    $"$.{referenceName}.{referencePropertyNames[0]}",
                };
            }

            return apiSchemaDocuments;
        }

        private static ApiSchemaDocuments CreateSchemaWithCollection(
            string resourceName,
            string collectionName,
            params string[] itemPropertyNames
        )
        {
            var itemProperties = new JsonObject();
            foreach (var name in itemPropertyNames)
            {
                itemProperties[name] = new JsonObject { ["type"] = "string" };
            }

            var itemSchema = new JsonObject { ["type"] = "object", ["properties"] = itemProperties };

            var collectionSchema = new JsonObject { ["type"] = "array", ["items"] = itemSchema };

            var jsonSchemaForInsert = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { [collectionName] = collectionSchema },
            };

            var apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource(resourceName)
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            var resourceNode = apiSchemaDocuments
                .GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new(resourceName));

            if (resourceNode is JsonObject resourceObj)
            {
                resourceObj["jsonSchemaForInsert"] = jsonSchemaForInsert;
            }

            return apiSchemaDocuments;
        }

        private static ApiSchemaDocuments CreateSchemaWithExtension(
            string resourceName,
            string extensionName,
            params string[] extensionPropertyNames
        )
        {
            var extProperties = new JsonObject();
            foreach (var name in extensionPropertyNames)
            {
                extProperties[name] = new JsonObject { ["type"] = "string" };
            }

            var extensionSchema = new JsonObject { ["type"] = "object", ["properties"] = extProperties };

            var extSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { [extensionName] = extensionSchema },
            };

            var jsonSchemaForInsert = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["_ext"] = extSchema },
            };

            var apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource(resourceName)
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            var resourceNode = apiSchemaDocuments
                .GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new(resourceName));

            if (resourceNode is JsonObject resourceObj)
            {
                resourceObj["jsonSchemaForInsert"] = jsonSchemaForInsert;
            }

            return apiSchemaDocuments;
        }

        /// <summary>
        /// Builds a schema whose insert schema exposes <c>_ext.sample</c> containing a nested
        /// object (<c>petPreference</c>) and a nested collection (<c>pets</c>), used to exercise
        /// unknown-extension detection inside an extension's own object/collection children.
        /// </summary>
        private static ApiSchemaDocuments CreateSchemaWithExtensionChildren(string resourceName)
        {
            var jsonSchemaForInsert = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["_ext"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["petPreference"] = new JsonObject
                                    {
                                        ["type"] = "object",
                                        ["properties"] = new JsonObject
                                        {
                                            ["minimumWeight"] = new JsonObject { ["type"] = "number" },
                                        },
                                    },
                                    ["pets"] = new JsonObject
                                    {
                                        ["type"] = "array",
                                        ["items"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["petName"] = new JsonObject { ["type"] = "string" },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            };

            var apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource(resourceName)
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            var resourceNode = apiSchemaDocuments
                .GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new(resourceName));

            if (resourceNode is JsonObject resourceObj)
            {
                resourceObj["jsonSchemaForInsert"] = jsonSchemaForInsert;
            }

            return apiSchemaDocuments;
        }

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

            // Assert — the unknown extension is reported once, as an error, by the
            // mode-independent unknown-extension pre-pass.
            result.IsValid.Should().BeFalse();
            result.HasErrors.Should().BeTrue();
            result.Failures.Should().HaveCount(1);
            result.Failures[0].Severity.Should().Be(ValidationSeverity.Error);
            result.Failures[0].MemberName.Should().Be("_ext.SampleExtension");
            result.Failures[0].Message.Should().Contain("does not exist");
        }

        [Test]
        public void Validate_should_return_error_for_server_generated_field_in_IncludeOnly_properties()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);
            // Schema has firstName so "ordinary members would validate" baseline holds.
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [new PropertyRule("link")],
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
            result.Failures[0].MemberName.Should().Be("link");
            result.Failures[0].Message.Should().Contain("server-generated field");
            result.Failures[0].Message.Should().Contain("not profile-addressable");
        }

        [TestCase("id")]
        [TestCase("link")]
        [TestCase("_etag")]
        [TestCase("_lastModifiedDate")]
        public void Validate_should_return_error_for_each_server_generated_field_in_IncludeOnly_properties(
            string serverGeneratedFieldName
        )
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [new PropertyRule(serverGeneratedFieldName)],
                [],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == serverGeneratedFieldName
                    && f.Message.Contains("server-generated field")
                    && f.Message.Contains("not profile-addressable")
                );
        }

        [TestCase("id")]
        [TestCase("link")]
        [TestCase("_etag")]
        [TestCase("_lastModifiedDate")]
        public void Validate_should_return_error_for_each_server_generated_field_in_ExcludeOnly_properties(
            string serverGeneratedFieldName
        )
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var contentType = new ContentTypeDefinition(
                MemberSelection.ExcludeOnly,
                [new PropertyRule(serverGeneratedFieldName)],
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
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == serverGeneratedFieldName
                    && f.Message.Contains("server-generated field")
                    && f.Message.Contains("not profile-addressable")
                );
        }

        [Test]
        public void Validate_should_return_error_for_server_generated_field_as_object_rule_name()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [new ObjectRule("link", MemberSelection.IncludeAll, null, null, null, null, null)],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_should_return_error_for_server_generated_field_as_collection_rule_name()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [],
                [new CollectionRule("link", MemberSelection.IncludeAll, null, null, null, null, null, null)],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_should_return_error_for_server_generated_field_as_extension_rule_name()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [],
                [],
                [new ExtensionRule("link", MemberSelection.IncludeAll, null, null, null, null)]
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "link"
                    && f.Message.Contains("server-generated field")
                );
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

        [Test]
        public void Validate_should_return_error_for_link_in_nested_object_rule_properties()
        {
            // Arrange — schema must contain schoolReference.schoolId so the failure
            // is the server-gen rule, not "object does not exist".
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithReferenceObject("Student", "schoolReference", "schoolId");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var schoolReferenceRule = new ObjectRule(
                Name: "schoolReference",
                MemberSelection: MemberSelection.IncludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("schoolId"), new PropertyRule("link")],
                NestedObjects: null,
                Collections: null,
                Extensions: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [schoolReferenceRule],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "schoolReference.link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_should_return_error_for_link_in_nested_collection_rule_properties()
        {
            // Arrange — schema has addresses[].streetAddress
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithCollection("Student", "addresses", "streetAddress");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var addressesRule = new CollectionRule(
                Name: "addresses",
                MemberSelection: MemberSelection.IncludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("link")],
                NestedObjects: null,
                NestedCollections: null,
                Extensions: null,
                ItemFilter: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [],
                [addressesRule],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "addresses[].link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_should_return_error_for_link_in_extension_rule_properties()
        {
            // Arrange — schema has _ext.sample.sampleField
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithExtension("Student", "sample", "sampleField");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var extensionRule = new ExtensionRule(
                Name: "sample",
                MemberSelection: MemberSelection.IncludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("link")],
                Objects: null,
                Collections: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [],
                [],
                [extensionRule]
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "_ext.sample.link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_should_resolve_mixed_case_extension_name_to_schema_key()
        {
            // Arrange — schema exposes the extension under the lower-case project key sample
            // while the profile authors it as Sample.
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithExtension("Student", "sample", "sampleField");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var extensionRule = new ExtensionRule(
                Name: "Sample",
                MemberSelection: MemberSelection.IncludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("sampleField")],
                Objects: null,
                Collections: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [],
                [],
                [extensionRule]
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert — Sample resolves to schema key sample, so there is no missing-extension failure.
            result.IsValid.Should().BeTrue();
            result.Failures.Should().BeEmpty();
        }

        [Test]
        public void Validate_should_error_for_unknown_extension_under_include_only_parent()
        {
            // Arrange — schema only has the sample extension, profile references an unknown one.
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithExtension("Student", "sample", "sampleField");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var extensionRule = new ExtensionRule(
                Name: "Nonexistent",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                Objects: null,
                Collections: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [],
                [],
                [extensionRule]
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert — unknown extension under IncludeOnly is an error (profile is dropped at load).
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "_ext.Nonexistent"
                    && f.Message.Contains("does not exist")
                );
        }

        [Test]
        public void Validate_should_warn_for_unknown_extension_under_exclude_only_parent()
        {
            // Arrange — schema only has the sample extension, profile excludes an unknown one.
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithExtension("Student", "sample", "sampleField");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var extensionRule = new ExtensionRule(
                Name: "Nonexistent",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                Objects: null,
                Collections: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.ExcludeOnly,
                [],
                [],
                [],
                [extensionRule]
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert — ExcludeOnly tolerates a missing reference: a warning is emitted (so the
            // profile still loads), and canonicalization drops the rule so no unresolved runtime
            // scope is created. This preserves the existing missing-reference contract.
            result.HasErrors.Should().BeFalse();
            result.HasWarnings.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Warning && f.MemberName == "_ext.Nonexistent"
                );
        }

        [Test]
        public void Validate_should_warn_for_unknown_extension_under_include_all_parent()
        {
            // Arrange — an IncludeAll extension under an IncludeAll parent must still be
            // existence-checked, because canonicalization would otherwise drop it silently.
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithExtension("Student", "sample", "sampleField");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var extensionRule = new ExtensionRule(
                Name: "DoesNotExist",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                Objects: null,
                Collections: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [],
                [],
                [],
                [extensionRule]
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert — IncludeAll tolerates a missing reference: a warning (clear feedback)
            // rather than a silent drop, and the rule is dropped by canonicalization.
            result.HasErrors.Should().BeFalse();
            result.HasWarnings.Should().BeTrue();
            result
                .Failures.Should()
                .Contain(f =>
                    f.Severity == ValidationSeverity.Warning
                    && f.MemberName == "_ext.DoesNotExist"
                    && f.Message.Contains("does not exist")
                );
        }

        [Test]
        public void Validate_should_report_unknown_extension_nested_in_include_all_object()
        {
            // Arrange — an unknown extension nested inside an IncludeAll object under an
            // IncludeAll parent. The member-selection validation short-circuits on IncludeAll,
            // so only the mode-independent unknown-extension pre-pass can surface this; without
            // it the rule would be dropped by canonicalization with no feedback.
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithReferenceObject("Student", "schoolReference", "schoolId");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var objectRule = new ObjectRule(
                Name: "schoolReference",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                NestedObjects: null,
                Collections: null,
                Extensions:
                [
                    new ExtensionRule("DoesNotExist", MemberSelection.IncludeAll, null, null, null, null),
                ]
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                Properties: [],
                Objects: [objectRule],
                Collections: [],
                Extensions: []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert — feedback is emitted (not silently dropped) with the nested rule's full
            // path; severity follows the enclosing IncludeAll object, so it is a warning.
            result
                .Failures.Should()
                .Contain(f =>
                    f.Severity == ValidationSeverity.Warning
                    && f.MemberName == "schoolReference._ext.DoesNotExist"
                    && f.Message.Contains("does not exist")
                );
        }

        [Test]
        public void Validate_should_report_unknown_extension_under_collection_item()
        {
            // Arrange — an unknown extension declared on a collection item. The collection-item
            // _ext has no such key, so canonicalization would drop the rule; the pre-pass must
            // report it (collections use their own traversal path).
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithCollection("Student", "studentPets", "petName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var collectionRule = new CollectionRule(
                Name: "studentPets",
                MemberSelection: MemberSelection.IncludeOnly,
                LogicalSchema: null,
                Properties: null,
                NestedObjects: null,
                NestedCollections: null,
                Extensions:
                [
                    new ExtensionRule("DoesNotExist", MemberSelection.IncludeAll, null, null, null, null),
                ],
                ItemFilter: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                Properties: [],
                Objects: [],
                Collections: [collectionRule],
                Extensions: []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert — reported with the collection-item path; severity follows the IncludeOnly
            // collection, so it is an error.
            result
                .Failures.Should()
                .Contain(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "studentPets[]._ext.DoesNotExist"
                    && f.Message.Contains("does not exist")
                );
        }

        [Test]
        public void Validate_should_report_unknown_extension_inside_resolved_extension_object()
        {
            // Arrange — an unknown extension nested inside an object that lives within a resolved
            // extension (_ext.sample.petPreference._ext.DoesNotExist). Exercises the recursion
            // from the extension into its object children.
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithExtensionChildren("Student");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var nestedObject = new ObjectRule(
                Name: "petPreference",
                MemberSelection: MemberSelection.IncludeOnly,
                LogicalSchema: null,
                Properties: null,
                NestedObjects: null,
                Collections: null,
                Extensions:
                [
                    new ExtensionRule("DoesNotExist", MemberSelection.IncludeAll, null, null, null, null),
                ]
            );
            var sampleExtension = new ExtensionRule(
                Name: "sample",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                Objects: [nestedObject],
                Collections: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [],
                [],
                [],
                [sampleExtension]
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert — reported with the full extension-child object path; severity follows the
            // IncludeOnly object, so it is an error.
            result
                .Failures.Should()
                .Contain(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "_ext.sample.petPreference._ext.DoesNotExist"
                    && f.Message.Contains("does not exist")
                );
        }

        [Test]
        public void Validate_should_report_unknown_extension_inside_resolved_extension_collection()
        {
            // Arrange — an unknown extension nested inside a collection within a resolved
            // extension (_ext.sample.pets[]._ext.DoesNotExist). Exercises the recursion from the
            // extension into its collection children.
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithExtensionChildren("Student");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var nestedCollection = new CollectionRule(
                Name: "pets",
                MemberSelection: MemberSelection.IncludeOnly,
                LogicalSchema: null,
                Properties: null,
                NestedObjects: null,
                NestedCollections: null,
                Extensions:
                [
                    new ExtensionRule("DoesNotExist", MemberSelection.IncludeAll, null, null, null, null),
                ],
                ItemFilter: null
            );
            var sampleExtension = new ExtensionRule(
                Name: "sample",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                Objects: null,
                Collections: [nestedCollection]
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [],
                [],
                [],
                [sampleExtension]
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert — reported with the full extension-child collection path; severity follows
            // the IncludeOnly collection, so it is an error.
            result
                .Failures.Should()
                .Contain(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "_ext.sample.pets[]._ext.DoesNotExist"
                    && f.Message.Contains("does not exist")
                );
        }

        [Test]
        public void Validate_should_error_for_duplicate_extension_names_differing_only_by_case()
        {
            // Arrange — sample and Sample both resolve to the schema key sample and would
            // collapse to a duplicate key in ExtensionRulesByName at runtime.
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithExtension("Student", "sample", "sampleField");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var contentType = new ContentTypeDefinition(
                MemberSelection.ExcludeOnly,
                [],
                [],
                [],
                [
                    new ExtensionRule("sample", MemberSelection.IncludeAll, null, null, null, null),
                    new ExtensionRule("Sample", MemberSelection.IncludeAll, null, null, null, null),
                ]
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert — both resolve to schema key "sample", so the collision is rejected with an
            // error rather than throwing during navigation.
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .Contain(f =>
                    f.Severity == ValidationSeverity.Error && f.Message.Contains("collapse to the same key")
                );
        }

        [Test]
        public void Validate_should_warn_not_error_for_unresolved_case_variant_extensions_under_exclude_only()
        {
            // Arrange — Foo and foo are both unknown (schema only has sample). Neither resolves,
            // so canonicalization drops both; they must keep the ExcludeOnly missing-reference
            // contract (warnings) rather than be rejected as a duplicate collision.
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithExtension("Student", "sample", "sampleField");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var contentType = new ContentTypeDefinition(
                MemberSelection.ExcludeOnly,
                [],
                [],
                [],
                [
                    new ExtensionRule("Foo", MemberSelection.IncludeAll, null, null, null, null),
                    new ExtensionRule("foo", MemberSelection.IncludeAll, null, null, null, null),
                ]
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert — two missing-reference warnings (one per unresolved name), no error, and no
            // duplicate-collision failure.
            result.HasErrors.Should().BeFalse();
            result.HasWarnings.Should().BeTrue();
            result.Failures.Should().HaveCount(2);
            result.Failures.Should().OnlyContain(f => f.Severity == ValidationSeverity.Warning);
            result.Failures.Should().Contain(f => f.MemberName == "_ext.Foo");
            result.Failures.Should().Contain(f => f.MemberName == "_ext.foo");
            result.Failures.Should().NotContain(f => f.Message.Contains("collapse to the same key"));
        }

        [Test]
        public void Validate_should_resolve_extension_name_to_non_lowercase_schema_key()
        {
            // Arrange — schema key is camelCase sampleStaff (not all-lowercase). The canonical
            // key must come from the schema, so a differently-cased authored name resolves.
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithExtension("Student", "sampleStaff", "field");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var extensionRule = new ExtensionRule(
                Name: "samplestaff",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                Objects: null,
                Collections: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.ExcludeOnly,
                [],
                [],
                [],
                [extensionRule]
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert — samplestaff resolves to schema key sampleStaff; no missing-extension failure.
            result.IsValid.Should().BeTrue();
            result.Failures.Should().BeEmpty();
        }

        [Test]
        public void Validate_should_return_error_for_nested_object_named_link()
        {
            // Arrange — schema has schoolReference.schoolId; profile defines a nested object named "link"
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithReferenceObject("Student", "schoolReference", "schoolId");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var nestedRule = new ObjectRule(
                Name: "link",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                NestedObjects: null,
                Collections: null,
                Extensions: null
            );
            var schoolReferenceRule = new ObjectRule(
                Name: "schoolReference",
                MemberSelection: MemberSelection.IncludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("schoolId")],
                NestedObjects: [nestedRule],
                Collections: null,
                Extensions: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [schoolReferenceRule],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "schoolReference.link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_should_return_error_for_nested_collection_named_link()
        {
            // Arrange — schema has schoolReference.schoolId; profile defines a nested collection named "link"
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithReferenceObject("Student", "schoolReference", "schoolId");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var nestedCollectionNamedLink = new CollectionRule(
                Name: "link",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                NestedObjects: null,
                NestedCollections: null,
                Extensions: null,
                ItemFilter: null
            );
            var schoolReferenceRule = new ObjectRule(
                Name: "schoolReference",
                MemberSelection: MemberSelection.IncludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("schoolId")],
                NestedObjects: null,
                Collections: [nestedCollectionNamedLink],
                Extensions: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [schoolReferenceRule],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "schoolReference.link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_link_in_IncludeOnly_produces_server_generated_error_not_does_not_exist_error()
        {
            // Arrange — schema intentionally omits "link"; if the server-gen check did
            // not run first, the validator would emit "Property 'link' does not exist".
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [new PropertyRule("link")],
                [],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert — exactly one failure, the server-gen message
            result.Failures.Should().HaveCount(1);
            result.Failures[0].Message.Should().Contain("server-generated field");
            result.Failures[0].Message.Should().NotContain("does not exist");
        }

        [Test]
        public void Validate_should_succeed_for_profile_addressing_only_ordinary_schema_members()
        {
            // Arrange — profile references schoolReference.schoolId only.
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithReferenceObject("Student", "schoolReference", "schoolId");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var schoolReferenceRule = new ObjectRule(
                Name: "schoolReference",
                MemberSelection: MemberSelection.IncludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("schoolId")],
                NestedObjects: null,
                Collections: null,
                Extensions: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [schoolReferenceRule],
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
        public void Validate_IncludeAll_content_type_should_return_error_for_object_rule_named_link()
        {
            // Arrange — parent content type is IncludeAll; server-gen names are
            // not profile-addressable regardless of parent member selection.
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [],
                [new ObjectRule("link", MemberSelection.IncludeAll, null, null, null, null, null)],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_IncludeAll_content_type_should_return_error_for_collection_rule_named_link()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [],
                [],
                [new CollectionRule("link", MemberSelection.IncludeAll, null, null, null, null, null, null)],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_IncludeAll_content_type_should_return_error_for_extension_rule_named_link()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [],
                [],
                [],
                [new ExtensionRule("link", MemberSelection.IncludeAll, null, null, null, null)]
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_should_return_error_for_link_in_object_rule_extensions()
        {
            // Arrange — ObjectRule.Extensions is populated by the parser and
            // consumed by both projectors; the validator must traverse it too.
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithReferenceObject("Student", "schoolReference", "schoolId");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var schoolReferenceRule = new ObjectRule(
                Name: "schoolReference",
                MemberSelection: MemberSelection.IncludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("schoolId")],
                NestedObjects: null,
                Collections: null,
                Extensions:
                [
                    new ExtensionRule(
                        Name: "link",
                        MemberSelection: MemberSelection.IncludeAll,
                        LogicalSchema: null,
                        Properties: null,
                        Objects: null,
                        Collections: null
                    ),
                ]
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [schoolReferenceRule],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "schoolReference.link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_should_return_error_for_link_in_collection_rule_nested_collections()
        {
            // Arrange — CollectionRule.NestedCollections is populated by the
            // parser and consumed by both projectors; the validator must
            // traverse it too.
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithCollection("Student", "addresses", "streetAddress");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var addressesRule = new CollectionRule(
                Name: "addresses",
                MemberSelection: MemberSelection.IncludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("streetAddress")],
                NestedObjects: null,
                NestedCollections:
                [
                    new CollectionRule(
                        Name: "link",
                        MemberSelection: MemberSelection.IncludeAll,
                        LogicalSchema: null,
                        Properties: null,
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: null
                    ),
                ],
                Extensions: null,
                ItemFilter: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [],
                [addressesRule],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "addresses[].link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_should_return_error_for_link_in_collection_rule_extensions()
        {
            // Arrange — CollectionRule.Extensions is populated by the parser
            // and consumed by both projectors; the validator must traverse it.
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithCollection("Student", "addresses", "streetAddress");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var addressesRule = new CollectionRule(
                Name: "addresses",
                MemberSelection: MemberSelection.IncludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("streetAddress")],
                NestedObjects: null,
                NestedCollections: null,
                Extensions:
                [
                    new ExtensionRule(
                        Name: "link",
                        MemberSelection: MemberSelection.IncludeAll,
                        LogicalSchema: null,
                        Properties: null,
                        Objects: null,
                        Collections: null
                    ),
                ],
                ItemFilter: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [],
                [addressesRule],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "addresses[].link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [TestCase("id")]
        [TestCase("link")]
        [TestCase("_etag")]
        [TestCase("_lastModifiedDate")]
        public void Validate_IncludeAll_content_type_should_return_error_for_property_rule_named_server_gen(
            string serverGeneratedFieldName
        )
        {
            // Arrange — top-level IncludeAll with a Property naming a server-generated
            // field. The dispatched IncludeAll path does not look at Properties, so the
            // server-gen rejection has to come from the recursive name pre-pass.
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithProperties("Student", "firstName");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [new PropertyRule(serverGeneratedFieldName)],
                [],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == serverGeneratedFieldName
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_IncludeAll_object_rule_should_return_error_for_child_property_named_link()
        {
            // Arrange — IncludeAll object containing an explicit Property("link").
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithReferenceObject("Student", "schoolReference", "schoolId");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var schoolReferenceRule = new ObjectRule(
                Name: "schoolReference",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: [new PropertyRule("link")],
                NestedObjects: null,
                Collections: null,
                Extensions: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [schoolReferenceRule],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "schoolReference.link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_IncludeAll_object_rule_should_return_error_for_child_nested_object_named_link()
        {
            // Arrange — IncludeAll object containing an explicit nested Object("link").
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithReferenceObject("Student", "schoolReference", "schoolId");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var schoolReferenceRule = new ObjectRule(
                Name: "schoolReference",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                NestedObjects:
                [
                    new ObjectRule("link", MemberSelection.IncludeAll, null, null, null, null, null),
                ],
                Collections: null,
                Extensions: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [schoolReferenceRule],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "schoolReference.link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_IncludeAll_object_rule_should_return_error_for_child_collection_named_link()
        {
            // Arrange — IncludeAll object containing an explicit Collection("link").
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithReferenceObject("Student", "schoolReference", "schoolId");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var schoolReferenceRule = new ObjectRule(
                Name: "schoolReference",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                NestedObjects: null,
                Collections:
                [
                    new CollectionRule(
                        "link",
                        MemberSelection.IncludeAll,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null
                    ),
                ],
                Extensions: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [schoolReferenceRule],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "schoolReference.link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_IncludeAll_object_rule_should_return_error_for_child_extension_named_link()
        {
            // Arrange — IncludeAll object containing an explicit Extension("link").
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithReferenceObject("Student", "schoolReference", "schoolId");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var schoolReferenceRule = new ObjectRule(
                Name: "schoolReference",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                NestedObjects: null,
                Collections: null,
                Extensions: [new ExtensionRule("link", MemberSelection.IncludeAll, null, null, null, null)]
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [schoolReferenceRule],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "schoolReference.link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_IncludeAll_collection_rule_should_return_error_for_child_property_named_link()
        {
            // Arrange — IncludeAll collection containing an explicit Property("link") on its items.
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithCollection("Student", "addresses", "streetAddress");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var addressesRule = new CollectionRule(
                Name: "addresses",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: [new PropertyRule("link")],
                NestedObjects: null,
                NestedCollections: null,
                Extensions: null,
                ItemFilter: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [],
                [addressesRule],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "addresses[].link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_IncludeAll_extension_rule_should_return_error_for_child_property_named_link()
        {
            // Arrange — IncludeAll extension containing an explicit Property("link").
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithExtension("Student", "sample", "sampleField");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var sampleExtensionRule = new ExtensionRule(
                Name: "sample",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: [new PropertyRule("link")],
                Objects: null,
                Collections: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [],
                [],
                [sampleExtensionRule]
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "_ext.sample.link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_extension_inside_object_should_report_full_enclosing_path_for_link_property()
        {
            // Arrange — Object("schoolReference") -> Extension("sample") -> Property("link").
            // Failure path must preserve the enclosing object prefix and surface as
            // "schoolReference._ext.sample.link", not "_ext.sample.link".
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithReferenceObject("Student", "schoolReference", "schoolId");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var schoolReferenceRule = new ObjectRule(
                Name: "schoolReference",
                MemberSelection: MemberSelection.IncludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("schoolId")],
                NestedObjects: null,
                Collections: null,
                Extensions:
                [
                    new ExtensionRule(
                        Name: "sample",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("link")],
                        Objects: null,
                        Collections: null
                    ),
                ]
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [schoolReferenceRule],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert — server-gen failure carries the full path.
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "schoolReference._ext.sample.link"
                    && f.Message.Contains("server-generated field")
                );
        }

        [TestCase("id")]
        [TestCase("link")]
        [TestCase("_etag")]
        [TestCase("_lastModifiedDate")]
        public void Validate_should_return_error_for_item_filter_property_named_server_gen(
            string serverGeneratedFieldName
        )
        {
            // Arrange — root collection rule whose ItemFilter selects a
            // server-generated field. The parser accepts any propertyName, so
            // the namespace contract has to be enforced by the validator.
            var validator = new ProfileDataValidator(_logger);
            _apiSchemaDocuments = CreateSchemaWithCollection("Student", "addresses", "streetAddress");
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var addressesRule = new CollectionRule(
                Name: "addresses",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                NestedObjects: null,
                NestedCollections: null,
                Extensions: null,
                ItemFilter: new CollectionItemFilter(
                    serverGeneratedFieldName,
                    FilterMode.IncludeOnly,
                    ["uri://ed-fi.org/AddressTypeDescriptor#Home"]
                )
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [],
                [addressesRule],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.HasErrors.Should().BeTrue();
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == $"addresses[].{serverGeneratedFieldName}"
                    && f.Message.Contains("server-generated field")
                );
        }

        [Test]
        public void Validate_should_return_error_for_item_filter_property_on_nested_collection()
        {
            // Arrange — Object("schoolReference") -> Collection("addresses")
            // with an ItemFilter.PropertyName of "link". Failure path must
            // preserve the enclosing object prefix. The schema declares
            // schoolReference.{ schoolId, addresses[].streetAddress } so the
            // only failure surfaced is the server-generated one.
            var validator = new ProfileDataValidator(_logger);
            var addressesSchema = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["streetAddress"] = new JsonObject { ["type"] = "string" },
                    },
                },
            };
            var schoolReferenceSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["schoolId"] = new JsonObject { ["type"] = "string" },
                    ["addresses"] = addressesSchema,
                },
            };
            var jsonSchemaForInsert = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["schoolReference"] = schoolReferenceSchema },
            };
            _apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Student")
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();
            var studentNode =
                _apiSchemaDocuments
                    .GetCoreProjectSchema()
                    .FindResourceSchemaNodeByResourceName(new("Student")) as JsonObject;
            studentNode!["jsonSchemaForInsert"] = jsonSchemaForInsert;
            studentNode["identityJsonPaths"] = new JsonArray { "$.schoolReference.schoolId" };
            A.CallTo(() => _effectiveApiSchemaProvider.Documents).Returns(_apiSchemaDocuments);

            var addressesRule = new CollectionRule(
                Name: "addresses",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                NestedObjects: null,
                NestedCollections: null,
                Extensions: null,
                ItemFilter: new CollectionItemFilter(
                    "link",
                    FilterMode.IncludeOnly,
                    ["uri://ed-fi.org/AddressTypeDescriptor#Home"]
                )
            );
            var schoolReferenceRule = new ObjectRule(
                Name: "schoolReference",
                MemberSelection: MemberSelection.IncludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("schoolId")],
                NestedObjects: null,
                Collections: [addressesRule],
                Extensions: null
            );
            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [],
                [schoolReferenceRule],
                [],
                []
            );
            var resourceProfile = new ResourceProfile("Student", null, contentType, null);
            var profileDefinition = new ProfileDefinition("TestProfile", [resourceProfile]);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert — exactly one failure: the server-generated item filter.
            result.HasErrors.Should().BeTrue();
            result.Failures.Should().HaveCount(1);
            result
                .Failures.Should()
                .ContainSingle(f =>
                    f.Severity == ValidationSeverity.Error
                    && f.MemberName == "schoolReference.addresses[].link"
                    && f.Message.Contains("server-generated field")
                );
        }
    }
}
