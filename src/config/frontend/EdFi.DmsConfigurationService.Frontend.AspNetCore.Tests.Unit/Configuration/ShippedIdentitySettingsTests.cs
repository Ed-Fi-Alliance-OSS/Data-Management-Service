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
/// A host started from the shipped appsettings.json must refuse to fetch OIDC metadata over plain
/// http unless an environment-specific override says otherwise. Reading the file from the build
/// output, rather than through the host, is what makes the shipped value visible: the running host
/// under test reads appsettings.Test.json.
/// </summary>
[TestFixture]
public class Given_the_shipped_identity_settings
{
    private IConfigurationRoot _shippedConfiguration = null!;

    [SetUp]
    public void Setup()
    {
        _shippedConfiguration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .Build();
    }

    [Test]
    public void It_requires_https_metadata()
    {
        // The JSON provider stores booleans as "True"; read it the way the options binder does. The
        // fallback is the insecure value, so a file without the key fails here too.
        _shippedConfiguration
            .GetValue("IdentitySettings:RequireHttpsMetadata", defaultValue: false)
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_binds_https_metadata_as_required()
    {
        // The call ConfigureIdentityProvider makes to build the settings authentication uses.
        IdentitySettings settings = _shippedConfiguration
            .GetSection("IdentitySettings")
            .Get<IdentitySettings>()!;

        settings.RequireHttpsMetadata.Should().BeTrue();
    }
}
