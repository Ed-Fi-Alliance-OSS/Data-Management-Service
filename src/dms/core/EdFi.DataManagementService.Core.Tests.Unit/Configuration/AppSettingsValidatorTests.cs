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
public class Given_The_App_Settings_Validator
{
    private static AppSettings ValidSettings() =>
        new()
        {
            AllowIdentityUpdateOverrides = string.Empty,
            MaximumPageSize = 500,
            DefaultPartitionCount = AppSettings.DefaultPartitionCountDefault,
        };

    private static ValidateOptionsResult Validate(AppSettings settings) =>
        new AppSettingsValidator().Validate(Options.DefaultName, settings);

    [Test]
    public void It_accepts_valid_settings()
    {
        Validate(ValidSettings()).Succeeded.Should().BeTrue();
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void It_rejects_a_nonpositive_maximum_page_size(int maximumPageSize)
    {
        AppSettings settings = ValidSettings();
        settings.MaximumPageSize = maximumPageSize;

        ValidateOptionsResult result = Validate(settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().Contain(nameof(AppSettings.MaximumPageSize));
    }

    [TestCase(0)]
    [TestCase(201)]
    [TestCase(-1)]
    public void It_rejects_a_partition_count_outside_the_supported_range(int defaultPartitionCount)
    {
        AppSettings settings = ValidSettings();
        settings.DefaultPartitionCount = defaultPartitionCount;

        ValidateOptionsResult result = Validate(settings);

        result.Failed.Should().BeTrue();
        result
            .Failures.Should()
            .ContainSingle()
            .Which.Should()
            .Contain(nameof(AppSettings.DefaultPartitionCount));
    }

    [TestCase(1)]
    [TestCase(10)]
    [TestCase(200)]
    public void It_accepts_a_partition_count_within_the_inclusive_range(int defaultPartitionCount)
    {
        AppSettings settings = ValidSettings();
        settings.DefaultPartitionCount = defaultPartitionCount;

        Validate(settings).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_reports_every_failure_rather_than_stopping_at_the_first()
    {
        AppSettings settings = ValidSettings();
        settings.MaximumPageSize = 0;
        settings.DefaultPartitionCount = 201;

        ValidateOptionsResult result = Validate(settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().HaveCount(2);
    }

    [Test]
    public void It_exposes_the_supported_partition_count_bounds()
    {
        AppSettingsValidator.MinimumDefaultPartitionCount.Should().Be(1);
        AppSettingsValidator.MaximumDefaultPartitionCount.Should().Be(200);
    }

    [Test]
    public void It_rejects_a_null_options_argument()
    {
        Action act = () => new AppSettingsValidator().Validate(Options.DefaultName, null!);

        act.Should().Throw<ArgumentNullException>();
    }
}

[TestFixture]
[Parallelizable]
public class Given_Default_App_Settings
{
    [Test]
    public void It_defaults_the_partition_count_to_ten()
    {
        new AppSettings { AllowIdentityUpdateOverrides = string.Empty }
            .DefaultPartitionCount.Should()
            .Be(10);
    }

    [Test]
    public void It_shares_one_literal_between_the_partition_count_property_default_and_the_constant()
    {
        new AppSettings { AllowIdentityUpdateOverrides = string.Empty }
            .DefaultPartitionCount.Should()
            .Be(AppSettings.DefaultPartitionCountDefault);
    }
}

[TestFixture]
[Parallelizable]
public class Given_The_App_Settings_Validator_With_Education_Organization_Projection_Settings
{
    private const string Section = "EducationOrganizationProjection";

    private static readonly EducationOrganizationProjectionSettings _defaults = new();

    private static AppSettings SettingsWith(EducationOrganizationProjectionSettings projection) =>
        new()
        {
            AllowIdentityUpdateOverrides = string.Empty,
            MaximumPageSize = 500,
            EducationOrganizationProjection = projection,
        };

    private static ValidateOptionsResult Validate(AppSettings settings) =>
        new AppSettingsValidator().Validate(Options.DefaultName, settings);

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
    public void It_accepts_the_default_projection_settings()
    {
        Validate(SettingsWith(new EducationOrganizationProjectionSettings())).Succeeded.Should().BeTrue();
    }

    [TestCaseSource(nameof(OutOfRangeCases))]
    public void It_rejects_a_value_outside_its_inclusive_range(
        EducationOrganizationProjectionSettings projection,
        string settingName,
        string range
    )
    {
        ValidateOptionsResult result = Validate(SettingsWith(projection));

        result.Failed.Should().BeTrue();
        result
            .Failures.Should()
            .ContainSingle()
            .Which.Should()
            .Be($"AppSettings value {Section}:{settingName} must be between {range}");
    }

    [TestCaseSource(nameof(InclusiveBoundCases))]
    public void It_accepts_a_value_on_its_inclusive_bound(EducationOrganizationProjectionSettings projection)
    {
        Validate(SettingsWith(projection)).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_validates_the_limits_when_the_endpoint_is_disabled()
    {
        AppSettings settings = SettingsWith(_defaults with { MaximumPageSize = 0 });
        settings.EnableEducationOrganizationProjection = false;

        ValidateOptionsResult result = Validate(settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().Contain($"{Section}:MaximumPageSize");
    }

    [Test]
    public void It_reports_every_projection_failure_alongside_the_other_failures()
    {
        AppSettings settings = SettingsWith(
            new EducationOrganizationProjectionSettings
            {
                MaximumPageSize = 0,
                MaxProjectionRows = 0,
                CursorLifetimeMinutes = 0,
                ReadLockTimeoutSeconds = 0,
                ReadCommandTimeoutSeconds = 0,
            }
        );
        settings.DefaultPartitionCount = 0;

        ValidateOptionsResult result = Validate(settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().HaveCount(6);
    }

    [Test]
    public void It_rejects_a_missing_projection_settings_object()
    {
        ValidateOptionsResult result = Validate(SettingsWith(null!));

        result.Failed.Should().BeTrue();
        result
            .Failures.Should()
            .ContainSingle()
            .Which.Should()
            .Be($"AppSettings value {Section} must be configured");
    }
}

[TestFixture]
[Parallelizable]
public class Given_Default_Education_Organization_Projection_Settings
{
    [Test]
    public void It_enables_the_endpoint_by_default()
    {
        new AppSettings { AllowIdentityUpdateOverrides = string.Empty }
            .EnableEducationOrganizationProjection.Should()
            .BeTrue();
    }

    [Test]
    public void It_creates_the_projection_settings_with_the_approved_defaults()
    {
        EducationOrganizationProjectionSettings settings = new AppSettings
        {
            AllowIdentityUpdateOverrides = string.Empty,
        }.EducationOrganizationProjection;

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
