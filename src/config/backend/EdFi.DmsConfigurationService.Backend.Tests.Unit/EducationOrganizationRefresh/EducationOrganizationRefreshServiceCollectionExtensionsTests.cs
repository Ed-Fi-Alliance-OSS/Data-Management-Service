// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationRefresh;

public class EducationOrganizationRefreshServiceCollectionExtensionsTests
{
    private const string Reader = DmsEducationOrganizationProjectionSettings.SectionName;

    /// <summary>
    /// Registers the refresh over <paramref name="values"/>. Job workers default to off here, because with workers on
    /// a configured reader is a startup requirement; the fixtures about that requirement set the switch themselves.
    /// </summary>
    private static ServiceProvider Provider(Dictionary<string, string?> values, int registrations = 1)
    {
        ServiceCollection services = new();
        Dictionary<string, string?> settings = new(values);
        settings.TryAdd("JobSettings:WorkerEnabled", "false");
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
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
        private int _readerRequirements;

        [SetUp]
        public void Setup()
        {
            try
            {
                using ServiceProvider provider = Provider(
                    new() { ["JobSettings:WorkerEnabled"] = "true" },
                    registrations: 2
                );
                provider.GetRequiredService<IJobErrorCodeRegistry>();
                provider.GetRequiredService<IJobHandlerRegistry>();
                _validators = provider
                    .GetServices<IValidateOptions<EducationOrganizationRefreshOptions>>()
                    .Count();
                _readerRequirements = provider
                    .GetServices<IValidateOptions<DmsEducationOrganizationProjectionSettings>>()
                    .OfType<RefreshReaderRequiredValidator>()
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

        [Test]
        public void It_registers_one_reader_requirement() => _readerRequirements.Should().Be(1);
    }

    [TestFixture("true", 1)]
    [TestFixture("false", 0)]
    public class Given_the_worker_switch(string worker, int requirements)
    {
        [Test]
        public void It_requires_a_configured_reader_only_when_workers_are_on()
        {
            using ServiceProvider provider = Provider(new() { ["JobSettings:WorkerEnabled"] = worker });
            provider
                .GetServices<IValidateOptions<DmsEducationOrganizationProjectionSettings>>()
                .OfType<RefreshReaderRequiredValidator>()
                .Should()
                .HaveCount(requirements);
        }
    }

    [TestFixture]
    public class Given_no_worker_setting
    {
        [Test]
        public void It_requires_a_configured_reader_because_workers_default_to_on()
        {
            ServiceCollection services = new();
            services.AddEducationOrganizationRefresh(new ConfigurationBuilder().Build());
            using ServiceProvider provider = services.BuildServiceProvider();
            StartupValidationException(provider)
                .Should()
                .BeOfType<OptionsValidationException>()
                .Which.Failures.Should()
                .Equal(
                    RefreshReaderRequiredValidator.MissingBaseUrl,
                    RefreshReaderRequiredValidator.MissingCredential
                );
        }
    }

    [TestFixture]
    public class Given_workers_on
    {
        private static Exception? Start(Dictionary<string, string?> reader)
        {
            Dictionary<string, string?> values = new(reader) { ["JobSettings:WorkerEnabled"] = "true" };
            using ServiceProvider provider = Provider(values);
            return StartupValidationException(provider);
        }

        [Test]
        public void It_starts_with_a_base_url_and_a_shared_credential() =>
            Start(
                    new()
                    {
                        [$"{Reader}:DmsBaseUrl"] = "https://dms.example.org/api",
                        [$"{Reader}:Credentials:ClientId"] = "projection-client",
                        [$"{Reader}:Credentials:ClientSecret"] = "projection-secret",
                    }
                )
                .Should()
                .BeNull();

        [Test]
        public void It_starts_with_a_base_url_and_only_a_tenant_credential() =>
            Start(
                    new()
                    {
                        [$"{Reader}:DmsBaseUrl"] = "https://dms.example.org/api",
                        [$"{Reader}:TenantCredentials:Tenant1:ClientId"] = "projection-client",
                        [$"{Reader}:TenantCredentials:Tenant1:ClientSecret"] = "projection-secret",
                    }
                )
                .Should()
                .BeNull();

        [Test]
        public void It_refuses_empty_reader_values_naming_both_settings() =>
            Start(
                    new()
                    {
                        [$"{Reader}:DmsBaseUrl"] = "",
                        [$"{Reader}:Credentials:ClientId"] = "",
                        [$"{Reader}:Credentials:ClientSecret"] = "",
                    }
                )
                .Should()
                .BeOfType<OptionsValidationException>()
                .Which.Failures.Should()
                .Equal(
                    RefreshReaderRequiredValidator.MissingBaseUrl,
                    RefreshReaderRequiredValidator.MissingCredential
                );

        [Test]
        public void It_refuses_a_credential_without_a_base_url_naming_only_the_base_url() =>
            Start(
                    new()
                    {
                        [$"{Reader}:Credentials:ClientId"] = "projection-client",
                        [$"{Reader}:Credentials:ClientSecret"] = "projection-secret",
                    }
                )
                .Should()
                .BeOfType<OptionsValidationException>()
                .Which.Failures.Should()
                .Equal(RefreshReaderRequiredValidator.MissingBaseUrl);

        [Test]
        public void It_refuses_a_base_url_without_a_credential_reporting_it_once() =>
            Start(new() { [$"{Reader}:DmsBaseUrl"] = "https://dms.example.org/api" })
                .Should()
                .BeOfType<OptionsValidationException>()
                .Which.Failures.Should()
                .Equal(
                    $"{Reader}:Credentials or at least one {Reader}:TenantCredentials entry must be set when "
                        + $"{Reader}:DmsBaseUrl is set."
                );
    }

    [TestFixture]
    public class Given_workers_off
    {
        [Test]
        public void It_starts_without_a_reader()
        {
            using ServiceProvider provider = Provider(new() { ["JobSettings:WorkerEnabled"] = "false" });
            StartupValidationException(provider).Should().BeNull();
        }

        [Test]
        public void It_still_validates_the_reader_settings_that_are_given()
        {
            using ServiceProvider provider = Provider(
                new() { ["JobSettings:WorkerEnabled"] = "false", [$"{Reader}:PageSize"] = "0" }
            );
            StartupValidationException(provider)
                .Should()
                .BeOfType<OptionsValidationException>()
                .Which.Failures.Should()
                .Equal($"{Reader}:PageSize must be between 1 and 10000; it is 0.");
        }
    }
}
