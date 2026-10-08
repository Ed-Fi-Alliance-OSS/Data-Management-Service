// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Identity;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Serilog;

namespace EdFi.DataManagementService.Core.Tests.Unit.Identity;

/// <summary>
/// Pins the host default IIdentityService registration that AddDmsDefaultConfiguration installs when
/// no identity provider plugin supplies its own IIdentityService: exactly one descriptor, a singleton
/// NoIdentityService implementation, singleton resolution in practice, and every operation refusing to
/// run rather than silently returning a status.
/// The descriptor count on a bare service collection catches a duplicate registration, but it does NOT
/// catch a swap to TryAdd: with a single call site, TryAdd leaves the count at 1 and looks identical.
/// It_registers_the_host_default_even_when_a_provider_is_already_registered is the test that catches
/// that swap, because TryAdd is precisely the mutation that declines to add a second descriptor.
/// </summary>
[TestFixture]
public class NoIdentityServiceTests
{
    private static readonly IdentityRequestContext _context = new()
    {
        ClientId = "client-id",
        TraceId = "trace-id",
    };

    private IServiceCollection _services = null!;
    private ServiceProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

        _services = new ServiceCollection();
        _services.AddDmsDefaultConfiguration(
            new LoggerConfiguration().CreateLogger(),
            configuration.GetSection("CircuitBreaker"),
            configuration.GetSection("DeadlockRetry"),
            false
        );
        _provider = _services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
    }

    // ---------------------------------------------------------------- registration shape

    [Test]
    public void It_registers_exactly_one_IIdentityService_descriptor()
    {
        _services.Count(descriptor => descriptor.ServiceType == typeof(IIdentityService)).Should().Be(1);
    }

    [Test]
    public void It_registers_the_descriptor_as_a_singleton_NoIdentityService()
    {
        ServiceDescriptor descriptor = _services.Single(descriptor =>
            descriptor.ServiceType == typeof(IIdentityService)
        );

        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().Be(typeof(NoIdentityService));
    }

    /// <summary>
    /// The registration must be unconditional. A plugin contributes its own IIdentityService with Add,
    /// and a later story's recording wrapper has to see a real host-default descriptor alongside it to
    /// report replace cardinality. TryAddSingleton would decline to add one here, so this is the test
    /// that fails on that swap - the plain descriptor count on an empty collection does not, because
    /// with a single call site TryAdd still leaves exactly one descriptor behind.
    /// </summary>
    [Test]
    public void It_registers_the_host_default_even_when_a_provider_is_already_registered()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IIdentityService>(new PreregisteredIdentityService());

        services.AddDmsDefaultConfiguration(
            new LoggerConfiguration().CreateLogger(),
            configuration.GetSection("CircuitBreaker"),
            configuration.GetSection("DeadlockRetry"),
            false
        );

        services
            .Count(descriptor => descriptor.ServiceType == typeof(IIdentityService))
            .Should()
            .Be(
                2,
                "the host default is registered with Add, not TryAdd, so it survives alongside a plugin's own registration"
            );
        services
            .Any(descriptor => descriptor.ImplementationType == typeof(NoIdentityService))
            .Should()
            .BeTrue();
    }

    /// <summary>
    /// Stands in for a plugin-contributed provider. Only its presence in the collection matters.
    /// </summary>
    private sealed class PreregisteredIdentityService : IIdentityService
    {
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

    [Test]
    public void It_resolves_the_same_instance_on_repeated_resolution()
    {
        IIdentityService first = _provider.GetRequiredService<IIdentityService>();
        IIdentityService second = _provider.GetRequiredService<IIdentityService>();

        second.Should().BeSameAs(first);
    }

    // ---------------------------------------------------------------- runtime behavior

    [Test]
    public void It_reports_no_capabilities()
    {
        _provider.GetRequiredService<IIdentityService>().Capabilities.Should().Be(IdentityCapabilities.None);
    }

    [Test]
    public void It_throws_NotSupportedException_from_CreateAsync()
    {
        IIdentityService identityService = _provider.GetRequiredService<IIdentityService>();

        Action act = () => identityService.CreateAsync(new JsonObject(), _context, CancellationToken.None);

        act.Should().Throw<NotSupportedException>();
    }

    [Test]
    public void It_throws_NotSupportedException_from_GetByIdAsync()
    {
        IIdentityService identityService = _provider.GetRequiredService<IIdentityService>();

        Action act = () => identityService.GetByIdAsync("unique-id", _context, CancellationToken.None);

        act.Should().Throw<NotSupportedException>();
    }

    [Test]
    public void It_throws_NotSupportedException_from_FindAsync()
    {
        IIdentityService identityService = _provider.GetRequiredService<IIdentityService>();

        Action act = () => identityService.FindAsync(["unique-id"], _context, CancellationToken.None);

        act.Should().Throw<NotSupportedException>();
    }

    [Test]
    public void It_throws_NotSupportedException_from_SearchAsync()
    {
        IIdentityService identityService = _provider.GetRequiredService<IIdentityService>();

        Action act = () => identityService.SearchAsync([new JsonObject()], _context, CancellationToken.None);

        act.Should().Throw<NotSupportedException>();
    }

    [Test]
    public void It_throws_NotSupportedException_from_ResultsAsync()
    {
        IIdentityService identityService = _provider.GetRequiredService<IIdentityService>();

        Action act = () => identityService.ResultsAsync("request-token", _context, CancellationToken.None);

        act.Should().Throw<NotSupportedException>();
    }
}
