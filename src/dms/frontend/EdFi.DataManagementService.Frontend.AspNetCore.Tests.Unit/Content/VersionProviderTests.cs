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

    [Test]
    public void Given_VersionProvider_When_RetrievingInformationalVersion_Then_ReturnsNonEmptyValueWithoutBuildMetadata()
    {
        // Arrange
        var versionProvider = new VersionProvider();

        // Act
        string informationalVersion = versionProvider.InformationalVersion;

        // Assert
        Assert.That(informationalVersion, Is.Not.Null.And.Not.Empty);
        Assert.That(informationalVersion, Does.Not.Contain("+"));
    }
}
