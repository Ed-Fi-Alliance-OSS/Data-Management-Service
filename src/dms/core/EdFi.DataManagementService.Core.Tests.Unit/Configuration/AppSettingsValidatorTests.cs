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
    public void It_defaults_the_maximum_page_size_to_500()
    {
        new AppSettings { AllowIdentityUpdateOverrides = string.Empty }
            .MaximumPageSize.Should()
            .Be(500);
    }

    [Test]
    public void It_defaults_the_partition_count_to_ten()
    {
        new AppSettings { AllowIdentityUpdateOverrides = string.Empty }
            .DefaultPartitionCount.Should()
            .Be(10);
    }

    [Test]
    public void It_shares_one_literal_between_the_maximum_page_size_property_default_and_the_constant()
    {
        new AppSettings { AllowIdentityUpdateOverrides = string.Empty }
            .MaximumPageSize.Should()
            .Be(AppSettings.MaximumPageSizeDefault);
    }

    [Test]
    public void It_shares_one_literal_between_the_partition_count_property_default_and_the_constant()
    {
        new AppSettings { AllowIdentityUpdateOverrides = string.Empty }
            .DefaultPartitionCount.Should()
            .Be(AppSettings.DefaultPartitionCountDefault);
    }
}
