// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

[TestFixture]
public class Given_AppSettingsValidator
{
    private AppSettingsValidator _validator = null!;

    [SetUp]
    public void SetUp() => _validator = new AppSettingsValidator();

    private static AppSettings ValidSettings() =>
        new()
        {
            Datastore = "postgresql",
            IdentityProvider = "keycloak",
            SpecificationVersion = "v3",
        };

    [Test]
    public void It_should_succeed_when_all_settings_are_valid()
    {
        var result = _validator.Validate(null, ValidSettings());

        result.Succeeded.Should().BeTrue();
    }

    [TestCase("")]
    [TestCase("   ")]
    public void It_should_fail_when_specification_version_is_missing_or_whitespace(string version)
    {
        var settings = ValidSettings();
        settings.SpecificationVersion = version;

        var result = _validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Missing required AppSettings value: SpecificationVersion");
    }

    [Test]
    public void It_should_fail_when_specification_version_is_invalid()
    {
        var settings = ValidSettings();
        settings.SpecificationVersion = "v99";

        var result = _validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("must be one of: v1, v2, v3");
    }

    [TestCase("v1")]
    [TestCase("v2")]
    [TestCase("v3")]
    [TestCase("V1")]
    [TestCase("V2")]
    [TestCase("V3")]
    public void It_should_succeed_for_valid_specification_versions(string version)
    {
        var settings = ValidSettings();
        settings.SpecificationVersion = version;

        var result = _validator.Validate(null, settings);

        result.Succeeded.Should().BeTrue();
    }
}
