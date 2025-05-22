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
public class ResourceActionAuthorizationMiddlewareTests
{
    private PipelineContext _context = No.PipelineContext();

    internal static IPipelineStep Middleware()
    {
        var expectedAuthStrategy = "NoFurtherAuthorizationRequired";
        var claimSetCacheService = A.Fake<IClaimSetCacheService>();
        A.CallTo(() => claimSetCacheService.GetClaimSets())
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
        return new ResourceActionAuthorizationMiddleware(claimSetCacheService, NullLogger.Instance);
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
        var claimSetCacheService = A.Fake<IClaimSetCacheService>();
        A.CallTo(() => claimSetCacheService.GetClaimSets())
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
        return new ResourceActionAuthorizationMiddleware(claimSetCacheService, NullLogger.Instance);
    }

    [TestFixture]
    public class GivenMatchingResourceActionClaim : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Header: null,
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                ClientAuthorizations: new ClientAuthorizations("", "SIS-Vendor", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.POST)
            {
                PathComponents = new PathComponents(
                    new ProjectNamespace("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
            };
            _context.ProjectSchema = ApiSchemaDocument("School")
                .FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            _context.ResourceSchema = new ResourceSchema(
                _context.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                    ?? new JsonObject()
            );

            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_has_No_response()
        {
            _context?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class Given_No_Matching_ClaimSet : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Header: null,
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                ClientAuthorizations: new ClientAuthorizations("", "NO-MATCH", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.POST)
            {
                PathComponents = new PathComponents(
                    new ProjectNamespace("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
            };
            _context.ProjectSchema = ApiSchemaDocument("School")
                .FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            _context.ResourceSchema = new ResourceSchema(
                _context.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                    ?? new JsonObject()
            );
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_has_forbidden_response()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(403);
        }
    }

    [TestFixture]
    public class GivenNoMatchingResourceActionClaim : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/stateDescriptor",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Header: null,
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                ClientAuthorizations: new ClientAuthorizations("", "SIS-Vendor", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.POST)
            {
                PathComponents = new PathComponents(
                    new ProjectNamespace("ed-fi"),
                    new EndpointName("stateDescriptor"),
                    new DocumentUuid()
                ),
            };
            _context.ProjectSchema = ApiSchemaDocument("StateDescriptor")
                .FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            _context.ResourceSchema = new ResourceSchema(
                _context.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("stateDescriptors"))
                    ?? new JsonObject()
            );
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_has_forbidden_response()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(403);
        }
    }

    [TestFixture]
    public class GivenMatchingResourceActionClaimAction : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Header: null,
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                ClientAuthorizations: new ClientAuthorizations("", "SIS-Vendor", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.POST)
            {
                PathComponents = new PathComponents(
                    new ProjectNamespace("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
            };
            _context.ProjectSchema = ApiSchemaDocument("School")
                .FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            _context.ResourceSchema = new ResourceSchema(
                _context.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                    ?? new JsonObject()
            );
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class GivenNoMatchingResourceActionClaimAction : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Header: null,
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                ClientAuthorizations: new ClientAuthorizations("", "SIS-Vendor", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.PUT)
            {
                PathComponents = new PathComponents(
                    new ProjectNamespace("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
            };
            _context.ProjectSchema = ApiSchemaDocument("School")
                .FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            _context.ResourceSchema = new ResourceSchema(
                _context.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                    ?? new JsonObject()
            );
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_has_forbidden_response()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(403);
        }

        [Test]
        public void It_returns_message_body_with_failures()
        {
            _context.FrontendResponse.Body?.ToJsonString().Should().Contain("Authorization Denied");

            string response = JsonSerializer.Serialize(_context.FrontendResponse.Body, SerializerOptions);

            response
                .Should()
                .Contain(
                    "\"errors\":[\"The API client's assigned claim set (currently 'SIS-Vendor') must grant permission of the 'Update' action on one of the following resource claims: School\"]"
                );
        }
    }

    [TestFixture]
    public class GivenNoResourceActionClaimActions : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            var claimSetCacheService = A.Fake<IClaimSetCacheService>();
            A.CallTo(() => claimSetCacheService.GetClaimSets())
                .Returns(
                    [new ClaimSet(Name: "SIS-Vendor", ResourceClaims: [new ResourceClaim("schools", "", [])])]
                );
            var authMiddleware = new ResourceActionAuthorizationMiddleware(
                claimSetCacheService,
                NullLogger.Instance
            );

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Header: null,
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                ClientAuthorizations: new ClientAuthorizations("", "SIS-Vendor", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.PUT)
            {
                PathComponents = new PathComponents(
                    new ProjectNamespace("ed-fi"),
                    new EndpointName("stateDescriptor"),
                    new DocumentUuid()
                ),
            };
            _context.ProjectSchema = ApiSchemaDocument("School")
                .FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            _context.ResourceSchema = new ResourceSchema(
                _context.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                    ?? new JsonObject()
            );
            await authMiddleware.Execute(_context, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_has_forbidden_response()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(403);
        }
    }

    [TestFixture]
    public class GivenMatchingResourceActionClaimActionAuthStrategy
        : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Header: null,
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                ClientAuthorizations: new ClientAuthorizations("", "SIS-Vendor", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.POST)
            {
                PathComponents = new PathComponents(
                    new ProjectNamespace("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
            };
            _context.ProjectSchema = ApiSchemaDocument("School")
                .FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            _context.ResourceSchema = new ResourceSchema(
                _context.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                    ?? new JsonObject()
            );
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class GivenNoMatchingResourceActionClaimActionAuthStrategy
        : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Header: null,
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                ClientAuthorizations: new ClientAuthorizations("", "SIS-Vendor", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.POST)
            {
                PathComponents = new PathComponents(
                    new ProjectNamespace("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
            };
            _context.ProjectSchema = ApiSchemaDocument("School")
                .FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            _context.ResourceSchema = new ResourceSchema(
                _context.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                    ?? new JsonObject()
            );
            await NoAuthStrategyMiddleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_has_forbidden_response()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(403);
        }

        [Test]
        public void It_returns_message_body_with_failures()
        {
            _context.FrontendResponse.Body?.ToJsonString().Should().Contain("Authorization Denied");

            string response = JsonSerializer.Serialize(
                _context.FrontendResponse.Body,
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
