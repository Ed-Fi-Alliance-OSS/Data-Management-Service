// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

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
    public void It_registers_the_request_validator()
    {
        IServiceCollection services = new ServiceCollection();

        services.AddCdcConnectorTemplates();

        using var _ = new AssertionScope();
        services
            .Should()
            .ContainSingle(service => service.ServiceType == typeof(ICdcConnectorTemplateInputValidator))
            .Subject.Lifetime.Should()
            .Be(ServiceLifetime.Scoped);
    }
}
