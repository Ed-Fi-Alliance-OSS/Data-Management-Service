// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.E2E.Management;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit.Management;

[TestFixture]
public class Given_Search_Container_Setup
{
    private int _resetRelationalDatabaseCalls;

    [SetUp]
    public async Task Setup()
    {
        _resetRelationalDatabaseCalls = 0;

        var sut = new SearchContainerSetup(resetRelationalDatabase: () =>
        {
            _resetRelationalDatabaseCalls++;
            return Task.CompletedTask;
        });

        await sut.ResetData();
    }

    [Test]
    public void It_resets_the_relational_database()
    {
        _resetRelationalDatabaseCalls.Should().Be(1);
    }
}
