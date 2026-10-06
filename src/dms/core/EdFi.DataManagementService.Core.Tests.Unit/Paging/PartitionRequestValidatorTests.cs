// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Paging;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Paging;

[TestFixture]
[Parallelizable]
public class PartitionRequestValidatorTests
{
    private const int MaximumPageSize = 500;

    private static readonly string ValidToken = PageTokenCodec.Encode(
        new CursorRange(1, 100),
        PageOrderingMode.DocumentId
    );

    private static PartitionValidationResult Validate(params (string Key, string Value)[] queryParameters) =>
        PartitionRequestValidator.Validate(
            queryParameters.ToDictionary(
                static parameter => parameter.Key,
                static parameter => parameter.Value,
                StringComparer.Ordinal
            ),
            MaximumPageSize
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
                ("pageToken", "!!!"),
                ("pageSize", "abc"),
                ("limit", "abc"),
                ("offset", "-1"),
                ("totalCount", "notabool")
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
    public class Given_Well_Formed_Reserved_Paging_Parameters : PartitionRequestValidatorTests
    {
        [Test]
        public void It_accepts_them_because_they_are_ignored()
        {
            PartitionValidationResult result = Validate(
                (PartitionRequestValidator.NumberParameter, "10"),
                ("pageToken", ValidToken),
                ("pageSize", "5"),
                ("limit", "10"),
                ("offset", "3"),
                ("totalCount", "true")
            );

            result.Errors.Should().BeEmpty();
            result.RequestedPartitionCount.Should().Be(10);
        }

        [Test]
        public void It_accepts_a_page_token_without_comparing_it_with_a_page_anchor()
        {
            string contentVersionToken = PageTokenCodec.Encode(
                new CursorRange(1, 100),
                PageOrderingMode.ContentVersion
            );

            Validate(("pageToken", contentVersionToken)).Errors.Should().BeEmpty();
        }

        [Test]
        public void It_accepts_a_page_size_without_a_page_token()
        {
            Validate(("pageSize", "5")).Errors.Should().BeEmpty();
        }

        [Test]
        public void It_accepts_combinations_only_a_cursor_walk_rejects()
        {
            Validate(("pageToken", ValidToken), ("offset", "3"), ("limit", "10")).Errors.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Malformed_Reserved_Paging_Parameters : PartitionRequestValidatorTests
    {
        [Test]
        public void It_reports_every_one_of_them_in_canonical_order()
        {
            Validate(
                ("totalCount", "notabool"),
                ("offset", "-1"),
                ("limit", "abc"),
                ("pageSize", "abc"),
                ("pageToken", "!!!")
            )
                .Errors.Should()
                .Equal(
                    "The page token provided was invalid.",
                    "PageSize must be a value between 0 and 500.",
                    "Limit must be omitted or set to a numeric value between 0 and 500.",
                    "Offset must be a numeric value greater than or equal to 0.",
                    "TotalCount must be a boolean value."
                );
        }

        [TestCase("limit", "501")]
        [TestCase("limit", "-1")]
        [TestCase("pageSize", "501")]
        [TestCase("offset", "abc")]
        [TestCase("totalCount", "")]
        public void It_applies_the_range_rule_get_many_applies(string parameter, string value)
        {
            Validate((parameter, value)).Errors.Should().ContainSingle();
        }

        [Test]
        public void It_withholds_the_partition_count_from_a_rejected_request()
        {
            Validate((PartitionRequestValidator.NumberParameter, "10"), ("limit", "abc"))
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
        public void It_leaves_other_unknown_fields_to_the_caller()
        {
            Validate(("notAKnownField", "value")).Errors.Should().BeEmpty();
        }
    }
}
