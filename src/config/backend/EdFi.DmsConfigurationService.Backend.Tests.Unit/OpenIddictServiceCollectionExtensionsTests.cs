// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Extensions;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// Covers what <see cref="OpenIddictServiceCollectionExtensions.AddOpenIddictIdentityOptions"/>
/// actually reads out of configuration. Nothing else does: the token-manager tests construct
/// <see cref="IdentityOptions"/> directly and the HTTP tests replace <c>ITokenManager</c>
/// wholesale, so a key spelled wrong here would bind nothing and fail no test.
///
/// That is not hypothetical. The same method reads
/// <c>IdentitySettings:TokenExpirationMinutes</c> while both appsettings files spell the key
/// <c>OpenIddictTokenExpirationTimeMinutes</c>, so that setting never binds and its hardcoded
/// default always wins - masked only because the two values happen to agree.
/// </summary>
[TestFixture]
public class OpenIddictServiceCollectionExtensionsTests
{
    private static IdentityOptions BindIdentityOptions(Dictionary<string, string?> settings)
    {
        return BuildProvider(settings).GetRequiredService<IOptions<IdentityOptions>>().Value;
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        ServiceCollection services = new();
        services.AddOpenIddictIdentityOptions(configuration);

        return services.BuildServiceProvider();
    }

    [TestFixture]
    public class Given_no_bearer_token_per_client_limit_is_configured
    {
        private IdentityOptions _options = null!;

        [SetUp]
        public void Setup() => _options = BindIdentityOptions([]);

        [Test]
        public void It_defaults_to_five() => _options.BearerTokenPerClientLimit.Should().Be(5);
    }

    [TestFixture]
    public class Given_a_bearer_token_per_client_limit_is_configured
    {
        private IdentityOptions _options = null!;

        [SetUp]
        public void Setup() =>
            _options = BindIdentityOptions(
                new Dictionary<string, string?> { ["IdentitySettings:BearerTokenPerClientLimit"] = "25" }
            );

        // Binding the configured value is also what proves the key name: read under any other
        // name, this would silently come back as the default 5.
        [Test]
        public void It_binds_the_configured_value() => _options.BearerTokenPerClientLimit.Should().Be(25);
    }

    [TestFixture]
    public class Given_a_bearer_token_per_client_limit_that_disables_enforcement
    {
        private IdentityOptions _options = null!;

        [SetUp]
        public void Setup() =>
            _options = BindIdentityOptions(
                new Dictionary<string, string?> { ["IdentitySettings:BearerTokenPerClientLimit"] = "-1" }
            );

        // The documented disable value reaches the repository unchanged; nothing clamps it to a
        // positive number on the way through.
        [Test]
        public void It_binds_the_negative_value_unchanged() =>
            _options.BearerTokenPerClientLimit.Should().Be(-1);
    }

    [TestFixture]
    public class Given_no_signing_key_settings_are_configured
    {
        private IdentityOptions _options = null!;

        [SetUp]
        public void Setup() => _options = BindIdentityOptions([]);

        [Test]
        public void It_defaults_the_refresh_interval_to_300_seconds() =>
            _options.SigningKeyRefreshIntervalSeconds.Should().Be(300);

        [Test]
        public void It_defaults_the_max_staleness_to_3600_seconds() =>
            _options.SigningKeyMaxStalenessSeconds.Should().Be(3600);

        [Test]
        public void It_defaults_the_unknown_key_cooldown_to_30_seconds() =>
            _options.SigningKeyUnknownKeyRefreshCooldownSeconds.Should().Be(30);

        [Test]
        public void It_defaults_the_load_timeout_to_10_seconds() =>
            _options.SigningKeyLoadTimeoutSeconds.Should().Be(10);
    }

    [TestFixture]
    public class Given_signing_key_settings_are_configured
    {
        private IdentityOptions _options = null!;

        [SetUp]
        public void Setup() =>
            _options = BindIdentityOptions(
                new Dictionary<string, string?>
                {
                    ["IdentitySettings:SigningKeyRefreshIntervalSeconds"] = "120",
                    ["IdentitySettings:SigningKeyMaxStalenessSeconds"] = "900",
                    ["IdentitySettings:SigningKeyUnknownKeyRefreshCooldownSeconds"] = "15",
                    ["IdentitySettings:SigningKeyLoadTimeoutSeconds"] = "5",
                }
            );

        // Each value differs from its default, so a misspelled key would come back as the default and fail here.
        [Test]
        public void It_binds_the_refresh_interval() =>
            _options.SigningKeyRefreshIntervalSeconds.Should().Be(120);

        [Test]
        public void It_binds_the_max_staleness() => _options.SigningKeyMaxStalenessSeconds.Should().Be(900);

        [Test]
        public void It_binds_the_unknown_key_cooldown() =>
            _options.SigningKeyUnknownKeyRefreshCooldownSeconds.Should().Be(15);

        [Test]
        public void It_binds_the_load_timeout() => _options.SigningKeyLoadTimeoutSeconds.Should().Be(5);
    }

    [TestFixture]
    public class Given_an_invalid_signing_key_setting
    {
        private ServiceProvider _provider = null!;

        [SetUp]
        public void Setup() =>
            _provider = BuildProvider(
                new Dictionary<string, string?> { ["IdentitySettings:SigningKeyLoadTimeoutSeconds"] = "0" }
            );

        [TearDown]
        public void TearDown() => _provider.Dispose();

        // The host runs IStartupValidator before it starts serving, so the options are rejected at startup rather
        // than on the first authenticated request.
        [Test]
        public void It_fails_startup_validation()
        {
            Action validate = () => _provider.GetRequiredService<IStartupValidator>().Validate();

            validate
                .Should()
                .Throw<OptionsValidationException>()
                .Which.Failures.Should()
                .Equal("IdentitySettings:SigningKeyLoadTimeoutSeconds must be between 1 and 60; it is 0.");
        }
    }

    [TestFixture]
    public class Given_the_options_are_registered_twice
    {
        private ServiceProvider _provider = null!;

        [SetUp]
        public void Setup()
        {
            IConfiguration configuration = new ConfigurationBuilder().Build();
            ServiceCollection services = new();
            services.AddOpenIddictIdentityOptions(configuration);
            services.AddOpenIddictIdentityOptions(configuration);
            _provider = services.BuildServiceProvider();
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_registers_the_signing_key_validator_once() =>
            _provider
                .GetServices<IValidateOptions<IdentityOptions>>()
                .OfType<SigningKeyOptionsValidator>()
                .Should()
                .ContainSingle();
    }
}
