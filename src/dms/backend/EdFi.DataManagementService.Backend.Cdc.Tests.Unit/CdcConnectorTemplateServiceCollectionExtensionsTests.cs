// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Unit.CdcConnectorTemplateTestData;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("CdcConnectorTemplateServiceRegistration")]
public class Given_CdcConnectorTemplateServiceRegistration
{
    [Test]
    public void It_registers_the_connector_template_service()
    {
        IServiceCollection services = new ServiceCollection();

        services.AddCdcConnectorTemplates();

        ServiceDescriptor descriptor = services
            .Should()
            .ContainSingle(service => service.ServiceType == typeof(ICdcConnectorTemplateService))
            .Subject;
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Test]
    public void It_consumes_the_shared_provider_setup_result_contract()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddCdcConnectorTemplates();

        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();

        CdcProviderSetupReadiness readiness = service.GetProviderSetupReadiness(
            BuildProviderSetupResult(CdcProvider.Postgresql, CdcProviderSetupOutcome.CreatedOrMatched)
        );

        readiness
            .Should()
            .Be(
                new CdcProviderSetupReadiness(
                    CdcProvider.Postgresql,
                    CdcProviderSetupOutcome.CreatedOrMatched,
                    CanRenderTemplate: true
                )
            );
    }

    [Test]
    public void It_rejects_failed_provider_setup_results_for_template_rendering()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddCdcConnectorTemplates();

        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();

        CdcProviderSetupReadiness readiness = service.GetProviderSetupReadiness(
            BuildProviderSetupResult(CdcProvider.Postgresql, CdcProviderSetupOutcome.Failed)
        );

        readiness.CanRenderTemplate.Should().BeFalse();
    }
}
