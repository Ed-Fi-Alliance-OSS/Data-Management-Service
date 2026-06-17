// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

[TestFixture]
[Parallelizable]
public class ApiVersionDetailsTests
{
    [TestCase("8.0.1+0a1b2c3", "8.0.1")]
    [TestCase("8.0.1-rc.1+0a1b2c3", "8.0.1-rc.1")]
    [TestCase("8.0.1", "8.0.1")]
    public void Given_InformationalVersionWithMetadata_When_Normalizing_Then_StripsBuildMetadata(
        string raw,
        string expected
    )
    {
        // Act
        string normalized = ApiVersionDetails.NormalizeInformationalVersion(raw);

        // Assert
        Assert.That(normalized, Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Given_MissingInformationalVersion_When_Normalizing_Then_ReturnsReleaseFallback(string? raw)
    {
        // Act
        string normalized = ApiVersionDetails.NormalizeInformationalVersion(raw);

        // Assert
        Assert.That(normalized, Is.EqualTo("8.0.0"));
    }
}
