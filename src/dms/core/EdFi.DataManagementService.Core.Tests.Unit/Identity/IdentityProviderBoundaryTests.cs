// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Identity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Json;

namespace EdFi.DataManagementService.Core.Tests.Unit.Identity;

/// <summary>
/// Pins <see cref="IdentityProviderBoundary" /> (design.md "Provider Execution and Exception
/// Boundary", D9, story B8): activation, the <c>Capabilities</c> getter, and an operation invocation
/// are each guarded narrowly; a non-cancellation failure at activation or the getter produces a
/// sanitized <c>500</c> provider-configuration response with no operation invoked, and a failure from
/// invocation produces a sanitized <c>502</c> upstream-failure response; a live-request cancellation at
/// any of the three call sites propagates a fresh <see cref="OperationCanceledException" /> carrying no
/// provider text; and no sanitized-away exception detail - including a sentinel planted deep in a
/// nested exception - ever reaches the client response, <see cref="RequestInfo.CaughtException" />, or
/// a captured log event above <c>Debug</c>.
/// </summary>
public class IdentityProviderBoundaryTests
{
    private const string Sentinel = "SENTINEL-Jane-1999-01-01";

    private sealed class CapturingSerilogSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent) => _events.Add(logEvent);
    }

    private static (IdentityProviderBoundary Boundary, CapturingSerilogSink Sink) CreateBoundary()
    {
        var sink = new CapturingSerilogSink();
        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var loggerFactory = new SerilogLoggerFactory(serilogLogger);
        var logger = loggerFactory.CreateLogger<IdentityProviderBoundary>();
        return (new IdentityProviderBoundary(logger), sink);
    }

    private static RequestInfo CreateRequestInfo(
        IServiceProvider? serviceProvider = null,
        CancellationToken cancellationToken = default
    )
    {
        var requestInfo = No.RequestInfo("boundary-trace", serviceProvider);
        requestInfo.IdentityOperation = IdentityOperation.Create;
        requestInfo.RequestCancellationToken = cancellationToken;
        return requestInfo;
    }

    /// <summary>
    /// Renders one event exactly as the collector would (JsonFormatter with message rendering),
    /// covering both structured properties and the rendered message text in a single string.
    /// </summary>
    private static string RenderEvent(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        new JsonFormatter(renderMessage: true).Format(logEvent, writer);
        return writer.ToString();
    }

    private static void AssertProviderConfiguration500(RequestInfo requestInfo)
    {
        requestInfo.FrontendResponse.StatusCode.Should().Be(500);
        requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        requestInfo.FrontendResponse.Body!["type"]!
            .ToString()
            .Should()
            .Be(IdentityFailureResponse.ProviderConfigurationType);
        requestInfo.CaughtException.Should().BeNull();
    }

    private static void AssertNoEventAboveDebugContains(CapturingSerilogSink sink, string text)
    {
        foreach (LogEvent logEvent in sink.Events.Where(e => e.Level > LogEventLevel.Debug))
        {
            RenderEvent(logEvent).Should().NotContain(text);
        }
    }

    private static void AssertSomeDebugEventContains(CapturingSerilogSink sink, string text)
    {
        sink.Events.Where(e => e.Level == LogEventLevel.Debug)
            .Select(RenderEvent)
            .Should()
            .Contain(rendered => rendered.Contains(text));
    }

    private sealed class ThrowingCapabilitiesIdentityService(Exception exceptionToThrow) : IIdentityService
    {
        public IdentityCapabilities Capabilities => throw exceptionToThrow;

        public Task<IdentityResult> CreateAsync(
            JsonObject request,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityResult> GetByIdAsync(
            string uniqueId,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityAsyncResult> FindAsync(
            IReadOnlyList<string> uniqueIds,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityAsyncResult> SearchAsync(
            IReadOnlyList<JsonObject> requests,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityResult> ResultsAsync(
            string requestToken,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    private sealed class ThrowingConstructorIdentityService : IIdentityService
    {
        // Invoked only through the DI factory lambda below (`new ThrowingConstructorIdentityService()`),
        // never resolved by type - constructing it directly through DI's reflection-based activator
        // would otherwise leave no source-visible call site.
        public ThrowingConstructorIdentityService() =>
            throw new InvalidOperationException("constructor boom");

        public IdentityCapabilities Capabilities => IdentityCapabilities.None;

        public Task<IdentityResult> CreateAsync(
            JsonObject request,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityResult> GetByIdAsync(
            string uniqueId,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityAsyncResult> FindAsync(
            IReadOnlyList<string> uniqueIds,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityAsyncResult> SearchAsync(
            IReadOnlyList<JsonObject> requests,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityResult> ResultsAsync(
            string requestToken,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    // A dependency a constructor requires, whose own registration throws when resolved - so a
    // provider whose own constructor is innocent still fails activation through a throwing scoped
    // dependency. Constructed only through the DI factory lambda below.
    private sealed class ScopedDependency
    {
        public ScopedDependency() => throw new InvalidOperationException("scoped dependency boom");
    }

    private sealed class DependentIdentityService(ScopedDependency dependency) : IIdentityService
    {
        // Held so the primary-constructor parameter is read; this stub never uses it otherwise, since
        // reaching this constructor at all means the dependency's own resolution already threw.
        public ScopedDependency Dependency { get; } = dependency;

        public IdentityCapabilities Capabilities => IdentityCapabilities.None;

        public Task<IdentityResult> CreateAsync(
            JsonObject request,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityResult> GetByIdAsync(
            string uniqueId,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityAsyncResult> FindAsync(
            IReadOnlyList<string> uniqueIds,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityAsyncResult> SearchAsync(
            IReadOnlyList<JsonObject> requests,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityResult> ResultsAsync(
            string requestToken,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    // ---------------------------------------------------------------- Activation: provider-configuration 500

    [TestFixture]
    public class Given_A_Registration_Factory_That_Throws : IdentityProviderBoundaryTests
    {
        private RequestInfo _requestInfo = null!;
        private IIdentityService? _provider;

        [SetUp]
        public void Setup()
        {
            (IdentityProviderBoundary boundary, _) = CreateBoundary();
            IServiceProvider services = new ServiceCollection()
                .AddScoped<IIdentityService>(_ => throw new InvalidOperationException("factory boom"))
                .BuildServiceProvider()
                .CreateScope()
                .ServiceProvider;
            _requestInfo = CreateRequestInfo(services);

            _provider = boundary.Activate(_requestInfo);
        }

        [Test]
        public void It_returns_a_null_provider()
        {
            _provider.Should().BeNull();
        }

        [Test]
        public void It_sets_a_500_provider_configuration_response()
        {
            AssertProviderConfiguration500(_requestInfo);
        }
    }

    [TestFixture]
    public class Given_A_Constructor_That_Throws : IdentityProviderBoundaryTests
    {
        private RequestInfo _requestInfo = null!;
        private IIdentityService? _provider;

        [SetUp]
        public void Setup()
        {
            (IdentityProviderBoundary boundary, _) = CreateBoundary();
            IServiceProvider services = new ServiceCollection()
                .AddScoped<IIdentityService>(_ => new ThrowingConstructorIdentityService())
                .BuildServiceProvider()
                .CreateScope()
                .ServiceProvider;
            _requestInfo = CreateRequestInfo(services);

            _provider = boundary.Activate(_requestInfo);
        }

        [Test]
        public void It_returns_a_null_provider()
        {
            _provider.Should().BeNull();
        }

        [Test]
        public void It_sets_a_500_provider_configuration_response()
        {
            AssertProviderConfiguration500(_requestInfo);
        }
    }

    [TestFixture]
    public class Given_A_Scoped_Dependency_That_Throws : IdentityProviderBoundaryTests
    {
        private RequestInfo _requestInfo = null!;
        private IIdentityService? _provider;

        [SetUp]
        public void Setup()
        {
            (IdentityProviderBoundary boundary, _) = CreateBoundary();
            IServiceProvider services = new ServiceCollection()
                .AddScoped(_ => new ScopedDependency())
                .AddScoped<IIdentityService>(sp => new DependentIdentityService(
                    sp.GetRequiredService<ScopedDependency>()
                ))
                .BuildServiceProvider()
                .CreateScope()
                .ServiceProvider;
            _requestInfo = CreateRequestInfo(services);

            _provider = boundary.Activate(_requestInfo);
        }

        [Test]
        public void It_returns_a_null_provider()
        {
            _provider.Should().BeNull();
        }

        [Test]
        public void It_sets_a_500_provider_configuration_response()
        {
            AssertProviderConfiguration500(_requestInfo);
        }
    }

    [TestFixture]
    public class Given_A_Capabilities_Getter_That_Throws : IdentityProviderBoundaryTests
    {
        private RequestInfo _requestInfo = null!;
        private IdentityCapabilities? _capabilities;

        [SetUp]
        public void Setup()
        {
            (IdentityProviderBoundary boundary, _) = CreateBoundary();
            _requestInfo = CreateRequestInfo();
            var provider = new ThrowingCapabilitiesIdentityService(
                new InvalidOperationException("getter boom")
            );

            _capabilities = boundary.ReadCapabilities(provider, _requestInfo);
        }

        [Test]
        public void It_returns_null_capabilities()
        {
            _capabilities.Should().BeNull();
        }

        [Test]
        public void It_sets_a_500_provider_configuration_response()
        {
            AssertProviderConfiguration500(_requestInfo);
        }
    }

    // ---------------------------------------------------------------- Invoke: upstream-failure 502

    [TestFixture]
    public class Given_An_Operation_That_Throws : IdentityProviderBoundaryTests
    {
        private RequestInfo _requestInfo = null!;
        private IdentityInvocation<IdentityResult> _invocation;

        [SetUp]
        public async Task Setup()
        {
            (IdentityProviderBoundary boundary, _) = CreateBoundary();
            _requestInfo = CreateRequestInfo();

            _invocation = await boundary.InvokeAsync<IdentityResult>(
                () => throw new InvalidOperationException("operation boom"),
                _requestInfo
            );
        }

        [Test]
        public void It_marks_the_boundary_as_failed()
        {
            _invocation.BoundaryFailed.Should().BeTrue();
        }

        [Test]
        public void It_returns_a_null_result()
        {
            _invocation.Result.Should().BeNull();
        }

        [Test]
        public void It_sets_a_502_upstream_failure_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(502);
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
            _requestInfo.FrontendResponse.Body!["type"]!
                .ToString()
                .Should()
                .Be(IdentityFailureResponse.UpstreamFailureType);
            _requestInfo.CaughtException.Should().BeNull();
        }
    }

    // ---------------------------------------------------------------- Invoke: provider returned null

    [TestFixture]
    public class Given_The_Operation_Returns_Null : IdentityProviderBoundaryTests
    {
        private RequestInfo _requestInfo = null!;
        private IdentityInvocation<IdentityResult> _invocation;

        [SetUp]
        public async Task Setup()
        {
            (IdentityProviderBoundary boundary, _) = CreateBoundary();
            _requestInfo = CreateRequestInfo();

            _invocation = await boundary.InvokeAsync<IdentityResult>(
                () => Task.FromResult<IdentityResult>(null!),
                _requestInfo
            );
        }

        [Test]
        public void It_does_not_mark_the_boundary_as_failed()
        {
            _invocation.BoundaryFailed.Should().BeFalse();
        }

        [Test]
        public void It_returns_a_null_result()
        {
            _invocation.Result.Should().BeNull();
        }

        [Test]
        public void It_leaves_the_response_untouched()
        {
            _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
        }

        [Test]
        public void It_leaves_CaughtException_null()
        {
            _requestInfo.CaughtException.Should().BeNull();
        }
    }

    // ---------------------------------------------------------------- Sentinel / sanitized logging

    [TestFixture]
    public class Given_A_Nested_Sentinel_During_Invocation : IdentityProviderBoundaryTests
    {
        private RequestInfo _requestInfo = null!;
        private CapturingSerilogSink _sink = null!;
        private IdentityInvocation<IdentityResult> _invocation;

        [SetUp]
        public async Task Setup()
        {
            (IdentityProviderBoundary boundary, _sink) = CreateBoundary();
            _requestInfo = CreateRequestInfo();
            var inner = new InvalidOperationException($"upstream said: {Sentinel}");
            var outer = new ApplicationException("wrapped failure", inner);

            _invocation = await boundary.InvokeAsync<IdentityResult>(() => throw outer, _requestInfo);
        }

        [Test]
        public void It_returns_a_null_result()
        {
            _invocation.Result.Should().BeNull();
        }

        [Test]
        public void It_leaves_CaughtException_null()
        {
            _requestInfo.CaughtException.Should().BeNull();
        }

        [Test]
        public void It_keeps_the_sentinel_out_of_the_response_body()
        {
            _requestInfo.FrontendResponse.Body!.ToJsonString().Should().NotContain(Sentinel);
        }

        [Test]
        public void It_emits_at_least_one_log_event()
        {
            _sink.Events.Should().NotBeEmpty();
        }

        [Test]
        public void It_keeps_the_sentinel_out_of_every_event_above_Debug()
        {
            AssertNoEventAboveDebugContains(_sink, Sentinel);
        }

        [Test]
        public void It_logs_the_sentinel_at_Debug()
        {
            AssertSomeDebugEventContains(_sink, Sentinel);
        }
    }

    [TestFixture]
    public class Given_An_Activation_Failure_With_A_Sentinel_In_The_Message : IdentityProviderBoundaryTests
    {
        private CapturingSerilogSink _sink = null!;

        [SetUp]
        public void Setup()
        {
            (IdentityProviderBoundary boundary, _sink) = CreateBoundary();
            IServiceProvider services = new ServiceCollection()
                .AddScoped<IIdentityService>(_ =>
                    throw new InvalidOperationException($"factory said: {Sentinel}")
                )
                .BuildServiceProvider()
                .CreateScope()
                .ServiceProvider;
            RequestInfo requestInfo = CreateRequestInfo(services);

            boundary.Activate(requestInfo);
        }

        [Test]
        public void It_keeps_the_sentinel_out_of_every_event_above_Debug()
        {
            AssertNoEventAboveDebugContains(_sink, Sentinel);
        }

        [Test]
        public void It_logs_the_sentinel_at_Debug()
        {
            AssertSomeDebugEventContains(_sink, Sentinel);
        }
    }

    // ---------------------------------------------------------------- Cancellation

    [TestFixture]
    public class Given_Activate_With_A_Cancelled_Request_Token : IdentityProviderBoundaryTests
    {
        private RequestInfo _requestInfo = null!;
        private Action _act = null!;

        [SetUp]
        public void Setup()
        {
            (IdentityProviderBoundary boundary, _) = CreateBoundary();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            _requestInfo = CreateRequestInfo(cancellationToken: cts.Token);

            _act = () => boundary.Activate(_requestInfo);
        }

        [Test]
        public void It_throws_OperationCanceledException_without_the_sentinel()
        {
            _act.Should().Throw<OperationCanceledException>().Which.Message.Should().NotContain(Sentinel);
        }

        [Test]
        public void It_leaves_the_response_untouched()
        {
            _act.Should().Throw<OperationCanceledException>();

            _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class Given_ReadCapabilities_With_A_Cancelled_Request_Token : IdentityProviderBoundaryTests
    {
        private Action _act = null!;

        [SetUp]
        public void Setup()
        {
            (IdentityProviderBoundary boundary, _) = CreateBoundary();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            RequestInfo requestInfo = CreateRequestInfo(cancellationToken: cts.Token);
            var provider = new ThrowingCapabilitiesIdentityService(
                new InvalidOperationException("unreachable")
            );

            _act = () => boundary.ReadCapabilities(provider, requestInfo);
        }

        [Test]
        public void It_throws_OperationCanceledException_without_the_sentinel()
        {
            _act.Should().Throw<OperationCanceledException>().Which.Message.Should().NotContain(Sentinel);
        }
    }

    [TestFixture]
    public class Given_InvokeAsync_With_A_Cancelled_Request_Token : IdentityProviderBoundaryTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public async Task Setup()
        {
            (IdentityProviderBoundary boundary, _) = CreateBoundary();
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            RequestInfo requestInfo = CreateRequestInfo(cancellationToken: cts.Token);

            _act = async () =>
                await boundary.InvokeAsync<IdentityResult>(
                    () => throw new InvalidOperationException("unreachable"),
                    requestInfo
                );
        }

        [Test]
        public async Task It_throws_OperationCanceledException_without_the_sentinel()
        {
            (await _act.Should().ThrowAsync<OperationCanceledException>())
                .Which.Message.Should()
                .NotContain(Sentinel);
        }
    }

    [TestFixture]
    public class Given_A_Provider_OperationCanceledException_After_Request_Cancellation
        : IdentityProviderBoundaryTests
    {
        /// <summary>
        /// Mid-flight orchestration: the request token is cancelled from inside the guarded operation
        /// itself, only after the boundary's pre-check has already passed, so this cannot be split into
        /// a plain SetUp arrange/act - the cancellation must happen at a precise point during the act.
        /// </summary>
        [Test]
        public async Task It_throws_a_fresh_sanitized_exception_and_logs_the_sentinel_only_at_Debug()
        {
            (IdentityProviderBoundary boundary, CapturingSerilogSink sink) = CreateBoundary();
            using var cts = new CancellationTokenSource();
            RequestInfo requestInfo = CreateRequestInfo(cancellationToken: cts.Token);
            var providerException = new OperationCanceledException($"provider said: {Sentinel}");

            Func<Task> act = async () =>
                await boundary.InvokeAsync<IdentityResult>(
                    () =>
                    {
                        // Cancel only after the boundary's pre-check has already passed, so the
                        // exception is thrown and caught inside the guarded try, not by the pre-check.
                        cts.Cancel();
                        throw providerException;
                    },
                    requestInfo
                );

            OperationCanceledException thrown = (
                await act.Should().ThrowAsync<OperationCanceledException>()
            ).Which;
            thrown.Should().NotBeSameAs(providerException);
            thrown.Message.Should().NotContain(Sentinel);
            thrown.InnerException.Should().BeNull();

            AssertNoEventAboveDebugContains(sink, Sentinel);
            AssertSomeDebugEventContains(sink, Sentinel);
            requestInfo.CaughtException.Should().BeNull();
            requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
        }
    }
}
