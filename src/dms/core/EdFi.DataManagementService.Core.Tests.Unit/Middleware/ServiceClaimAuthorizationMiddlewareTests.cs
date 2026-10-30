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
/// ServiceClaimAuthorizationMiddleware maps the identity operation to the required CMS action
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
            )
            {
                ScopeValues = [claimSetName],
            },
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
        private int _nextCalls;

        [SetUp]
        public async Task Setup()
        {
            ClaimSet claimSet = new(ClaimSetName, []);
            IClaimSetProvider claimSetProvider = CreateProvider(claimSet);
            _requestInfo = CreateRequestInfo(IdentityOperation.Create);
            _requestInfo.FrontendRequest = _requestInfo.FrontendRequest with
            {
                DuplicatePropertyPath = "$.duplicate",
            };
            _nextCalls = 0;
            await CreateMiddleware(claimSetProvider)
                .Execute(
                    _requestInfo,
                    async () =>
                    {
                        _nextCalls++;
                        await new DuplicatePropertiesMiddleware(NullLogger.Instance).Execute(
                            _requestInfo,
                            TestHelper.NullNext
                        );
                    }
                );
        }

        [Test]
        public void It_is_forbidden()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(403);
        }

        [Test]
        public void It_keeps_forbidden_before_duplicate_reporting() => _nextCalls.Should().Be(0);
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
        public async Task It_lets_the_cancellation_propagate()
        {
            var claimSetProvider = A.Fake<IClaimSetProvider>();
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
                .Throws(new OperationCanceledException(cts.Token));
            RequestInfo requestInfo = CreateRequestInfo(IdentityOperation.Create);
            requestInfo.RequestCancellationToken = cts.Token;

            Func<Task> act = async () =>
                await CreateMiddleware(claimSetProvider).Execute(requestInfo, TestHelper.NullNext);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }
    }

    private static ClaimSet IdentityClaimSet(string name, string action = "Create") =>
        new(
            name,
            [IdentityResourceClaim(action, AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired)]
        );

    /// <summary>
    /// An identity request whose authorizations carry '|'-separated scope values, shaped as JWT
    /// extraction produces them: the claim-set name is set only for one distinct scope value.
    /// </summary>
    private static RequestInfo CreateScopedRequestInfo(
        string scopeValueList,
        string traceId = "service-claim-authorization",
        string tenant = Tenant
    )
    {
        string[] scopeValues = scopeValueList.Split('|');
        RequestInfo requestInfo = CreateRequestInfo(
            IdentityOperation.Create,
            claimSetName: scopeValues.Length == 1 ? scopeValues[0] : "",
            traceId: traceId
        );
        requestInfo.FrontendRequest = requestInfo.FrontendRequest with { Tenant = tenant };
        requestInfo.ClientAuthorizations = requestInfo.ClientAuthorizations with
        {
            ScopeValues = scopeValues,
        };
        return requestInfo;
    }

    private static async Task<int> ExecuteCountingContinuations(
        ServiceClaimAuthorizationMiddleware middleware,
        RequestInfo requestInfo
    )
    {
        int continuations = 0;
        await middleware.Execute(
            requestInfo,
            () =>
            {
                continuations++;
                return Task.CompletedTask;
            }
        );
        return continuations;
    }

    private static string[] Errors(RequestInfo requestInfo) =>
        [.. requestInfo.FrontendResponse.Body!["errors"]!.AsArray().Select(node => node!.GetValue<string>())];

    [TestFixture("Alpha|profile")]
    [TestFixture("profile|Alpha")]
    [Parallelizable]
    public class Given_Scope_Values_That_Identify_One_Claim_Set(string scopeValueList)
    {
        private IClaimSetProvider _claimSetProvider = null!;
        private RequestInfo _requestInfo = null!;
        private ClientAuthorizations _authenticatedAuthorizations = null!;
        private int _continuations;

        [SetUp]
        public async Task Setup()
        {
            // Beta also grants the identity claim, but it is not in the token.
            _claimSetProvider = CreateProvider(IdentityClaimSet("Alpha"), IdentityClaimSet("Beta"));
            _requestInfo = CreateScopedRequestInfo(scopeValueList);
            _authenticatedAuthorizations = _requestInfo.ClientAuthorizations;

            _continuations = await ExecuteCountingContinuations(
                CreateMiddleware(_claimSetProvider),
                _requestInfo
            );
        }

        [Test]
        public void It_continues_the_protected_work_once()
        {
            _continuations.Should().Be(1);
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_puts_the_selected_name_on_a_request_local_copy()
        {
            _requestInfo.ClientAuthorizations.ClaimSetName.Should().Be("Alpha");
            _requestInfo.ClientAuthorizations.Should().NotBeSameAs(_authenticatedAuthorizations);
            _authenticatedAuthorizations.ClaimSetName.Should().BeEmpty();
        }

        [Test]
        public void It_reads_the_claim_set_metadata_once() =>
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(Tenant, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Scope_Values_Whose_Selected_Claim_Set_Lacks_The_Identity_Grant
    {
        private RequestInfo _requestInfo = null!;
        private int _continuations;

        [SetUp]
        public async Task Setup()
        {
            // Alpha is selected and grants only Read; Beta grants Create but is not in the token.
            IClaimSetProvider claimSetProvider = CreateProvider(
                IdentityClaimSet("Alpha", "Read"),
                IdentityClaimSet("Beta", "Create")
            );
            _requestInfo = CreateScopedRequestInfo("profile|Alpha");

            _continuations = await ExecuteCountingContinuations(
                CreateMiddleware(claimSetProvider),
                _requestInfo
            );
        }

        [Test]
        public void It_is_forbidden_with_the_existing_envelope()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(403);
            Errors(_requestInfo).Should().BeEmpty();
            _requestInfo.ClientAuthorizations.ClaimSetName.Should().Be("Alpha");
        }

        [Test]
        public void It_does_not_continue() => _continuations.Should().Be(0);
    }

    [TestFixture("Alpha|Beta")]
    [TestFixture("Beta|Alpha")]
    [TestFixture("profile|Beta|Alpha")]
    [Parallelizable]
    public class Given_Scope_Values_That_Identify_Two_Claim_Sets(string scopeValueList)
    {
        private IClaimSetProvider _claimSetProvider = null!;
        private RecordingLogger<ServiceClaimAuthorizationMiddleware> _logger = null!;
        private RequestInfo _requestInfo = null!;
        private int _continuations;

        [SetUp]
        public async Task Setup()
        {
            _claimSetProvider = CreateProvider(IdentityClaimSet("Alpha"), IdentityClaimSet("Beta"));
            _logger = new RecordingLogger<ServiceClaimAuthorizationMiddleware>();
            _requestInfo = CreateScopedRequestInfo(scopeValueList);

            _continuations = await ExecuteCountingContinuations(
                CreateMiddleware(_claimSetProvider, _logger),
                _requestInfo
            );
        }

        [Test]
        public void It_returns_403_with_the_generic_ambiguity_error()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(403);
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
            _requestInfo.FrontendResponse.Headers.Should().BeEmpty();
            Errors(_requestInfo).Should().Equal("The token identifies multiple claim sets.");
        }

        [Test]
        public void It_does_not_continue() => _continuations.Should().Be(0);

        [Test]
        public void It_selects_no_claim_set_name() =>
            _requestInfo.ClientAuthorizations.ClaimSetName.Should().BeEmpty();

        [Test]
        public void It_reads_the_claim_set_metadata_once() =>
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(Tenant, A<CancellationToken>._))
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
    public class Given_Scope_Values_That_Match_No_Claim_Set
    {
        private RequestInfo _requestInfo = null!;
        private RecordingLogger<ServiceClaimAuthorizationMiddleware> _logger = null!;
        private int _continuations;

        [SetUp]
        public async Task Setup()
        {
            IClaimSetProvider claimSetProvider = CreateProvider(IdentityClaimSet("Alpha"));
            _logger = new RecordingLogger<ServiceClaimAuthorizationMiddleware>();
            _requestInfo = CreateScopedRequestInfo("pro\r\nfile|Gamma");

            _continuations = await ExecuteCountingContinuations(
                CreateMiddleware(claimSetProvider, _logger),
                _requestInfo
            );
        }

        [Test]
        public void It_keeps_the_existing_forbidden_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(403);
            Errors(_requestInfo).Should().BeEmpty();
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
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Duplicate_Matching_Claim_Set_Metadata
    {
        private RecordingLogger<ServiceClaimAuthorizationMiddleware> _logger = null!;
        private RequestInfo _requestInfo = null!;
        private int _continuations;

        [SetUp]
        public async Task Setup()
        {
            IClaimSetProvider claimSetProvider = CreateProvider(
                IdentityClaimSet("Alpha"),
                IdentityClaimSet("alpha")
            );
            _logger = new RecordingLogger<ServiceClaimAuthorizationMiddleware>();
            _requestInfo = CreateScopedRequestInfo("Alpha", traceId: "trace\r\nid", tenant: "Tenant\r\nA");

            _continuations = await ExecuteCountingContinuations(
                CreateMiddleware(claimSetProvider, _logger),
                _requestInfo
            );
        }

        [Test]
        public void It_returns_a_security_configuration_500()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(500);
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
            _requestInfo.FrontendResponse.Body!["type"]!
                .GetValue<string>()
                .Should()
                .Be("urn:ed-fi:api:system:configuration:security");
            Errors(_requestInfo).Should().Equal("The security metadata contains duplicate claim-set names.");
        }

        [Test]
        public void It_does_not_continue() => _continuations.Should().Be(0);

        [Test]
        public void It_logs_duplicate_names_with_sanitized_request_context()
        {
            LogRecord record = _logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Error).Subject;
            record.Message.Should().Contain("duplicate claim-set names");
            record.Properties["DuplicateNames"].Should().Be("Alpha alpha");
            record.Properties["Tenant"].Should().Be("TenantA");
            record.Properties["CorrelationId"].Should().Be("traceid");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Claim_Set_Lookup_Fails_For_Multiple_Scope_Values
    {
        private Func<Task> _act = null!;
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public void Setup()
        {
            var claimSetProvider = A.Fake<IClaimSetProvider>();
            A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
                .Throws(new InvalidOperationException("simulated failure"));
            _requestInfo = CreateScopedRequestInfo("Alpha|Beta");

            _act = () => CreateMiddleware(claimSetProvider).Execute(_requestInfo, TestHelper.NullNext);
        }

        [Test]
        public async Task It_lets_the_failure_propagate_instead_of_a_no_match_or_ambiguity_response()
        {
            await _act.Should().ThrowAsync<InvalidOperationException>();
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }
}
