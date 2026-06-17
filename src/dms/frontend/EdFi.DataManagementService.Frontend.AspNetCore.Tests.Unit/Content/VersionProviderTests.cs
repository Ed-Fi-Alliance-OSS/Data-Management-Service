// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Content;

[TestFixture]
[Parallelizable]
public class VersionProviderTests
{
    [Test]
    public void Given_VersionProvider_When_RetrievingVersion_Then_ReturnsCorrectFormat()
    {
        // Arrange
        var versionProvider = new VersionProvider();

        // Act
        string version = versionProvider.Version;

        // Assert
        Assert.That(version, Is.Not.EqualTo("0.0.0"));
    }

    [Test]
    public void Given_VersionProvider_When_RetrievingApplicationName_Then_ReturnsEdFiApi()
    {
        // Arrange
        var versionProvider = new VersionProvider();

        // Act
        string applicationName = versionProvider.ApplicationName;

        // Assert
        Assert.That(applicationName, Is.EqualTo("Ed-Fi API"));
    }

    [TestCase("8.0.1+0a1b2c3", "8.0.1")]
    [TestCase("8.0.1-rc.1+0a1b2c3", "8.0.1-rc.1")]
    [TestCase("8.0.1", "8.0.1")]
    public void Given_InformationalVersionWithMetadata_When_Normalizing_Then_StripsBuildMetadata(
        string raw,
        string expected
    )
    {
        // Act
        string normalized = VersionProvider.NormalizeInformationalVersion(raw);

        // Assert
        Assert.That(normalized, Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Given_MissingInformationalVersion_When_Normalizing_Then_ReturnsReleaseFallback(string? raw)
    {
        // Act
        string normalized = VersionProvider.NormalizeInformationalVersion(raw);

        // Assert
        Assert.That(normalized, Is.EqualTo("8.0.0"));
    }
}
