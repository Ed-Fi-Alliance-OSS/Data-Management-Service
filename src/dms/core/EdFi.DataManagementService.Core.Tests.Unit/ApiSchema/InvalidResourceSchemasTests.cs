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
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.ResourceLoadOrder;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Validation;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Polly;

namespace EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;

/// <summary>
/// Tests error handling for invalid resource schemas.
/// </summary>
[TestFixture]
[NonParallelizable]
public class InvalidResourceSchemasTests
{
    [TestFixture]
    public class Given_An_ApiSchema_File_With_Invalid_ResourceSchemas
    {
        internal static ApiService BuildCoreFacade(IApiSchemaProvider apiSchemaProvider)
        {
            var services = new ServiceCollection();

            // Create a fake JWT validation service that always returns success
            var fakeJwtValidationService = A.Fake<IJwtValidationService>();
            A.CallTo(() =>
                    fakeJwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                        A<string>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(
                    Task.FromResult<(System.Security.Claims.ClaimsPrincipal?, ClientAuthorizations?)>(
                        (
                            new System.Security.Claims.ClaimsPrincipal(),
                            new ClientAuthorizations(
                                TokenId: "test-token",
                                ClaimSetName: "test-claimset",
                                EducationOrganizationIds: new List<EducationOrganizationId>(),
                                NamespacePrefixes: new List<NamespacePrefix>()
                            )
                        )
                    )
                );

            // Register the fake as singleton first, before any other registrations
            services.AddSingleton<IJwtValidationService>(fakeJwtValidationService);

            // Register JWT authentication services
            services.Configure<JwtAuthenticationOptions>(options => { });
            services.AddTransient<JwtAuthenticationMiddleware>();
            services.AddTransient<ILogger<JwtAuthenticationMiddleware>>(_ =>
                NullLogger<JwtAuthenticationMiddleware>.Instance
            );

            var serviceProvider = services.BuildServiceProvider();
            var apiSchemaUploadService = A.Fake<IUploadApiSchemaService>();

            return new ApiService(
                apiSchemaProvider,
                new SuccessDocumentStoreRepository(NullLogger<SuccessDocumentStoreRepository>.Instance),
                new NoClaimsClaimSetProvider(NullLogger.Instance),
                new DocumentValidator(),
                new SuccessDocumentStoreRepository(NullLogger<SuccessDocumentStoreRepository>.Instance),
                new MatchingDocumentUuidsValidator(),
                new EqualityConstraintValidator(),
                new DecimalValidator(),
                NullLogger<ApiService>.Instance,
                Options.Create(new AppSettings { AllowIdentityUpdateOverrides = "" }),
                new NamedAuthorizationServiceFactory(serviceProvider),
                ResiliencePipeline.Empty,
                new ResourceLoadOrderCalculator(
                    A.Fake<IApiSchemaProvider>(),
                    [],
                    [],
                    NullLogger<ResourceLoadOrderCalculator>.Instance
                ),
                apiSchemaUploadService,
                serviceProvider
            );
        }

        internal IApiSchemaProvider? apiSchemaProvider;

        private static Dictionary<string, string> AuthHeaders =>
            new() { ["Authorization"] = "Bearer test-token" };

        [SetUp]
        public void Setup()
        {
            var schemaContent = JsonNode.Parse(File.ReadAllText("ApiSchema/InvalidResourceSchemas.json"));
            apiSchemaProvider = A.Fake<IApiSchemaProvider>();
            A.CallTo(() => apiSchemaProvider.GetApiSchemaNodes())
                .Returns(new ApiSchemaDocumentNodes(schemaContent!, []));
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
                FrontendRequest request = new(
                    Path: new("/ed-fi/noresourcenames/123"),
                    Body: null,
                    Headers: AuthHeaders,
                    QueryParameters: [],
                    TraceId: new TraceId("")
                );

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
                FrontendRequest request = new(
                    Path: new("/ed-fi/noIsDescriptors/123"),
                    Body: null,
                    Headers: AuthHeaders,
                    QueryParameters: [],
                    TraceId: new TraceId("")
                );

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
                FrontendRequest request = new(
                    Path: new("/ed-fi/noallowidentityupdates/123"),
                    Body: null,
                    Headers: AuthHeaders,
                    QueryParameters: [],
                    TraceId: new TraceId("")
                );

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
                FrontendRequest request = new(
                    Path: new("/ed-fi/noIsSchoolYearEnumerations/123"),
                    Body: null,
                    Headers: AuthHeaders,
                    QueryParameters: [],
                    TraceId: new TraceId("")
                );

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
                FrontendRequest request = new(
                    Path: new("/ed-fi/noJsonSchemaForInserts/123"),
                    Body: null,
                    Headers: AuthHeaders,
                    QueryParameters: [],
                    TraceId: new TraceId("")
                );

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
                FrontendRequest request = new(
                    Path: new("/ed-fi/noidentityjsonpaths/123"),
                    Body: null,
                    Headers: AuthHeaders,
                    QueryParameters: [],
                    TraceId: new TraceId("")
                );

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
                FrontendRequest request = new(
                    Path: new("/ed-fi/noequalityconstraints/123"),
                    Body: null,
                    Headers: AuthHeaders,
                    QueryParameters: [],
                    TraceId: new TraceId("")
                );

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
                FrontendRequest request = new(
                    Path: new("/ed-fi/noIsSubclasses/123"),
                    Body: null,
                    Headers: AuthHeaders,
                    QueryParameters: [],
                    TraceId: new TraceId("")
                );

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
                FrontendRequest request = new(
                    Path: new("/ed-fi/noSubClassTypes/123"),
                    Body: null,
                    Headers: AuthHeaders,
                    QueryParameters: [],
                    TraceId: new TraceId("")
                );

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
                FrontendRequest request = new(
                    Path: new("/ed-fi/nosuperclassresourcenames/123"),
                    Body: null,
                    Headers: AuthHeaders,
                    QueryParameters: [],
                    TraceId: new TraceId("")
                );

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
                FrontendRequest request = new(
                    Path: new("/ed-fi/nosuperclassprojectnames/123"),
                    Body: null,
                    Headers: AuthHeaders,
                    QueryParameters: [],
                    TraceId: new TraceId("")
                );

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
                FrontendRequest request = new(
                    Path: new("/ed-fi/nosuperclassidentitydocumentkeys/123"),
                    Body: null,
                    Headers: AuthHeaders,
                    QueryParameters: [],
                    TraceId: new TraceId("")
                );

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
                FrontendRequest request = new(
                    Path: new("/ed-fi/noSubclassIdentityDocumentKeys/123"),
                    Body: null,
                    Headers: AuthHeaders,
                    QueryParameters: [],
                    TraceId: new TraceId("")
                );

                // Act
                IFrontendResponse response = await apiService.Get(request);

                // Assert
                response.StatusCode.Should().Be(500);
            }
        }
    }
}
