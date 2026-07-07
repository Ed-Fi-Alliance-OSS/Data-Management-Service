// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;
using static EdFi.DataManagementService.Core.UtilityService;
using RelationalWriteSeamFixture = EdFi.DataManagementService.Core.Tests.Unit.Handler.RelationalWriteSeamFixture;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ResourceActionAuthorizationMiddlewareTests
{
    private RequestInfo _requestInfo = No.RequestInfo();

    internal static IPipelineStep Middleware(
        string action = "Create",
        params string[] expectedAuthStrategies
    ) => MiddlewareCore(action, expectedAuthStrategies);

    private static IPipelineStep MiddlewareCore(string action, params string[] expectedAuthStrategies)
    {
        string[] authorizationStrategies =
            expectedAuthStrategies.Length == 0
                ? [AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired]
                : expectedAuthStrategies;

        var claimSetProvider = A.Fake<IClaimSetProvider>();
        A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>.Ignored))
            .Returns([
                new ClaimSet(
                    Name: "SIS-Vendor",
                    ResourceClaims:
                    [
                        new ResourceClaim(
                            $"{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school",
                            action,
                            [
                                .. authorizationStrategies.Select(
                                    static strategy => new AuthorizationStrategy(strategy)
                                ),
                            ]
                        ),
                    ]
                ),
            ]);
        return new ResourceActionAuthorizationMiddleware(claimSetProvider, NullLogger.Instance);
    }

    internal static IPipelineStep MiddlewareCoreReadChanges(string action, params string[] strategies)
    {
        var claimSetProvider = A.Fake<IClaimSetProvider>();
        A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>.Ignored))
            .Returns([
                new ClaimSet(
                    Name: "SIS-Vendor",
                    ResourceClaims:
                    [
                        new ResourceClaim(
                            $"{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school",
                            action,
                            [.. strategies.Select(static s => new AuthorizationStrategy(s))]
                        ),
                    ]
                ),
            ]);
        return new ResourceActionAuthorizationMiddleware(claimSetProvider, NullLogger.Instance);
    }

    internal static RequestInfo CreateRequestInfo(
        RequestMethod requestMethod,
        string path,
        bool hasDocumentUuidSegment = false,
        string endpointName = "schools",
        string resourceName = "School"
    )
    {
        FrontendRequest frontEndRequest = new(
            Path: path,
            Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
            Form: null,
            Headers: [],
            QueryParameters: [],
            TraceId: new TraceId("traceId"),
            RouteQualifiers: []
        );

        var documentUuid = hasDocumentUuidSegment
            ? new DocumentUuid(Guid.Parse("11111111-1111-1111-1111-111111111111"))
            : new DocumentUuid();

        var requestInfo = new RequestInfo(frontEndRequest, requestMethod, No.ServiceProvider)
        {
            ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], []),
            PathComponents = new PathComponents(
                new ProjectEndpointName("ed-fi"),
                new EndpointName(endpointName),
                documentUuid,
                hasDocumentUuidSegment
            ),
        };

        requestInfo.ProjectSchema = ApiSchemaDocument(resourceName)
            .FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
        requestInfo.ResourceSchema = new ResourceSchema(
            requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new(endpointName))
                ?? new JsonObject()
        );
        requestInfo.ResourceInfo = new ResourceInfo(
            requestInfo.ProjectSchema.ProjectName,
            requestInfo.ResourceSchema.ResourceName,
            requestInfo.ResourceSchema.IsDescriptor,
            requestInfo.ProjectSchema.ResourceVersion,
            requestInfo.ResourceSchema.AllowIdentityUpdates
        );

        return requestInfo;
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

    internal static void AssertExpectedServerErrorResponse(
        IFrontendResponse response,
        string expectedMessage,
        string expectedTraceId
    )
    {
        response.StatusCode.Should().Be(500);
        response.ContentType.Should().Be("application/json");

        JsonObject body = response.Body!.AsObject();

        body.Select(property => property.Key).Should().BeEquivalentTo("message", "traceId");
        body["message"]?.GetValue<string>().Should().Be(expectedMessage);
        body["traceId"]?.GetValue<string>().Should().Be(expectedTraceId);
        body["detail"].Should().BeNull();
        body["type"].Should().BeNull();
        body["title"].Should().BeNull();
        body["status"].Should().BeNull();
        body["correlationId"].Should().BeNull();
    }

    internal static void AssertExpectedSecurityConfigurationResponse(
        IFrontendResponse response,
        string expectedError
    )
    {
        response.StatusCode.Should().Be(500);
        response.ContentType.Should().Be("application/problem+json");

        JsonObject body = response.Body!.AsObject();

        body["type"]?.GetValue<string>().Should().Be(SecurityConfigurationProblemDetails.Type);
        body["title"]?.GetValue<string>().Should().Be(SecurityConfigurationProblemDetails.Title);
        body["status"]?.GetValue<int>().Should().Be(SecurityConfigurationProblemDetails.Status);
        body["detail"]?.GetValue<string>().Should().Be(SecurityConfigurationProblemDetails.Detail);
        body["correlationId"]?.GetValue<string>().Should().Be("traceId");
        body["validationErrors"]!.AsObject().Should().BeEmpty();
        body["errors"]!.AsArray().Select(error => error!.GetValue<string>()).Should().Equal(expectedError);
    }

    internal static IPipelineStep MiddlewareWithClaimSets(params ClaimSet[] claimSets) =>
        MiddlewareWithClaimSetsCore(claimSets);

    private static IPipelineStep MiddlewareWithClaimSetsCore(params ClaimSet[] claimSets)
    {
        var claimSetProvider = A.Fake<IClaimSetProvider>();
        A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>.Ignored)).Returns(claimSets.ToList());
        return new ResourceActionAuthorizationMiddleware(claimSetProvider, NullLogger.Instance);
    }

    internal static IPipelineStep NoAuthStrategyMiddleware(string action = "Create", ILogger? logger = null)
    {
        var claimSetProvider = A.Fake<IClaimSetProvider>();
        A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>.Ignored))
            .Returns([
                new ClaimSet(
                    Name: "SIS-Vendor",
                    ResourceClaims:
                    [
                        new ResourceClaim(
                            $"{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school",
                            action,
                            []
                        ),
                    ]
                ),
            ]);
        return new ResourceActionAuthorizationMiddleware(claimSetProvider, logger ?? NullLogger.Instance);
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
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontEndRequest, RequestMethod.POST, No.ServiceProvider)
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
    public class Given_No_Client_Authorizations : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontEndRequest, RequestMethod.POST, No.ServiceProvider)
            {
                ClientAuthorizations = No.ClientAuthorizations,
            };

            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_401_unauthorized()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(401);
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        }

        [Test]
        public void It_returns_the_authentication_failed_problem_details()
        {
            AssertUnauthorizedProblemDetails(
                _requestInfo.FrontendResponse,
                "No authorization information found. Ensure valid JWT token is provided."
            );
        }

        [Test]
        public void It_includes_www_authenticate_header()
        {
            _requestInfo.FrontendResponse.Headers.Should().ContainKey("WWW-Authenticate");
            _requestInfo
                .FrontendResponse.Headers["WWW-Authenticate"]
                .Should()
                .Be("Bearer error=\"invalid_token\"");
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
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontEndRequest, RequestMethod.POST, No.ServiceProvider)
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
        public void It_returns_missing_security_metadata_problem_details()
        {
            AssertExpectedSecurityConfigurationResponse(
                _requestInfo.FrontendResponse,
                SecurityConfigurationFailureMessages.MissingSecurityMetadata
            );
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
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontEndRequest, RequestMethod.POST, No.ServiceProvider)
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
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontEndRequest, RequestMethod.POST, No.ServiceProvider)
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
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontEndRequest, RequestMethod.PUT, No.ServiceProvider)
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
            A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>.Ignored))
                .Returns([
                    new ClaimSet(Name: "SIS-Vendor", ResourceClaims: [new ResourceClaim("schools", "", [])]),
                ]);
            var authMiddleware = new ResourceActionAuthorizationMiddleware(
                claimSetProvider,
                NullLogger.Instance
            );

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontEndRequest, RequestMethod.PUT, No.ServiceProvider)
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
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontEndRequest, RequestMethod.POST, No.ServiceProvider)
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
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontEndRequest, RequestMethod.POST, No.ServiceProvider)
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
        public void It_returns_security_configuration_problem_details()
        {
            AssertExpectedSecurityConfigurationResponse(
                _requestInfo.FrontendResponse,
                $"No authorization strategies were defined for the requested action 'Create' against resource URIs ['{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school'] matched by the caller's claim '{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school'."
            );
        }

        [Test]
        public void It_returns_the_matched_resource_claim_uri()
        {
            string response = JsonSerializer.Serialize(
                _requestInfo.FrontendResponse.Body,
                UtilityService.SerializerOptions
            );

            response
                .Should()
                .Contain(
                    $"\"errors\":[\"No authorization strategies were defined for the requested action 'Create' against resource URIs ['{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school'] matched by the caller's claim '{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school'.\"]"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Claim_Set_Provider_Throws : ResourceActionAuthorizationMiddlewareTests
    {
        private FrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var claimSetProvider = A.Fake<IClaimSetProvider>();

            A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>.Ignored))
                .Throws(new InvalidOperationException("simulated failure"));

            var middleware = new ResourceActionAuthorizationMiddleware(claimSetProvider, NullLogger.Instance);

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontEndRequest, RequestMethod.POST, No.ServiceProvider)
            {
                ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], []),
            };

            await middleware.Execute(_requestInfo, NullNext);

            _response = (FrontendResponse)_requestInfo.FrontendResponse;
        }

        [Test]
        public void It_returns_the_expected_500_body()
        {
            AssertExpectedServerErrorResponse(_response, "Error while authorizing the request.", "traceId");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_RelationshipsWithEdOrgsOnlyInverted_On_Non_Query_Request
        : ResourceActionAuthorizationMiddlewareTests
    {
        [TestCase("GET", "Read", "ed-fi/schools/11111111-1111-1111-1111-111111111111", true)]
        [TestCase("POST", "Create", "ed-fi/schools", false)]
        [TestCase("PUT", "Update", "ed-fi/schools/11111111-1111-1111-1111-111111111111", true)]
        [TestCase("DELETE", "Delete", "ed-fi/schools/11111111-1111-1111-1111-111111111111", true)]
        public async Task It_allows_the_strategy_for_the_relational_authorization_pipeline(
            string requestMethodName,
            string action,
            string path,
            bool hasDocumentUuidSegment
        )
        {
            var requestMethod = Enum.Parse<RequestMethod>(requestMethodName);
            _requestInfo = CreateRequestInfo(requestMethod, path, hasDocumentUuidSegment);

            await Middleware(action, AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted)
                .Execute(_requestInfo, NullNext);

            _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
            _requestInfo
                .ResourceActionAuthStrategies.Should()
                .ContainSingle(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_RelationshipsWithEdOrgsOnlyInverted_On_Get_Many
        : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(RequestMethod.GET, "ed-fi/schools");

            await Middleware("Read", AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted)
                .Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_allows_the_request_to_continue()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Relational_Single_Record_With_RelationshipsWithEdOrgsOnlyInverted
        : ResourceActionAuthorizationMiddlewareTests
    {
        [TestCase("GET", "Read", "ed-fi/schools/11111111-1111-1111-1111-111111111111")]
        [TestCase("DELETE", "Delete", "ed-fi/schools/11111111-1111-1111-1111-111111111111")]
        public async Task It_allows_the_request_to_continue(
            string requestMethodName,
            string action,
            string path
        )
        {
            var requestMethod = Enum.Parse<RequestMethod>(requestMethodName);
            _requestInfo = CreateRequestInfo(requestMethod, path, hasDocumentUuidSegment: true);
            _requestInfo.MappingSet = RelationalWriteSeamFixture
                .Create()
                .CreateSupportedMappingSet(SqlDialect.Pgsql);

            await Middleware(action, AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted)
                .Execute(_requestInfo, NullNext);

            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            _requestInfo
                .ResourceActionAuthStrategies.Should()
                .Equal(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Relational_Post_With_RelationshipsWithEdOrgsOnlyInverted
        : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(RequestMethod.POST, "ed-fi/schools");
            _requestInfo.MappingSet = RelationalWriteSeamFixture
                .Create()
                .CreateSupportedMappingSet(SqlDialect.Pgsql);

            await Middleware("Create", AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted)
                .Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_allows_the_request_to_continue()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            _requestInfo
                .ResourceActionAuthStrategies.Should()
                .Equal(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Relational_Post_With_EdOrg_And_Known_Not_Enabled_Strategies
        : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(RequestMethod.POST, "ed-fi/schools");
            _requestInfo.MappingSet = RelationalWriteSeamFixture
                .Create()
                .CreateSupportedMappingSet(SqlDialect.Pgsql);

            await Middleware(
                    "Create",
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                    AuthorizationStrategyNameConstants.NamespaceBased
                )
                .Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_preserves_the_strategy_set_for_repository_planning()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            _requestInfo
                .ResourceActionAuthStrategies.Should()
                .Equal(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                    AuthorizationStrategyNameConstants.NamespaceBased
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Relational_Put_With_RelationshipsWithEdOrgsOnlyInverted
        : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(
                RequestMethod.PUT,
                "ed-fi/schools/11111111-1111-1111-1111-111111111111",
                hasDocumentUuidSegment: true
            );
            _requestInfo.MappingSet = RelationalWriteSeamFixture
                .Create()
                .CreateSupportedMappingSet(SqlDialect.Pgsql);

            await Middleware("Update", AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted)
                .Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_allows_the_request_to_continue()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            _requestInfo
                .ResourceActionAuthStrategies.Should()
                .Equal(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Relational_Get_Many_With_Empty_Claim_Set_Catalog
        : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(RequestMethod.GET, "ed-fi/schools");
            _requestInfo.MappingSet = RelationalWriteSeamFixture
                .Create()
                .CreateSupportedMappingSet(SqlDialect.Pgsql);

            await MiddlewareWithClaimSets().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_missing_security_metadata_problem_details()
        {
            AssertExpectedSecurityConfigurationResponse(
                _requestInfo.FrontendResponse,
                SecurityConfigurationFailureMessages.MissingSecurityMetadata
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Relational_Get_Many_With_Assigned_Claim_Set_Missing
        : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(RequestMethod.GET, "ed-fi/schools");
            _requestInfo.MappingSet = RelationalWriteSeamFixture
                .Create()
                .CreateSupportedMappingSet(SqlDialect.Pgsql);

            await MiddlewareWithClaimSets(new ClaimSet(Name: "Other-Claim-Set", ResourceClaims: []))
                .Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_missing_security_metadata_problem_details()
        {
            AssertExpectedSecurityConfigurationResponse(
                _requestInfo.FrontendResponse,
                SecurityConfigurationFailureMessages.MissingSecurityMetadata
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Relational_Get_Many_With_Empty_Assigned_Claim_Set_Resource_Claims
        : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(RequestMethod.GET, "ed-fi/schools");
            _requestInfo.MappingSet = RelationalWriteSeamFixture
                .Create()
                .CreateSupportedMappingSet(SqlDialect.Pgsql);

            await MiddlewareWithClaimSets(new ClaimSet(Name: "SIS-Vendor", ResourceClaims: []))
                .Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_still_returns_forbidden()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(403);
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Get_Many_With_No_Matching_ClaimSet : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(RequestMethod.GET, "ed-fi/schools");

            await MiddlewareWithClaimSets(new ClaimSet("OtherClaimSet", [])).Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_missing_metadata_security_configuration_problem_details()
        {
            AssertExpectedSecurityConfigurationResponse(
                _requestInfo.FrontendResponse,
                SecurityConfigurationFailureMessages.MissingSecurityMetadata
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_No_Authorization_Strategies_Before_Mapping_Set
        : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(RequestMethod.GET, "ed-fi/schools");

            await NoAuthStrategyMiddleware("Read").Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_security_configuration_problem_details()
        {
            AssertExpectedSecurityConfigurationResponse(
                _requestInfo.FrontendResponse,
                $"No authorization strategies were defined for the requested action 'Read' against resource URIs ['{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school'] matched by the caller's claim '{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school'."
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Relational_Get_Many_With_Duplicate_Matching_Action_Claims
        : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(RequestMethod.GET, "ed-fi/schools");
            _requestInfo.MappingSet = RelationalWriteSeamFixture
                .Create()
                .CreateSupportedMappingSet(SqlDialect.Pgsql);

            string schoolResourceClaimUri = $"{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school";

            await MiddlewareWithClaimSets(
                    new ClaimSet(
                        Name: "SIS-Vendor",
                        ResourceClaims:
                        [
                            new ResourceClaim(
                                schoolResourceClaimUri,
                                "Read",
                                [new AuthorizationStrategy(AuthorizationStrategyNameConstants.NamespaceBased)]
                            ),
                            new ResourceClaim(
                                schoolResourceClaimUri,
                                "Read",
                                [
                                    new AuthorizationStrategy(
                                        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                                    ),
                                ]
                            ),
                        ]
                    )
                )
                .Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_uses_deterministic_matching_action_metadata_without_error()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            _requestInfo
                .ResourceActionAuthStrategies.Should()
                .Equal(
                    AuthorizationStrategyNameConstants.NamespaceBased,
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Relational_Get_Many_With_No_Authorization_Strategies
        : ResourceActionAuthorizationMiddlewareTests
    {
        private RecordingLogger _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new RecordingLogger();
            _requestInfo = CreateRequestInfo(RequestMethod.GET, "ed-fi/schools");
            _requestInfo.MappingSet = RelationalWriteSeamFixture
                .Create()
                .CreateSupportedMappingSet(SqlDialect.Pgsql);

            await NoAuthStrategyMiddleware("Read", _logger).Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_security_configuration_problem_details()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(500);
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
            _requestInfo
                .FrontendResponse.Body?["type"]?.GetValue<string>()
                .Should()
                .Be("urn:ed-fi:api:system:configuration:security");
            _requestInfo
                .FrontendResponse.Body?["title"]?.GetValue<string>()
                .Should()
                .Be("Security Configuration Error");
            _requestInfo
                .FrontendResponse.Body?["detail"]?.GetValue<string>()
                .Should()
                .Be("A security configuration problem was detected. The request cannot be authorized.");
            _requestInfo
                .FrontendResponse.Body?["errors"]?[0]?.GetValue<string>()
                .Should()
                .Be(
                    $"No authorization strategies were defined for the requested action 'Read' against resource URIs ['{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school'] matched by the caller's claim '{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school'."
                );
        }

        [Test]
        public void It_logs_security_configuration_failure_with_request_context()
        {
            var logRecord = _logger
                .Records.Where(static record => record.Level == LogLevel.Error)
                .Should()
                .ContainSingle()
                .Subject;
            string schoolResourceClaimUri = $"{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school";

            logRecord.Level.Should().Be(LogLevel.Error);
            logRecord.Message.Should().Contain("SecurityConfigurationFailure");
            logRecord.Properties["Tenant"].Should().Be(string.Empty);
            logRecord.Properties["CorrelationId"].Should().Be("traceId");
            logRecord.Properties["HttpMethod"].Should().Be("GET");
            logRecord.Properties["RoutePath"].Should().Be("ed-fi/schools");
            logRecord.Properties["RequestSurface"].Should().Be("GetManyResource");
            logRecord.Properties["CmsAction"].Should().Be("Read");
            logRecord.Properties["AssignedClaimSet"].Should().Be("SIS-Vendor");
            logRecord.Properties["ResourceFullName"].Should().Be("Ed-Fi.School");
            ((IEnumerable<string>)logRecord.Properties["MatchedResourceClaimUris"]!)
                .Should()
                .Equal(schoolResourceClaimUri);
            logRecord.Properties["MatchedResourceClaimName"].Should().Be(schoolResourceClaimUri);
            ((IEnumerable<string>)logRecord.Properties["SecurityConfigurationErrors"]!)
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("No authorization strategies were defined");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Relational_Single_Record_With_No_Authorization_Strategies
        : ResourceActionAuthorizationMiddlewareTests
    {
        [TestCase("GET", "Read", "ed-fi/schools/11111111-1111-1111-1111-111111111111")]
        [TestCase("DELETE", "Delete", "ed-fi/schools/11111111-1111-1111-1111-111111111111")]
        public async Task It_returns_security_configuration_problem_details(
            string requestMethodName,
            string action,
            string path
        )
        {
            var requestMethod = Enum.Parse<RequestMethod>(requestMethodName);
            _requestInfo = CreateRequestInfo(requestMethod, path, hasDocumentUuidSegment: true);
            _requestInfo.MappingSet = RelationalWriteSeamFixture
                .Create()
                .CreateSupportedMappingSet(SqlDialect.Pgsql);

            await NoAuthStrategyMiddleware(action).Execute(_requestInfo, NullNext);

            _requestInfo.FrontendResponse.StatusCode.Should().Be(500);
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
            _requestInfo
                .FrontendResponse.Body?["type"]?.GetValue<string>()
                .Should()
                .Be("urn:ed-fi:api:system:configuration:security");
            _requestInfo
                .FrontendResponse.Body?["errors"]?[0]?.GetValue<string>()
                .Should()
                .Be(
                    $"No authorization strategies were defined for the requested action '{action}' against resource URIs ['{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school'] matched by the caller's claim '{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school'."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Relational_Get_Many_With_NoFurtherAuthorizationRequired
        : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(RequestMethod.GET, "ed-fi/schools");
            _requestInfo.MappingSet = RelationalWriteSeamFixture
                .Create()
                .CreateSupportedMappingSet(SqlDialect.Pgsql);

            await Middleware("Read", AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired)
                .Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_allows_the_request_to_continue()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            _requestInfo
                .ResourceActionAuthStrategies.Should()
                .Equal(AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Relational_Get_Many_With_Duplicate_Relationship_Strategies
        : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(RequestMethod.GET, "ed-fi/schools");
            _requestInfo.MappingSet = RelationalWriteSeamFixture
                .Create()
                .CreateSupportedMappingSet(SqlDialect.Pgsql);

            await Middleware(
                    "Read",
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted
                )
                .Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_preserves_raw_strategy_order_for_relational_get_many()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            _requestInfo
                .ResourceActionAuthStrategies.Should()
                .Equal(
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Relational_Get_Many_With_No_Matching_Claim : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(
                RequestMethod.GET,
                "ed-fi/stateDescriptors",
                endpointName: "stateDescriptors",
                resourceName: "StateDescriptor"
            );
            _requestInfo.MappingSet = RelationalWriteSeamFixture
                .Create()
                .CreateSupportedMappingSet(SqlDialect.Pgsql);

            await Middleware("Read", AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired)
                .Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_still_returns_forbidden()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(403);
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Relational_Get_Many_With_No_Matching_Action
        : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(RequestMethod.GET, "ed-fi/schools");
            _requestInfo.MappingSet = RelationalWriteSeamFixture
                .Create()
                .CreateSupportedMappingSet(SqlDialect.Pgsql);

            await Middleware("Create", AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired)
                .Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_still_returns_forbidden()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(403);
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class GivenTrackedChangeGetWithReadChangesClaim : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(RequestMethod.GET, "ed-fi/schools/deletes");
            _requestInfo.ChangeQueryOperation = ChangeQueryEndpointOperation.Deletes;
            await MiddlewareCoreReadChanges("ReadChanges", "NoFurtherAuthorizationRequired")
                .Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_does_not_set_a_failure_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_resolves_the_ReadChanges_strategies()
        {
            _requestInfo.ResourceActionAuthStrategies.Should().Contain("NoFurtherAuthorizationRequired");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class GivenTrackedChangeGetWithoutReadChangesClaim : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(RequestMethod.GET, "ed-fi/schools/deletes");
            _requestInfo.ChangeQueryOperation = ChangeQueryEndpointOperation.Deletes;
            await MiddlewareCoreReadChanges("Read", "NoFurtherAuthorizationRequired")
                .Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_responds_403()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(403);
        }

        [Test]
        public void It_returns_the_ReadChanges_action_denied_problem_details()
        {
            AssertExpectedReadChangesActionDeniedResponse(_requestInfo.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class GivenTrackedChangeKeyChangesGetWithoutReadChangesClaim
        : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(RequestMethod.GET, "ed-fi/schools/keyChanges");
            _requestInfo.ChangeQueryOperation = ChangeQueryEndpointOperation.KeyChanges;
            await MiddlewareCoreReadChanges("Read", "NoFurtherAuthorizationRequired")
                .Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_responds_403()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(403);
        }

        [Test]
        public void It_returns_the_ReadChanges_action_denied_problem_details()
        {
            AssertExpectedReadChangesActionDeniedResponse(_requestInfo.FrontendResponse);
        }
    }

    private static void AssertExpectedReadChangesActionDeniedResponse(IFrontendResponse response)
    {
        response.ContentType.Should().Be("application/problem+json");

        JsonObject body = response.Body!.AsObject();
        body["type"]!
            .GetValue<string>()
            .Should()
            .Be("urn:ed-fi:api:security:authorization:access-denied:action");
        body["title"]!.GetValue<string>().Should().Be("Authorization Denied");
        body["status"]!.GetValue<int>().Should().Be(403);
        body["detail"]!.GetValue<string>().Should().Be("Access to the resource could not be authorized.");
        body["correlationId"]!.GetValue<string>().Should().Be("traceId");
        body["errors"]!
            .AsArray()
            .Select(error => error!.GetValue<string>())
            .Should()
            .Equal(
                "The API client's assigned claim set (currently 'SIS-Vendor') must grant permission of the 'ReadChanges' action on one of the following resource claims: School"
            );
    }
}
