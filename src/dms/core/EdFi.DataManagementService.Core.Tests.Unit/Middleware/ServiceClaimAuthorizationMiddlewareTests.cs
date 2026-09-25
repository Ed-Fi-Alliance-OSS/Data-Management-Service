// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Identity;
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

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

/// <summary>
/// D7/A12: ServiceClaimAuthorizationMiddleware maps the identity operation to the required CMS action
/// (Create for IdentityOperation.Create, Read for every other operation - Update is never consulted),
/// loads the token's claim set, matches the seeded
/// http://ed-fi.org/identity/claims/services/identity claim, and enforces that a matched action's
/// authorization strategies are exactly [NoFurtherAuthorizationRequired].
/// </summary>
public class ServiceClaimAuthorizationMiddlewareTests
{
    private const string Tenant = "TenantA";
    private const string ClaimSetName = "IdentityClaims";
    private static readonly string _identityClaimUri = $"{Conventions.EdFiOdsServiceClaimBaseUri}/identity";

    private static ServiceClaimAuthorizationMiddleware CreateMiddleware(
        IClaimSetProvider claimSetProvider,
        ILogger<ServiceClaimAuthorizationMiddleware>? logger = null
    ) => new(claimSetProvider, logger ?? NullLogger<ServiceClaimAuthorizationMiddleware>.Instance);

