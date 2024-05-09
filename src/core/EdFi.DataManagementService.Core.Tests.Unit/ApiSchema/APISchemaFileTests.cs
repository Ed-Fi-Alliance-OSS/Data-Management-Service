// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Validation;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;

/// <summary>
/// APISchemaFileTests contain tests to cover all errors that may arise from the "ResourceSchema.cs" file due to invalid resource schemas.
/// Within the "InvalidResourceSchemas.json" file, we have curated a collection of resource schemas representing various invalid use cases.
/// On the frontend layer, there is no observable difference. The "CoreLoggingMiddleware" is to catch specific errors and consistently throw an
/// "Internal server error." However, the specific error will be logged.Through these various tests,
/// we are ensuring that invalid resource schemas are appropriately captured and throws error.
/// </summary>
[TestFixture]
public class APISchemaFileTests
{
    [TestFixture]
    public class Given_an_ApiSchema_file_with_invalid_resourceschemas
    {
        public static CoreFacade BuildCoreFacade(IApiSchemaProvider apiSchemaProvider)
        {
            return new CoreFacade(
                apiSchemaProvider,
                new ApiSchemaValidator(
                    new ApiSchemaSchemaProvider(NullLogger<ApiSchemaSchemaProvider>.Instance)
                ),
                new SuccessDocumentStoreRepository(NullLogger<SuccessDocumentStoreRepository>.Instance),
                new DocumentValidator(),
                new EqualityConstraintValidator(),
                NullLogger<CoreFacade>.Instance
            );
        }

        public IApiSchemaProvider? apiSchemaProvider;

        [SetUp]
        public void Setup()
        {
            var schemaContent = JsonNode.Parse(File.ReadAllText("ApiSchema/InvalidResourceSchemas.json"));
            apiSchemaProvider = A.Fake<IApiSchemaProvider>();
            A.CallTo(() => apiSchemaProvider.ApiSchemaRootNode).Returns(schemaContent!);
        }

        [TestFixture]
        public class Should_respond_with_internal_server_error_for_a_GET_request
            : Given_an_ApiSchema_file_with_invalid_resourceschemas
        {
            // "resourceName" element does not exist on resource schema.
            [Test]
            public async Task When_no_resourcename_element()
            {
                // Arrange
                CoreFacade coreFacade = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/noresourcenames/123"), null, [], new(""));

                // Act
                FrontendResponse response = await coreFacade.GetById(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "isDescriptor" element does not exist on resource schema.
            [Test]
            public async Task When_no_isdescriptor_element()
            {
                // Arrange
                CoreFacade coreFacade = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/noIsDescriptors/123"), null, [], new(""));

                // Act
                FrontendResponse response = await coreFacade.GetById(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "allowIdentityUpdates" element does not exist on resource schema.
            [Test]
            public async Task When_no_allowidentityupdates_element()
            {
                // Arrange
                CoreFacade coreFacade = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/noallowidentityupdates/123"), null, [], new(""));

                // Act
                FrontendResponse response = await coreFacade.GetById(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }
        }

        [TestFixture]
        public class Should_respond_with_internal_server_error_for_a_POST_request
            : Given_an_ApiSchema_file_with_invalid_resourceschemas
        {
            // "isShoolyearEnumeration" element does not exist on resource schema.
            [Test]
            public async Task When_no_isshoolyearenumeration_element()
            {
                // Arrange
                CoreFacade coreFacade = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request =
                    new(new("/ed-fi/noIsSchoolYearEnumerations/123"), null, [], new(""));

                // Act
                FrontendResponse response = await coreFacade.GetById(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "jsonSchemaForInsert" element does not exist on resource schema.
            [Test]
            public async Task When_no_jsonschemaforinsert_element()
            {
                // Arrange
                CoreFacade coreFacade = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/noJsonSchemaForInserts/123"), null, [], new(""));

                // Act
                FrontendResponse response = await coreFacade.GetById(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "identityjsonpaths" element does not exist on resource schema.
            [Test]
            public async Task When_no_identityjsonpaths_element()
            {
                // Arrange
                CoreFacade coreFacade = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/noidentityjsonpaths/123"), null, [], new(""));

                // Act
                FrontendResponse response = await coreFacade.GetById(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "equalityconstraints" element does not exist on resource schema.
            [Test]
            public async Task When_no_equalityconstraints_element()
            {
                // Arrange
                CoreFacade coreFacade = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/noequalityconstraints/123"), null, [], new(""));

                // Act
                FrontendResponse response = await coreFacade.GetById(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "isSubclass" element does not exist on resource schema.
            [Test]
            public async Task When_no_issubclass_element()
            {
                // Arrange
                CoreFacade coreFacade = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/noIsSubclasses/123"), null, [], new(""));

                // Act
                FrontendResponse response = await coreFacade.GetById(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "subclassType" element does not exist on resource schema.
            [Test]
            public async Task When_no_subclasstype_element()
            {
                // Arrange
                CoreFacade coreFacade = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/noSubClassTypes/123"), null, [], new(""));

                // Act
                FrontendResponse response = await coreFacade.GetById(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "superClassResourceName" element does not exist on resource schema.
            [Test]
            public async Task When_no_superclassresourcename_element()
            {
                // Arrange
                CoreFacade coreFacade = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/nosuperclassresourcenames/123"), null, [], new(""));

                // Act
                FrontendResponse response = await coreFacade.GetById(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "superclassProjectName" element does not exist on resource schema.
            [Test]
            public async Task When_no_superclassprojectname_element()
            {
                // Arrange
                CoreFacade coreFacade = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/nosuperclassprojectnames/123"), null, [], new(""));

                // Act
                FrontendResponse response = await coreFacade.GetById(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "superclassIdentityDocumentKey" element does not exist on resource schema.
            [Test]
            public async Task When_no_superclassidentitydocumentkey_element()
            {
                // Arrange
                CoreFacade coreFacade = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request =
                    new(new("/ed-fi/nosuperclassidentitydocumentkeys/123"), null, [], new(""));

                // Act
                FrontendResponse response = await coreFacade.GetById(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "subclassIdentityDocumentKey" element does not exist on resource schema.
            [Test]
            public async Task When_no_subclassidentitydocumentkey_element()
            {
                // Arrange
                CoreFacade coreFacade = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request =
                    new(new("/ed-fi/noSubclassIdentityDocumentKeys/123"), null, [], new(""));

                // Act
                FrontendResponse response = await coreFacade.GetById(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }
        }
    }
}
