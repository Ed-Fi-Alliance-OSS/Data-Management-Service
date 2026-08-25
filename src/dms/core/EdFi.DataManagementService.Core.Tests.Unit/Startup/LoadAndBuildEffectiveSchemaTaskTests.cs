// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Startup;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

[TestFixture]
public class LoadAndBuildEffectiveSchemaTaskTests
{
    private IEffectiveSchemaBootstrapper _bootstrapper = null!;
    private LoadAndBuildEffectiveSchemaTask _task = null!;

    [SetUp]
    public void Setup()
    {
        _bootstrapper = A.Fake<IEffectiveSchemaBootstrapper>();
        A.CallTo(() => _bootstrapper.InitializeAsync(A<CancellationToken>._)).Returns(Task.CompletedTask);

        _task = new LoadAndBuildEffectiveSchemaTask(_bootstrapper);
    }

    [Test]
    public void It_has_order_100()
    {
        _task.Order.Should().Be(100);
    }

    [Test]
    public void It_has_expected_name()
    {
        _task.Name.Should().Be("Load and Build Effective Schema");
    }

    [Test]
    public async Task It_delegates_to_the_effective_schema_bootstrapper()
    {
        using var cancellationTokenSource = new CancellationTokenSource();

        await _task.ExecuteAsync(cancellationTokenSource.Token);

        A.CallTo(() => _bootstrapper.InitializeAsync(cancellationTokenSource.Token))
            .MustHaveHappenedOnceExactly();
    }
}
