// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

[TestFixture]
public class Given_ApplicationContext_Cache_Expiration_Configuration
{
    [Test]
    public void It_uses_the_default_expiration_of_600_seconds()
    {
        using ServiceProvider serviceProvider = CreateServiceProvider();

        serviceProvider.GetRequiredService<IStartupValidator>().Validate();

        serviceProvider
            .GetRequiredService<CacheSettings>()
            .ApplicationContextCacheExpirationSeconds.Should()
            .Be(600);
    }

    [TestCase("0")]
    [TestCase("-1")]
    public void It_rejects_nonpositive_expiration_values(string configuredValue)
    {
        using ServiceProvider serviceProvider = CreateServiceProvider(configuredValue);

        Action validate = () => serviceProvider.GetRequiredService<IStartupValidator>().Validate();

        validate
            .Should()
            .Throw<OptionsValidationException>()
            .Which.Failures.Should()
            .Contain("ApplicationContextCacheExpirationSeconds must be positive.");
    }

    [Test]
    public void It_accepts_an_arbitrary_positive_expiration_above_600_seconds()
    {
        using ServiceProvider serviceProvider = CreateServiceProvider("86400");

        serviceProvider.GetRequiredService<IStartupValidator>().Validate();

        serviceProvider
            .GetRequiredService<CacheSettings>()
            .ApplicationContextCacheExpirationSeconds.Should()
            .Be(86400);
    }

    private static ServiceProvider CreateServiceProvider(string? expirationSeconds = null)
    {
        Dictionary<string, string?> settings = new()
        {
            ["ConfigurationServiceSettings:BaseUrl"] = "https://cms.example.com",
        };

        if (expirationSeconds is not null)
        {
            settings["CacheSettings:ApplicationContextCacheExpirationSeconds"] = expirationSeconds;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddDmsConfigurationServiceDataStoreProvider(configuration);

        return services.BuildServiceProvider();
    }
}
