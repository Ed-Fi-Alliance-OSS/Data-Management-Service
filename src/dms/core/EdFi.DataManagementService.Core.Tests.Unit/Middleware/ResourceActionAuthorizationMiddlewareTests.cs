// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;
using static EdFi.DataManagementService.Core.UtilityService;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ResourceActionAuthorizationMiddlewareTests
{
    private RequestInfo _requestInfo = No.RequestInfo();

    internal static IPipelineStep Middleware()
    {
        var expectedAuthStrategy = "NoFurtherAuthorizationRequired";
        var claimSetProvider = A.Fake<IClaimSetProvider>();
        A.CallTo(() => claimSetProvider.GetAllClaimSets())
            .Returns(
                [
                    new ClaimSet(
                        Name: "SIS-Vendor",
                        ResourceClaims:
                        [
                            new ResourceClaim(
                                $"{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school",
                                "Create",
                                [new AuthorizationStrategy(expectedAuthStrategy)]
                            ),
                        ]
                    ),
                ]
            );
        return new ResourceActionAuthorizationMiddleware(claimSetProvider, NullLogger.Instance);
    }

    internal static ApiSchemaDocuments ApiSchemaDocument(string resourceName)
    {
        ApiSchemaDocuments apiSchemaDocument = new ApiSchemaBuilder()
            .WithStartProject("Ed-Fi", "5.0.0")
            .WithStartResource(resourceName)
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();
        return apiSchemaDocument;
    }

    internal static IPipelineStep NoAuthStrategyMiddleware()
    {
        var claimSetProvider = A.Fake<IClaimSetProvider>();
        A.CallTo(() => claimSetProvider.GetAllClaimSets())
            .Returns(
                [
                    new ClaimSet(
                        Name: "SIS-Vendor",
                        ResourceClaims:
                        [
                            new ResourceClaim(
                                $"{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school",
                                "Create",
                                []
                            ),
                        ]
                    ),
                ]
            );
        return new ResourceActionAuthorizationMiddleware(claimSetProvider, NullLogger.Instance);
    }

    [TestFixture]
    [Parallelizable]
    public class GivenMatchingResourceActionClaim : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontEndRequest, RequestMethod.POST)
            {
                ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], []),
                PathComponents = new PathComponents(
                    new Core.ApiSchema.Model.ProjectEndpointName("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
            };
            _requestInfo.ProjectSchema = ApiSchemaDocument("School")
                .FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                    ?? new JsonObject()
            );

            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_No_response()
        {
            _requestInfo?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_No_Matching_ClaimSet : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontEndRequest, RequestMethod.POST)
            {
                ClientAuthorizations = new ClientAuthorizations("", "", "NO-MATCH", [], [], []),
                PathComponents = new PathComponents(
                    new Core.ApiSchema.Model.ProjectEndpointName("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
            };
            _requestInfo.ProjectSchema = ApiSchemaDocument("School")
                .FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                    ?? new JsonObject()
            );
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_has_forbidden_response()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(403);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class GivenNoMatchingResourceActionClaim : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/stateDescriptor",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontEndRequest, RequestMethod.POST)
            {
                ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], []),
                PathComponents = new PathComponents(
                    new Core.ApiSchema.Model.ProjectEndpointName("ed-fi"),
                    new EndpointName("stateDescriptor"),
                    new DocumentUuid()
                ),
            };
            _requestInfo.ProjectSchema = ApiSchemaDocument("StateDescriptor")
                .FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("stateDescriptors"))
                    ?? new JsonObject()
            );
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_has_forbidden_response()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(403);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class GivenMatchingResourceActionClaimAction : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontEndRequest, RequestMethod.POST)
            {
                ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], []),
                PathComponents = new PathComponents(
                    new Core.ApiSchema.Model.ProjectEndpointName("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
            };
            _requestInfo.ProjectSchema = ApiSchemaDocument("School")
                .FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                    ?? new JsonObject()
            );
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class GivenNoMatchingResourceActionClaimAction : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontEndRequest, RequestMethod.PUT)
            {
                ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], []),
                PathComponents = new PathComponents(
                    new Core.ApiSchema.Model.ProjectEndpointName("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
            };
            _requestInfo.ProjectSchema = ApiSchemaDocument("School")
                .FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                    ?? new JsonObject()
            );
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_has_forbidden_response()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(403);
        }

        [Test]
        public void It_returns_message_body_with_failures()
        {
            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("Authorization Denied");

            string response = JsonSerializer.Serialize(_requestInfo.FrontendResponse.Body, SerializerOptions);

            response
                .Should()
                .Contain(
                    "\"errors\":[\"The API client's assigned claim set (currently 'SIS-Vendor') must grant permission of the 'Update' action on one of the following resource claims: School\"]"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class GivenNoResourceActionClaimActions : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            var claimSetProvider = A.Fake<IClaimSetProvider>();
            A.CallTo(() => claimSetProvider.GetAllClaimSets())
                .Returns(
                    [new ClaimSet(Name: "SIS-Vendor", ResourceClaims: [new ResourceClaim("schools", "", [])])]
                );
            var authMiddleware = new ResourceActionAuthorizationMiddleware(
                claimSetProvider,
                NullLogger.Instance
            );

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontEndRequest, RequestMethod.PUT)
            {
                ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], []),
                PathComponents = new PathComponents(
                    new Core.ApiSchema.Model.ProjectEndpointName("ed-fi"),
                    new EndpointName("stateDescriptor"),
                    new DocumentUuid()
                ),
            };
            _requestInfo.ProjectSchema = ApiSchemaDocument("School")
                .FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                    ?? new JsonObject()
            );
            await authMiddleware.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_has_forbidden_response()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(403);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class GivenMatchingResourceActionClaimActionAuthStrategy
        : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontEndRequest, RequestMethod.POST)
            {
                ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], []),
                PathComponents = new PathComponents(
                    new Core.ApiSchema.Model.ProjectEndpointName("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
            };
            _requestInfo.ProjectSchema = ApiSchemaDocument("School")
                .FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                    ?? new JsonObject()
            );
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class GivenNoMatchingResourceActionClaimActionAuthStrategy
        : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontEndRequest, RequestMethod.POST)
            {
                ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], []),
                PathComponents = new PathComponents(
                    new Core.ApiSchema.Model.ProjectEndpointName("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
            };
            _requestInfo.ProjectSchema = ApiSchemaDocument("School")
                .FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                    ?? new JsonObject()
            );
            await NoAuthStrategyMiddleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_has_forbidden_response()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(403);
        }

        [Test]
        public void It_returns_message_body_with_failures()
        {
            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("Authorization Denied");

            string response = JsonSerializer.Serialize(
                _requestInfo.FrontendResponse.Body,
                UtilityService.SerializerOptions
            );

            response
                .Should()
                .Contain(
                    "\"errors\":[\"No authorization strategies were defined for the requested action 'Create' against resource ['School'] matched by the caller's claim 'SIS-Vendor'.\"]"
                );
        }
    }
}
