// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

[TestFixture]
[Parallelizable]
public class Given_The_Education_Organization_Projection_Settings_Validator
{
    private const string Section = "DataManagement:EducationOrganizationProjection";

    private static readonly EducationOrganizationProjectionSettings _defaults = new();

    private static ValidateOptionsResult Validate(EducationOrganizationProjectionSettings settings) =>
        new EducationOrganizationProjectionSettingsValidator().Validate(Options.DefaultName, settings);

    private static IEnumerable<TestCaseData> OutOfRangeCases()
    {
        yield return Case(_defaults with { MaximumPageSize = 0 }, "MaximumPageSize", 0, "1 and 10000");
        yield return Case(
            _defaults with
            {
                MaximumPageSize = 10001,
            },
            "MaximumPageSize",
            10001,
            "1 and 10000"
        );
        yield return Case(
            _defaults with
            {
                MaxProjectionRows = 999,
            },
            "MaxProjectionRows",
            999,
            "1000 and 1000000"
        );
        yield return Case(
            _defaults with
            {
                MaxProjectionRows = 1000001,
            },
            "MaxProjectionRows",
            1000001,
            "1000 and 1000000"
        );
        yield return Case(
            _defaults with
            {
                CursorLifetimeMinutes = 0,
            },
            "CursorLifetimeMinutes",
            0,
            "1 and 1440"
        );
        yield return Case(
            _defaults with
            {
                CursorLifetimeMinutes = 1441,
            },
            "CursorLifetimeMinutes",
            1441,
            "1 and 1440"
        );
        yield return Case(
            _defaults with
            {
                ReadLockTimeoutSeconds = 0,
            },
            "ReadLockTimeoutSeconds",
            0,
            "1 and 60"
        );
        yield return Case(
            _defaults with
            {
                ReadLockTimeoutSeconds = 61,
            },
            "ReadLockTimeoutSeconds",
            61,
            "1 and 60"
        );
        yield return Case(
            _defaults with
            {
                ReadCommandTimeoutSeconds = 4,
            },
            "ReadCommandTimeoutSeconds",
            4,
            "5 and 600"
        );
        yield return Case(
            _defaults with
            {
                ReadCommandTimeoutSeconds = 601,
            },
            "ReadCommandTimeoutSeconds",
            601,
            "5 and 600"
        );

        static TestCaseData Case(
            EducationOrganizationProjectionSettings settings,
            string name,
            int value,
            string range
        ) => new TestCaseData(settings, name, range).SetName($"It_rejects_{name}_of_{value}");
    }

    private static IEnumerable<TestCaseData> InclusiveBoundCases()
    {
        yield return Case(_defaults with { MaximumPageSize = 1 }, "MaximumPageSize", 1);
        yield return Case(_defaults with { MaximumPageSize = 10000 }, "MaximumPageSize", 10000);
        yield return Case(_defaults with { MaxProjectionRows = 1000 }, "MaxProjectionRows", 1000);
        yield return Case(_defaults with { MaxProjectionRows = 1000000 }, "MaxProjectionRows", 1000000);
        yield return Case(_defaults with { CursorLifetimeMinutes = 1 }, "CursorLifetimeMinutes", 1);
        yield return Case(_defaults with { CursorLifetimeMinutes = 1440 }, "CursorLifetimeMinutes", 1440);
        yield return Case(_defaults with { ReadLockTimeoutSeconds = 1 }, "ReadLockTimeoutSeconds", 1);
        yield return Case(_defaults with { ReadLockTimeoutSeconds = 60 }, "ReadLockTimeoutSeconds", 60);
        yield return Case(_defaults with { ReadCommandTimeoutSeconds = 5 }, "ReadCommandTimeoutSeconds", 5);
        yield return Case(
            _defaults with
            {
                ReadCommandTimeoutSeconds = 600,
            },
            "ReadCommandTimeoutSeconds",
            600
        );

        static TestCaseData Case(EducationOrganizationProjectionSettings settings, string name, int value) =>
            new TestCaseData(settings).SetName($"It_accepts_{name}_of_{value}");
    }

    [Test]
    public void It_accepts_the_default_settings()
    {
        Validate(new EducationOrganizationProjectionSettings()).Succeeded.Should().BeTrue();
    }

    [TestCaseSource(nameof(OutOfRangeCases))]
    public void It_rejects_a_value_outside_its_inclusive_range(
        EducationOrganizationProjectionSettings settings,
        string settingName,
        string range
    )
    {
        ValidateOptionsResult result = Validate(settings);

        result.Failed.Should().BeTrue();
        result
            .Failures.Should()
            .ContainSingle()
            .Which.Should()
            .Be($"Setting {Section}:{settingName} must be between {range}");
    }

    [TestCaseSource(nameof(InclusiveBoundCases))]
    public void It_accepts_a_value_on_its_inclusive_bound(EducationOrganizationProjectionSettings settings)
    {
        Validate(settings).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_validates_the_limits_when_the_endpoint_is_disabled()
    {
        ValidateOptionsResult result = Validate(_defaults with { Enabled = false, MaximumPageSize = 0 });

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().Contain($"{Section}:MaximumPageSize");
    }

    [Test]
    public void It_reports_every_failure()
    {
        ValidateOptionsResult result = Validate(
            new EducationOrganizationProjectionSettings
            {
                MaximumPageSize = 0,
                MaxProjectionRows = 0,
                CursorLifetimeMinutes = 0,
                ReadLockTimeoutSeconds = 0,
                ReadCommandTimeoutSeconds = 0,
            }
        );

        result.Failed.Should().BeTrue();
        result.Failures.Should().HaveCount(5);
    }
}

[TestFixture]
[Parallelizable]
public class Given_Default_Education_Organization_Projection_Settings
{
    [Test]
    public void It_binds_from_the_data_management_section()
    {
        EducationOrganizationProjectionSettings
            .SectionName.Should()
            .Be("DataManagement:EducationOrganizationProjection");
    }

    [Test]
    public void It_enables_the_endpoint_with_the_approved_limits()
    {
        EducationOrganizationProjectionSettings settings = new();

        settings.Enabled.Should().BeTrue();
        settings.MaximumPageSize.Should().Be(2000);
        settings.MaxProjectionRows.Should().Be(50000);
        settings.CursorLifetimeMinutes.Should().Be(60);
        settings.ReadLockTimeoutSeconds.Should().Be(5);
        settings.ReadCommandTimeoutSeconds.Should().Be(60);
    }

    [Test]
    public void It_places_the_bounds_where_the_named_constants_say()
    {
        EducationOrganizationProjectionSettings.MaximumPageSizeMinimum.Should().Be(1);
        EducationOrganizationProjectionSettings.MaximumPageSizeMaximum.Should().Be(10000);
        EducationOrganizationProjectionSettings.MaxProjectionRowsMinimum.Should().Be(1000);
        EducationOrganizationProjectionSettings.MaxProjectionRowsMaximum.Should().Be(1000000);
        EducationOrganizationProjectionSettings.CursorLifetimeMinutesMinimum.Should().Be(1);
        EducationOrganizationProjectionSettings.CursorLifetimeMinutesMaximum.Should().Be(1440);
        EducationOrganizationProjectionSettings.ReadLockTimeoutSecondsMinimum.Should().Be(1);
        EducationOrganizationProjectionSettings.ReadLockTimeoutSecondsMaximum.Should().Be(60);
        EducationOrganizationProjectionSettings.ReadCommandTimeoutSecondsMinimum.Should().Be(5);
        EducationOrganizationProjectionSettings.ReadCommandTimeoutSecondsMaximum.Should().Be(600);
    }
}
