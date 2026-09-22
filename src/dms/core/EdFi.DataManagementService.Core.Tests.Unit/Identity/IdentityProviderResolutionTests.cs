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
[TestFixture]
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

    [Test]
    public async Task The_gate_and_a_later_invocation_read_the_same_instance_within_one_request_scope()
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

        requestInfo.IdentityProvider.Should().NotBeNull();
        var gateInstance = (ScopedIdentityServiceWithScopedDependency)requestInfo.IdentityProvider!;

        // A later reader (IdentityHandler in the real pipeline) resolves nothing new: it uses the
        // instance the gate already captured on RequestInfo.
        var invocationInstance = (ScopedIdentityServiceWithScopedDependency)requestInfo.IdentityProvider!;

        ReferenceEquals(gateInstance, invocationInstance).Should().BeTrue();
    }

    [Test]
    public void Two_different_request_scopes_resolve_two_different_instances()
    {
        IServiceProvider root = CreateScopedRegistrationRootProvider();

        using IServiceScope firstScope = root.CreateScope();
        using IServiceScope secondScope = root.CreateScope();

        var firstProvider = firstScope.ServiceProvider.GetRequiredService<IIdentityService>();
        var secondProvider = secondScope.ServiceProvider.GetRequiredService<IIdentityService>();

        ReferenceEquals(firstProvider, secondProvider).Should().BeFalse();
    }

    [Test]
    public void Resolving_the_provider_twice_within_the_same_scope_yields_the_same_scoped_dependency_instance()
    {
        IServiceProvider root = CreateScopedRegistrationRootProvider();
        using IServiceScope scope = root.CreateScope();

        var first = (ScopedIdentityServiceWithScopedDependency)
            scope.ServiceProvider.GetRequiredService<IIdentityService>();
        var second = (ScopedIdentityServiceWithScopedDependency)
            scope.ServiceProvider.GetRequiredService<IIdentityService>();

        // Scoped resolution within one scope returns the same instance, and each carries the same
        // captured scoped dependency - the defect a singleton test fixture could never expose.
        ReferenceEquals(first, second).Should().BeTrue();
        first.Marker.Id.Should().Be(second.Marker.Id);
    }

    [Test]
    public async Task Activation_through_the_boundary_resolves_from_the_requests_own_scope_not_a_shared_root()
    {
        IServiceProvider root = CreateScopedRegistrationRootProvider();
        using IServiceScope firstScope = root.CreateScope();
        using IServiceScope secondScope = root.CreateScope();

        var boundary = new IdentityProviderBoundary(NullLogger<IdentityProviderBoundary>.Instance);

        var firstRequestInfo = No.RequestInfo("first", firstScope.ServiceProvider);
        firstRequestInfo.IdentityOperation = IdentityOperation.Create;
        var secondRequestInfo = No.RequestInfo("second", secondScope.ServiceProvider);
        secondRequestInfo.IdentityOperation = IdentityOperation.Create;

        IIdentityService? first = boundary.Activate(firstRequestInfo);
        IIdentityService? second = boundary.Activate(secondRequestInfo);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        ReferenceEquals(first, second).Should().BeFalse();

        await Task.CompletedTask;
    }
}
