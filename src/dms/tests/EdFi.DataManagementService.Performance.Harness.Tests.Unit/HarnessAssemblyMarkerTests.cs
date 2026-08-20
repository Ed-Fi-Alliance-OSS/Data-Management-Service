// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Performance.Harness;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit;

[TestFixture]
public class Given_The_Performance_Harness_Assembly
{
    private Assembly _harnessAssembly = null!;

    [SetUp]
    public void Setup()
    {
        _harnessAssembly = typeof(HarnessAssemblyMarker).Assembly;
    }

    [Test]
    public void It_is_marked_non_parallelizable()
    {
        _harnessAssembly
            .GetCustomAttributes<NonParallelizableAttribute>()
            .Should()
            .NotBeEmpty("measured scenario runs share process-wide observers and a leased database");
    }
}
