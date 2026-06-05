// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Fixtures;

public sealed class Given_FixtureContextLoader
{
    [Test]
    public void It_materializes_authoritative_ds52_tpdm_with_survey_response_resources()
    {
        FixtureContext fixture = FixtureContextLoader.Load(FixtureKey.AuthoritativeDs52Tpdm);

        File.Exists(Path.Combine(fixture.ApiSchemaDirectory, "fixture.json")).Should().BeTrue();
        fixture
            .ProfileXmlDirectory.Should()
            .EndWith(Path.Combine("Fixtures", "Profiles", nameof(FixtureKey.AuthoritativeDs52Tpdm)));
        fixture
            .Resources.Should()
            .Contain([
                new("Ed-Fi", "SurveyResponse"),
                new("TPDM", "SurveyResponse"),
                new("Ed-Fi", "Survey"),
                new("Ed-Fi", "Contact"),
                new("Ed-Fi", "Staff"),
                new("Ed-Fi", "Student"),
                new("Ed-Fi", "School"),
                new("Ed-Fi", "Session"),
                new("Ed-Fi", "SchoolYearType"),
            ]);
    }
}
