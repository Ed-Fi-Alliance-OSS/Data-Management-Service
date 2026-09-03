// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.CustomValidation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

public class CustomResourceValidationMiddlewareTests
{
    internal static IPipelineStep Middleware(
        CustomValidationOperation operation = CustomValidationOperation.Upsert
    )
    {
        return new CustomResourceValidationMiddleware(NullLogger.Instance, operation);
    }

    internal static ResourceInfo DefaultResourceInfo() =>
        new(
            ProjectName: new ProjectName("Ed-Fi"),
            ResourceName: new ResourceName("School"),
            IsDescriptor: false,
            ResourceVersion: new SemVer("5.0.0"),
            AllowIdentityUpdates: false
        );

    internal static RequestInfo BuildRequestInfo(
        IServiceProvider scopedServiceProvider,
        ResourceInfo? resourceInfo = null,
        JsonNode? parsedBody = null,
        string? tenant = null,
        Dictionary<RouteQualifierName, RouteQualifierValue>? routeQualifiers = null,
        string traceId = "test-trace-id",
        CancellationToken cancellationToken = default,
        BackendProfileWriteContext? backendProfileWriteContext = null
    )
    {
        FrontendRequest frontendRequest = new(
            Path: "ed-fi/schools",
            Body: "{}",
            Form: null,
            Headers: [],
            QueryParameters: [],
            TraceId: new TraceId(traceId),
            RouteQualifiers: routeQualifiers ?? [],
            Tenant: tenant
        );

        return new RequestInfo(frontendRequest, RequestMethod.POST, scopedServiceProvider, cancellationToken)
        {
            ResourceInfo = resourceInfo ?? DefaultResourceInfo(),
            ParsedBody = parsedBody ?? new JsonObject(),
            BackendProfileWriteContext = backendProfileWriteContext,
        };
    }

    internal static async Task<bool> ExecuteAndCaptureNext(RequestInfo requestInfo)
    {
        var nextCalled = false;

        await Middleware()
            .Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

        return nextCalled;
    }

    /// <summary>
    /// A validator whose invocations, received documents, and return value are all directly
    /// inspectable, for tests that need to assert on what the step passed it rather than merely
    /// whether it ran.
    /// </summary>
    internal sealed class FakeValidator : ICustomResourceValidator
    {
        public IReadOnlyList<ValidatedResource> AppliesTo { get; init; } = [];

        public IReadOnlyList<CustomValidationFailure> ReturnValue { get; init; } = [];

        public Func<JsonNode, Task>? OnValidateAsync { get; init; }

        public int CallCount { get; private set; }

        public List<JsonNode> ReceivedDocuments { get; } = [];

        public List<ValidatedResourceInfo> ReceivedResources { get; } = [];

        public List<CustomValidationOperation> ReceivedOperations { get; } = [];

        public List<ValidationScope> ReceivedScopes { get; } = [];

        public List<string> ReceivedTraceIds { get; } = [];

        public List<CancellationToken> ReceivedCancellationTokens { get; } = [];

