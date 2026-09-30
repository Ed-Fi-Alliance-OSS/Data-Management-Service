// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins.Hosting;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Secrets;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// Where the service contribution phase sits in the host's own registrations, observed on a boot
/// with nothing allowlisted. The contribution phase is a no-op there, so what these assert is the
/// placement a plugin would see: after every registration AddServices makes, and before the audit
/// input the host registers for the post-build audit.
/// </summary>
[TestFixture]
public class Given_a_host_booted_with_no_plugins_allowlisted
{
    private WebApplicationFactory<Program> _factory = null!;
    private List<ServiceDescriptor> _auditInputDescriptors = null!;
    private PluginAuditInput _resolvedAuditInput = null!;

    [SetUp]
    public void Setup()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            // Test services are applied after Program's own registrations, so this sees the collection
            // as AddServices left it.
            builder.ConfigureTestServices(services =>
                _auditInputDescriptors = [.. services.Where(d => d.ServiceType == typeof(PluginAuditInput))]
            );
        });

        // WebApplicationFactory defers the entry point until the server is first needed, and the audit
        // runs inside it, so creating a client is what proves the audit accepted the boot.
        using HttpClient client = _factory.CreateClient();
        _resolvedAuditInput = _factory.Services.GetRequiredService<PluginAuditInput>();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public void It_registers_one_audit_input_as_a_singleton_instance()
    {
        _auditInputDescriptors
            .Should()
            .ContainSingle()
            .Which.Should()
            .Match<ServiceDescriptor>(d =>
                d.Lifetime == ServiceLifetime.Singleton && d.ImplementationInstance != null
            );
    }

    [Test]
    public void It_resolves_the_registered_instance()
    {
        _resolvedAuditInput.Should().BeSameAs(_auditInputDescriptors[0].ImplementationInstance);
    }

    [Test]
    public void It_audits_against_the_configuration_service_registry()
    {
        _resolvedAuditInput.Registry.Should().BeSameAs(CmsPluginContracts.Registry);
    }

    [Test]
    public void It_records_no_plugin_contributions()
    {
        _resolvedAuditInput.Records.Should().BeEmpty();
    }

    [Test]
    public void It_takes_the_contribution_snapshot_after_the_host_registrations_in_AddServices()
    {
        // IAuditContext is the last registration AddServices makes, and the client secret hasher's
        // default is registered by its datastore configuration, so a contribution phase moved ahead of
        // either would leave it out of the snapshot.
        _resolvedAuditInput
            .DescriptorsAfterContribution.Select(d => d.ServiceType)
            .Should()
            .Contain([typeof(IAuditContext), typeof(IClientSecretHasher)]);
    }

    [Test]
    public void It_registers_the_audit_input_after_the_contribution_snapshot()
    {
        _resolvedAuditInput
            .DescriptorsAfterContribution.Select(d => d.ServiceType)
            .Should()
            .NotContain(typeof(PluginAuditInput));
    }
}
