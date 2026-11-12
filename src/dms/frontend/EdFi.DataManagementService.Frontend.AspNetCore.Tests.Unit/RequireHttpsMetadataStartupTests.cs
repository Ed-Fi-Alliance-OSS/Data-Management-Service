// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// Starts the real host to prove Program.cs wires the https-metadata diagnostic, including on the path
/// where invalid configuration skips every startup phase. The Test environment is non-development, and
/// its BypassAuthorization means the https guard in the metadata factory is never resolved, so no
/// identity provider is needed.
/// </summary>
public abstract class RequireHttpsMetadataStartupTests
{
    private const string ApplicationCategory = "EdFi.DataManagementService.Frontend.AspNetCore";

    private RecordingLoggerProvider _logs = null!;

    protected WebApplicationFactory<Program> Factory { get; private set; } = null!;

    protected HttpClient Client { get; private set; } = null!;

    protected abstract IReadOnlyDictionary<string, string?> Settings { get; }

    protected int WarningCount =>
        _logs.Entries.Count(entry =>
            entry.Level == LogLevel.Warning
            && entry.Category == ApplicationCategory
            && entry.Message == RequireHttpsMetadataStartupWarning.Message
        );

    [SetUp]
    public void Setup()
    {
        // A fresh recorder per test: NUnit reuses one fixture instance, and each test starts its own host.
        RecordingLoggerProvider logs = new();
        _logs = logs;

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) => configuration.AddInMemoryCollection(Settings)
            );
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureServices(TestMockHelper.AddEssentialMocks);
        });

        // Creating the client starts the host, which runs Program.cs to app.RunAsync().
        Client = Factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        Client.Dispose();
        Factory.Dispose();
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_the_host_starts_in_Test_with_https_metadata_not_required
        : RequireHttpsMetadataStartupTests
    {
        // appsettings.Test.json supplies JwtAuthentication:RequireHttpsMetadata = false.
        protected override IReadOnlyDictionary<string, string?> Settings { get; } =
            new Dictionary<string, string?>();

        [Test]
        public void It_logs_the_warning_once() => WarningCount.Should().Be(1);
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_the_host_starts_in_Test_with_https_metadata_required : RequireHttpsMetadataStartupTests
    {
        protected override IReadOnlyDictionary<string, string?> Settings { get; } =
            new Dictionary<string, string?> { [RequireHttpsMetadataStartupWarning.SettingKey] = "true" };

        [Test]
        public void It_logs_no_warning() => WarningCount.Should().Be(0);
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_the_host_starts_in_Test_with_https_metadata_not_required_and_invalid_unrelated_configuration
        : RequireHttpsMetadataStartupTests
    {
        // ReverseProxySettingsValidator checks KnownProxies whether or not forwarded headers are enabled,
        // and the options bind after the host is built, so this late in-memory value is enough to send
        // the host into configuration-reporting mode.
        protected override IReadOnlyDictionary<string, string?> Settings { get; } =
            new Dictionary<string, string?> { ["AppSettings:ReverseProxy:KnownProxies"] = "not-an-ip" };

        [Test]
        public void It_logs_the_warning_once() => WarningCount.Should().Be(1);

        [Test]
        public async Task It_runs_in_configuration_reporting_mode()
        {
            HttpResponseMessage response = await Client.GetAsync("/");

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Category, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, Entries);

        public void Dispose() { }

        private sealed class Recorder(
            string category,
            ConcurrentQueue<(LogLevel Level, string Category, string Message)> entries
        ) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => NullLogger.Instance.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) => entries.Enqueue((logLevel, category, formatter(state, exception)));
        }
    }
}
