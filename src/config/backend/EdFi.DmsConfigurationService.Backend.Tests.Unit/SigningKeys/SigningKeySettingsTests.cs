// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;

public class SigningKeySettingsTests
{
    [TestFixture]
    public class Given_the_default_identity_options
    {
        private SigningKeySettings _settings = null!;

        [SetUp]
        public void Act() => _settings = SigningKeySettings.FromIdentityOptions(new IdentityOptions());

        [Test]
        public void It_refreshes_every_five_minutes() =>
            _settings.RefreshInterval.Should().Be(TimeSpan.FromMinutes(5));

        [Test]
        public void It_trusts_a_snapshot_for_at_most_one_hour() =>
            _settings.MaxStaleness.Should().Be(TimeSpan.FromHours(1));

        [Test]
        public void It_throttles_unknown_key_refreshes_to_one_per_thirty_seconds() =>
            _settings.UnknownKeyRefreshCooldown.Should().Be(TimeSpan.FromSeconds(30));

        [Test]
        public void It_bounds_a_load_to_ten_seconds() =>
            _settings.LoadTimeout.Should().Be(TimeSpan.FromSeconds(10));
    }

    [TestFixture]
    public class Given_configured_values
    {
        private SigningKeySettings _settings = null!;

        [SetUp]
        public void Act() =>
            _settings = SigningKeySettings.FromIdentityOptions(
                new IdentityOptions
                {
                    SigningKeyRefreshIntervalSeconds = 120,
                    SigningKeyMaxStalenessSeconds = 900,
                    SigningKeyUnknownKeyRefreshCooldownSeconds = 15,
                    SigningKeyLoadTimeoutSeconds = 5,
                }
            );

        [Test]
        public void It_converts_the_refresh_interval() =>
            _settings.RefreshInterval.Should().Be(TimeSpan.FromSeconds(120));

        [Test]
        public void It_converts_the_max_staleness() =>
            _settings.MaxStaleness.Should().Be(TimeSpan.FromSeconds(900));

        [Test]
        public void It_converts_the_cooldown() =>
            _settings.UnknownKeyRefreshCooldown.Should().Be(TimeSpan.FromSeconds(15));

        [Test]
        public void It_converts_the_load_timeout() =>
            _settings.LoadTimeout.Should().Be(TimeSpan.FromSeconds(5));
    }

    [TestFixture]
    public class Given_invalid_identity_options
    {
        private OptionsValidationException _exception = null!;

        [SetUp]
        public void Act()
        {
            Action create = () =>
                SigningKeySettings.FromIdentityOptions(
                    new IdentityOptions
                    {
                        SigningKeyLoadTimeoutSeconds = 0,
                        SigningKeyUnknownKeyRefreshCooldownSeconds = 0,
                    }
                );
            _exception = create.Should().Throw<OptionsValidationException>().Which;
        }

        // Settings are only ever built through the validator, so a component constructed from them fails fast
        // even where no startup validation ran.
        [Test]
        public void It_lists_every_failure() =>
            _exception
                .Failures.Should()
                .Equal(
                    "IdentitySettings:SigningKeyUnknownKeyRefreshCooldownSeconds must be between 1 and 3600; it is 0.",
                    "IdentitySettings:SigningKeyLoadTimeoutSeconds must be between 1 and 60; it is 0."
                );

        [Test]
        public void It_names_the_options_type() => _exception.OptionsType.Should().Be<IdentityOptions>();
    }
}
