// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

/// <summary>
/// A section that omits RequireHttpsMetadata must bind to the secure default, the same value the
/// startup diagnostic falls back to, while an explicit false still wins. Bound with the call
/// ConfigureIdentityProvider makes to build the settings authentication uses.
/// </summary>
public abstract class IdentitySettingsDefaultsTests
{
    protected static IdentitySettings Bind(params (string Key, string Value)[] extraSettings)
    {
        Dictionary<string, string?> settings = new()
        {
            ["IdentitySettings:Authority"] = "http://localhost",
            ["IdentitySettings:ClientId"] = "client",
            ["IdentitySettings:ClientSecret"] = "ValidClientSecret1234567890!Abcd",
            ["IdentitySettings:Audience"] = "account",
            ["IdentitySettings:RoleClaimType"] = "role",
            ["IdentitySettings:ConfigServiceRole"] = "config-service-app",
            ["IdentitySettings:ClientRole"] = "dms-client",
        };
        foreach ((string key, string value) in extraSettings)
        {
            settings[key] = value;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build()
            .GetSection("IdentitySettings")
            .Get<IdentitySettings>()!;
    }

    [TestFixture]
    public class Given_identity_settings_bound_without_the_key : IdentitySettingsDefaultsTests
    {
        private IdentitySettings _settings = null!;

        [SetUp]
        public void Setup() => _settings = Bind();

        [Test]
        public void It_requires_https_metadata() => _settings.RequireHttpsMetadata.Should().BeTrue();
    }

    [TestFixture]
    public class Given_identity_settings_bound_with_an_explicit_false : IdentitySettingsDefaultsTests
    {
        private IdentitySettings _settings = null!;

        [SetUp]
        public void Setup() => _settings = Bind(("IdentitySettings:RequireHttpsMetadata", "false"));

        [Test]
        public void It_keeps_false() => _settings.RequireHttpsMetadata.Should().BeFalse();
    }
}
