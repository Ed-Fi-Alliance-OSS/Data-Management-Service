// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Paging;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Paging;

[TestFixture]
[Parallelizable]
public class PartitionRequestValidatorTests
{
    private static PartitionValidationResult Validate(params (string Key, string Value)[] queryParameters) =>
        PartitionRequestValidator.Validate(
            queryParameters.ToDictionary(
                static parameter => parameter.Key,
                static parameter => parameter.Value,
                StringComparer.Ordinal
            )
        );

    [TestFixture]
    [Parallelizable]
    public class Given_A_Malformed_Or_Out_Of_Range_Number : PartitionRequestValidatorTests
    {
        [TestCase("abc")]
        [TestCase("")]
        [TestCase("0")]
        [TestCase("201")]
        [TestCase("-1")]
        [TestCase("1.5")]
        [TestCase(" ")]
        public void It_reports_only_the_range_error(string number)
        {
            Validate((PartitionRequestValidator.NumberParameter, number))
                .Errors.Should()
                .ContainSingle()
                .Which.Should()
                .Be(PartitionRequestValidator.NumberOutOfRange);
        }

        [TestCase("abc")]
        [TestCase("")]
        [TestCase("201")]
        public void It_reports_no_partition_count(string number)
        {
            Validate((PartitionRequestValidator.NumberParameter, number))
                .RequestedPartitionCount.Should()
                .BeNull();
        }

        [Test]
        public void It_suppresses_every_reserved_parameter_error()
        {
            Validate(
                (PartitionRequestValidator.NumberParameter, "abc"),
                ("pageToken", "anything"),
                ("pageSize", "5"),
                ("limit", "10"),
                ("offset", "3"),
                ("totalCount", "true")
            )
                .Errors.Should()
                .ContainSingle()
                .Which.Should()
                .Be(PartitionRequestValidator.NumberOutOfRange);
        }

        [Test]
        public void It_renders_the_configured_bounds_in_the_message()
        {
            PartitionRequestValidator
                .NumberOutOfRange.Should()
                .Be("Number of partitions must be between 1 and 200.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Number_Within_Its_Bounds : PartitionRequestValidatorTests
    {
        [TestCase(AppSettingsValidator.MinimumDefaultPartitionCount)]
        [TestCase(10)]
        [TestCase(AppSettingsValidator.MaximumDefaultPartitionCount)]
        public void It_is_accepted_and_carried_through(int number)
        {
            PartitionValidationResult result = Validate(
                (PartitionRequestValidator.NumberParameter, number.ToString())
            );

            result.Errors.Should().BeEmpty();
            result.RequestedPartitionCount.Should().Be(number);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_No_Number : PartitionRequestValidatorTests
    {
        [Test]
        public void It_is_accepted_with_no_requested_count()
        {
            PartitionValidationResult result = Validate();

            result.Errors.Should().BeEmpty();
            result.RequestedPartitionCount.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Reserved_Paging_Parameters : PartitionRequestValidatorTests
    {
        [Test]
        public void It_reports_every_one_of_them_in_canonical_order()
        {
            Validate(
                ("totalCount", "true"),
                ("offset", "3"),
                ("limit", "10"),
                ("pageSize", "5"),
                ("pageToken", "anything")
            )
                .Errors.Should()
                .Equal(
                    PartitionRequestValidator.UnsupportedParameter("pageToken"),
                    PartitionRequestValidator.UnsupportedParameter("pageSize"),
                    PartitionRequestValidator.UnsupportedParameter("limit"),
                    PartitionRequestValidator.UnsupportedParameter("offset"),
                    PartitionRequestValidator.UnsupportedParameter("totalCount")
                );
        }

        [TestCase("pageToken")]
        [TestCase("pageSize")]
        [TestCase("limit")]
        [TestCase("offset")]
        [TestCase("totalCount")]
        public void It_reports_a_single_reserved_parameter_on_its_own(string parameter)
        {
            Validate((parameter, "anything"))
                .Errors.Should()
                .ContainSingle()
                .Which.Should()
                .Be($"The '{parameter}' parameter is not supported by the partitions endpoint.");
        }

        [TestCase("pageToken", "!!!")]
        [TestCase("pageSize", "abc")]
        [TestCase("limit", "abc")]
        [TestCase("offset", "-1")]
        [TestCase("totalCount", "notabool")]
        public void It_does_not_parse_their_values(string parameter, string malformedValue)
        {
            Validate((parameter, malformedValue))
                .Errors.Should()
                .ContainSingle()
                .Which.Should()
                .Be(PartitionRequestValidator.UnsupportedParameter(parameter));
        }

        [Test]
        public void It_reports_them_alongside_a_valid_number()
        {
            Validate((PartitionRequestValidator.NumberParameter, "10"), ("limit", "10"))
                .Errors.Should()
                .ContainSingle()
                .Which.Should()
                .Be(PartitionRequestValidator.UnsupportedParameter("limit"));
        }

        [Test]
        public void It_withholds_the_partition_count_from_a_rejected_request()
        {
            Validate((PartitionRequestValidator.NumberParameter, "10"), ("limit", "10"))
                .RequestedPartitionCount.Should()
                .BeNull("a count from a rejected request must not be usable by mistake");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Ordinary_Filters : PartitionRequestValidatorTests
    {
        [Test]
        public void It_accepts_resource_property_and_change_version_filters()
        {
            PartitionValidationResult result = Validate(
                ("studentUniqueId", "123"),
                ("minChangeVersion", "1"),
                ("maxChangeVersion", "2")
            );

            result.Errors.Should().BeEmpty();
            result.RequestedPartitionCount.Should().BeNull();
        }

        [Test]
        public void It_accepts_them_alongside_a_valid_number()
        {
            PartitionValidationResult result = Validate(
                (PartitionRequestValidator.NumberParameter, "10"),
                ("schoolId", "255901001"),
                ("minChangeVersion", "1")
            );

            result.Errors.Should().BeEmpty();
            result.RequestedPartitionCount.Should().Be(10);
        }

        [Test]
        public void It_leaves_other_unknown_fields_to_the_unknown_query_field_rule()
        {
            Validate(("notAKnownField", "value")).Errors.Should().BeEmpty();
        }
    }
}