    private static IClaimSetProvider CreateProvider(params ClaimSet[] claimSets)
    {
        var provider = A.Fake<IClaimSetProvider>();
        A.CallTo(() => provider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
            .Returns((IList<ClaimSet>)[.. claimSets]);
        return provider;
    }

    private static ResourceClaim IdentityResourceClaim(string action, params string[] strategyNames) =>
        new(_identityClaimUri, action, [.. strategyNames.Select(name => new AuthorizationStrategy(name))]);

    private static RequestInfo CreateRequestInfo(
        IdentityOperation operation,
        string claimSetName = ClaimSetName,
        string traceId = "service-claim-authorization"
    )
    {
        var frontendRequest = new FrontendRequest(
            Path: "/identity/v2/identities",
            Body: null,
            Form: null,
            Headers: [],
            QueryParameters: [],
            TraceId: new TraceId(traceId),
            RouteQualifiers: [],
            Tenant: Tenant
        );

        return new RequestInfo(frontendRequest, RequestMethod.POST, No.ServiceProvider)
        {
            IdentityOperation = operation,
            ClientAuthorizations = new ClientAuthorizations(
                TokenId: "token-id",
                ClientId: "identity-client-1",
                ClaimSetName: claimSetName,
                EducationOrganizationIds: [],
                NamespacePrefixes: [],
                DataStoreIds: []
            ),
        };
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Required_Action_For_Each_Operation
    {
        [TestCase("Create", "Create")]
        [TestCase("GetById", "Read")]
        [TestCase("Find", "Read")]
        [TestCase("Search", "Read")]
        [TestCase("Results", "Read")]
        public async Task It_authorizes_when_the_claim_set_grants_the_required_action(
            string operationName,
            string requiredAction
        )
        {
            IdentityOperation operation = Enum.Parse<IdentityOperation>(operationName);
            ClaimSet claimSet = new(
                ClaimSetName,
                [
                    IdentityResourceClaim(
                        requiredAction,
                        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                    ),
                ]
            );
            IClaimSetProvider claimSetProvider = CreateProvider(claimSet);
            RequestInfo requestInfo = CreateRequestInfo(operation);
            var nextCalled = false;

            await CreateMiddleware(claimSetProvider)
                .Execute(
                    requestInfo,
                    () =>
                    {
                        nextCalled = true;
                        return Task.CompletedTask;
                    }
                );

            nextCalled.Should().BeTrue();
            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [TestCase("Create", "Read")]
        [TestCase("GetById", "Create")]
        [TestCase("Find", "Create")]
        [TestCase("Search", "Create")]
        [TestCase("Results", "Create")]
        public async Task It_forbids_when_the_claim_set_grants_only_the_other_action(
            string operationName,
            string grantedAction
        )
        {
            IdentityOperation operation = Enum.Parse<IdentityOperation>(operationName);
            ClaimSet claimSet = new(
                ClaimSetName,
                [
                    IdentityResourceClaim(
                        grantedAction,
                        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                    ),
                ]
            );
            IClaimSetProvider claimSetProvider = CreateProvider(claimSet);
            RequestInfo requestInfo = CreateRequestInfo(operation);

            await CreateMiddleware(claimSetProvider).Execute(requestInfo, TestHelper.NullNext);

            requestInfo.FrontendResponse.StatusCode.Should().Be(403);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Claim_Set_With_Only_Update_On_The_Identity_Claim
    {
        [TestCase("Create")]
        [TestCase("GetById")]
        [TestCase("Find")]
        [TestCase("Search")]
        [TestCase("Results")]
        public async Task It_is_forbidden_on_every_operation(string operationName)
        {
            IdentityOperation operation = Enum.Parse<IdentityOperation>(operationName);
            ClaimSet claimSet = new(
                ClaimSetName,
                [
                    IdentityResourceClaim(
                        "Update",
                        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                    ),
                ]
            );
            IClaimSetProvider claimSetProvider = CreateProvider(claimSet);
            RequestInfo requestInfo = CreateRequestInfo(operation);

            await CreateMiddleware(claimSetProvider).Execute(requestInfo, TestHelper.NullNext);

            requestInfo.FrontendResponse.StatusCode.Should().Be(403);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Claim_Set_Without_The_Identity_Claim
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            ClaimSet claimSet = new(ClaimSetName, []);
            IClaimSetProvider claimSetProvider = CreateProvider(claimSet);
            _requestInfo = CreateRequestInfo(IdentityOperation.Create);

            await CreateMiddleware(claimSetProvider).Execute(_requestInfo, TestHelper.NullNext);
        }

        [Test]
        public void It_is_forbidden()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(403);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_No_Claim_Set_Matches_The_Tokens_Claim_Set_Name
    {
        private RequestInfo _requestInfo = null!;
        private IClaimSetProvider _claimSetProvider = null!;

        [SetUp]
        public async Task Setup()
        {
            ClaimSet unrelatedClaimSet = new(
                "SomeOtherClaimSet",
                [
                    IdentityResourceClaim(
                        "Create",
                        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                    ),
                ]
            );
            _claimSetProvider = CreateProvider(unrelatedClaimSet);
            _requestInfo = CreateRequestInfo(IdentityOperation.Create);

            await CreateMiddleware(_claimSetProvider).Execute(_requestInfo, TestHelper.NullNext);
        }

        [Test]
        public void It_is_forbidden()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(403);
        }
    }

    /// <summary>
    /// AGENTS.md Logging: a claim-set name is derived from the JWT scope and a trace id may come
    /// from a client correlation header, so both must be sanitized before reaching the "no matching
    /// claim set" log line.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_No_Claim_Set_Matches_A_Scope_And_Trace_Id_Containing_Control_Characters
    {
        private const string InjectedClaimSetName = "Bad\r\nInjected";
        private const string InjectedTraceId = "trace\r\nid";
        private RecordingLogger<ServiceClaimAuthorizationMiddleware> _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            ClaimSet unrelatedClaimSet = new(
                "SomeOtherClaimSet",
                [
                    IdentityResourceClaim(
                        "Create",
                        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                    ),
                ]
            );
            IClaimSetProvider claimSetProvider = CreateProvider(unrelatedClaimSet);
            _logger = new RecordingLogger<ServiceClaimAuthorizationMiddleware>();
            RequestInfo requestInfo = CreateRequestInfo(
                IdentityOperation.Create,
                claimSetName: InjectedClaimSetName,
                traceId: InjectedTraceId
            );

            await CreateMiddleware(claimSetProvider, _logger).Execute(requestInfo, TestHelper.NullNext);
        }

        [Test]
        public void It_logs_the_scope_without_carriage_return_or_line_feed()
        {
            LogRecord record = _logger.Records.Should().ContainSingle().Subject;
            record.Level.Should().Be(LogLevel.Information);
            record.Properties["Scope"].Should().Be("BadInjected");
        }

        [Test]
        public void It_logs_the_trace_id_without_carriage_return_or_line_feed()
        {
            LogRecord record = _logger.Records.Should().ContainSingle().Subject;
            record.Properties["TraceId"].Should().Be("traceid");
        }
    }

    /// <summary>
    /// AGENTS.md Logging: the matched claim set's name and the request's trace id must be sanitized
    /// before reaching the "does not grant the required action" log line.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Claim_Set_Does_Not_Grant_The_Required_Action_And_Contains_Control_Characters
    {
        private const string InjectedClaimSetName = "Bad\r\nInjected";
        private const string InjectedTraceId = "trace\r\nid";
        private RecordingLogger<ServiceClaimAuthorizationMiddleware> _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            ClaimSet claimSet = new(
                InjectedClaimSetName,
                [
                    IdentityResourceClaim(
                        "Update",
                        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                    ),
                ]
            );
            IClaimSetProvider claimSetProvider = CreateProvider(claimSet);
            _logger = new RecordingLogger<ServiceClaimAuthorizationMiddleware>();
            RequestInfo requestInfo = CreateRequestInfo(
                IdentityOperation.Create,
                claimSetName: InjectedClaimSetName,
                traceId: InjectedTraceId
            );

            await CreateMiddleware(claimSetProvider, _logger).Execute(requestInfo, TestHelper.NullNext);
        }

        [Test]
        public void It_logs_the_claim_set_name_without_carriage_return_or_line_feed()
        {
            LogRecord record = _logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Debug).Subject;
            record.Properties["ClaimSetName"].Should().Be("BadInjected");
        }

        [Test]
        public void It_logs_the_trace_id_without_carriage_return_or_line_feed()
        {
            LogRecord record = _logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Debug).Subject;
            record.Properties["TraceId"].Should().Be("traceid");
        }
    }

    /// <summary>
    /// AGENTS.md Logging: the security-configuration-failure log line composes the claim-set name and
    /// trace id into the log message itself, so both must be sanitized there even though the response
    /// body's message keeps the raw claim-set name - the body is not a log.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Matched_Action_Has_An_Unknown_Strategy_And_Contains_Control_Characters
    {
        private const string InjectedClaimSetName = "Bad\r\nInjected";
        private const string InjectedTraceId = "trace\r\nid";
        private RequestInfo _requestInfo = null!;
        private RecordingLogger<ServiceClaimAuthorizationMiddleware> _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            ClaimSet claimSet = new(
                InjectedClaimSetName,
                [IdentityResourceClaim("Create", "SomeUnknownStrategy")]
            );
            IClaimSetProvider claimSetProvider = CreateProvider(claimSet);
            _logger = new RecordingLogger<ServiceClaimAuthorizationMiddleware>();
            _requestInfo = CreateRequestInfo(
                IdentityOperation.Create,
                claimSetName: InjectedClaimSetName,
                traceId: InjectedTraceId
            );

            await CreateMiddleware(claimSetProvider, _logger).Execute(_requestInfo, TestHelper.NullNext);
        }

        [Test]
        public void It_logs_the_claim_set_name_without_carriage_return_or_line_feed()
        {
            LogRecord record = _logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Error).Subject;
            record.Properties["ClaimSetName"].Should().Be("BadInjected");
        }

