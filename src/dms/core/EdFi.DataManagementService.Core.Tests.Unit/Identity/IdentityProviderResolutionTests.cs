// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Identity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Identity;

/// <summary>
/// Pins provider lifetime and resolution: a provider registered scoped, whose own dependency is
/// also scoped, resolves once per request scope, and
/// <see cref="IdentityOperationCapabilityMiddleware" /> and <see cref="IdentityHandler" /> serve one
/// request with the one instance the gate resolved and the one <c>Capabilities</c> value it read -
/// so a provider can never observe a <c>Capabilities</c> value that differs from the one its call
/// was gated on. A singleton test fixture cannot detect a captured-scope defect, so this fixture
/// specifically exercises scoped registrations.
/// </summary>
public class IdentityProviderResolutionTests
{
    private const string PollPathPrefix = "/identity/v2/identities/results";

    /// <summary>
    /// A scoped dependency the identity provider stub below captures, so the stub is a scoped
    /// registration with its own scoped dependency rather than a self-contained one.
    /// </summary>
    private sealed class ScopedMarker;

    /// <summary>Records every provider instance activated in one test and what each one was asked.</summary>
    private sealed class ProviderLedger
    {
        public List<RecordingIdentityService> Activated { get; } = [];

        public int CapabilityReads { get; set; }

        public List<RecordingIdentityService> FindCalledOn { get; } = [];
    }

    /// <summary>
    /// Advertises <c>Find | Results</c> on the first <c>Capabilities</c> read across the ledger and
    /// <c>Find</c> alone on every later read, and answers find with a request token, which the handler
    /// accepts only while the gated capabilities include <c>Results</c>. A second read anywhere in
    /// the request therefore turns the handler's <c>202</c> into a provider-contract-violation <c>502</c>.
    /// </summary>
    private sealed class RecordingIdentityService : IIdentityService
    {
        private readonly ProviderLedger _ledger;

        public RecordingIdentityService(ScopedMarker marker, ProviderLedger ledger)
        {
            _ = marker;
            _ledger = ledger;
            ledger.Activated.Add(this);
        }

        public IdentityCapabilities Capabilities
        {
            get
            {
                _ledger.CapabilityReads++;
                return _ledger.CapabilityReads == 1
                    ? IdentityCapabilities.Find | IdentityCapabilities.Results
                    : IdentityCapabilities.Find;
            }
        }

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
        )
        {
            _ledger.FindCalledOn.Add(this);
            return Task.FromResult(
                new IdentityAsyncResult
                {
                    Status = IdentityResultStatus.Success,
                    RequestToken = "job-token-1",
                }
            );
        }

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

    private static ServiceProvider CreateRootProvider(ServiceLifetime providerLifetime, ProviderLedger ledger)
    {
        var services = new ServiceCollection().AddScoped<ScopedMarker>();
        services.Add(
            new ServiceDescriptor(
                typeof(IIdentityService),
                serviceProvider => new RecordingIdentityService(
                    serviceProvider.GetRequiredService<ScopedMarker>(),
                    ledger
                ),
                providerLifetime
            )
        );
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Runs the capability gate and then the handler over one find request, the order the identity
    /// pipeline runs them in. A scoped registration proves the design's scoped-with-scoped-dependency
    /// provider serves the request; a transient registration hands out a new instance on every
    /// resolution, so it is the one that fails if the handler resolves a provider of its own.
    /// </summary>
    [TestFixture(ServiceLifetime.Scoped)]
    [TestFixture(ServiceLifetime.Transient)]
    public class Given_The_Capability_Gate_And_The_Handler_Serving_One_Request(
        ServiceLifetime providerLifetime
    ) : IdentityProviderResolutionTests
    {
        private ProviderLedger _ledger = null!;
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
        {
            _ledger = new ProviderLedger();
            using ServiceProvider root = CreateRootProvider(providerLifetime, _ledger);
            using IServiceScope scope = root.CreateScope();

            var requestInfo = No.RequestInfo("resolution-trace", scope.ServiceProvider);
            requestInfo.IdentityOperation = IdentityOperation.Find;
            requestInfo.IdentityPollPathPrefix = PollPathPrefix;
            requestInfo.ParsedBody = new JsonArray("a");
            requestInfo.ClientAuthorizations = No.ClientAuthorizations with { ClientId = "client-1" };

            var boundary = new IdentityProviderBoundary(NullLogger<IdentityProviderBoundary>.Instance);
            var middleware = new IdentityOperationCapabilityMiddleware(
                boundary,
                NullLogger<IdentityOperationCapabilityMiddleware>.Instance
            );
            var handler = new IdentityHandler(
                boundary,
                IdentityHandler.DefaultMaxRequestLineSize,
                NullLogger<IdentityHandler>.Instance
            );

            await middleware.Execute(requestInfo, () => handler.Execute(requestInfo, TestHelper.NullNext));

            _response = requestInfo.FrontendResponse;
        }

        [Test]
        public void It_answers_202_with_the_poll_Location()
        {
            _response.StatusCode.Should().Be(202);
            _response.LocationHeaderPath.Should().Be($"{PollPathPrefix}/job-token-1");
        }

        [Test]
        public void It_reads_the_capabilities_exactly_once()
        {
            _ledger.CapabilityReads.Should().Be(1);
        }

        [Test]
        public void It_activates_exactly_one_provider()
        {
            _ledger.Activated.Should().ContainSingle();
        }

        [Test]
        public void It_invokes_the_provider_the_gate_resolved()
        {
            _ledger.FindCalledOn.Should().ContainSingle().Which.Should().BeSameAs(_ledger.Activated[0]);
        }
    }

    [TestFixture]
    public class Given_Activation_Through_The_Boundary_Across_Two_Scopes : IdentityProviderResolutionTests
    {
        private IIdentityService? _first;
        private IIdentityService? _second;

        [SetUp]
        public void Setup()
        {
            using ServiceProvider root = CreateRootProvider(ServiceLifetime.Scoped, new ProviderLedger());
            using IServiceScope firstScope = root.CreateScope();
            using IServiceScope secondScope = root.CreateScope();

            var boundary = new IdentityProviderBoundary(NullLogger<IdentityProviderBoundary>.Instance);

            var firstRequestInfo = No.RequestInfo("first", firstScope.ServiceProvider);
            firstRequestInfo.IdentityOperation = IdentityOperation.Create;
            var secondRequestInfo = No.RequestInfo("second", secondScope.ServiceProvider);
            secondRequestInfo.IdentityOperation = IdentityOperation.Create;

            _first = boundary.Activate(firstRequestInfo);
            _second = boundary.Activate(secondRequestInfo);
        }

        [Test]
        public void It_resolves_a_non_null_provider_for_the_first_scope()
        {
            _first.Should().NotBeNull();
        }

        [Test]
        public void It_resolves_a_non_null_provider_for_the_second_scope()
        {
            _second.Should().NotBeNull();
        }

        [Test]
        public void It_resolves_different_instances_across_scopes()
        {
            ReferenceEquals(_first, _second).Should().BeFalse();
        }
    }
}
