// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationRefresh;

public class EducationOrganizationRefreshOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(int interval) =>
        new EducationOrganizationRefreshOptionsValidator().Validate(
            null,
            new EducationOrganizationRefreshOptions { EdOrgsRefreshIntervalInMins = interval }
        );

    [TestFixture]
    public class Given_the_default_options
    {
        [Test]
        public void It_refreshes_every_60_minutes() =>
            new EducationOrganizationRefreshOptions().EdOrgsRefreshIntervalInMins.Should().Be(60);

        [Test]
        public void It_passes_validation() =>
            new EducationOrganizationRefreshOptionsValidator()
                .Validate(null, new EducationOrganizationRefreshOptions())
                .Succeeded.Should()
                .BeTrue();

        [Test]
        public void It_bounds_the_interval_by_what_a_job_schedule_accepts() =>
            EducationOrganizationRefreshOptions
                .MaximumRefreshIntervalInMins.Should()
                .Be(JobScheduleService.MaxIntervalMinutes)
                .And.Be(527040);
    }

    [TestFixture]
    public class Given_an_interval_in_range
    {
        [TestCase(1)]
        [TestCase(60)]
        [TestCase(527040)]
        public void It_passes_validation(int interval) => Validate(interval).Succeeded.Should().BeTrue();
    }

    [TestFixture]
    public class Given_an_interval_out_of_range
    {
        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(527041)]
        [TestCase(int.MinValue)]
        [TestCase(int.MaxValue)]
        public void It_fails_and_names_the_setting_and_the_value(int interval)
        {
            ValidateOptionsResult result = Validate(interval);

            result.Failed.Should().BeTrue();
            result
                .Failures.Should()
                .Equal(
                    $"AppSettings:EdOrgsRefreshIntervalInMins must be between 1 and 527040 minutes; it is {interval}."
                );
        }
    }
}
