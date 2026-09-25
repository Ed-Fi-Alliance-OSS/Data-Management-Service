// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
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
/// Pins provider lifetime and resolution (design.md "Provider Lifetime and Resolution", story B2): a
/// provider registered scoped, whose own dependency is also scoped, resolves once per request scope,
/// and the same instance serves both the capability gate
/// (<see cref="IdentityOperationCapabilityMiddleware" />) and whatever later reads
/// <see cref="RequestInfo.IdentityProvider" /> for invocation - so a provider can never observe a
/// <c>Capabilities</c> value that differs from the one its call was gated on. A singleton test fixture
/// cannot detect a captured-scope defect, so this fixture specifically exercises scoped registrations.
/// </summary>
public class IdentityProviderResolutionTests
{
    /// <summary>
    /// A scoped dependency the identity provider stub below captures, so instance identity can be
    /// traced back to a particular DI scope rather than to a shared singleton.
    /// </summary>
    private sealed class ScopedMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private sealed class ScopedIdentityServiceWithScopedDependency(ScopedMarker marker) : IIdentityService
    {
        public ScopedMarker Marker { get; } = marker;

        public IdentityCapabilities Capabilities => IdentityCapabilities.Create;

        public Task<IdentityResult> CreateAsync(
            JsonObject request,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => Task.FromResult(new IdentityResult { Status = IdentityResultStatus.Success, Payload = "id" });

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

    private static IServiceProvider CreateScopedRegistrationRootProvider() =>
        new ServiceCollection()
            .AddScoped<ScopedMarker>()
            .AddScoped<IIdentityService, ScopedIdentityServiceWithScopedDependency>()
            .BuildServiceProvider();

    [TestFixture]
    public class Given_A_Provider_Resolved_Within_One_Request_Scope : IdentityProviderResolutionTests
    {
        private ScopedIdentityServiceWithScopedDependency? _gateInstance;
        private ScopedIdentityServiceWithScopedDependency? _invocationInstance;

        [SetUp]
        public async Task Setup()
        {
            IServiceProvider root = CreateScopedRegistrationRootProvider();
            using IServiceScope scope = root.CreateScope();

            var requestInfo = No.RequestInfo("resolution-trace", scope.ServiceProvider);
            requestInfo.IdentityOperation = IdentityOperation.Create;

            var boundary = new IdentityProviderBoundary(NullLogger<IdentityProviderBoundary>.Instance);
            var middleware = new IdentityOperationCapabilityMiddleware(
                boundary,
                NullLogger<IdentityOperationCapabilityMiddleware>.Instance
            );

            await middleware.Execute(requestInfo, TestHelper.NullNext);

            _gateInstance = (ScopedIdentityServiceWithScopedDependency?)requestInfo.IdentityProvider;

            // A later reader (IdentityHandler in the real pipeline) resolves nothing new: it uses the
            // instance the gate already captured on RequestInfo.
            _invocationInstance = (ScopedIdentityServiceWithScopedDependency?)requestInfo.IdentityProvider;
        }

        [Test]
        public void It_resolves_a_non_null_provider()
        {
            _gateInstance.Should().NotBeNull();
        }

        [Test]
        public void It_uses_the_same_instance_for_a_later_read()
        {
            ReferenceEquals(_gateInstance, _invocationInstance).Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_Two_Different_Request_Scopes : IdentityProviderResolutionTests
    {
        private IIdentityService _firstProvider = null!;
        private IIdentityService _secondProvider = null!;

        [SetUp]
        public void Setup()
        {
            IServiceProvider root = CreateScopedRegistrationRootProvider();

            using IServiceScope firstScope = root.CreateScope();
            using IServiceScope secondScope = root.CreateScope();

            _firstProvider = firstScope.ServiceProvider.GetRequiredService<IIdentityService>();
            _secondProvider = secondScope.ServiceProvider.GetRequiredService<IIdentityService>();
        }

        [Test]
        public void It_resolves_two_different_instances()
        {
            ReferenceEquals(_firstProvider, _secondProvider).Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_The_Same_Scope_Resolved_Twice : IdentityProviderResolutionTests
    {
        private ScopedIdentityServiceWithScopedDependency _first = null!;
        private ScopedIdentityServiceWithScopedDependency _second = null!;

        [SetUp]
        public void Setup()
        {
            IServiceProvider root = CreateScopedRegistrationRootProvider();
            using IServiceScope scope = root.CreateScope();

            _first = (ScopedIdentityServiceWithScopedDependency)
                scope.ServiceProvider.GetRequiredService<IIdentityService>();
            _second = (ScopedIdentityServiceWithScopedDependency)
                scope.ServiceProvider.GetRequiredService<IIdentityService>();
        }

        [Test]
        public void It_returns_the_same_instance()
        {
            // Scoped resolution within one scope returns the same instance - the defect a singleton
            // test fixture could never expose.
            ReferenceEquals(_first, _second).Should().BeTrue();
        }

        [Test]
        public void It_carries_the_same_scoped_dependency()
        {
            _first.Marker.Id.Should().Be(_second.Marker.Id);
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
            IServiceProvider root = CreateScopedRegistrationRootProvider();
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
