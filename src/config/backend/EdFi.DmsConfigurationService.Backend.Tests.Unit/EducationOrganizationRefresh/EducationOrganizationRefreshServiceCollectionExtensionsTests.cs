// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationRefresh;

public class EducationOrganizationRefreshServiceCollectionExtensionsTests
{
    private static ServiceProvider Provider(Dictionary<string, string?> values, int registrations = 1)
    {
        ServiceCollection services = new();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        for (int i = 0; i < registrations; i++)
        {
            services.AddEducationOrganizationRefresh(configuration);
        }
        return services.BuildServiceProvider();
    }

    /// <summary>What the host runs at start for every options type registered with ValidateOnStart.</summary>
    private static Exception? StartupValidationException(ServiceProvider provider)
    {
        try
        {
            provider.GetRequiredService<IStartupValidator>().Validate();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    [TestFixture]
    public class Given_no_refresh_interval_setting
    {
        private ServiceProvider _provider = null!;

        [SetUp]
        public void Setup() => _provider = Provider([]);

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_passes_startup_validation() => StartupValidationException(_provider).Should().BeNull();

        [Test]
        public void It_uses_the_default_interval() =>
            _provider
                .GetRequiredService<IOptions<EducationOrganizationRefreshOptions>>()
                .Value.EdOrgsRefreshIntervalInMins.Should()
                .Be(60);

        [Test]
        public void It_registers_every_single_refresh_error_code_with_its_message()
        {
            IJobErrorCodeRegistry registry = _provider.GetRequiredService<IJobErrorCodeRegistry>();
            foreach ((string code, string message) in EducationOrganizationRefreshJobErrorCodes.All)
            {
                registry.TryGet(code, out JobErrorCode? errorCode).Should().BeTrue(code);
                errorCode.Should().Be(new JobErrorCode(code, message));
            }
        }

        [Test]
        public void It_registers_the_incomplete_code_as_a_composite_of_the_allowed_categories()
        {
            IJobErrorCodeRegistry registry = _provider.GetRequiredService<IJobErrorCodeRegistry>();
            registry.TryGet(EducationOrganizationRefreshJobErrorCodes.Incomplete, out _).Should().BeFalse();
            registry
                .TryCompose(
                    EducationOrganizationRefreshJobErrorCodes.Incomplete,
                    EducationOrganizationRefreshJobErrorCodes.IncompleteCategories,
                    out _
                )
                .Should()
                .BeTrue();
            registry
                .TryCompose(
                    EducationOrganizationRefreshJobErrorCodes.Incomplete,
                    [EducationOrganizationRefreshJobErrorCodes.DataStoreNotFound],
                    out _
                )
                .Should()
                .BeFalse();
        }

        [Test]
        public void It_registers_the_refresh_job_handler()
        {
            _provider
                .GetRequiredService<IJobHandlerRegistry>()
                .TryGet(
                    EducationOrganizationRefreshJobTypes.Refresh,
                    out JobHandlerRegistration? registration
                )
                .Should()
                .BeTrue();
            registration!.HandlerType.Should().Be<EducationOrganizationRefreshJobHandler>();
            registration.PayloadType.Should().Be<EducationOrganizationRefreshPayload>();
            registration.PayloadVersions.Should().Equal(EducationOrganizationRefreshJobTypes.PayloadVersion);
        }
    }

    [TestFixture]
    public class Given_a_refresh_interval_beside_the_other_app_settings
    {
        private ServiceProvider _provider = null!;

        [SetUp]
        public void Setup() =>
            _provider = Provider(
                new()
                {
                    ["AppSettings:EdOrgsRefreshIntervalInMins"] = "15",
                    ["AppSettings:MultiTenancy"] = "true",
                    ["AppSettings:Datastore"] = "postgresql",
                    ["AppSettings:ReverseProxy:UseForwardedHeaders"] = "false",
                }
            );

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_passes_startup_validation() => StartupValidationException(_provider).Should().BeNull();

        [Test]
        public void It_binds_the_configured_interval() =>
            _provider
                .GetRequiredService<IOptions<EducationOrganizationRefreshOptions>>()
                .Value.EdOrgsRefreshIntervalInMins.Should()
                .Be(15);
    }

    [TestFixture]
    public class Given_an_out_of_range_refresh_interval
    {
        private ServiceProvider _provider = null!;

        [SetUp]
        public void Setup() =>
            _provider = Provider(new() { ["AppSettings:EdOrgsRefreshIntervalInMins"] = "0" });

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_fails_startup_validation_naming_the_setting() =>
            StartupValidationException(_provider)
                .Should()
                .BeOfType<OptionsValidationException>()
                .Which.Message.Should()
                .Contain(
                    "AppSettings:EdOrgsRefreshIntervalInMins must be between 1 and 527040 minutes; it is 0."
                );
    }

    [TestFixture]
    public class Given_a_refresh_interval_that_is_not_a_number
    {
        private ServiceProvider _provider = null!;

        [SetUp]
        public void Setup() =>
            _provider = Provider(new() { ["AppSettings:EdOrgsRefreshIntervalInMins"] = "hourly" });

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_fails_startup_naming_the_setting() =>
            StartupValidationException(_provider)
                .Should()
                .BeOfType<InvalidOperationException>()
                .Which.Message.Should()
                .Contain("AppSettings:EdOrgsRefreshIntervalInMins");
    }

    [TestFixture]
    public class Given_the_registration_called_twice
    {
        private Exception? _exception;
        private int _validators;

        [SetUp]
        public void Setup()
        {
            try
            {
                using ServiceProvider provider = Provider([], registrations: 2);
                provider.GetRequiredService<IJobErrorCodeRegistry>();
                provider.GetRequiredService<IJobHandlerRegistry>();
                _validators = provider
                    .GetServices<IValidateOptions<EducationOrganizationRefreshOptions>>()
                    .Count();
                _exception = null;
            }
            catch (Exception exception)
            {
                _exception = exception;
            }
        }

        [Test]
        public void It_does_not_register_the_error_codes_twice() => _exception.Should().BeNull();

        [Test]
        public void It_registers_one_validator() => _validators.Should().Be(1);
    }
}
