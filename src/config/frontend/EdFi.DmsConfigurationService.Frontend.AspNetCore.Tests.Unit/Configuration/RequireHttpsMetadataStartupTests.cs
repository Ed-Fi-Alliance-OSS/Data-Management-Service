// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

/// <summary>
/// Starts the real host to prove Program.cs wires the https-metadata diagnostic, including on the path
/// where invalid configuration installs the reporting middleware. The Test environment is non-development
/// and uses the self-contained identity provider, where the flag governs no metadata retrieval, so no
/// identity provider is needed. The key store is unreachable; its failed loads are non-fatal.
/// </summary>
public abstract class RequireHttpsMetadataStartupTests
{
    private const string ApplicationCategory = "EdFi.DmsConfigurationService.Frontend.AspNetCore";

    private const string UnreachableDatabase =
        "host=127.0.0.1;port=1;database=unreachable;username=none;timeout=1";

    private RecordingLoggerProvider _logs = null!;

    protected WebApplicationFactory<Program> Factory { get; private set; } = null!;

    protected HttpClient Client { get; private set; } = null!;

    /// <summary>
    /// Supplied through UseSetting, so registration-time readers and the options binders see the same
    /// values a deployment would.
    /// </summary>
    protected abstract IReadOnlyDictionary<string, string> Settings { get; }

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
            builder.UseSetting("DatabaseSettings:DatabaseConnection", UnreachableDatabase);
            foreach ((string key, string value) in Settings)
            {
                builder.UseSetting(key, value);
            }
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
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
        // appsettings.Test.json supplies IdentitySettings:RequireHttpsMetadata = false.
        protected override IReadOnlyDictionary<string, string> Settings { get; } =
            new Dictionary<string, string>();

        [Test]
        public void It_logs_the_warning_once() => WarningCount.Should().Be(1);
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_the_host_starts_in_Test_with_https_metadata_required : RequireHttpsMetadataStartupTests
    {
        protected override IReadOnlyDictionary<string, string> Settings { get; } =
            new Dictionary<string, string> { [RequireHttpsMetadataStartupWarning.SettingKey] = "true" };

        [Test]
        public void It_logs_no_warning() => WarningCount.Should().Be(0);
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_the_host_starts_in_Test_with_https_metadata_not_required_and_invalid_unrelated_configuration
        : RequireHttpsMetadataStartupTests
    {
        protected override IReadOnlyDictionary<string, string> Settings { get; } =
            new Dictionary<string, string>
            {
                ["AppSettings:ReverseProxy:UseForwardedHeaders"] = "true",
                ["AppSettings:ReverseProxy:KnownProxies"] = "not-an-ip",
            };

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
