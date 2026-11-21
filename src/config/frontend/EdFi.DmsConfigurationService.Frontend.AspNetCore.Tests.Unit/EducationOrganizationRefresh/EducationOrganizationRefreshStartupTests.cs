// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Net;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.EducationOrganizationRefresh;

/// <summary>
/// Verifies that the host validates <c>AppSettings:EdOrgsRefreshIntervalInMins</c> at startup (DMS-1441 spec §6.10,
/// E2 rule 3): an invalid interval stops the host with a message that names the setting, and the shipped settings
/// start it with the default interval and the refresh error codes registered.
/// </summary>
public class EducationOrganizationRefreshStartupTests
{
    private const string Setting = "AppSettings:EdOrgsRefreshIntervalInMins";

    private static WebApplicationFactory<Program> CreateFactory(Dictionary<string, string?> settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) => configuration.AddInMemoryCollection(settings)
            );
        });

    /// <summary>The test host wraps exceptions thrown from the entry point, so the chain is walked.</summary>
    private static TException? Find<TException>(Exception? exception)
        where TException : Exception =>
        exception switch
        {
            null => null,
            TException match => match,
            AggregateException aggregate => aggregate
                .InnerExceptions.Select(Find<TException>)
                .FirstOrDefault(found => found is not null),
            _ => Find<TException>(exception.InnerException),
        };

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

    private static int ConfiguredInterval(WebApplicationFactory<Program> factory) =>
        factory
            .Services.GetRequiredService<IOptions<EducationOrganizationRefreshOptions>>()
            .Value.EdOrgsRefreshIntervalInMins;

    [TestFixture("0")]
    [TestFixture("-5")]
    [TestFixture("527041")]
    public class Given_an_out_of_range_refresh_interval_at_startup(string value)
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory(new() { [Setting] = value });
            _exception = StartupExceptionFor(_factory);
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_fails_to_start() => _exception.Should().NotBeNull();

        [Test]
        public void It_names_the_setting_and_the_range() =>
            Find<OptionsValidationException>(_exception)
                .Should()
                .BeOfType<OptionsValidationException>()
                .Which.Message.Should()
                .Contain($"{Setting} must be between 1 and 527040 minutes; it is {value}.");
    }

    [TestFixture]
    public class Given_a_refresh_interval_that_is_not_a_number_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory(new() { [Setting] = "hourly" });
            _exception = StartupExceptionFor(_factory);
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_fails_to_start_naming_the_setting() =>
            Find<InvalidOperationException>(_exception)
                .Should()
                .BeOfType<InvalidOperationException>()
                .Which.Message.Should()
                .Contain(Setting);
    }

    [TestFixture]
    public class Given_the_shipped_refresh_settings_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpStatusCode _health;

        [SetUp]
        public async Task Act()
        {
            _factory = CreateFactory([]);
            using var client = _factory.CreateClient();
            _health = (await client.GetAsync("/health")).StatusCode;
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_starts() => _health.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_refreshes_every_60_minutes() => ConfiguredInterval(_factory).Should().Be(60);

        [Test]
        public void It_registers_the_refresh_error_codes()
        {
            IJobErrorCodeRegistry registry = _factory.Services.GetRequiredService<IJobErrorCodeRegistry>();
            EducationOrganizationRefreshJobErrorCodes
                .All.Where(pair => !registry.TryGet(pair.Key, out _))
                .Should()
                .BeEmpty();
        }
    }

    [TestFixture("1")]
    [TestFixture("527040")]
    public class Given_a_boundary_refresh_interval_at_startup(string value)
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpStatusCode _health;

        [SetUp]
        public async Task Act()
        {
            _factory = CreateFactory(new() { [Setting] = value });
            using var client = _factory.CreateClient();
            _health = (await client.GetAsync("/health")).StatusCode;
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_starts() => _health.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_uses_the_configured_interval() =>
            ConfiguredInterval(_factory).Should().Be(int.Parse(value));
    }

    /// <summary>The documented variable for deployments; it changes the process environment, so it runs alone.</summary>
    [TestFixture]
    [NonParallelizable]
    public class Given_the_refresh_interval_in_the_environment_at_startup
    {
        private const string Variable = "AppSettings__EdOrgsRefreshIntervalInMins";
        private EnvironmentVariableScope _scope = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpStatusCode _health;

        [SetUp]
        public async Task Act()
        {
            _scope = new EnvironmentVariableScope(Variable);
            Environment.SetEnvironmentVariable(Variable, "5");
            _factory = CreateFactory([]);
            using var client = _factory.CreateClient();
            _health = (await client.GetAsync("/health")).StatusCode;
        }

        [TearDown]
        public void TearDown()
        {
            _factory.Dispose();
            _scope.Dispose();
        }

        [Test]
        public void It_starts() => _health.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_uses_the_interval_from_the_environment() =>
            ConfiguredInterval(_factory).Should().Be(5);
    }

    [TestFixture("true", 1)]
    [TestFixture("false", 0)]
    public class Given_the_scheduler_switch_at_startup(string scheduler, int reconcilers)
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;

        [SetUp]
        public void Act()
        {
            // The switch is read while the services are registered, so it goes through UseSetting. With the scheduler
            // on, the reconciler starts with the host, against a database it can never reach.
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("JobSettings:SchedulerEnabled", scheduler);
                builder.UseSetting(
                    "DatabaseSettings:DatabaseConnection",
                    "host=127.0.0.1;port=1;database=unreachable;username=none;timeout=1"
                );
            });
            _exception = StartupExceptionFor(_factory);
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_starts() => _exception.Should().BeNull();

        [Test]
        public void It_runs_the_schedule_reconciler_only_when_the_scheduler_is_on() =>
            _factory
                .Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                .OfType<EducationOrganizationRefreshScheduleReconciler>()
                .Should()
                .HaveCount(reconcilers);
    }
}
