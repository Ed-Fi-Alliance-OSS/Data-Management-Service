// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

[TestFixture]
public class IdentitySettingsValidatorTests
{
    private IdentitySettingsValidator _validator = null!;

    [SetUp]
    public void SetUp()
    {
        _validator = new IdentitySettingsValidator(
            Options.Create(
                new ClientSecretValidationOptions
                {
                    MinimumLength = 8,
                    MaximumLength = 12,
                }
            )
        );
    }

    [Test]
    public void It_should_fail_when_client_secret_is_shorter_than_the_configured_minimum()
    {
        var result = _validator.Validate(
            null,
            new IdentitySettings
            {
                Authority = "http://localhost",
                ClientId = "client-id",
                ClientSecret = "Short1!",
                RequireHttpsMetadata = false,
                AllowRegistration = true,
                Audience = "audience",
                RoleClaimType = "role",
                ConfigServiceRole = "cms-client",
                ClientRole = "dms-client",
            }
        );

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("between 8 and 12 characters long");
    }

    [Test]
    public void It_should_fail_when_client_secret_is_longer_than_the_configured_maximum()
    {
        var result = _validator.Validate(
            null,
            new IdentitySettings
            {
                Authority = "http://localhost",
                ClientId = "client-id",
                ClientSecret = "ValidSecret123!",
                RequireHttpsMetadata = false,
                AllowRegistration = true,
                Audience = "audience",
                RoleClaimType = "role",
                ConfigServiceRole = "cms-client",
                ClientRole = "dms-client",
            }
        );

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("between 8 and 12 characters long");
    }

    [Test]
    public void It_should_fail_when_client_secret_does_not_meet_complexity_requirements()
    {
        var result = _validator.Validate(
            null,
            new IdentitySettings
            {
                Authority = "http://localhost",
                ClientId = "client-id",
                ClientSecret = "alllower1!",
                RequireHttpsMetadata = false,
                AllowRegistration = true,
                Audience = "audience",
                RoleClaimType = "role",
                ConfigServiceRole = "cms-client",
                ClientRole = "dms-client",
            }
        );

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("at least one lowercase letter");
    }

    [Test]
    public void It_should_fail_when_whitespace_is_the_only_special_character()
    {
        var result = _validator.Validate(
            null,
            new IdentitySettings
            {
                Authority = "http://localhost",
                ClientId = "client-id",
                ClientSecret = "Secret1 A",
                RequireHttpsMetadata = false,
                AllowRegistration = true,
                Audience = "audience",
                RoleClaimType = "role",
                ConfigServiceRole = "cms-client",
                ClientRole = "dms-client",
            }
        );

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("at least one lowercase letter");
    }
}
