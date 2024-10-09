// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Content;

[TestFixture]
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
}

