// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;

public class SigningKeyOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(IdentityOptions options) =>
        new SigningKeyOptionsValidator().Validate(null, options);

    [TestFixture]
    public class Given_the_default_settings
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() => _result = Validate(new IdentityOptions());

        [Test]
        public void It_succeeds_validation() => _result.Succeeded.Should().BeTrue();
    }

    [TestFixture]
    public class Given_every_setting_at_its_smallest_accepted_value
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(
                new IdentityOptions
                {
                    SigningKeyRefreshIntervalSeconds = 30,
                    SigningKeyMaxStalenessSeconds = 60,
                    SigningKeyUnknownKeyRefreshCooldownSeconds = 1,
                    SigningKeyLoadTimeoutSeconds = 1,
                }
            );

        [Test]
        public void It_succeeds_validation() => _result.Succeeded.Should().BeTrue();
    }

    [TestFixture]
    public class Given_every_setting_at_its_largest_accepted_value
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(
                new IdentityOptions
                {
                    SigningKeyRefreshIntervalSeconds = 43_200,
                    SigningKeyMaxStalenessSeconds = 86_400,
                    SigningKeyUnknownKeyRefreshCooldownSeconds = 3_600,
                    SigningKeyLoadTimeoutSeconds = 60,
                }
            );

        [Test]
        public void It_succeeds_validation() => _result.Succeeded.Should().BeTrue();
    }

    [TestFixture]
    public class Given_a_refresh_interval_below_the_minimum
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(new IdentityOptions { SigningKeyRefreshIntervalSeconds = 29 });

        [Test]
        public void It_reports_only_the_range_failure() =>
            _result
                .Failures.Should()
                .Equal(
                    "IdentitySettings:SigningKeyRefreshIntervalSeconds must be between 30 and 43200; it is 29."
                );
    }

    // 43,201 s is the first interval no max staleness can satisfy: twice it exceeds the 86,400 s staleness cap. It
    // must fail as an out-of-range interval, not as an unsatisfiable staleness rule.
    [TestFixture]
    public class Given_a_refresh_interval_just_above_the_maximum
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(
                new IdentityOptions
                {
                    SigningKeyRefreshIntervalSeconds = 43_201,
                    SigningKeyMaxStalenessSeconds = 86_400,
                }
            );

        [Test]
        public void It_reports_only_the_range_failure() =>
            _result
                .Failures.Should()
                .Equal(
                    "IdentitySettings:SigningKeyRefreshIntervalSeconds must be between 30 and 43200; it is 43201."
                );
    }

    [TestFixture]
    public class Given_the_maximum_refresh_interval_with_a_max_staleness_just_below_twice_it
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(
                new IdentityOptions
                {
                    SigningKeyRefreshIntervalSeconds = 43_200,
                    SigningKeyMaxStalenessSeconds = 86_399,
                }
            );

        [Test]
        public void It_reports_the_staleness_rule() =>
            _result
                .Failures.Should()
                .Equal(
                    "IdentitySettings:SigningKeyMaxStalenessSeconds must be between 2 x "
                        + "IdentitySettings:SigningKeyRefreshIntervalSeconds (86400) and 86400; it is 86399."
                );
    }

    [TestFixture]
    public class Given_a_max_staleness_shorter_than_two_refresh_intervals
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(
                new IdentityOptions
                {
                    SigningKeyRefreshIntervalSeconds = 300,
                    SigningKeyMaxStalenessSeconds = 599,
                }
            );

        [Test]
        public void It_names_both_settings_and_the_derived_minimum() =>
            _result
                .Failures.Should()
                .Equal(
                    "IdentitySettings:SigningKeyMaxStalenessSeconds must be between 2 x "
                        + "IdentitySettings:SigningKeyRefreshIntervalSeconds (600) and 86400; it is 599."
                );
    }

    [TestFixture]
    public class Given_a_max_staleness_of_exactly_two_refresh_intervals
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(
                new IdentityOptions
                {
                    SigningKeyRefreshIntervalSeconds = 300,
                    SigningKeyMaxStalenessSeconds = 600,
                }
            );

        [Test]
        public void It_succeeds_validation() => _result.Succeeded.Should().BeTrue();
    }

    [TestFixture]
    public class Given_a_max_staleness_above_the_maximum
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(new IdentityOptions { SigningKeyMaxStalenessSeconds = 86_401 });

        [Test]
        public void It_fails_validation() =>
            _result
                .Failures.Should()
                .Equal(
                    "IdentitySettings:SigningKeyMaxStalenessSeconds must be between 2 x "
                        + "IdentitySettings:SigningKeyRefreshIntervalSeconds (600) and 86400; it is 86401."
                );
    }

    [TestFixture]
    public class Given_an_invalid_max_staleness_with_an_invalid_refresh_interval
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(
                new IdentityOptions
                {
                    SigningKeyRefreshIntervalSeconds = 0,
                    SigningKeyMaxStalenessSeconds = 0,
                }
            );

        // Without a valid refresh interval there is no derived minimum, so max staleness is checked against the
        // smallest value any valid interval allows.
        [Test]
        public void It_reports_each_setting_against_its_own_range() =>
            _result
                .Failures.Should()
                .Equal(
                    "IdentitySettings:SigningKeyRefreshIntervalSeconds must be between 30 and 43200; it is 0.",
                    "IdentitySettings:SigningKeyMaxStalenessSeconds must be between 60 and 86400; it is 0."
                );
    }

    [TestFixture]
    public class Given_a_zero_unknown_key_refresh_cooldown
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(new IdentityOptions { SigningKeyUnknownKeyRefreshCooldownSeconds = 0 });

        // A zero cooldown would let every request carrying an unknown kid start a key load (I-6).
        [Test]
        public void It_fails_validation() =>
            _result
                .Failures.Should()
                .Equal(
                    "IdentitySettings:SigningKeyUnknownKeyRefreshCooldownSeconds must be between 1 and 3600; it is 0."
                );
    }

    [TestFixture]
    public class Given_a_cooldown_above_the_maximum
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(new IdentityOptions { SigningKeyUnknownKeyRefreshCooldownSeconds = 3_601 });

        [Test]
        public void It_fails_validation() =>
            _result
                .Failures.Should()
                .Equal(
                    "IdentitySettings:SigningKeyUnknownKeyRefreshCooldownSeconds must be between 1 and 3600; it is 3601."
                );
    }

    [TestFixture]
    public class Given_a_zero_load_timeout
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() => _result = Validate(new IdentityOptions { SigningKeyLoadTimeoutSeconds = 0 });

        [Test]
        public void It_fails_validation() =>
            _result
                .Failures.Should()
                .Equal("IdentitySettings:SigningKeyLoadTimeoutSeconds must be between 1 and 60; it is 0.");
    }

    [TestFixture]
    public class Given_a_load_timeout_above_the_maximum
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() => _result = Validate(new IdentityOptions { SigningKeyLoadTimeoutSeconds = 61 });

        [Test]
        public void It_fails_validation() =>
            _result
                .Failures.Should()
                .Equal("IdentitySettings:SigningKeyLoadTimeoutSeconds must be between 1 and 60; it is 61.");
    }

    [TestFixture]
    public class Given_a_load_timeout_equal_to_the_refresh_interval
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(
                new IdentityOptions
                {
                    SigningKeyRefreshIntervalSeconds = 30,
                    SigningKeyLoadTimeoutSeconds = 30,
                }
            );

        [Test]
        public void It_fails_validation() =>
            _result
                .Failures.Should()
                .Equal(
                    "IdentitySettings:SigningKeyLoadTimeoutSeconds must be less than "
                        + "IdentitySettings:SigningKeyRefreshIntervalSeconds (30); it is 30."
                );
    }

    [TestFixture]
    public class Given_a_load_timeout_just_below_the_refresh_interval
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(
                new IdentityOptions
                {
                    SigningKeyRefreshIntervalSeconds = 30,
                    SigningKeyLoadTimeoutSeconds = 29,
                }
            );

        [Test]
        public void It_succeeds_validation() => _result.Succeeded.Should().BeTrue();
    }

    [TestFixture]
    public class Given_several_invalid_settings
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(
                new IdentityOptions
                {
                    SigningKeyMaxStalenessSeconds = 100,
                    SigningKeyUnknownKeyRefreshCooldownSeconds = -5,
                    SigningKeyLoadTimeoutSeconds = 0,
                }
            );

        [Test]
        public void It_reports_every_failure() =>
            _result
                .Failures.Should()
                .Equal(
                    "IdentitySettings:SigningKeyUnknownKeyRefreshCooldownSeconds must be between 1 and 3600; it is -5.",
                    "IdentitySettings:SigningKeyLoadTimeoutSeconds must be between 1 and 60; it is 0.",
                    "IdentitySettings:SigningKeyMaxStalenessSeconds must be between 2 x "
                        + "IdentitySettings:SigningKeyRefreshIntervalSeconds (600) and 86400; it is 100."
                );
    }
}
