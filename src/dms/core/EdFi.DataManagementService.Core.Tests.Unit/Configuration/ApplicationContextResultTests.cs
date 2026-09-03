// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

[TestFixture]
public class Given_ApplicationContextResult
{
    [Test]
    public void It_Should_Expose_The_Successful_Application_Context_Payload()
    {
        var applicationContext = new ApplicationContext(1, 2, "client-id", Guid.NewGuid(), [3], 4, [5, 6]);

        ApplicationContextResult result = new ApplicationContextResult.Success(applicationContext);

        result.Should().BeOfType<ApplicationContextResult.Success>();
        ((ApplicationContextResult.Success)result).ApplicationContext.Should().Be(applicationContext);
    }

    [Test]
    public void It_Should_Accept_A_Null_Creator_And_Empty_Ownership_Tokens()
    {
        var applicationContext = new ApplicationContext(1, 2, "client-id", Guid.NewGuid(), [3], null, []);

        applicationContext.CreatorOwnershipTokenId.Should().BeNull();
        applicationContext.OwnershipTokenIds.Should().BeEmpty();
    }

    [TestCase(ApplicationContextResultKind.Success)]
    [TestCase(ApplicationContextResultKind.NotFound)]
    [TestCase(ApplicationContextResultKind.Unavailable)]
    public void It_Should_Support_Exhaustive_Typed_Outcome_Matching(ApplicationContextResultKind kind)
    {
        ApplicationContextResult result = kind switch
        {
            ApplicationContextResultKind.Success => new ApplicationContextResult.Success(
                new ApplicationContext(1, 2, "client-id", Guid.NewGuid(), [3], null, [])
            ),
            ApplicationContextResultKind.NotFound => new ApplicationContextResult.NotFound(),
            ApplicationContextResultKind.Unavailable => new ApplicationContextResult.Unavailable(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var outcome = result switch
        {
            ApplicationContextResult.Success => ApplicationContextResultKind.Success,
            ApplicationContextResult.NotFound => ApplicationContextResultKind.NotFound,
            ApplicationContextResult.Unavailable => ApplicationContextResultKind.Unavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        outcome.Should().Be(kind);
    }

    public enum ApplicationContextResultKind
    {
        Success,
        NotFound,
        Unavailable,
    }
}
