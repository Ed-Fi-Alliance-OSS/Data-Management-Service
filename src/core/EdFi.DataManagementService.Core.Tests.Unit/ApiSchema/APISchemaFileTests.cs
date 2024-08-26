// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Validation;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Polly;

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
    public class Given_An_ApiSchema_File_With_Invalid_ResourceSchemas
    {
        internal static ApiService BuildCoreFacade(IApiSchemaProvider apiSchemaProvider)
        {
            return new ApiService(
                apiSchemaProvider,
                new ApiSchemaValidator(
                    new ApiSchemaSchemaProvider(NullLogger<ApiSchemaSchemaProvider>.Instance)
                ),
                new SuccessDocumentStoreRepository(NullLogger<SuccessDocumentStoreRepository>.Instance),
                new DocumentValidator(),
                new SuccessDocumentStoreRepository(NullLogger<SuccessDocumentStoreRepository>.Instance),
                new MatchingDocumentUuidsValidator(),
                new EqualityConstraintValidator(),
                NullLogger<ApiService>.Instance,
                Options.Create(new AppSettings
                {
                    AllowIdentityUpdateOverrides = ""
                }),
                ResiliencePipeline.Empty
            );
        }

        internal IApiSchemaProvider? apiSchemaProvider;

        [SetUp]
        public void Setup()
        {
            var schemaContent = JsonNode.Parse(File.ReadAllText("ApiSchema/InvalidResourceSchemas.json"));
            apiSchemaProvider = A.Fake<IApiSchemaProvider>();
            A.CallTo(() => apiSchemaProvider.ApiSchemaRootNode).Returns(schemaContent!);
        }

        [TestFixture]
        public class Should_Respond_With_Internal_Server_Error_For_A_Get_Request
            : Given_An_ApiSchema_File_With_Invalid_ResourceSchemas
        {
            // "resourceName" element does not exist on resource schema.
            [Test]
            public async Task When_no_resourcename_element()
            {
                // Arrange
                ApiService apiService = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/noresourcenames/123"), null, [], new TraceId(""));

                // Act
                IFrontendResponse response = await apiService.Get(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "isDescriptor" element does not exist on resource schema.
            [Test]
            public async Task When_no_isdescriptor_element()
            {
                // Arrange
                ApiService apiService = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/noIsDescriptors/123"), null, [], new TraceId(""));

                // Act
                IFrontendResponse response = await apiService.Get(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "allowIdentityUpdates" element does not exist on resource schema.
            [Test]
            public async Task When_no_allowidentityupdates_element()
            {
                // Arrange
                ApiService apiService = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/noallowidentityupdates/123"), null, [], new TraceId(""));

                // Act
                IFrontendResponse response = await apiService.Get(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }
        }

        [TestFixture]
        public class Should_Respond_With_Internal_Server_Error_For_A_Post_Request
            : Given_An_ApiSchema_File_With_Invalid_ResourceSchemas
        {
            // "isShoolyearEnumeration" element does not exist on resource schema.
            [Test]
            public async Task When_no_isshoolyearenumeration_element()
            {
                // Arrange
                ApiService apiService = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request =
                    new(new("/ed-fi/noIsSchoolYearEnumerations/123"), null, [], new TraceId(""));

                // Act
                IFrontendResponse response = await apiService.Get(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "jsonSchemaForInsert" element does not exist on resource schema.
            [Test]
            public async Task When_no_jsonschemaforinsert_element()
            {
                // Arrange
                ApiService apiService = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/noJsonSchemaForInserts/123"), null, [], new TraceId(""));

                // Act
                IFrontendResponse response = await apiService.Get(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "identityjsonpaths" element does not exist on resource schema.
            [Test]
            public async Task When_no_identityjsonpaths_element()
            {
                // Arrange
                ApiService apiService = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/noidentityjsonpaths/123"), null, [], new TraceId(""));

                // Act
                IFrontendResponse response = await apiService.Get(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "equalityconstraints" element does not exist on resource schema.
            [Test]
            public async Task When_no_equalityconstraints_element()
            {
                // Arrange
                ApiService apiService = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/noequalityconstraints/123"), null, [], new TraceId(""));

                // Act
                IFrontendResponse response = await apiService.Get(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "isSubclass" element does not exist on resource schema.
            [Test]
            public async Task When_no_issubclass_element()
            {
                // Arrange
                ApiService apiService = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/noIsSubclasses/123"), null, [], new TraceId(""));

                // Act
                IFrontendResponse response = await apiService.Get(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "subclassType" element does not exist on resource schema.
            [Test]
            public async Task When_no_subclasstype_element()
            {
                // Arrange
                ApiService apiService = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/noSubClassTypes/123"), null, [], new TraceId(""));

                // Act
                IFrontendResponse response = await apiService.Get(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "superClassResourceName" element does not exist on resource schema.
            [Test]
            public async Task When_no_superclassresourcename_element()
            {
                // Arrange
                ApiService apiService = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/nosuperclassresourcenames/123"), null, [], new TraceId(""));

                // Act
                IFrontendResponse response = await apiService.Get(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "superclassProjectName" element does not exist on resource schema.
            [Test]
            public async Task When_no_superclassprojectname_element()
            {
                // Arrange
                ApiService apiService = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request = new(new("/ed-fi/nosuperclassprojectnames/123"), null, [], new TraceId(""));

                // Act
                IFrontendResponse response = await apiService.Get(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "superclassIdentityDocumentKey" element does not exist on resource schema.
            [Test]
            public async Task When_no_superclassidentitydocumentkey_element()
            {
                // Arrange
                ApiService apiService = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request =
                    new(new("/ed-fi/nosuperclassidentitydocumentkeys/123"), null, [], new TraceId(""));

                // Act
                IFrontendResponse response = await apiService.Get(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }

            // "subclassIdentityDocumentKey" element does not exist on resource schema.
            [Test]
            public async Task When_no_subclassidentitydocumentkey_element()
            {
                // Arrange
                ApiService apiService = BuildCoreFacade(apiSchemaProvider!);
                FrontendRequest request =
                    new(new("/ed-fi/noSubclassIdentityDocumentKeys/123"), null, [], new TraceId(""));

                // Act
                IFrontendResponse response = await apiService.Get(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }
        }
    }
}
