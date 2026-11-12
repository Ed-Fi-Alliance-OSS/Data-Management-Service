// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using EdFi.Api.Plugins.Hosting;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// The deprecated 8.x token cache cap, through the real application entry point. An explicit cap
/// warns once in guarded startup before the first Configuration Service load, an absent one is
/// silent, and an invalid one reaches the startup diagnostics without its value and before any
/// Configuration Service load.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ConfigurationServiceTokenCacheStartupTests
{
    private const string CapKey = "CacheSettings:TokenCacheExpirationSeconds";
    private const string MalformedValueSentinel = "cap-sentinel-4b7e2d";

    private WebApplicationFactory<Program>? _factory;
    private string _statusDirectory = null!;
    private string _statusFilePath = null!;
    private RecordingLogs _logs = null!;
    private RecordingStartupLoad _startupLoad = null!;

    /// <summary>Records every rendered log message, and the cap warnings separately.</summary>
    private sealed class RecordingLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ConcurrentQueue<string> CapWarnings { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        public void Dispose() { }

        private sealed class RecordingLogger(RecordingLogs logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            )
            {
                string message = $"{formatter(state, exception)} {exception}";
                logs.Messages.Enqueue(message);

                if (logLevel == LogLevel.Warning && message.Contains("TokenCacheExpirationSeconds"))
                {
                    logs.CapWarnings.Enqueue(message);
                }
            }
        }
    }

    /// <summary>
    /// A data store provider that records each startup load from the Configuration Service and the
    /// cap warnings already logged when the first one began.
    /// </summary>
    private sealed class RecordingStartupLoad
    {
        private int _loads;

        public int Loads => Volatile.Read(ref _loads);

        public string[]? CapWarningsBeforeFirstLoad { get; private set; }

        public IDataStoreProvider Create(RecordingLogs logs)
        {
            var dataStore = new DataStore(1, "Test", "TestInstance", "test-connection-string", []);
            IDataStoreProvider provider = A.Fake<IDataStoreProvider>();

            A.CallTo(() => provider.LoadDataStores(A<string?>.Ignored, A<CancellationToken>._))
                .ReturnsLazily(() =>
                {
                    RecordLoad(logs);
                    return Task.FromResult<IList<DataStore>>([dataStore]);
                });
            A.CallTo(() => provider.LoadTenants(A<CancellationToken>._))
                .ReturnsLazily(() =>
                {
                    RecordLoad(logs);
                    return Task.FromResult<IList<string>>(["TestTenant"]);
                });
            A.CallTo(() => provider.GetAll(A<string?>.Ignored)).Returns([dataStore]);
            A.CallTo(() => provider.GetById(A<long>.Ignored, A<string?>.Ignored)).Returns(dataStore);
            A.CallTo(() => provider.IsLoaded(A<string?>.Ignored)).Returns(true);
            A.CallTo(() => provider.TenantExists(A<string>.That.IsNotNull())).Returns(true);
            A.CallTo(() => provider.GetLoadedTenantKeys()).Returns(new List<string> { "" }.AsReadOnly());

            return provider;
        }

        private void RecordLoad(RecordingLogs logs)
        {
            if (Interlocked.Increment(ref _loads) == 1)
            {
                CapWarningsBeforeFirstLoad = [.. logs.CapWarnings];
            }
        }
    }

    private void CreateFactoryWith(string? capValue)
    {
        _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _statusFilePath = Path.Combine(_statusDirectory, "dms-startup-status.json");
        _logs = new RecordingLogs();
        _startupLoad = new RecordingStartupLoad();

        var settings = new Dictionary<string, string?>
        {
            ["AppSettings:StartupStatusFilePath"] = _statusFilePath,
        };

        if (capValue is not null)
        {
            settings[CapKey] = capValue;
        }

        IDataStoreProvider dataStoreProvider = _startupLoad.Create(_logs);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) => configuration.AddInMemoryCollection(settings)
            );
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);
                services.AddTransient(_ => dataStoreProvider);
            });
        });
    }

    private async Task<JsonObject> ReadStartupStatus()
    {
        File.Exists(_statusFilePath).Should().BeTrue();
        return JsonNode.Parse(await File.ReadAllTextAsync(_statusFilePath))!.AsObject();
    }

    [TearDown]
    public void Teardown()
    {
        _factory?.Dispose();
        _logs?.Dispose();

        if (Directory.Exists(_statusDirectory))
        {
            Directory.Delete(_statusDirectory, recursive: true);
        }
    }

    [Test]
    public void It_is_silent_when_the_cap_is_absent()
    {
        CreateFactoryWith(capValue: null);

        _factory!.CreateClient();

        _startupLoad.Loads.Should().BeGreaterThan(0);
        _logs.CapWarnings.Should().BeEmpty();
    }

    [TestCase("1500", TestName = "It_warns_once_before_the_first_load_for_the_legacy_1500_cap")]
    [TestCase("90", TestName = "It_warns_once_before_the_first_load_for_a_lower_cap")]
    public void It_warns_once_before_the_first_configuration_service_load(string capValue)
    {
        CreateFactoryWith(capValue);

        _factory!.CreateClient();

        _startupLoad.CapWarningsBeforeFirstLoad.Should().ContainSingle();
        _logs.CapWarnings.Should().ContainSingle();
    }

    [TestCase("", null, TestName = "It_rejects_a_blank_cap_before_any_load")]
    [TestCase("0", null, TestName = "It_rejects_a_zero_cap_before_any_load")]
    [TestCase("-7341", "7341", TestName = "It_rejects_a_negative_cap_without_echoing_it")]
    [TestCase(
        "98765432109876",
        "98765432109876",
        TestName = "It_rejects_an_overflowing_cap_without_echoing_it"
    )]
    [TestCase(
        MalformedValueSentinel,
        MalformedValueSentinel,
        TestName = "It_rejects_a_malformed_cap_without_echoing_it"
    )]
    public async Task It_reports_an_invalid_cap_through_the_startup_diagnostics(
        string capValue,
        string? echoedValue
    )
    {
        CreateFactoryWith(capValue);

        // The host refuses to start; which exception the deferred host surfaces is a race, so the
        // cause is asserted through the status file, as CoreAppSettingsStartupValidationTests does.
        Action startHost = () => _factory!.CreateClient();
        startHost.Should().Throw<Exception>();

        JsonObject startupStatus = await ReadStartupStatus();
        string invalidStartupStatus = startupStatus.ToJsonString();

        startupStatus["State"]!.GetValue<string>().Should().Be("Failed");
        startupStatus["ErrorType"]!.GetValue<string>().Should().Be(nameof(OptionsValidationException));
        invalidStartupStatus.Should().Contain("TokenCacheExpirationSeconds");
        _startupLoad.Loads.Should().Be(0, "an invalid cap must stop startup before any CMS load");

        if (echoedValue is not null)
        {
            invalidStartupStatus.Should().NotContain(echoedValue);
            string.Join(Environment.NewLine, _logs.Messages).Should().NotContain(echoedValue);
        }
    }
}

