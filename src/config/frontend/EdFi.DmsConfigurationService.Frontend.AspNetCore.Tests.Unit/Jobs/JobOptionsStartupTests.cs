// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Net;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Jobs;

/// <summary>
/// Verifies that an invalid <c>JobSettings</c> value stops the host at startup with a message that names the setting
/// (spec D-15, §6.3), that the shipped settings start it, and that the job services are registered for the configured
/// datastore.
/// </summary>
public class JobOptionsStartupTests
{
    /// <summary>
    /// <paramref name="settings"/> reach the options binders; <paramref name="hostSettings"/> go through
    /// <c>UseSetting</c>, because the datastore and the tenancy mode are read while the services are registered, before
    /// the application configuration below is applied.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(
        Dictionary<string, string?> settings,
        Dictionary<string, string>? hostSettings = null
    ) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            foreach ((string key, string value) in hostSettings ?? [])
            {
                builder.UseSetting(key, value);
            }
            builder.ConfigureAppConfiguration(
                (_, configuration) => configuration.AddInMemoryCollection(settings)
            );
        });

    /// <summary>
    /// The test host wraps exceptions thrown from the entry point before RunAsync, so the validation failure is
    /// located by walking the chain instead of asserting on the outermost type.
    /// </summary>
    private static OptionsValidationException? FindOptionsValidationException(Exception? exception) =>
        exception switch
        {
            null => null,
            OptionsValidationException match => match,
            AggregateException aggregate => aggregate
                .InnerExceptions.Select(FindOptionsValidationException)
                .FirstOrDefault(found => found is not null),
            _ => FindOptionsValidationException(exception.InnerException),
        };

    /// <summary>Boot is triggered here: WebApplicationFactory defers the entry point until the server is needed.</summary>
    private static Exception? StartupExceptionFor(WebApplicationFactory<Program> factory)
    {
        try
        {
            using var client = factory.CreateClient();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    [TestFixture("JobSettings:PollInterval", "00:00:00.5")]
    [TestFixture("JobSettings:LeaseDuration", "02:00:00")]
    [TestFixture("JobSettings:RenewalInterval", "00:00:05")]
    [TestFixture("JobSettings:FenceTimeout", "00:05:00")]
    [TestFixture("JobSettings:MaxAttempts", "0")]
    [TestFixture("JobSettings:RetryBackoffBase", "00:00:00")]
    [TestFixture("JobSettings:RetryBackoffMaximum", "00:00:10")]
    [TestFixture("JobSettings:MaxConcurrentJobs", "64")]
    [TestFixture("JobSettings:FinishedJobRetention", "00:30:00")]
    [TestFixture("JobSettings:RetentionInterval", "2.00:00:00")]
    [TestFixture("JobSettings:RetentionBatchSize", "5000")]
    public class Given_an_invalid_job_setting_at_startup(string key, string value)
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;
        private OptionsValidationException? _validationFailure;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory(new Dictionary<string, string?> { [key] = value });
            _exception = StartupExceptionFor(_factory);
            _validationFailure = FindOptionsValidationException(_exception);
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_fails_to_start() => _exception.Should().NotBeNull();

        [Test]
        public void It_fails_with_an_options_validation_exception() =>
            _validationFailure.Should().NotBeNull();

        [Test]
        public void It_names_the_setting_and_its_accepted_range() =>
            _validationFailure!.Message.Should().Contain($"{key} must be");
    }

    [TestFixture]
    public class Given_a_lease_too_short_for_a_late_renewal_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private OptionsValidationException? _validationFailure;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory(
                new Dictionary<string, string?>
                {
                    ["JobSettings:LeaseDuration"] = "00:01:00",
                    ["JobSettings:RenewalInterval"] = "00:00:20",
                    ["JobSettings:FenceTimeout"] = "00:00:30",
                }
            );
            _validationFailure = FindOptionsValidationException(StartupExceptionFor(_factory));
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_reports_each_term_of_the_lateness_inequality() =>
            _validationFailure!
                .Message.Should()
                .Contain("JobSettings:LeaseDuration (00:01:00) must be at least")
                .And.Contain("RenewalInterval (00:00:20)")
                .And.Contain("FenceTimeout (00:00:30)")
                .And.Contain("SafetyMargin (00:00:10");
    }

    [TestFixture]
    public class Given_the_shipped_job_settings_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpStatusCode _health;
        private JobOptions _options = null!;
        private JobLeaseTimings _timings = null!;
        private JobRuntimeEnvironment _environment = null!;
        private readonly Dictionary<Type, Type> _registered = [];

        [SetUp]
        public async Task Act()
        {
            _registered.Clear();
            _factory = CreateFactory([]);
            using (var client = _factory.CreateClient())
            {
                _health = (await client.GetAsync("/health")).StatusCode;
            }

            using IServiceScope scope = _factory.Services.CreateScope();
            _options = scope.ServiceProvider.GetRequiredService<IOptions<JobOptions>>().Value;
            _timings = scope.ServiceProvider.GetRequiredService<JobLeaseTimings>();
            _environment = scope.ServiceProvider.GetRequiredService<JobRuntimeEnvironment>();
            foreach (
                Type service in new[]
                {
                    typeof(IJobRepository),
                    typeof(ICmsTransactionFactory),
                    typeof(IJobLeaseRepository),
                    typeof(IJobFenceFactory),
                    typeof(IJobRetentionRepository),
                    typeof(IJobScheduleRepository),
                }
            )
            {
                _registered[service] = scope.ServiceProvider.GetRequiredService(service).GetType();
            }
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_starts() => _health.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_binds_the_approved_defaults()
        {
            _options.LeaseDuration.Should().Be(TimeSpan.FromMinutes(5));
            _options.RenewalInterval.Should().Be(TimeSpan.FromMinutes(1));
            _options.PollInterval.Should().Be(TimeSpan.FromSeconds(5));
            _options.RetentionBatchSize.Should().Be(500);
        }

        [Test]
        public void It_disables_every_hosted_service_in_the_test_environment()
        {
            _options.WorkerEnabled.Should().BeFalse();
            _options.SchedulerEnabled.Should().BeFalse();
            _options.RetentionEnabled.Should().BeFalse();
        }

        [Test]
        public void It_publishes_the_configured_lease_timings() =>
            _timings.Should().Be(new JobLeaseTimings(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)));

        [Test]
        public void It_publishes_the_tenancy_mode() => _environment.MultiTenancy.Should().BeFalse();

        [Test]
        public void It_registers_the_postgresql_job_services() =>
            _registered
                .Values.Should()
                .OnlyContain(type =>
                    type.Namespace!.StartsWith("EdFi.DmsConfigurationService.Backend.Postgresql")
                );
    }

    [TestFixture]
    public class Given_the_mssql_datastore_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private readonly List<Type> _registered = [];

        [SetUp]
        public void Act()
        {
            _registered.Clear();
            _factory = CreateFactory(
                [],
                new Dictionary<string, string> { ["AppSettings:Datastore"] = "mssql" }
            );
            using IServiceScope scope = _factory.Services.CreateScope();
            foreach (
                Type service in new[]
                {
                    typeof(IJobRepository),
                    typeof(ICmsTransactionFactory),
                    typeof(IJobLeaseRepository),
                    typeof(IJobFenceFactory),
                    typeof(IJobRetentionRepository),
                    typeof(IJobScheduleRepository),
                }
            )
            {
                _registered.Add(scope.ServiceProvider.GetRequiredService(service).GetType());
            }
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_registers_the_sql_server_job_services() =>
            _registered
                .Should()
                .HaveCount(6)
                .And.OnlyContain(type =>
                    type.Namespace!.StartsWith("EdFi.DmsConfigurationService.Backend.Mssql")
                );
    }

    [TestFixture]
    public class Given_multi_tenancy_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private JobRuntimeEnvironment _environment = null!;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory(
                [],
                new Dictionary<string, string> { ["AppSettings:MultiTenancy"] = "true" }
            );
            _environment = _factory.Services.GetRequiredService<JobRuntimeEnvironment>();
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_publishes_the_tenancy_mode() => _environment.MultiTenancy.Should().BeTrue();
    }
}
