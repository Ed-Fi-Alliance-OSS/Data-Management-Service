// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

/// <summary>
/// The iteration count is not stored with a client-secret hash, so a shipped value that drifted
/// from the model default would silently invalidate every secret hashed before the key became live.
/// Reading the file from the build output, rather than through the host, is what makes the shipped
/// value visible: the running host under test reads appsettings.Test.json.
/// </summary>
[TestFixture]
public class Given_the_shipped_client_secret_hashing_iterations
{
    private string? _shippedValue;

    [SetUp]
    public void Setup()
    {
        IConfigurationRoot shippedConfiguration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .Build();

        _shippedValue = shippedConfiguration["IdentitySettings:ClientSecretHashingIterations"];
    }

    [Test]
    public void It_equals_the_model_default() =>
        _shippedValue.Should().Be(new IdentityOptions().ClientSecretHashingIterations.ToString());
}
