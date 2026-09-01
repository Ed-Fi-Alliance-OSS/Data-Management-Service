// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

/// <summary>
/// What the registered CacheSettings singleton ends up holding, and what it logs on the way. Binding
/// alone cannot answer this: an absent setting and one an operator explicitly set to the same number
/// leave the bound property identical, and only one of them is worth a warning.
/// </summary>
[TestFixture]
[Parallelizable]
public class DerivativeValidationCacheExpirationBindingTests
{
    private const string SettingKey = "CacheSettings:DerivativeValidationCacheExpirationSeconds";

    /// <summary>Captures warnings so a test can count them as well as read them.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<string> Warnings { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Warnings);

        public void Dispose() { }

        private sealed class RecordingLogger(List<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            )
            {
                if (logLevel >= LogLevel.Warning)
                {
                    warnings.Add(formatter(state, exception));
                }
            }
        }
    }

    private static (CacheSettings CacheSettings, List<string> Warnings) ResolveCacheSettings(
        string? configuredValue
    )
    {
        Dictionary<string, string?> settings = new()
        {
            ["ConfigurationServiceSettings:BaseUrl"] = "http://localhost:5126",
        };

        if (configuredValue is not null)
        {
            settings[SettingKey] = configuredValue;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        RecordingLoggerProvider loggerProvider = new();

        ServiceCollection services = new();
        services.AddLogging(logging => logging.AddProvider(loggerProvider));
        services.AddDmsConfigurationServiceDataStoreProvider(configuration);

        using var serviceProvider = services.BuildServiceProvider();

        return (serviceProvider.GetRequiredService<CacheSettings>(), loggerProvider.Warnings);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Setting_Is_Absent : DerivativeValidationCacheExpirationBindingTests
    {
        private CacheSettings _cacheSettings = null!;
        private List<string> _warnings = null!;

        [SetUp]
        public void Setup()
        {
            (_cacheSettings, _warnings) = ResolveCacheSettings(configuredValue: null);
        }

        [Test]
        public void It_holds_the_default()
        {
            _cacheSettings.DerivativeValidationCacheExpirationSeconds.Should().Be(600);
        }

        /// <summary>
        /// Absence is silent. On its own this does not prove the raw value is what decides: while the
        /// property default equals the resolver default, an absent setting and one explicitly set to
        /// 600 resolve identically, so no observable behavior separates them. The guard below is what
        /// keeps that coincidence from going unnoticed.
        /// </summary>
        [Test]
        public void It_warns_about_nothing()
        {
            _warnings.Should().BeEmpty();
        }
    }

    /// <summary>
    /// The factory reads the raw configuration value alongside the bound one, so an absent setting is
    /// never resolved as though an operator had configured it. That is currently precautionary rather
    /// than load-bearing, because the property default is itself a valid in-range value that resolves
    /// to itself, which is what makes the two cases indistinguishable from outside. This pins the
    /// coincidence: the moment the two defaults diverge this fails, and the raw read starts mattering.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Two_Defaults : DerivativeValidationCacheExpirationBindingTests
    {
        [Test]
        public void It_holds_that_the_property_default_matches_the_resolver_default()
        {
            new CacheSettings()
                .DerivativeValidationCacheExpirationSeconds.Should()
                .Be(
                    DerivativeValidationCacheExpiration.Default,
                    "an absent setting and the bound default are only interchangeable while these agree"
                );
        }
    }

    /// <summary>
    /// A present-but-empty value is rejected by configuration binding itself, before resolution runs,
    /// exactly as it is for every other expiration in this section. It is pinned here so the absence
    /// of a blank-value branch in the factory reads as deliberate rather than as an oversight.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Setting_Is_Blank : DerivativeValidationCacheExpirationBindingTests
    {
        [Test]
        public void It_fails_to_bind()
        {
            Action resolve = () => ResolveCacheSettings(configuredValue: "");

            resolve
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*DerivativeValidationCacheExpirationSeconds*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Configured_Value_Inside_The_Range : DerivativeValidationCacheExpirationBindingTests
    {
        private CacheSettings _cacheSettings = null!;
        private List<string> _warnings = null!;

        [SetUp]
        public void Setup()
        {
            (_cacheSettings, _warnings) = ResolveCacheSettings(configuredValue: "120");
        }

        [Test]
        public void It_holds_the_configured_value()
        {
            _cacheSettings.DerivativeValidationCacheExpirationSeconds.Should().Be(120);
        }

        [Test]
        public void It_warns_about_nothing()
        {
            _warnings.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Configured_Value_Above_The_Maximum : DerivativeValidationCacheExpirationBindingTests
    {
        private CacheSettings _cacheSettings = null!;
        private List<string> _warnings = null!;

        [SetUp]
        public void Setup()
        {
            (_cacheSettings, _warnings) = ResolveCacheSettings(configuredValue: "86400");
        }

        [Test]
        public void It_holds_the_clamped_value()
        {
            _cacheSettings.DerivativeValidationCacheExpirationSeconds.Should().Be(3600);
        }

        /// <summary>
        /// Once, not once per resolution: the settings object is a singleton, so a warning emitted
        /// from the factory body would otherwise repeat for every consumer that resolved it.
        /// </summary>
        [Test]
        public void It_logs_its_warning_exactly_once()
        {
            _warnings.Should().ContainSingle();
        }

        [Test]
        public void It_names_the_configured_and_the_effective_value()
        {
            _warnings[0].Should().Contain("86400");
            _warnings[0].Should().Contain("3600");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Configured_Non_Positive_Value : DerivativeValidationCacheExpirationBindingTests
    {
        private CacheSettings _cacheSettings = null!;
        private List<string> _warnings = null!;

        [SetUp]
        public void Setup()
        {
            (_cacheSettings, _warnings) = ResolveCacheSettings(configuredValue: "0");
        }

        [Test]
        public void It_holds_the_default()
        {
            _cacheSettings.DerivativeValidationCacheExpirationSeconds.Should().Be(600);
        }

        [Test]
        public void It_logs_its_warning_exactly_once()
        {
            _warnings.Should().ContainSingle();
        }

        /// <summary>
        /// An operator who carried the non-expiring convention over from the neighbouring data store
        /// setting is told plainly that it does not apply here.
        /// </summary>
        [Test]
        public void It_says_that_expiration_is_not_disabled()
        {
            _warnings[0].Should().Contain("does not disable expiration");
        }
    }
}
