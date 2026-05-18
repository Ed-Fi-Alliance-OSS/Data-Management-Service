// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Startup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Serilog;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

[TestFixture]
public class AuthStartupTaskRegistrationTests
{
    [TestFixture]
    public class Given_DmsCoreServices_Are_Registered : AuthStartupTaskRegistrationTests
    {
        private IServiceCollection _services = null!;

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
        }

        [Test]
        public void It_registers_the_oidc_warm_up_task_as_an_IDmsStartupTask()
        {
            _services
                .Should()
                .Contain(descriptor =>
                    descriptor.ServiceType == typeof(IDmsStartupTask)
                    && descriptor.ImplementationType != null
                    && descriptor.ImplementationType.Name == "WarmUpOidcMetadataTask"
                );
        }

        [Test]
        public void It_registers_the_cache_claim_sets_task_as_an_IDmsStartupTask()
        {
            _services
                .Should()
                .Contain(descriptor =>
                    descriptor.ServiceType == typeof(IDmsStartupTask)
                    && descriptor.ImplementationType != null
                    && descriptor.ImplementationType.Name == "CacheClaimSetsTask"
                );
        }
    }
}
