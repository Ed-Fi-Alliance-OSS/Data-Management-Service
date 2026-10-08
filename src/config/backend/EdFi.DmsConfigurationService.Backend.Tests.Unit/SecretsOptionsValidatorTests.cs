// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

public class SecretsOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(int cacheExpirationSeconds, int resolveTimeoutSeconds) =>
        new SecretsOptionsValidator().Validate(
            null,
            new SecretsOptions
            {
                CacheExpirationSeconds = cacheExpirationSeconds,
                ResolveTimeoutSeconds = resolveTimeoutSeconds,
            }
        );

    [TestFixture]
    public class Given_the_defaults
    {
        private SecretsOptions _options = null!;
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act()
        {
            _options = new SecretsOptions();
            _result = new SecretsOptionsValidator().Validate(null, _options);
        }

        [Test]
        public void It_caches_for_three_hundred_seconds() => _options.CacheExpirationSeconds.Should().Be(300);

        [Test]
        public void It_times_out_after_ten_seconds() => _options.ResolveTimeoutSeconds.Should().Be(10);

        [Test]
        public void It_succeeds_validation() => _result.Succeeded.Should().BeTrue();
    }

    [TestFixture]
    public class Given_a_cache_expiration
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(int.MaxValue)]
        public void It_accepts_zero_or_greater(int seconds) =>
            Validate(seconds, 10).Succeeded.Should().BeTrue();

        [TestCase(-1)]
        [TestCase(int.MinValue)]
        public void It_rejects_a_negative_value(int seconds) =>
            Validate(seconds, 10)
                .FailureMessage.Should()
                .Contain("SecretsSettings:CacheExpirationSeconds must be zero");
    }

    [TestFixture]
    public class Given_a_resolve_timeout
    {
        [TestCase(1)]
        [TestCase(4294967)]
        public void It_accepts_a_value_a_timer_can_arm(int seconds) =>
            Validate(300, seconds).Succeeded.Should().BeTrue();

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(4294968)]
        [TestCase(int.MaxValue)]
        public void It_rejects_a_value_outside_the_range(int seconds) =>
            Validate(300, seconds)
                .FailureMessage.Should()
                .Contain("SecretsSettings:ResolveTimeoutSeconds must be between 1 and 4294967.");
    }

    [TestFixture]
    public class Given_both_values_are_invalid
    {
        [Test]
        public void It_names_both_settings()
        {
            ValidateOptionsResult result = Validate(-1, 0);

            result.Failures.Should().HaveCount(2);
        }
    }
}
