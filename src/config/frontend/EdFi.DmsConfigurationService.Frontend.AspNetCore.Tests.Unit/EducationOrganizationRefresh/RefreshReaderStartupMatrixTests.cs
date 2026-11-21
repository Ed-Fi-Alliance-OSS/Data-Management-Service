// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using static EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration.TokenRevocationStartupTests;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.EducationOrganizationRefresh;

/// <summary>
/// The startup half of the configuration-preservation matrix: the values the compose files render for each deployment
/// case either start the host in the declared mode or stop it naming the missing reader settings. The compose half,
/// <c>eng/docker-compose/tests/EducationOrganizationRefreshWiring.Tests.ps1</c>, proves which values each case renders.
/// </summary>
public class RefreshReaderStartupMatrixTests
{
    private const string Reader = "DmsEducationOrganizationProjectionSettings";

    /// <summary>
    /// Starts a host with the rendered <c>AppSettings:EnableDataStoreManagement</c> and <c>JobSettings:WorkerEnabled</c>
    /// values, and reader settings that are either a base URL with a credential or, as compose renders unset
    /// variables, empty strings.
    /// </summary>
    private sealed class RenderedHost : IDisposable
    {
        private readonly WebApplicationFactory<Program> _factory;

        public RenderedHost(string management, string worker, bool reader)
        {
            Dictionary<string, string> overrides = new()
            {
                ["JobSettings:WorkerEnabled"] = worker,
                [$"{Reader}:DmsBaseUrl"] = reader ? "https://dms.example.org/api" : "",
                [$"{Reader}:Credentials:ClientId"] = reader ? "projection-client" : "",
                [$"{Reader}:Credentials:ClientSecret"] = reader ? "projection-secret" : "",
            };
            _factory = DmsDataStoreProvisioningStartupTests.Given_managed_data_store_startup.Host(
                management,
                provisioning: management == "true",
                overrides: overrides,
                configure: services =>
                {
                    services.AddSingleton<ILoggerProvider>(Logs);
                    services.AddLogging(logging =>
                        logging.AddFilter<StartupLogCapture>(category: null, LogLevel.Trace)
                    );
                }
            );
            try
            {
                using var client = _factory.CreateClient();
            }
            catch (Exception exception)
            {
                StartupException = exception;
            }
        }

        public StartupLogCapture Logs { get; } = new();

        public Exception? StartupException { get; }

        /// <summary>The validation failure in the startup exception, which the test host wraps.</summary>
        public OptionsValidationException? ValidationFailure => Find(StartupException);

        public IEnumerable<(LogLevel Level, int Id, string Message)> WorkersDisabledWarnings =>
            Logs
                .Records.Where(record =>
                    record.EventId.Name == JobWorkersDisabledNotice.JobWorkersDisabled.Name
                )
                .Select(record => (record.Level, record.EventId.Id, record.Message));

        public void Dispose() => _factory.Dispose();

        private static OptionsValidationException? Find(Exception? exception) =>
            exception switch
            {
                null => null,
                OptionsValidationException match => match,
                AggregateException aggregate => aggregate
                    .InnerExceptions.Select(Find)
                    .FirstOrDefault(found => found is not null),
                _ => Find(exception.InnerException),
            };
    }

    /// <summary>Workers on (explicitly, or by an unset variable) without reader settings, with management on or off.</summary>
    [TestFixture("true")]
    [TestFixture("false")]
    public class Given_rendered_settings_with_workers_on_and_no_reader(string management)
    {
        private RenderedHost _host = null!;

        [SetUp]
        public void Act() => _host = new RenderedHost(management, worker: "true", reader: false);

        [TearDown]
        public void TearDown() => _host.Dispose();

        [Test]
        public void It_refuses_to_start_naming_the_missing_reader_settings() =>
            _host
                .ValidationFailure.Should()
                .NotBeNull()
                .And.Subject.As<OptionsValidationException>()
                .Failures.Should()
                .Equal(
                    RefreshReaderRequiredValidator.MissingBaseUrl,
                    RefreshReaderRequiredValidator.MissingCredential
                );
    }

    /// <summary>Management on, workers on, with reader settings: an operational upgrade.</summary>
    [TestFixture]
    public class Given_rendered_settings_with_workers_on_and_a_reader
    {
        private RenderedHost _host = null!;

        [SetUp]
        public void Act() => _host = new RenderedHost(management: "true", worker: "true", reader: true);

        [TearDown]
        public void TearDown() => _host.Dispose();

        [Test]
        public void It_starts() => _host.StartupException.Should().BeNull();

        [Test]
        public void It_does_not_warn_that_workers_are_disabled() =>
            _host.WorkersDisabledWarnings.Should().BeEmpty();
    }

    /// <summary>
    /// Workers explicitly off without reader settings: management on with an explicit opt-out, and a fresh copy of
    /// <c>.env.example</c> (management unset, so off).
    /// </summary>
    [TestFixture("true")]
    [TestFixture("false")]
    public class Given_rendered_settings_with_workers_off_and_no_reader(string management)
    {
        private RenderedHost _host = null!;

        [SetUp]
        public void Act() => _host = new RenderedHost(management, worker: "false", reader: false);

        [TearDown]
        public void TearDown() => _host.Dispose();

        [Test]
        public void It_starts_in_bootstrap() => _host.StartupException.Should().BeNull();

        [Test]
        public void It_warns_once_that_workers_are_disabled() =>
            _host
                .WorkersDisabledWarnings.Should()
                .Equal((LogLevel.Warning, 1441_11, JobWorkersDisabledNotice.Message));
    }
}