        public async Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
            JsonNode document,
            ValidatedResourceInfo resource,
            CustomValidationOperation operation,
            ValidationScope scope,
            string traceId,
            CancellationToken cancellationToken
        )
        {
            CallCount++;
            ReceivedDocuments.Add(document);
            ReceivedResources.Add(resource);
            ReceivedOperations.Add(operation);
            ReceivedScopes.Add(scope);
            ReceivedTraceIds.Add(traceId);
            ReceivedCancellationTokens.Add(cancellationToken);

            if (OnValidateAsync is not null)
            {
                await OnValidateAsync(document);
            }

            return ReturnValue;
        }
    }

    /// <summary>
    /// A validator built only to record when it was entered and exited, for proving two applicable
    /// validators run one after the other rather than concurrently.
    /// </summary>
    internal sealed class SequencingValidator(string _name, List<string> _log, bool _yieldBeforeExit)
        : ICustomResourceValidator
    {
        public IReadOnlyList<ValidatedResource> AppliesTo { get; init; } = [];

        public async Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
            JsonNode document,
            ValidatedResourceInfo resource,
            CustomValidationOperation operation,
            ValidationScope scope,
            string traceId,
            CancellationToken cancellationToken
        )
        {
            _log.Add($"{_name}:Enter");

            if (_yieldBeforeExit)
            {
                // A synchronous completion here would let a Task.WhenAll-based implementation
                // record entry and exit in order too, since each ValidateAsync would run to
                // completion before the next started. Yielding defeats that: it only proves
                // sequencing if the first validator has genuinely suspended before the second one
                // is ever entered.
                await Task.Yield();
            }

            _log.Add($"{_name}:Exit");
            return [];
        }
    }

    /// <summary>
    /// A validator that downcasts <see cref="ValidationScope.RouteQualifiers"/> back to the mutable
    /// concrete dictionary type and mutates it, for proving the middleware's projection is hardened
    /// against that rather than merely typed as read-only.
    /// Dictionary&lt;string, string&gt; implements IReadOnlyDictionary&lt;string, string&gt;, so
    /// nothing in the interface itself stops a conforming-looking validator from doing this.
    /// </summary>
    internal sealed class RouteQualifierMutatingValidator : ICustomResourceValidator
    {
        public IReadOnlyList<ValidatedResource> AppliesTo { get; init; } = [];

        public Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
            JsonNode document,
            ValidatedResourceInfo resource,
            CustomValidationOperation operation,
            ValidationScope scope,
            string traceId,
            CancellationToken cancellationToken
        )
        {
            if (scope.RouteQualifiers is Dictionary<string, string> mutable)
            {
                mutable["district"] = "tampered-by-first-validator";
            }

            return Task.FromResult<IReadOnlyList<CustomValidationFailure>>([]);
        }
    }

    /// <summary>
    /// A simple ILogger that captures log entries for test verification, matching the pattern
    /// used by the other middleware fixtures in this directory.
    /// </summary>
    internal sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>
    /// A validator that always throws, for proving that the outcomes of a throwing validator are
    /// produced by Core's existing catch chain (<see cref="CoreExceptionLoggingMiddleware"/>) and
    /// not by this step, which adds no exception handling of its own.
    /// </summary>
    internal sealed class ThrowingValidator(Func<Exception> _exceptionFactory) : ICustomResourceValidator
    {
        public IReadOnlyList<ValidatedResource> AppliesTo { get; init; } = [];

        public Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
            JsonNode document,
            ValidatedResourceInfo resource,
            CustomValidationOperation operation,
            ValidationScope scope,
            string traceId,
            CancellationToken cancellationToken
        )
        {
            throw _exceptionFactory();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_No_Validators_Registered_At_All : CustomResourceValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var scopedServiceProvider = new ServiceCollection().BuildServiceProvider();
            _requestInfo = BuildRequestInfo(scopedServiceProvider);
            _nextCalled = await ExecuteAndCaptureNext(_requestInfo);
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_leaves_the_frontend_response_untouched()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Registered_Validator_Whose_AppliesTo_Does_Not_Match
        : CustomResourceValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;
        private FakeValidator _validator = null!;

        [SetUp]
        public async Task Setup()
        {
            _validator = new FakeValidator { AppliesTo = [new ValidatedResource("Ed-Fi", "Student")] };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(_validator)
                .BuildServiceProvider();

            _requestInfo = BuildRequestInfo(scopedServiceProvider);
            _nextCalled = await ExecuteAndCaptureNext(_requestInfo);
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_leaves_the_frontend_response_untouched()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_never_invokes_the_non_matching_validator()
        {
            _validator.CallCount.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Applicable_Validator_Returning_An_Empty_List
        : CustomResourceValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;
        private FakeValidator _validator = null!;

        [SetUp]
        public async Task Setup()
        {
            _validator = new FakeValidator
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
                ReturnValue = [],
            };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(_validator)
                .BuildServiceProvider();

            _requestInfo = BuildRequestInfo(scopedServiceProvider);
            _nextCalled = await ExecuteAndCaptureNext(_requestInfo);
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_leaves_the_frontend_response_untouched()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_invokes_the_applicable_validator_exactly_once()
        {
            _validator.CallCount.Should().Be(1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Validator_Whose_AppliesTo_Differs_Only_By_ProjectName_Case
        : CustomResourceValidationMiddlewareTests
    {
        private FakeValidator _validator = null!;

        [SetUp]
        public async Task Setup()
        {
            // The request's own resource is Ed-Fi/School (see DefaultResourceInfo); this entry
            // matches ResourceName exactly but flips only the case of ProjectName.
            _validator = new FakeValidator { AppliesTo = [new ValidatedResource("ed-fi", "School")] };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(_validator)
                .BuildServiceProvider();

            var requestInfo = BuildRequestInfo(scopedServiceProvider);
            await ExecuteAndCaptureNext(requestInfo);
        }

        [Test]
        public void It_never_invokes_the_case_mismatched_validator()
        {
            _validator.CallCount.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Validator_Whose_AppliesTo_Differs_Only_By_ResourceName_Case
        : CustomResourceValidationMiddlewareTests
    {
        private FakeValidator _validator = null!;

        [SetUp]
        public async Task Setup()
        {
            // The request's own resource is Ed-Fi/School (see DefaultResourceInfo); this entry
            // matches ProjectName exactly but flips only the case of ResourceName.
            _validator = new FakeValidator { AppliesTo = [new ValidatedResource("Ed-Fi", "school")] };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(_validator)
                .BuildServiceProvider();

            var requestInfo = BuildRequestInfo(scopedServiceProvider);
            await ExecuteAndCaptureNext(requestInfo);
        }

        [Test]
        public void It_never_invokes_the_case_mismatched_validator()
        {
            _validator.CallCount.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Two_Applicable_Validators : CustomResourceValidationMiddlewareTests
    {
        private List<string> _log = null!;

        [SetUp]
        public async Task Setup()
        {
            _log = [];

            var first = new SequencingValidator("First", _log, _yieldBeforeExit: true)
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
            };
            var second = new SequencingValidator("Second", _log, _yieldBeforeExit: false)
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
            };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(first)
                .AddSingleton<ICustomResourceValidator>(second)
                .BuildServiceProvider();

            var requestInfo = BuildRequestInfo(scopedServiceProvider);
            await ExecuteAndCaptureNext(requestInfo);
        }

        [Test]
        public void It_runs_them_sequentially_rather_than_concurrently()
        {
            _log.Should().Equal("First:Enter", "First:Exit", "Second:Enter", "Second:Exit");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Two_Applicable_Validators_Each_Receives_Its_Own_Document_Instance
        : CustomResourceValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private FakeValidator _second = null!;

        [SetUp]
        public async Task Setup()
        {
            var first = new FakeValidator
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
                OnValidateAsync = document =>
                {
                    ((JsonObject)document)["mutatedBy"] = "first";
                    return Task.CompletedTask;
                },
            };
            _second = new FakeValidator { AppliesTo = [new ValidatedResource("Ed-Fi", "School")] };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(first)
                .AddSingleton<ICustomResourceValidator>(_second)
                .BuildServiceProvider();

            var parsedBody = new JsonObject { ["original"] = "value" };
            _requestInfo = BuildRequestInfo(scopedServiceProvider, parsedBody: parsedBody);
            await ExecuteAndCaptureNext(_requestInfo);
        }

        [Test]
        public void It_does_not_let_the_second_validator_see_the_first_validators_mutation()
        {
            _second.ReceivedDocuments.Should().HaveCount(1);
            _second.ReceivedDocuments[0]["mutatedBy"].Should().BeNull();
        }

        [Test]
        public void It_leaves_the_requests_parsed_body_unchanged()
        {
            _requestInfo.ParsedBody["mutatedBy"].Should().BeNull();
            _requestInfo.ParsedBody["original"]!.GetValue<string>().Should().Be("value");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Step_Forwards_The_Operation_It_Was_Constructed_With
        : CustomResourceValidationMiddlewareTests
    {
        // This proves forwarding only: the choice of which operation a given pipeline wires the
        // step with lives in a constructor argument at the wiring site, which a test that news up
        // the middleware directly cannot see. That assertion belongs to the pipeline-wiring tests.
        [TestCase(CustomValidationOperation.Upsert)]
        [TestCase(CustomValidationOperation.Update)]
        public async Task It_forwards_that_operation_to_the_validator(CustomValidationOperation operation)
        {
            var validator = new FakeValidator { AppliesTo = [new ValidatedResource("Ed-Fi", "School")] };
            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(validator)
                .BuildServiceProvider();
            var requestInfo = BuildRequestInfo(scopedServiceProvider);

            await new CustomResourceValidationMiddleware(NullLogger.Instance, operation).Execute(
                requestInfo,
                () => Task.CompletedTask
            );

            validator.ReceivedOperations.Should().Equal(operation);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Validator_Receives_The_Requests_Own_Resource
        : CustomResourceValidationMiddlewareTests
    {
        private FakeValidator _validator = null!;

        [SetUp]
        public async Task Setup()
        {
            _validator = new FakeValidator { AppliesTo = [new ValidatedResource("Ed-Fi", "School")] };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(_validator)
                .BuildServiceProvider();

            var resourceInfo = new ResourceInfo(
                ProjectName: new ProjectName("Ed-Fi"),
                ResourceName: new ResourceName("School"),
                IsDescriptor: false,
                ResourceVersion: new SemVer("5.1.0"),
                AllowIdentityUpdates: false
            );

            var requestInfo = BuildRequestInfo(scopedServiceProvider, resourceInfo: resourceInfo);
            await ExecuteAndCaptureNext(requestInfo);
        }

        // Asserted member by member, rather than on the whole ValidatedResourceInfo at once, so a
        // swapped or dropped member cannot pass.
        [Test]
        public void It_matches_the_requests_own_project_name()
        {
            _validator.ReceivedResources.Should().ContainSingle().Which.ProjectName.Should().Be("Ed-Fi");
        }

        [Test]
        public void It_matches_the_requests_own_resource_name()
        {
            _validator.ReceivedResources.Should().ContainSingle().Which.ResourceName.Should().Be("School");
        }

        [Test]
        public void It_matches_the_requests_own_resource_version()
        {
            _validator.ReceivedResources.Should().ContainSingle().Which.ResourceVersion.Should().Be("5.1.0");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Validator_Receives_The_Requests_Own_Trace_Id
        : CustomResourceValidationMiddlewareTests
    {
        private FakeValidator _validator = null!;

        [SetUp]
        public async Task Setup()
        {
            _validator = new FakeValidator { AppliesTo = [new ValidatedResource("Ed-Fi", "School")] };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(_validator)
                .BuildServiceProvider();

            var requestInfo = BuildRequestInfo(scopedServiceProvider, traceId: "distinct-trace-id-42");
            await ExecuteAndCaptureNext(requestInfo);
        }

        [Test]
        public void It_matches_the_requests_own_trace_id()
        {
            _validator.ReceivedTraceIds.Should().Equal("distinct-trace-id-42");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Validator_Receives_The_Requests_Own_Cancellation_Token
        : CustomResourceValidationMiddlewareTests
    {
        private FakeValidator _validator = null!;
        private CancellationTokenSource _cts = null!;

        [SetUp]
        public async Task Setup()
        {
            _validator = new FakeValidator { AppliesTo = [new ValidatedResource("Ed-Fi", "School")] };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(_validator)
                .BuildServiceProvider();

            // A real, non-default token: a request left at CancellationToken.None (the default)
            // would make this assertion vacuous against an implementation that hands out
            // CancellationToken.None outright rather than forwarding the request's own token.
            _cts = new CancellationTokenSource();
            var requestInfo = BuildRequestInfo(scopedServiceProvider, cancellationToken: _cts.Token);
            await ExecuteAndCaptureNext(requestInfo);
        }

        [TearDown]
        public void TearDown() => _cts.Dispose();

        [Test]
        public void It_matches_the_requests_own_cancellation_token()
        {
            _validator.ReceivedCancellationTokens.Should().Equal(_cts.Token);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Validator_Receives_The_Requests_Own_Tenant_When_Non_Null
        : CustomResourceValidationMiddlewareTests
    {
        private FakeValidator _validator = null!;

        [SetUp]
        public async Task Setup()
        {
            _validator = new FakeValidator { AppliesTo = [new ValidatedResource("Ed-Fi", "School")] };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(_validator)
                .BuildServiceProvider();

            var requestInfo = BuildRequestInfo(scopedServiceProvider, tenant: "tenant-a");
            await ExecuteAndCaptureNext(requestInfo);
        }

        [Test]
        public void It_carries_the_requests_own_tenant()
        {
            _validator.ReceivedScopes.Should().ContainSingle().Which.Tenant.Should().Be("tenant-a");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Validator_Receives_The_Requests_Own_Tenant_When_Null_Single_Tenant
        : CustomResourceValidationMiddlewareTests
    {
        private FakeValidator _validator = null!;

        [SetUp]
        public async Task Setup()
        {
            _validator = new FakeValidator { AppliesTo = [new ValidatedResource("Ed-Fi", "School")] };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(_validator)
                .BuildServiceProvider();

            // A null-only assertion would pass against an implementation that hardcodes null and
            // never reads the request; the sibling non-null-tenant fixture is what actually rules
            // that out. This fixture only proves the null single-tenant case is passed through too.
            var requestInfo = BuildRequestInfo(scopedServiceProvider, tenant: null);
            await ExecuteAndCaptureNext(requestInfo);
        }

        [Test]
        public void It_carries_a_null_tenant()
        {
            _validator.ReceivedScopes.Should().ContainSingle().Which.Tenant.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Validator_Receives_The_Requests_Own_Route_Qualifiers
        : CustomResourceValidationMiddlewareTests
    {
        private FakeValidator _validator = null!;

        [SetUp]
        public async Task Setup()
        {
            _validator = new FakeValidator { AppliesTo = [new ValidatedResource("Ed-Fi", "School")] };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(_validator)
                .BuildServiceProvider();

            // Tenant is left at its null default here (a single-tenant, routed deployment) so this
            // test cannot pass against an implementation that only ever populates Tenant.
            var requestInfo = BuildRequestInfo(
                scopedServiceProvider,
                routeQualifiers: new Dictionary<RouteQualifierName, RouteQualifierValue>
                {
                    [new RouteQualifierName("district")] = new RouteQualifierValue("255901"),
                    [new RouteQualifierName("schoolYear")] = new RouteQualifierValue("2024"),
                }
            );
            await ExecuteAndCaptureNext(requestInfo);
        }

        [Test]
        public void It_carries_the_requests_own_route_qualifiers()
        {
            _validator
                .ReceivedScopes.Should()
                .ContainSingle()
                .Which.RouteQualifiers.Should()
                .BeEquivalentTo(
                    new Dictionary<string, string> { ["district"] = "255901", ["schoolYear"] = "2024" }
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Requests_Route_Qualifiers_Are_Mutated_After_The_Pipeline_Runs
        : CustomResourceValidationMiddlewareTests
    {
        private FakeValidator _validator = null!;
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            _validator = new FakeValidator { AppliesTo = [new ValidatedResource("Ed-Fi", "School")] };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(_validator)
                .BuildServiceProvider();

            _requestInfo = BuildRequestInfo(
                scopedServiceProvider,
                routeQualifiers: new Dictionary<RouteQualifierName, RouteQualifierValue>
                {
                    [new RouteQualifierName("district")] = new RouteQualifierValue("255901"),
                }
            );
            await ExecuteAndCaptureNext(_requestInfo);

            // Mutating the request's own dictionary only after the pipeline has already run and
            // handed a scope to the validator: the captured scope must not observe this.
            _requestInfo.FrontendRequest.RouteQualifiers[new RouteQualifierName("district")] =
                new RouteQualifierValue("999999");
            _requestInfo.FrontendRequest.RouteQualifiers[new RouteQualifierName("schoolYear")] =
                new RouteQualifierValue("2099");
        }

        [Test]
        public void It_does_not_observe_the_post_run_mutation()
        {
            _validator
                .ReceivedScopes.Should()
                .ContainSingle()
                .Which.RouteQualifiers.Should()
                .BeEquivalentTo(new Dictionary<string, string> { ["district"] = "255901" });
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Validator_Downcasts_And_Mutates_The_Route_Qualifiers_Dictionary
        : CustomResourceValidationMiddlewareTests
    {
        private FakeValidator _second = null!;

        [SetUp]
        public async Task Setup()
        {
            var first = new RouteQualifierMutatingValidator
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
            };
            _second = new FakeValidator { AppliesTo = [new ValidatedResource("Ed-Fi", "School")] };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(first)
                .AddSingleton<ICustomResourceValidator>(_second)
                .BuildServiceProvider();

            var requestInfo = BuildRequestInfo(
                scopedServiceProvider,
                routeQualifiers: new Dictionary<RouteQualifierName, RouteQualifierValue>
                {
                    [new RouteQualifierName("district")] = new RouteQualifierValue("255901"),
                }
            );
            await ExecuteAndCaptureNext(requestInfo);
        }

        /// <summary>
        /// The projection is handed out via AsReadOnly() rather than as the raw Dictionary&lt;,&gt;
        /// it was built from, so the first validator's downcast above cannot succeed and the second
        /// validator in the same request never observes the attempted mutation - the same
        /// validator-to-validator leak the per-validator DeepClone() on the document already
        /// prevents, closed here on the route-qualifiers input too.
        /// </summary>
        [Test]
        public void It_does_not_let_the_second_validator_observe_the_attempted_mutation()
        {
            _second
                .ReceivedScopes.Should()
                .ContainSingle()
                .Which.RouteQualifiers.Should()
                .BeEquivalentTo(new Dictionary<string, string> { ["district"] = "255901" });
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Writable_Profile_Applies : CustomResourceValidationMiddlewareTests
    {
        private FakeValidator _validator = null!;
        private JsonObject _shapedBody = null!;

        [SetUp]
        public async Task Setup()
        {
            _validator = new FakeValidator { AppliesTo = [new ValidatedResource("Ed-Fi", "School")] };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(_validator)
                .BuildServiceProvider();

            var parsedBody = new JsonObject { ["raw"] = "value", ["hidden"] = "secret" };
            _shapedBody = new JsonObject { ["raw"] = "value" };

            var requestInfo = BuildRequestInfo(
                scopedServiceProvider,
                parsedBody: parsedBody,
                backendProfileWriteContext: new BackendProfileWriteContext(
                    Request: new ProfileAppliedWriteRequest(
                        WritableRequestBody: _shapedBody,
                        RootResourceCreatable: true,
                        RequestScopeStates: [],
                        VisibleRequestCollectionItems: []
                    ),
                    ProfileName: "TestProfile",
                    CompiledScopeCatalog: [],
                    StoredStateProjectionInvoker: null!
                )
            );

            await ExecuteAndCaptureNext(requestInfo);
        }

        [Test]
        public void It_gives_the_validator_the_profile_shaped_body_rather_than_the_raw_parsed_body()
        {
            _validator.ReceivedDocuments.Should().ContainSingle();
            _validator.ReceivedDocuments[0]["hidden"].Should().BeNull();
            _validator.ReceivedDocuments[0]["raw"]!.GetValue<string>().Should().Be("value");
        }

        /// <summary>
        /// Selecting the right body is not enough on its own: the profile branch has to be cloned
        /// too. WritableRequestBody is the instance the backend goes on to persist
        /// (ProfileWritePipelineMiddleware passes it as canonicalizedRequestBody), so handing the
        /// live object to a validator would let one that ignores the read-only contract rule change
        /// what gets written. Asserting only which body was selected would stay green against an
        /// implementation that cloned unprofiled bodies and passed this one through.
        /// </summary>
        [Test]
        public void It_gives_the_validator_a_clone_rather_than_the_writable_request_body_itself()
        {
            _validator.ReceivedDocuments[0].Should().NotBeSameAs(_shapedBody);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_No_Writable_Profile_Applies : CustomResourceValidationMiddlewareTests
    {
        private FakeValidator _validator = null!;

        [SetUp]
        public async Task Setup()
        {
            _validator = new FakeValidator { AppliesTo = [new ValidatedResource("Ed-Fi", "School")] };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(_validator)
                .BuildServiceProvider();

            var parsedBody = new JsonObject { ["raw"] = "value" };
            var requestInfo = BuildRequestInfo(scopedServiceProvider, parsedBody: parsedBody);

            await ExecuteAndCaptureNext(requestInfo);
        }

        [Test]
        public void It_gives_the_validator_the_raw_parsed_body()
        {
            _validator.ReceivedDocuments.Should().ContainSingle();
            _validator.ReceivedDocuments[0]["raw"]!.GetValue<string>().Should().Be("value");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Applicable_Validator_Returns_An_OnPath_Failure
        : CustomResourceValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var validator = new FakeValidator
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
                ReturnValue = [new CustomValidationFailure.OnPath("$.schoolId", "schoolId is invalid.")],
            };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(validator)
                .BuildServiceProvider();

            _requestInfo = BuildRequestInfo(scopedServiceProvider);
            _nextCalled = await ExecuteAndCaptureNext(_requestInfo);
        }

        [Test]
        public void It_does_not_call_next()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_lands_the_failure_message_under_its_json_path_in_validationErrors()
        {
            _requestInfo.FrontendResponse.Body!["validationErrors"]!["$.schoolId"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal("schoolId is invalid.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Applicable_Validator_Returns_An_OnResource_Failure
        : CustomResourceValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var validator = new FakeValidator
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
                ReturnValue = [new CustomValidationFailure.OnResource("The resource is invalid overall.")],
            };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(validator)
                .BuildServiceProvider();

            _requestInfo = BuildRequestInfo(scopedServiceProvider);
            _nextCalled = await ExecuteAndCaptureNext(_requestInfo);
        }

        [Test]
        public void It_does_not_call_next()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_lands_the_failure_message_in_errors()
        {
            _requestInfo.FrontendResponse.Body!["errors"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal("The resource is invalid overall.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Two_Validators_Return_OnPath_Failures_For_The_Same_Path
        : CustomResourceValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            var first = new FakeValidator
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
                ReturnValue = [new CustomValidationFailure.OnPath("$.schoolId", "first failure.")],
            };
            var second = new FakeValidator
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
                ReturnValue = [new CustomValidationFailure.OnPath("$.schoolId", "second failure.")],
            };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(first)
                .AddSingleton<ICustomResourceValidator>(second)
                .BuildServiceProvider();

            _requestInfo = BuildRequestInfo(scopedServiceProvider);
            await ExecuteAndCaptureNext(_requestInfo);
        }

        [Test]
        public void It_carries_both_validators_messages_under_the_shared_path()
        {
            // An implementation that assigns rather than appends drops the first validator's
            // message once the second validator reports a failure for the same path.
            _requestInfo.FrontendResponse.Body!["validationErrors"]!["$.schoolId"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal("first failure.", "second failure.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Two_Applicable_Validators_Both_Return_Failures
        : CustomResourceValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            var first = new FakeValidator
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
                ReturnValue = [new CustomValidationFailure.OnPath("$.schoolId", "first failure.")],
            };
            var second = new FakeValidator
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
                ReturnValue = [new CustomValidationFailure.OnResource("second failure.")],
            };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(first)
                .AddSingleton<ICustomResourceValidator>(second)
                .BuildServiceProvider();

            _requestInfo = BuildRequestInfo(scopedServiceProvider);
            await ExecuteAndCaptureNext(_requestInfo);
        }

        [Test]
        public void It_runs_both_validators_rather_than_short_circuiting_on_the_first_failure()
        {
            _requestInfo.FrontendResponse.Body!["validationErrors"]!["$.schoolId"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal("first failure.");
            _requestInfo.FrontendResponse.Body!["errors"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal("second failure.");
        }

        /// <summary>
        /// This is the only fixture where both arms are populated at once, so it is the only place
        /// the factory-selection rule can be pinned for the mixed case: an errors-arm failure picks
        /// ForBadRequest even when validationErrors is also non-empty. Both factories emit
        /// validationErrors and errors identically and differ only in detail, type and title, so
        /// asserting arm contents cannot tell them apart - narrowing the rule to
        /// "errors.Count > 0 &amp;&amp; validationErrors.Count == 0" leaves every other test green.
        /// </summary>
        [Test]
        public void It_picks_ForBadRequest_even_though_validationErrors_is_also_populated()
        {
            JsonNode expectedForBadRequest = FailureResponse.ForBadRequest(
                "ignored",
                new TraceId("ignored"),
                [],
                []
            );

            _requestInfo.FrontendResponse.Body!["type"]!
                .GetValue<string>()
                .Should()
                .Be(expectedForBadRequest["type"]!.GetValue<string>());
            _requestInfo.FrontendResponse.Body!["title"]!
                .GetValue<string>()
                .Should()
                .Be(expectedForBadRequest["title"]!.GetValue<string>());
            _requestInfo.FrontendResponse.Body!["detail"]!
                .GetValue<string>()
                .Should()
                .Be(FailureResponse.ErrorsArmDetail);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Failure_On_The_Errors_Arm : CustomResourceValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private JsonNode _expectedForBadRequest = null!;

        [SetUp]
        public async Task Setup()
        {
            var validator = new FakeValidator
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
                ReturnValue = [new CustomValidationFailure.OnResource("The resource is invalid overall.")],
            };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(validator)
                .BuildServiceProvider();

            _requestInfo = BuildRequestInfo(scopedServiceProvider, traceId: "errors-arm-trace-id-23!");
            await ExecuteAndCaptureNext(_requestInfo);

            // The arm's own factory output, compared against rather than a hand-built literal: the
            // two factories differ in both type and title, so a detail-only comparison would pass
            // against a body a client could tell apart from core's. Built with placeholder arguments
            // because only the factory-fixed fields are compared against it - the correlationId is
            // request-derived, so it is asserted against the request's own trace id instead.
            _expectedForBadRequest = FailureResponse.ForBadRequest("ignored", new TraceId("ignored"), [], []);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_uses_the_bad_request_detail_literal()
        {
            _requestInfo.FrontendResponse.Body!["detail"]!
                .GetValue<string>()
                .Should()
                .Be(FailureResponse.ErrorsArmDetail);
        }

        [Test]
        public void It_matches_ForBadRequests_own_type()
        {
            _requestInfo.FrontendResponse.Body!["type"]!
                .GetValue<string>()
                .Should()
                .Be(_expectedForBadRequest["type"]!.GetValue<string>());
        }

        [Test]
        public void It_matches_ForBadRequests_own_title()
        {
            _requestInfo.FrontendResponse.Body!["title"]!
                .GetValue<string>()
                .Should()
                .Be(_expectedForBadRequest["title"]!.GetValue<string>());
        }

        [Test]
        public void It_matches_ForBadRequests_own_status()
        {
            _requestInfo.FrontendResponse.Body!["status"]!
                .GetValue<int>()
                .Should()
                .Be(_expectedForBadRequest["status"]!.GetValue<int>());
        }

        /// <summary>
        /// The correlationId is the only client-visible value in this body that comes from the
        /// request rather than from the factory, so nothing else here can pin it. Without this,
        /// hardcoding any other trace id at the factory call site leaves the whole suite green
        /// while every custom-validation 400 reports the wrong correlation id to the client.
        /// The trace id ends in a character LoggingSanitizer strips, so this also pins that the
        /// response echoes the client's value verbatim: the sanitizing this step does for its log
        /// records must not reach the body, or the client cannot match the id it sent.
        /// </summary>
        [Test]
        public void It_carries_the_requests_own_trace_id_as_the_correlation_id()
        {
            _requestInfo.FrontendResponse.Body!["correlationId"]!
                .GetValue<string>()
                .Should()
                .Be("errors-arm-trace-id-23!");
        }

        [Test]
        public void It_uses_the_json_content_type()
        {
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/json");
        }

        [Test]
        public void It_carries_no_headers()
        {
            _requestInfo.FrontendResponse.Headers.Should().BeEmpty();
        }

        [Test]
        public void It_carries_no_location_header_path()
        {
            _requestInfo.FrontendResponse.LocationHeaderPath.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Failure_On_The_ValidationErrors_Arm : CustomResourceValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private JsonNode _expectedForDataValidation = null!;

        [SetUp]
        public async Task Setup()
        {
            var validator = new FakeValidator
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
                ReturnValue = [new CustomValidationFailure.OnPath("$.schoolId", "schoolId is invalid.")],
            };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(validator)
                .BuildServiceProvider();

            _requestInfo = BuildRequestInfo(
                scopedServiceProvider,
                traceId: "validation-errors-arm-trace-id-29!"
            );
            await ExecuteAndCaptureNext(_requestInfo);

            _expectedForDataValidation = FailureResponse.ForDataValidation(
                "ignored",
                new TraceId("ignored"),
                [],
                []
            );
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_uses_the_data_validation_detail_literal()
        {
            _requestInfo.FrontendResponse.Body!["detail"]!
                .GetValue<string>()
                .Should()
                .Be(FailureResponse.ValidationErrorsArmDetail);
        }

        [Test]
        public void It_matches_ForDataValidations_own_type()
        {
            _requestInfo.FrontendResponse.Body!["type"]!
                .GetValue<string>()
                .Should()
                .Be(_expectedForDataValidation["type"]!.GetValue<string>());
        }

        [Test]
        public void It_matches_ForDataValidations_own_title()
        {
            _requestInfo.FrontendResponse.Body!["title"]!
                .GetValue<string>()
                .Should()
                .Be(_expectedForDataValidation["title"]!.GetValue<string>());
        }

        [Test]
        public void It_matches_ForDataValidations_own_status()
        {
            _requestInfo.FrontendResponse.Body!["status"]!
                .GetValue<int>()
                .Should()
                .Be(_expectedForDataValidation["status"]!.GetValue<int>());
        }

        /// <summary>
        /// The correlationId is the only client-visible value in this body that comes from the
        /// request rather than from the factory, so nothing else here can pin it. Without this,
        /// hardcoding any other trace id at the factory call site leaves the whole suite green
        /// while every custom-validation 400 reports the wrong correlation id to the client.
        /// The trace id ends in a character LoggingSanitizer strips, so this also pins that the
        /// response echoes the client's value verbatim: the sanitizing this step does for its log
        /// records must not reach the body, or the client cannot match the id it sent.
        /// </summary>
        [Test]
        public void It_carries_the_requests_own_trace_id_as_the_correlation_id()
        {
            _requestInfo.FrontendResponse.Body!["correlationId"]!
                .GetValue<string>()
                .Should()
                .Be("validation-errors-arm-trace-id-29!");
        }

        [Test]
        public void It_uses_the_json_content_type()
        {
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/json");
        }

        [Test]
        public void It_carries_no_headers()
        {
            _requestInfo.FrontendResponse.Headers.Should().BeEmpty();
        }

        [Test]
        public void It_carries_no_location_header_path()
        {
            _requestInfo.FrontendResponse.LocationHeaderPath.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Applicable_Validator_Its_Elapsed_Time_Log_Entry
        : CustomResourceValidationMiddlewareTests
    {
        private CapturingLogger _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new CapturingLogger();

            var validator = new FakeValidator { AppliesTo = [new ValidatedResource("Ed-Fi", "School")] };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(validator)
                .BuildServiceProvider();

            var requestInfo = BuildRequestInfo(scopedServiceProvider, traceId: "elapsed-trace-id-3");

            await new CustomResourceValidationMiddleware(_logger, CustomValidationOperation.Upsert).Execute(
                requestInfo,
                () => Task.CompletedTask
            );
        }

        /// <summary>
        /// This design puts third-party network I/O on the write path with no timeout the contract
        /// controls, so per-validator elapsed time is what a deployment reaches for when a validator
        /// is slow. This proves that record actually exists, names the validator, and carries the
        /// request's own TraceId for correlation. See
        /// <see cref="Given_An_Applicable_Validator_That_Throws_Its_Elapsed_Time_Log_Entry"/> for
        /// the same record on the path where the validator does not return normally.
        /// </summary>
        [Test]
        public void It_logs_the_validators_elapsed_time_against_the_trace_id()
        {
            _logger
                .Entries.Should()
                .Contain(entry =>
                    entry.Level == LogLevel.Debug
                    && entry.Message.Contains(nameof(FakeValidator))
                    && entry.Message.Contains("ran in")
                    && entry.Message.Contains("ms")
                    && entry.Message.Contains("elapsed-trace-id-3")
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Applicable_Validator_Returns_Failures_Its_Log_Entries
        : CustomResourceValidationMiddlewareTests
    {
        // Distinctive enough that its presence in any captured log entry is unmistakable - a
        // failure message must never reach a log record, since failure messages can quote
        // submitted document values.
        private const string DistinctiveFailureMessage = "UNMISTAKABLE_SUBMITTED_VALUE_49213_DO_NOT_LOG";

        private CapturingLogger _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new CapturingLogger();

            var validator = new FakeValidator
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
                ReturnValue = [new CustomValidationFailure.OnPath("$.schoolId", DistinctiveFailureMessage)],
            };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(validator)
                .BuildServiceProvider();

            var requestInfo = BuildRequestInfo(scopedServiceProvider, traceId: "logging-trace-id-7");

            await new CustomResourceValidationMiddleware(_logger, CustomValidationOperation.Upsert).Execute(
                requestInfo,
                () => Task.CompletedTask
            );
        }

        [Test]
        public void It_logs_an_entry_naming_the_validator_and_its_failure_count_against_the_trace_id()
        {
            _logger
                .Entries.Should()
                .Contain(entry =>
                    entry.Level == LogLevel.Information
                    && entry.Message.Contains(nameof(FakeValidator))
                    && entry.Message.Contains("1 failure")
                    && entry.Message.Contains("logging-trace-id-7")
                );
        }

        [Test]
        public void It_never_logs_the_failure_message_text_in_any_captured_entry()
        {
            _logger.Entries.Should().NotContain(entry => entry.Message.Contains(DistinctiveFailureMessage));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Validator_Returns_Null_From_ValidateAsync : CustomResourceValidationMiddlewareTests
    {
        private Func<Task> _execute = null!;

        [SetUp]
        public void Setup()
        {
            // A null return is not a substitute for an empty list per ICustomResourceValidator's own
            // contract, so it must throw rather than be silently coerced into "no failures found".
            var validator = new FakeValidator
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
                ReturnValue = null!,
            };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(validator)
                .BuildServiceProvider();

            var requestInfo = BuildRequestInfo(scopedServiceProvider);

            _execute = () =>
                Middleware().Execute(requestInfo, () => throw new AssertionException("next should not run"));
        }

        [Test]
        public async Task It_throws_rather_than_treating_the_null_as_an_empty_list()
        {
            await _execute.Should().ThrowAsync<InvalidOperationException>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Validator_Whose_AppliesTo_Is_Null : CustomResourceValidationMiddlewareTests
    {
        private Func<Task> _execute = null!;

        [SetUp]
        public void Setup()
        {
            // AppliesTo is read for every registered validator on every write, so an unguarded null
            // here fails writes to every resource rather than only to this validator's own. That is
            // wider reach than a null ValidateAsync return has, so it gets at least as loud a
            // failure: an exception naming the property and the validator, not a bare
            // NullReferenceException raised inside a filtering lambda.
            var validator = new FakeValidator { AppliesTo = null! };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(validator)
                .BuildServiceProvider();

            var requestInfo = BuildRequestInfo(scopedServiceProvider);

            _execute = () =>
                Middleware().Execute(requestInfo, () => throw new AssertionException("next should not run"));
        }

        [Test]
        public async Task It_throws_an_InvalidOperationException_rather_than_dereferencing_the_null()
        {
            await _execute.Should().ThrowAsync<InvalidOperationException>();
        }

        [Test]
        public async Task It_names_the_offending_validator_and_the_property()
        {
            (await _execute.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should()
                .Contain(nameof(FakeValidator))
                .And.Contain(nameof(ICustomResourceValidator.AppliesTo));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Validator_Whose_AppliesTo_Contains_A_Null_Entry
        : CustomResourceValidationMiddlewareTests
    {
        private Func<Task> _execute = null!;

        [SetUp]
        public void Setup()
        {
            // The null entry deliberately sits after a matching one. A short-circuiting match would
            // hide it, so this also pins that the null check does not depend on entry order: the
            // same broken validator must be reported whether or not it happens to apply here.
            var validator = new FakeValidator
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School"), null!],
            };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(validator)
                .BuildServiceProvider();

            var requestInfo = BuildRequestInfo(scopedServiceProvider);

            _execute = () =>
                Middleware().Execute(requestInfo, () => throw new AssertionException("next should not run"));
        }

        [Test]
        public async Task It_throws_an_InvalidOperationException_rather_than_dereferencing_the_entry()
        {
            await _execute.Should().ThrowAsync<InvalidOperationException>();
        }

        [Test]
        public async Task It_names_the_offending_validator()
        {
            (await _execute.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should()
                .Contain(nameof(FakeValidator));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Validator_Returns_A_Null_Failure_In_Its_List
        : CustomResourceValidationMiddlewareTests
    {
        private Func<Task> _execute = null!;

        [SetUp]
        public void Setup()
        {
            // A type pattern never matches null, so without an explicit null arm this element falls
            // through to the switch expression's discard arm and throws NullReferenceException on
            // failure.GetType() - inside the very arm that exists to fail loud with a name. Asserting
            // InvalidOperationException is what separates the two: NullReferenceException is not one.
            var validator = new FakeValidator
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
                ReturnValue = [null!],
            };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(validator)
                .BuildServiceProvider();

            var requestInfo = BuildRequestInfo(scopedServiceProvider);

            _execute = () =>
                Middleware().Execute(requestInfo, () => throw new AssertionException("next should not run"));
        }

        [Test]
        public async Task It_throws_an_InvalidOperationException_rather_than_a_NullReferenceException()
        {
            await _execute.Should().ThrowAsync<InvalidOperationException>();
        }

        [Test]
        public async Task It_names_the_offending_validator()
        {
            (await _execute.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should()
                .Contain(nameof(FakeValidator));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Applicable_Validator_That_Throws_Its_Elapsed_Time_Log_Entry
        : CustomResourceValidationMiddlewareTests
    {
        private CapturingLogger _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new CapturingLogger();

            var validator = new ThrowingValidator(() => new InvalidOperationException("validator failed"))
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
            };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(validator)
                .BuildServiceProvider();

            var requestInfo = BuildRequestInfo(scopedServiceProvider, traceId: "throwing-trace-id-11");

            try
            {
                await new CustomResourceValidationMiddleware(
                    _logger,
                    CustomValidationOperation.Upsert
                ).Execute(requestInfo, () => Task.CompletedTask);
            }
            catch (InvalidOperationException)
            {
                // Expected, and not what this fixture is about: the step adds no exception handling
                // of its own, so a throwing validator escapes to Core's existing catch chain. What
                // is asserted below is what was recorded on the way out.
            }
        }

        /// <summary>
        /// A validator that hangs and then throws - an HttpClient timeout, say - is exactly the case
        /// the elapsed-time record exists for, and it is the case that would lose the record if the
        /// log sat after the await rather than in a finally.
        /// </summary>
        [Test]
        public void It_still_logs_the_validators_elapsed_time_against_the_trace_id()
        {
            _logger
                .Entries.Should()
                .Contain(entry =>
                    entry.Level == LogLevel.Debug
                    && entry.Message.Contains(nameof(ThrowingValidator))
                    && entry.Message.Contains("ran in")
                    && entry.Message.Contains("ms")
                    && entry.Message.Contains("throwing-trace-id-11")
                );
        }
    }

    /// <summary>
    /// The outcomes below belong to Core's existing catch chain, not to this step, so each fixture
    /// runs the step behind a real <see cref="CoreExceptionLoggingMiddleware"/> in a small
    /// <see cref="PipelineProvider"/> rather than in isolation. No exception handling is added to
    /// the step itself: the design's fail-loud posture is that the existing catch-all already
    /// covers a throwing validator.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Validator_Throws_An_Ordinary_Exception_Behind_The_Real_Catch_Chain
        : CustomResourceValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private Exception? _escaped;

        [SetUp]
        public async Task Setup()
        {
            var validator = new ThrowingValidator(() => new InvalidOperationException("validator blew up"))
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
            };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(validator)
                .BuildServiceProvider();

            _requestInfo = BuildRequestInfo(scopedServiceProvider);

            var pipeline = new PipelineProvider([
                new CoreExceptionLoggingMiddleware(NullLogger.Instance, null),
                Middleware(),
            ]);

            try
            {
                await pipeline.Run(_requestInfo);
            }
            catch (Exception ex)
            {
                _escaped = ex;
            }
        }

        [Test]
        public void It_does_not_let_the_exception_escape_the_pipeline()
        {
            _escaped.Should().BeNull();
        }

        [Test]
        public void It_produces_a_500_through_the_catch_all()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(500);
        }

        [Test]
        public void It_does_not_produce_a_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().NotBe(400);
        }
    }

    /// <summary>
    /// CoreExceptionLoggingMiddleware's rethrow arm is guarded by
    /// <c>requestInfo.RequestCancellationToken.IsCancellationRequested</c>, so an
    /// OperationCanceledException on an already-cancelled request must propagate rather than be
    /// converted into a response.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Validator_Throws_OperationCanceledException_On_A_Cancelled_Request
        : CustomResourceValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private CancellationTokenSource _cts = null!;
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _cts = new CancellationTokenSource();
            _cts.Cancel();

            var validator = new ThrowingValidator(() =>
                new OperationCanceledException("validator observed cancellation")
            )
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
            };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(validator)
                .BuildServiceProvider();

            // RequestInfo.RequestCancellationToken has a public setter, so an already-cancelled
            // source can be handed to it directly.
            _requestInfo = BuildRequestInfo(scopedServiceProvider, cancellationToken: _cts.Token);

            var pipeline = new PipelineProvider([
                new CoreExceptionLoggingMiddleware(NullLogger.Instance, null),
                Middleware(),
            ]);

            _act = () => pipeline.Run(_requestInfo);
        }

        [TearDown]
        public void TearDown() => _cts.Dispose();

        [Test]
        public async Task It_propagates_rather_than_being_caught_by_the_catch_all()
        {
            await _act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Test]
        public async Task It_leaves_the_frontend_response_untouched()
        {
            await _act.Should().ThrowAsync<OperationCanceledException>();

            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    /// <summary>
    /// The inverse of the cancelled case: the rethrow arm's <c>when</c> filter reads
    /// <c>requestInfo.RequestCancellationToken.IsCancellationRequested</c>, not the exception's own
    /// token, so an OperationCanceledException carrying a different, already-cancelled token must
    /// still fall through to the catch-all and become a 500 when the request's own token was never
    /// cancelled.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Validator_Throws_OperationCanceledException_On_A_Request_That_Was_Not_Cancelled
        : CustomResourceValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            using var unrelatedCancelledSource = new CancellationTokenSource();
            await unrelatedCancelledSource.CancelAsync();

            var validator = new ThrowingValidator(() =>
                new OperationCanceledException(
                    "validator observed an unrelated cancellation",
                    unrelatedCancelledSource.Token
                )
            )
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
            };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(validator)
                .BuildServiceProvider();

            // The request's own cancellation token is left at its default, un-cancelled value, even
            // though the thrown exception carries a distinct, already-cancelled token of its own.
            _requestInfo = BuildRequestInfo(scopedServiceProvider);

            var pipeline = new PipelineProvider([
                new CoreExceptionLoggingMiddleware(NullLogger.Instance, null),
                Middleware(),
            ]);

            await pipeline.Run(_requestInfo);
        }

        [Test]
        public void It_still_answers_500_rather_than_propagating()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(500);
        }
    }

    /// <summary>
    /// The null-return guard's throw (see
    /// <see cref="Given_A_Validator_Returns_Null_From_ValidateAsync"/> for the isolated proof of the
    /// throw itself) is an ordinary exception once it leaves this step, so it surfaces through the
    /// same catch-all as any other unhandled exception.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Validator_Returns_Null_From_ValidateAsync_Behind_The_Real_Catch_Chain
        : CustomResourceValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            var validator = new FakeValidator
            {
                AppliesTo = [new ValidatedResource("Ed-Fi", "School")],
                ReturnValue = null!,
            };

            var scopedServiceProvider = new ServiceCollection()
                .AddSingleton<ICustomResourceValidator>(validator)
                .BuildServiceProvider();

            _requestInfo = BuildRequestInfo(scopedServiceProvider);

            var pipeline = new PipelineProvider([
                new CoreExceptionLoggingMiddleware(NullLogger.Instance, null),
                Middleware(),
            ]);

            await pipeline.Run(_requestInfo);
        }

        [Test]
        public void It_surfaces_as_a_500_through_the_same_catch_chain()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(500);
        }
    }
}