/// <summary>
/// Runs the eager startup validator with one invalid cap. Its failure names the setting.
/// </summary>
public abstract class InvalidTokenCacheCapAtStartup(string capValue)
{
    protected Exception? Failure { get; private set; }

    [SetUp]
    public void Setup()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });

        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AppSettings:Datastore"] = "postgresql",
                ["AppSettings:MaximumPageSize"] = "500",
                ["AppSettings:DefaultPartitionCount"] = "10",
                ["ConfigurationServiceSettings:BaseUrl"] = "https://example.org",
                ["ConfigurationServiceSettings:ClientId"] = "client-id",
                ["ConfigurationServiceSettings:ClientSecret"] = "client-secret",
                ["ConfigurationServiceSettings:Scope"] = "scope",
                ["ConfigurationServiceSettings:EncryptionKey"] =
                    "TestEncryptionKey123456789012345678901234567890",
                ["CacheSettings:TokenCacheExpirationSeconds"] = capValue,
            }
        );

        builder.AddServices(LoadedPlugins.Empty);

        using ServiceProvider serviceProvider = builder.Services.BuildServiceProvider();

        try
        {
            serviceProvider.GetRequiredService<IStartupValidator>().Validate();
        }
        catch (Exception exception)
        {
            Failure = exception;
        }
    }

    [Test]
    public void It_fails_options_validation() => Failure.Should().BeOfType<OptionsValidationException>();

    [Test]
    public void It_names_the_setting() => Failure!.Message.Should().Contain("TokenCacheExpirationSeconds");
}

/// <summary>
/// A blank or zero cap fails validation. Neither value can be told apart from other text, so neither
/// has an echo to look for.
/// </summary>
[TestFixture("")]
[TestFixture("0")]
[Parallelizable]
public class Given_An_Invalid_Token_Cache_Cap_At_Startup(string capValue)
    : InvalidTokenCacheCapAtStartup(capValue);

/// <summary>
/// A distinctive invalid cap fails validation, and its full exception chain carries none of the
/// supplied value.
/// </summary>
[TestFixture("-7341", "7341")]
[TestFixture("98765432109876", "98765432109876")]
[TestFixture("cap-sentinel-4b7e2d", "cap-sentinel-4b7e2d")]
[Parallelizable]
public class Given_A_Distinctive_Invalid_Token_Cache_Cap_At_Startup(string capValue, string echoedValue)
    : InvalidTokenCacheCapAtStartup(capValue)
{
    [Test]
    public void It_keeps_the_supplied_value_out_of_the_exception_chain() =>
        Failure!.ToString().Should().NotContain(echoedValue);
}
