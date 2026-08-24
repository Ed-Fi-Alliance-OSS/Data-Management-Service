// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Configuration;

[TestFixture]
public class Given_Known_Provider_Names
{
    [Test]
    public void It_parses_postgresql()
    {
        PerfProviders.Parse("postgresql").Should().Be(PerfProvider.Postgresql);
    }

    [Test]
    public void It_parses_mssql()
    {
        PerfProviders.Parse("mssql").Should().Be(PerfProvider.Mssql);
    }

    [Test]
    public void It_parses_case_insensitively()
    {
        PerfProviders.Parse(" PostgreSQL ").Should().Be(PerfProvider.Postgresql);
    }

    [Test]
    public void It_round_trips_the_artifact_names()
    {
        PerfProviders.ArtifactName(PerfProvider.Postgresql).Should().Be("postgresql");
        PerfProviders.ArtifactName(PerfProvider.Mssql).Should().Be("mssql");
    }
}

[TestFixture]
public class Given_An_Unknown_Provider_Name
{
    [Test]
    public void It_rejects_the_name()
    {
        FluentActions
            .Invoking(() => PerfProviders.Parse("sqlite"))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*sqlite*");
    }
}
