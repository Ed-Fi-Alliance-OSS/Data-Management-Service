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
using EdFi.DataManagementService.Core.Response;
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
        A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._))
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
        A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._))
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

        ResourcePathOperation operation = hasDocumentUuidSegment
            ? new ResourcePathOperation.ById(
                new DocumentUuid(Guid.Parse("11111111-1111-1111-1111-111111111111"))
            )
            : ResourcePathOperation.Collection.Instance;

        var requestInfo = new RequestInfo(frontEndRequest, requestMethod, No.ServiceProvider)
        {
            ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], [])
            {
                ScopeValues = ["SIS-Vendor"],
            },
            PathComponents = new PathComponents(
                ProjectEndpointName: new ProjectEndpointName("ed-fi"),
                EndpointName: new EndpointName(endpointName),
                Operation: operation
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
        A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._))
            .Returns(claimSets.ToList());
        return new ResourceActionAuthorizationMiddleware(claimSetProvider, NullLogger.Instance);
    }

    internal static IPipelineStep NoAuthStrategyMiddleware(string action = "Create", ILogger? logger = null)
    {
        var claimSetProvider = A.Fake<IClaimSetProvider>();
        A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._))
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
                ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], [])
                {
                    ScopeValues = ["SIS-Vendor"],
                },
                PathComponents = new PathComponents(
                    ProjectEndpointName: new Core.ApiSchema.Model.ProjectEndpointName("ed-fi"),
                    EndpointName: new EndpointName("schools"),
                    Operation: ResourcePathOperation.Collection.Instance
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
                ClientAuthorizations = new ClientAuthorizations("", "", "NO-MATCH", [], [], [])
                {
                    ScopeValues = ["NO-MATCH"],
                },
                PathComponents = new PathComponents(
                    ProjectEndpointName: new Core.ApiSchema.Model.ProjectEndpointName("ed-fi"),
                    EndpointName: new EndpointName("schools"),
                    Operation: ResourcePathOperation.Collection.Instance
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

    /// <summary>
    /// The token's scope values must be sanitized before reaching a log
    /// line - the "Scope values from token" information log fires unconditionally, and the
    /// "No ClaimSet matching Scope" log fires because no configured claim set matches the injected
    /// name.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_No_Matching_ClaimSet_With_Control_Characters_In_The_Scope
        : ResourceActionAuthorizationMiddlewareTests
    {
        private const string InjectedClaimSetName = "Bad\r\nInjected";
        private RecordingLogger _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new RecordingLogger();
            var claimSetProvider = A.Fake<IClaimSetProvider>();
            A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>.Ignored))
                .Returns([
                    new ClaimSet(
                        Name: "SIS-Vendor",
                        ResourceClaims:
                        [
                            new ResourceClaim(
                                $"{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school",
                                "Create",
                                [
                                    new AuthorizationStrategy(
                                        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                                    ),
                                ]
                            ),
                        ]
                    ),
                ]);
            var middleware = new ResourceActionAuthorizationMiddleware(claimSetProvider, _logger);

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
                ClientAuthorizations = new ClientAuthorizations("", "", InjectedClaimSetName, [], [], [])
                {
                    ScopeValues = [InjectedClaimSetName],
                },
                PathComponents = new PathComponents(
                    ProjectEndpointName: new Core.ApiSchema.Model.ProjectEndpointName("ed-fi"),
                    EndpointName: new EndpointName("schools"),
                    Operation: ResourcePathOperation.Collection.Instance
                ),
            };
            _requestInfo.ProjectSchema = ApiSchemaDocument("School")
                .FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                    ?? new JsonObject()
            );

            await middleware.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_logs_the_scope_values_without_carriage_return_or_line_feed()
        {
            LogRecord record = _logger
                .Records.Should()
                .ContainSingle(record => record.Message.StartsWith("Scope values from token"))
                .Subject;

            record.Level.Should().Be(LogLevel.Information);
            record.Properties["Scope"].Should().Be("BadInjected");
        }

        [Test]
        public void It_logs_the_no_matching_claim_set_scope_without_carriage_return_or_line_feed()
        {
            LogRecord record = _logger
                .Records.Should()
                .ContainSingle(record => record.Message.Contains("No ClaimSet matching Scope"))
                .Subject;

            record.Level.Should().Be(LogLevel.Information);
            record.Properties["Scope"].Should().Be("BadInjected");
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
                ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], [])
                {
                    ScopeValues = ["SIS-Vendor"],
                },
                PathComponents = new PathComponents(
                    ProjectEndpointName: new Core.ApiSchema.Model.ProjectEndpointName("ed-fi"),
                    EndpointName: new EndpointName("stateDescriptor"),
                    Operation: ResourcePathOperation.Collection.Instance
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
                ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], [])
                {
                    ScopeValues = ["SIS-Vendor"],
                },
                PathComponents = new PathComponents(
                    ProjectEndpointName: new Core.ApiSchema.Model.ProjectEndpointName("ed-fi"),
                    EndpointName: new EndpointName("schools"),
                    Operation: ResourcePathOperation.Collection.Instance
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
                ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], [])
                {
                    ScopeValues = ["SIS-Vendor"],
                },
                PathComponents = new PathComponents(
                    ProjectEndpointName: new Core.ApiSchema.Model.ProjectEndpointName("ed-fi"),
                    EndpointName: new EndpointName("schools"),
                    Operation: ResourcePathOperation.Collection.Instance
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
            A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._))
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
                ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], [])
                {
                    ScopeValues = ["SIS-Vendor"],
                },
                PathComponents = new PathComponents(
                    ProjectEndpointName: new Core.ApiSchema.Model.ProjectEndpointName("ed-fi"),
                    EndpointName: new EndpointName("stateDescriptor"),
                    Operation: ResourcePathOperation.Collection.Instance
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
                ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], [])
                {
                    ScopeValues = ["SIS-Vendor"],
                },
                PathComponents = new PathComponents(
                    ProjectEndpointName: new Core.ApiSchema.Model.ProjectEndpointName("ed-fi"),
                    EndpointName: new EndpointName("schools"),
                    Operation: ResourcePathOperation.Collection.Instance
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
                ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], [])
                {
                    ScopeValues = ["SIS-Vendor"],
                },
                PathComponents = new PathComponents(
                    ProjectEndpointName: new Core.ApiSchema.Model.ProjectEndpointName("ed-fi"),
                    EndpointName: new EndpointName("schools"),
                    Operation: ResourcePathOperation.Collection.Instance
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

        // A POST resolves to an update when its target exists, so a Create grant with no strategies is answered
        // only once the target is known; the evidence keeps what that 500 renders.
        [Test]
        public void It_leaves_the_response_to_the_selected_action()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_keeps_the_create_no_strategies_evidence()
        {
            string schoolResourceClaimUri = $"{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school";
            var noStrategies = _requestInfo
                .UpsertActionPolicies!.Create.Should()
                .BeOfType<UpsertActionPolicyEvidence.NoStrategies>()
                .Subject;
            noStrategies.ActionName.Should().Be("Create");
            noStrategies.MatchedResourceClaimUris.Should().Equal(schoolResourceClaimUri);
            noStrategies.MatchedResourceClaimName.Should().Be(schoolResourceClaimUri);
        }

        [Test]
        public void It_keeps_the_ungranted_update_as_denied()
        {
            _requestInfo
                .UpsertActionPolicies!.Update.Should()
                .Be(new UpsertActionPolicyEvidence.Denied("Update", "School", "SIS-Vendor"));
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

            A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._))
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
                ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], [])
                {
                    ScopeValues = ["SIS-Vendor"],
                },
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

    /// <summary>
    /// The request's own cancellation token must reach the claim set provider, and a cancellation
    /// that the provider observes must propagate as OperationCanceledException rather than being
    /// caught by the middleware's catch-all and turned into a 500.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Requests_Token_Is_Cancelled_During_Claim_Set_Retrieval
        : ResourceActionAuthorizationMiddlewareTests
    {
        private IClaimSetProvider _claimSetProvider = null!;
        private CancellationTokenSource _cts = null!;
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _cts = new CancellationTokenSource();
            _cts.Cancel();
            _claimSetProvider = A.Fake<IClaimSetProvider>();
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._))
                .Throws(() => new OperationCanceledException(_cts.Token));

            var middleware = new ResourceActionAuthorizationMiddleware(
                _claimSetProvider,
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

            _requestInfo = new RequestInfo(
                frontEndRequest,
                RequestMethod.POST,
                No.ServiceProvider,
                _cts.Token
            )
            {
                ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], [])
                {
                    ScopeValues = ["SIS-Vendor"],
                },
            };

            _act = () => middleware.Execute(_requestInfo, NullNext);
        }

        [TearDown]
        public void TearDown() => _cts.Dispose();

        [Test]
        public async Task It_passes_the_requests_cancellation_token_to_the_claim_set_provider()
        {
            await _act.Should().ThrowAsync<OperationCanceledException>();

            A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>.Ignored, _cts.Token))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_propagates_operation_canceled_instead_of_a_response()
        {
            await _act.Should().ThrowAsync<OperationCanceledException>();

            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_RelationshipsWithEdOrgsOnlyInverted_On_Non_Query_Request
        : ResourceActionAuthorizationMiddlewareTests
    {
        [TestCase("GET", "Read", "ed-fi/schools/11111111-1111-1111-1111-111111111111", true)]
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
            _requestInfo.UpsertActionPolicies.Should().BeNull();
        }

        [Test]
        public async Task It_allows_the_strategy_as_the_create_policy_for_a_post()
        {
            _requestInfo = CreateRequestInfo(RequestMethod.POST, "ed-fi/schools");

            await Middleware("Create", AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted)
                .Execute(_requestInfo, NullNext);

            _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
            _requestInfo
                .UpsertActionPolicies!.Create.Should()
                .BeOfType<UpsertActionPolicyEvidence.Permitted>()
                .Which.StrategyNames.Should()
                .Equal(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted);
            _requestInfo.ResourceActionAuthStrategies.Should().BeEmpty();
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
                .UpsertActionPolicies!.Create.Should()
                .BeOfType<UpsertActionPolicyEvidence.Permitted>()
                .Which.StrategyNames.Should()
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
                .UpsertActionPolicies!.Create.Should()
                .BeOfType<UpsertActionPolicyEvidence.Permitted>()
                .Which.StrategyNames.Should()
                .Equal(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                    AuthorizationStrategyNameConstants.NamespaceBased
                );
        }
    }

    /// <summary>
    /// How the claim set grants one action: not at all, with no strategies, or with strategies.
    /// </summary>
    public enum ActionGrant
    {
        NotGranted,
        NoStrategies,
        Strategies,
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_With_Independently_Granted_Create_And_Update
        : ResourceActionAuthorizationMiddlewareTests
    {
        private static readonly string _schoolResourceClaimUri =
            $"{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school";

        private static IEnumerable<TestCaseData> GrantCombinations() =>
            from create in Enum.GetValues<ActionGrant>()
            from update in Enum.GetValues<ActionGrant>()
            where !(create is ActionGrant.NotGranted && update is ActionGrant.NotGranted)
            select new TestCaseData(create, update);

        [TestCaseSource(nameof(GrantCombinations))]
        public async Task It_records_each_actions_evidence_and_continues(
            ActionGrant create,
            ActionGrant update
        )
        {
            var requestInfo = CreateRequestInfo(RequestMethod.POST, "ed-fi/schools");
            bool nextCalled = false;

            await MiddlewareWithGrants(create, update)
                .Execute(
                    requestInfo,
                    () =>
                    {
                        nextCalled = true;
                        return Task.CompletedTask;
                    }
                );

            nextCalled.Should().BeTrue();
            requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
            requestInfo.ResourceActionAuthStrategies.Should().BeEmpty();
            AssertEvidence(requestInfo.UpsertActionPolicies!.Create, "Create", create, "CreateStrategy");
            AssertEvidence(requestInfo.UpsertActionPolicies.Update, "Update", update, "UpdateStrategy");
        }

        [Test]
        public async Task It_refuses_a_post_neither_action_permits_with_the_create_denial()
        {
            var requestInfo = CreateRequestInfo(RequestMethod.POST, "ed-fi/schools");
            bool nextCalled = false;

            await MiddlewareWithGrants(ActionGrant.NotGranted, ActionGrant.NotGranted)
                .Execute(
                    requestInfo,
                    () =>
                    {
                        nextCalled = true;
                        return Task.CompletedTask;
                    }
                );

            nextCalled.Should().BeFalse();
            requestInfo.UpsertActionPolicies.Should().BeNull();
            requestInfo.FrontendResponse.StatusCode.Should().Be(403);
            requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
            requestInfo.FrontendResponse.Headers.Should().BeEmpty();
            requestInfo
                .FrontendResponse.Body!.ToJsonString()
                .Should()
                .Be(
                    FailureResponse
                        .ForForbidden(
                            traceId: new TraceId("traceId"),
                            errors:
                            [
                                "The API client's assigned claim set (currently 'SIS-Vendor') must grant permission of the 'Create' action on one of the following resource claims: School",
                            ],
                            typeExtension: "access-denied:action"
                        )
                        .ToJsonString()
                );
        }

        [Test]
        public async Task It_keeps_a_put_on_its_single_update_action()
        {
            var requestInfo = CreateRequestInfo(
                RequestMethod.PUT,
                "ed-fi/schools/11111111-1111-1111-1111-111111111111",
                hasDocumentUuidSegment: true
            );

            await MiddlewareWithGrants(ActionGrant.Strategies, ActionGrant.Strategies)
                .Execute(requestInfo, NullNext);

            requestInfo.ResourceActionAuthStrategies.Should().Equal("UpdateStrategy");
            requestInfo.UpsertActionPolicies.Should().BeNull();
        }

        private static void AssertEvidence(
            UpsertActionPolicyEvidence evidence,
            string actionName,
            ActionGrant grant,
            string strategyName
        )
        {
            evidence.ActionName.Should().Be(actionName);

            switch (grant)
            {
                case ActionGrant.NotGranted:
                    evidence
                        .Should()
                        .Be(new UpsertActionPolicyEvidence.Denied(actionName, "School", "SIS-Vendor"));
                    break;
                case ActionGrant.NoStrategies:
                    var noStrategies = evidence
                        .Should()
                        .BeOfType<UpsertActionPolicyEvidence.NoStrategies>()
                        .Subject;
                    noStrategies.MatchedResourceClaimUris.Should().Equal(_schoolResourceClaimUri);
                    noStrategies.MatchedResourceClaimName.Should().Be(_schoolResourceClaimUri);
                    break;
                default:
                    evidence
                        .Should()
                        .BeOfType<UpsertActionPolicyEvidence.Permitted>()
                        .Which.StrategyNames.Should()
                        .Equal(strategyName);
                    break;
            }
        }

        private static IPipelineStep MiddlewareWithGrants(ActionGrant create, ActionGrant update)
        {
            // Read keeps the resource claimed even when neither Create nor Update is granted.
            ResourceClaim[] resourceClaims =
            [
                .. Grant("Read", ActionGrant.Strategies, "ReadStrategy"),
                .. Grant("Create", create, "CreateStrategy"),
                .. Grant("Update", update, "UpdateStrategy"),
            ];

            var claimSetProvider = A.Fake<IClaimSetProvider>();
            A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>.Ignored))
                .Returns([new ClaimSet(Name: "SIS-Vendor", ResourceClaims: [.. resourceClaims])]);

            return new ResourceActionAuthorizationMiddleware(claimSetProvider, NullLogger.Instance);

            static IEnumerable<ResourceClaim> Grant(string action, ActionGrant grant, string strategyName) =>
                grant switch
                {
                    ActionGrant.NotGranted => [],
                    ActionGrant.NoStrategies => [new ResourceClaim(_schoolResourceClaimUri, action, [])],
                    _ =>
                    [
                        new ResourceClaim(
                            _schoolResourceClaimUri,
                            action,
                            [new AuthorizationStrategy(strategyName)]
                        ),
                    ],
                };
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

    private const string AmbiguousClaimSetsError = "The token identifies multiple claim sets.";

    private const string DuplicateClaimSetNamesError =
        "The security metadata contains duplicate claim-set names.";

    /// <summary>
    /// A school claim set granting one action with the given strategies.
    /// </summary>
    private static ClaimSet SchoolClaimSet(string name, string action, params string[] strategies) =>
        new(
            name,
            [
                new ResourceClaim(
                    $"{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/school",
                    action,
                    [.. strategies.Select(static strategy => new AuthorizationStrategy(strategy))]
                ),
            ]
        );

    private static IClaimSetProvider ClaimSetProvider(params ClaimSet[] claimSets)
    {
        var claimSetProvider = A.Fake<IClaimSetProvider>();
        A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._))
            .Returns(claimSets.ToList());
        return claimSetProvider;
    }

    /// <summary>
    /// A GET collection request whose authorizations carry the given scope values, shaped as JWT
    /// extraction produces them: the claim-set name is set only for one distinct scope value.
    /// </summary>
    private static RequestInfo ScopedReadRequest(params string[] scopeValues)
    {
        RequestInfo requestInfo = CreateRequestInfo(RequestMethod.GET, "ed-fi/schools");
        requestInfo.ClientAuthorizations = new ClientAuthorizations(
            "token-id",
            "client-id",
            scopeValues.Length == 1 ? scopeValues[0] : "",
            [],
            [],
            []
        )
        {
            ScopeValues = scopeValues,
        };
        return requestInfo;
    }

    [TestFixture("Alpha|profile")]
    [TestFixture("profile|Alpha")]
    [Parallelizable]
    public class Given_Scope_Values_That_Identify_One_Claim_Set(string scopeValueList)
        : ResourceActionAuthorizationMiddlewareTests
    {
        private readonly string[] _scopeValues = scopeValueList.Split('|');
        private IClaimSetProvider _claimSetProvider = null!;
        private ClientAuthorizations _authenticatedAuthorizations = null!;
        private int _continuations;

        [SetUp]
        public async Task Setup()
        {
            _claimSetProvider = ClaimSetProvider(
                SchoolClaimSet(
                    "Alpha",
                    "Read",
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                ),
                SchoolClaimSet("Beta", "Read", AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly)
            );
            _requestInfo = ScopedReadRequest(_scopeValues);
            _authenticatedAuthorizations = _requestInfo.ClientAuthorizations;
            _continuations = 0;

            await new ResourceActionAuthorizationMiddleware(_claimSetProvider, NullLogger.Instance).Execute(
                _requestInfo,
                () =>
                {
                    _continuations++;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_continues_the_protected_work_once()
        {
            _continuations.Should().Be(1);
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_authorizes_with_only_the_selected_claim_set_strategies() =>
            _requestInfo
                .ResourceActionAuthStrategies.Should()
                .Equal(AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired);

        [Test]
        public void It_puts_the_selected_name_on_a_request_local_copy()
        {
            _requestInfo.ClientAuthorizations.ClaimSetName.Should().Be("Alpha");
            _requestInfo.ClientAuthorizations.ScopeValues.Should().Equal(_scopeValues);
            _requestInfo.ClientAuthorizations.Should().NotBeSameAs(_authenticatedAuthorizations);
        }

        [Test]
        public void It_does_not_mutate_the_authenticated_authorizations() =>
            _authenticatedAuthorizations.ClaimSetName.Should().BeEmpty();

        [Test]
        public void It_reads_the_claim_set_metadata_once() =>
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Scope_Values_Whose_Selected_Claim_Set_Does_Not_Grant_The_Action
        : ResourceActionAuthorizationMiddlewareTests
    {
        private int _continuations;

        [SetUp]
        public async Task Setup()
        {
            // Only Alpha is recognized; Beta grants Read but is not in the token, so its grant never
            // reaches this request.
            IClaimSetProvider claimSetProvider = ClaimSetProvider(
                SchoolClaimSet(
                    "Alpha",
                    "Delete",
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                ),
                SchoolClaimSet(
                    "Beta",
                    "Read",
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                )
            );
            _requestInfo = ScopedReadRequest("profile", "Alpha");
            _continuations = 0;

            await new ResourceActionAuthorizationMiddleware(claimSetProvider, NullLogger.Instance).Execute(
                _requestInfo,
                () =>
                {
                    _continuations++;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_denies_the_action_for_the_selected_claim_set()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(403);
            _requestInfo.FrontendResponse.Body!["errors"]!
                .AsArray()
                .Select(error => error!.GetValue<string>())
                .Should()
                .Equal(
                    "The API client's assigned claim set (currently 'Alpha') must grant permission of the 'Read' action on one of the following resource claims: School"
                );
        }

        [Test]
        public void It_does_not_continue() => _continuations.Should().Be(0);
    }

    [TestFixture("Alpha|Beta")]
    [TestFixture("Beta|Alpha")]
    [TestFixture("profile|Beta|Alpha")]
    [Parallelizable]
    public class Given_Scope_Values_That_Identify_Two_Claim_Sets(string scopeValueList)
        : ResourceActionAuthorizationMiddlewareTests
    {
        private readonly string[] _scopeValues = scopeValueList.Split('|');
        private IClaimSetProvider _claimSetProvider = null!;
        private RecordingLogger _logger = null!;
        private int _continuations;

        [SetUp]
        public async Task Setup()
        {
            _claimSetProvider = ClaimSetProvider(
                SchoolClaimSet(
                    "Alpha",
                    "Read",
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                ),
                SchoolClaimSet(
                    "Beta",
                    "Read",
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                )
            );
            _logger = new RecordingLogger();
            _requestInfo = ScopedReadRequest(_scopeValues);
            _continuations = 0;

            await new ResourceActionAuthorizationMiddleware(_claimSetProvider, _logger).Execute(
                _requestInfo,
                () =>
                {
                    _continuations++;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_returns_403_with_the_generic_ambiguity_error()
        {
            IFrontendResponse response = _requestInfo.FrontendResponse;

            response.StatusCode.Should().Be(403);
            response.ContentType.Should().Be("application/problem+json");
            response.Headers.Should().BeEmpty();
            response.Body!["status"]!.GetValue<int>().Should().Be(403);
            response.Body!["errors"]!
                .AsArray()
                .Select(n => n!.GetValue<string>())
                .Should()
                .Equal(AmbiguousClaimSetsError);
        }

        [Test]
        public void It_does_not_continue_or_authorize_any_strategy()
        {
            _continuations.Should().Be(0);
            _requestInfo.ResourceActionAuthStrategies.Should().BeEmpty();
        }

        [Test]
        public void It_selects_no_claim_set_name() =>
            _requestInfo.ClientAuthorizations.ClaimSetName.Should().BeEmpty();

        [Test]
        public void It_reads_the_claim_set_metadata_once() =>
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_logs_the_scope_values_for_diagnosis() =>
            _logger
                .Records.Should()
                .ContainSingle(record =>
                    record.Message.Contains("multiple claim sets")
                    && Equals(record.Properties["Scope"], scopeValueList.Replace('|', ' '))
                );
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Scope_Values_That_Match_No_Claim_Set : ResourceActionAuthorizationMiddlewareTests
    {
        private RecordingLogger _logger = null!;
        private int _continuations;

        [SetUp]
        public async Task Setup()
        {
            IClaimSetProvider claimSetProvider = ClaimSetProvider(
                SchoolClaimSet(
                    "Alpha",
                    "Read",
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                )
            );
            _logger = new RecordingLogger();
            _requestInfo = ScopedReadRequest("pro\r\nfile", "Gamma");
            _continuations = 0;

            await new ResourceActionAuthorizationMiddleware(claimSetProvider, _logger).Execute(
                _requestInfo,
                () =>
                {
                    _continuations++;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_keeps_the_missing_security_metadata_response()
        {
            AssertExpectedSecurityConfigurationResponse(
                _requestInfo.FrontendResponse,
                SecurityConfigurationFailureMessages.MissingSecurityMetadata
            );
        }

        [Test]
        public void It_does_not_continue() => _continuations.Should().Be(0);

        [Test]
        public void It_logs_all_unmatched_scope_values() =>
            _logger
                .Records.Should()
                .ContainSingle(record =>
                    record.Message.Contains("No ClaimSet matching Scope")
                    && Equals(record.Properties["Scope"], "profile Gamma")
                );

        [Test]
        public void It_includes_the_scopes_in_the_security_configuration_diagnostic() =>
            _logger
                .Records.Should()
                .ContainSingle(record =>
                    record.Message.StartsWith("SecurityConfigurationFailure.")
                    && Equals(record.Properties["AssignedClaimSet"], "profile Gamma")
                );
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Duplicate_Matching_Claim_Set_Metadata : ResourceActionAuthorizationMiddlewareTests
    {
        private RecordingLogger _logger = null!;
        private int _continuations;

        [SetUp]
        public async Task Setup()
        {
            IClaimSetProvider claimSetProvider = ClaimSetProvider(
                SchoolClaimSet(
                    "Alpha",
                    "Read",
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                ),
                SchoolClaimSet(
                    "ALPHA",
                    "Read",
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                )
            );
            _logger = new RecordingLogger();
            _requestInfo = ScopedReadRequest("Alpha");
            _requestInfo.FrontendRequest = _requestInfo.FrontendRequest with { Tenant = "Tenant\r\nA" };
            _continuations = 0;

            await new ResourceActionAuthorizationMiddleware(claimSetProvider, _logger).Execute(
                _requestInfo,
                () =>
                {
                    _continuations++;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_returns_a_security_configuration_500()
        {
            AssertExpectedSecurityConfigurationResponse(
                _requestInfo.FrontendResponse,
                DuplicateClaimSetNamesError
            );
        }

        [Test]
        public void It_does_not_continue() => _continuations.Should().Be(0);

        [Test]
        public void It_logs_duplicate_names_with_sanitized_request_context()
        {
            LogRecord record = _logger
                .Records.Should()
                .ContainSingle(record => record.Level == LogLevel.Error)
                .Subject;

            record.Message.Should().Contain("duplicate claim-set names");
            record.Properties["DuplicateNames"].Should().Be("Alpha ALPHA");
            record.Properties["Tenant"].Should().Be("TenantA");
            record.Properties["CorrelationId"].Should().Be("traceId");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Claim_Set_Provider_Throws_For_Multiple_Scope_Values
        : ResourceActionAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            var claimSetProvider = A.Fake<IClaimSetProvider>();
            A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._))
                .Throws(new InvalidOperationException("simulated failure"));
            _requestInfo = ScopedReadRequest("Alpha", "Beta");

            await new ResourceActionAuthorizationMiddleware(claimSetProvider, NullLogger.Instance).Execute(
                _requestInfo,
                NullNext
            );
        }

        [Test]
        public void It_keeps_the_authorization_error_instead_of_no_match_or_ambiguity()
        {
            AssertExpectedServerErrorResponse(
                _requestInfo.FrontendResponse,
                "Error while authorizing the request.",
                "traceId"
            );
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