        [Test]
        public void It_logs_the_trace_id_without_carriage_return_or_line_feed()
        {
            LogRecord record = _logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Error).Subject;
            record.Properties["TraceId"].Should().Be("traceid");
        }

        [Test]
        public void It_keeps_the_unsanitized_claim_set_name_in_the_response_body()
        {
            string[] errors = _requestInfo.FrontendResponse.Body!["errors"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray();
            errors.Should().ContainSingle(error => error.Contains(InjectedClaimSetName));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Matched_Action_Has_No_Authorization_Strategies
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            ClaimSet claimSet = new(ClaimSetName, [IdentityResourceClaim("Create")]);
            IClaimSetProvider claimSetProvider = CreateProvider(claimSet);
            _requestInfo = CreateRequestInfo(IdentityOperation.Create);

            await CreateMiddleware(claimSetProvider).Execute(_requestInfo, TestHelper.NullNext);
        }

        [Test]
        public void It_returns_500()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(500);
        }

        [Test]
        public void It_returns_the_security_configuration_type()
        {
            _requestInfo.FrontendResponse.Body!["type"]!
                .GetValue<string>()
                .Should()
                .Be("urn:ed-fi:api:system:configuration:security");
        }

        [Test]
        public void It_names_the_claim_set_and_action_in_the_errors()
        {
            string[] errors = _requestInfo.FrontendResponse.Body!["errors"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray();
            errors.Should().ContainSingle(error => error.Contains(ClaimSetName) && error.Contains("Create"));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Matched_Action_Has_An_Unknown_Authorization_Strategy
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            ClaimSet claimSet = new(ClaimSetName, [IdentityResourceClaim("Read", "SomeUnknownStrategy")]);
            IClaimSetProvider claimSetProvider = CreateProvider(claimSet);
            _requestInfo = CreateRequestInfo(IdentityOperation.GetById);

            await CreateMiddleware(claimSetProvider).Execute(_requestInfo, TestHelper.NullNext);
        }

        [Test]
        public void It_returns_500()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(500);
        }

        [Test]
        public void It_returns_the_security_configuration_type()
        {
            _requestInfo.FrontendResponse.Body!["type"]!
                .GetValue<string>()
                .Should()
                .Be("urn:ed-fi:api:system:configuration:security");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Matched_Action_Has_A_Recognized_Strategy_Other_Than_NoFurtherAuthorizationRequired
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            ClaimSet claimSet = new(
                ClaimSetName,
                [
                    IdentityResourceClaim(
                        "Read",
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                    ),
                ]
            );
            IClaimSetProvider claimSetProvider = CreateProvider(claimSet);
            _requestInfo = CreateRequestInfo(IdentityOperation.Search);

            await CreateMiddleware(claimSetProvider).Execute(_requestInfo, TestHelper.NullNext);
        }

        [Test]
        public void It_returns_500()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(500);
        }

        [Test]
        public void It_returns_the_security_configuration_type()
        {
            _requestInfo.FrontendResponse.Body!["type"]!
                .GetValue<string>()
                .Should()
                .Be("urn:ed-fi:api:system:configuration:security");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Matched_Action_Has_More_Than_One_Authorization_Strategy
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            ClaimSet claimSet = new(
                ClaimSetName,
                [
                    IdentityResourceClaim(
                        "Read",
                        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                    ),
                ]
            );
            IClaimSetProvider claimSetProvider = CreateProvider(claimSet);
            _requestInfo = CreateRequestInfo(IdentityOperation.Find);

            await CreateMiddleware(claimSetProvider).Execute(_requestInfo, TestHelper.NullNext);
        }

        [Test]
        public void It_returns_500()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(500);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Claim_Set_Lookup_Uses_The_Request_Tenant_And_Cancellation_Token
    {
        [Test]
        public async Task It_calls_GetAllClaimSets_with_the_request_tenant_and_token()
        {
            ClaimSet claimSet = new(
                ClaimSetName,
                [
                    IdentityResourceClaim(
                        "Create",
                        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                    ),
                ]
            );
            var claimSetProvider = A.Fake<IClaimSetProvider>();
            A.CallTo(() => claimSetProvider.GetAllClaimSets(Tenant, A<CancellationToken>._))
                .Returns((IList<ClaimSet>)[claimSet]);
            using var cts = new CancellationTokenSource();
            RequestInfo requestInfo = CreateRequestInfo(IdentityOperation.Create);
            requestInfo.RequestCancellationToken = cts.Token;

            await CreateMiddleware(claimSetProvider).Execute(requestInfo, TestHelper.NullNext);

            A.CallTo(() => claimSetProvider.GetAllClaimSets(Tenant, cts.Token)).MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Claim_Set_Lookup_Is_Cancelled
    {
        [Test]
        public void It_lets_the_cancellation_propagate()
        {
            var claimSetProvider = A.Fake<IClaimSetProvider>();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
                .Throws(new OperationCanceledException(cts.Token));
            RequestInfo requestInfo = CreateRequestInfo(IdentityOperation.Create);
            requestInfo.RequestCancellationToken = cts.Token;

            Func<Task> act = async () =>
                await CreateMiddleware(claimSetProvider).Execute(requestInfo, TestHelper.NullNext);

            act.Should().ThrowAsync<OperationCanceledException>();
        }
    }
}
