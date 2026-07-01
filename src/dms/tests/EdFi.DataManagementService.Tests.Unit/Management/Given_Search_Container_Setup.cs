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
    private int _resetDatabaseCalls;

    [SetUp]
    public async Task Setup()
    {
        _resetDatabaseCalls = 0;

        var sut = new SearchContainerSetup(resetDatabase: () =>
        {
            _resetDatabaseCalls++;
            return Task.CompletedTask;
        });

        await sut.ResetData();
    }

    [Test]
    public void It_resets_the_e2e_database()
    {
        _resetDatabaseCalls.Should().Be(1);
    }
}
