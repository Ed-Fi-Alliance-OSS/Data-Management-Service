// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Content;

[TestFixture]
public class AssemblyProviderTests
{
    [Test]
    public void Given_ValidType_When_GetAssemblyByType_Then_ReturnsAssembly()
    {
        // Arrange
        var logger = A.Fake<ILogger<AssemblyProvider>>();
        var assemblyProvider = new AssemblyProvider(logger);
        var type = typeof(string);
        var expected = Assembly.GetAssembly(type);

        // Act
        var result = assemblyProvider.GetAssemblyByType(type);

        // Assert
        Assert.That(result, Is.EqualTo(expected));
    }
}

