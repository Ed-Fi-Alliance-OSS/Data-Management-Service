// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

public class ConnectRateLimitSettingsValidatorTests
{
    [TestFixture]
    public class Given_the_default_settings
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = new ConnectRateLimitSettingsValidator().Validate(null, new ConnectRateLimitSettings());

        [Test]
        public void It_succeeds() => _result.Succeeded.Should().BeTrue();
    }

    [TestFixture("SourcePermitLimit", 0)]
    [TestFixture("SourcePermitLimit", -1)]
    [TestFixture("ClientPermitLimit", 0)]
    [TestFixture("ClientPermitLimit", -5)]
    [TestFixture("WindowSeconds", 0)]
    [TestFixture("WindowSeconds", -60)]
    public class Given_a_non_positive_value(string setting, int value)
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup()
        {
            var settings = new ConnectRateLimitSettings();
            typeof(ConnectRateLimitSettings).GetProperty(setting)!.SetValue(settings, value);
            _result = new ConnectRateLimitSettingsValidator().Validate(null, settings);
        }

        [Test]
        public void It_fails() => _result.Failed.Should().BeTrue();

        [Test]
        public void It_names_the_setting() =>
            _result
                .Failures.Should()
                .ContainSingle()
                .Which.Should()
                .Be($"ConnectRateLimit:{setting} must be a positive integer.");
    }

    [TestFixture]
    public class Given_several_invalid_values
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = new ConnectRateLimitSettingsValidator().Validate(
                null,
                new ConnectRateLimitSettings { SourcePermitLimit = 0, WindowSeconds = 0 }
            );

        [Test]
        public void It_reports_each_one() =>
            _result
                .Failures.Should()
                .BeEquivalentTo(
                    "ConnectRateLimit:SourcePermitLimit must be a positive integer.",
                    "ConnectRateLimit:WindowSeconds must be a positive integer."
                );
    }

    [TestFixture]
    public class Given_limiting_is_disabled_with_zero_values
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = new ConnectRateLimitSettingsValidator().Validate(
                null,
                new ConnectRateLimitSettings
                {
                    Enabled = false,
                    SourcePermitLimit = 0,
                    ClientPermitLimit = 0,
                    WindowSeconds = 0,
                }
            );

        [Test]
        public void It_succeeds() => _result.Succeeded.Should().BeTrue();
    }
}
