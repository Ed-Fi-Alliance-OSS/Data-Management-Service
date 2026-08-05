// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Paging;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Paging;

[TestFixture]
[Parallelizable]
public class CursorRequestValidatorTests
{
    private const int MaximumPageSize = 500;

    /// <summary>
    /// A token the codec actually produces, so decoding is exercised rather than stubbed.
    /// </summary>
    private static readonly string ValidToken = PageTokenCodec.Encode(new CursorRange(1, 100));

    private const string UndecodableToken = "!!!";

    private static CursorValidationResult Validate(params (string Key, string Value)[] queryParameters) =>
        CursorRequestValidator.Validate(
            queryParameters.ToDictionary(
                static parameter => parameter.Key,
                static parameter => parameter.Value,
                StringComparer.Ordinal
            ),
            MaximumPageSize
        );

    private static string ErrorFrom(params (string Key, string Value)[] queryParameters) =>
        Validate(queryParameters).Should().BeOfType<CursorValidationResult.Invalid>().Subject.Error;

    [TestFixture]
    [Parallelizable]
    public class Given_Neither_Cursor_Parameter_Is_Present : CursorRequestValidatorTests
    {
        [Test]
        public void It_is_not_a_cursor_request_when_no_parameters_are_supplied()
        {
            Validate().Should().BeOfType<CursorValidationResult.NotCursorRequest>();
        }

        [Test]
        public void It_is_not_a_cursor_request_when_only_traditional_parameters_are_supplied()
        {
            Validate(("limit", "10"), ("offset", "20"), ("totalCount", "true"))
                .Should()
                .BeOfType<CursorValidationResult.NotCursorRequest>();
        }
    }

    /// <summary>
    /// Every row of the design's worked precedence table. Each returns exactly one message.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Worked_Precedence_Row : CursorRequestValidatorTests
    {
        [Test]
        public void Row_01_offset_with_a_valid_token_reports_the_mixed_mode_conflict()
        {
            ErrorFrom(("pageToken", ValidToken), ("offset", "-1"))
                .Should()
                .Be(CursorRequestValidator.OffsetWithPageToken);
        }

        [Test]
        public void Row_02_limit_with_a_valid_token_reports_the_mixed_mode_conflict()
        {
            ErrorFrom(("pageToken", ValidToken), ("limit", "99999"))
                .Should()
                .Be(CursorRequestValidator.LimitWithPageToken);
        }

        [Test]
        public void Row_03_page_size_without_a_token_requires_a_token()
        {
            ErrorFrom(("pageSize", "99999")).Should().Be(CursorRequestValidator.PageTokenRequired);
        }

        [Test]
        public void Row_04_blank_page_size_without_a_token_requires_a_token()
        {
            ErrorFrom(("pageSize", "")).Should().Be(CursorRequestValidator.PageTokenRequired);
        }

        [Test]
        public void Row_05_an_undecodable_token_suppresses_the_offset_conflict()
        {
            ErrorFrom(("pageToken", UndecodableToken), ("offset", "5"))
                .Should()
                .Be(CursorRequestValidator.InvalidPageToken);
        }

        [Test]
        public void Row_06_an_undecodable_token_suppresses_the_limit_conflict()
        {
            ErrorFrom(("pageToken", UndecodableToken), ("limit", "10"))
                .Should()
                .Be(CursorRequestValidator.InvalidPageToken);
        }

        [Test]
        public void Row_07_page_size_with_limit_and_no_token_requires_a_token()
        {
            ErrorFrom(("pageSize", "5"), ("limit", "10"))
                .Should()
                .Be(CursorRequestValidator.PageTokenRequired);
        }

        [Test]
        public void Row_08_page_size_with_total_count_and_no_token_requires_a_token()
        {
            ErrorFrom(("pageSize", "5"), ("totalCount", "true"))
                .Should()
                .Be(CursorRequestValidator.PageTokenRequired);
        }

        [Test]
        public void Row_09_page_size_with_offset_and_no_limit_reports_the_wrong_paging_mode()
        {
            ErrorFrom(("pageSize", "5"), ("offset", "3"), ("totalCount", "true"))
                .Should()
                .Be(CursorRequestValidator.PageSizeWithOffset);
        }

        [Test]
        public void Row_10_a_negative_page_size_is_out_of_range()
        {
            ErrorFrom(("pageToken", ValidToken), ("pageSize", "-1"))
                .Should()
                .Be(CursorRequestValidator.PageSizeOutOfRange(MaximumPageSize));
        }

        [Test]
        public void Row_11_a_non_numeric_page_size_is_out_of_range()
        {
            ErrorFrom(("pageToken", ValidToken), ("pageSize", "abc"))
                .Should()
                .Be(CursorRequestValidator.PageSizeOutOfRange(MaximumPageSize));
        }

        [Test]
        public void Row_12_limit_is_reported_ahead_of_a_well_formed_page_size()
        {
            ErrorFrom(("pageToken", ValidToken), ("limit", "10"), ("pageSize", "5"))
                .Should()
                .Be(CursorRequestValidator.LimitWithPageToken);
        }

        [Test]
        public void Row_13_total_count_true_with_a_valid_token_is_rejected()
        {
            ErrorFrom(("pageToken", ValidToken), ("totalCount", "true"))
                .Should()
                .Be(CursorRequestValidator.TotalCountWithPageToken);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_Several_Faults : CursorRequestValidatorTests
    {
        [Test]
        public void It_reports_the_token_failure_ahead_of_every_other_fault()
        {
            ErrorFrom(
                    ("pageToken", UndecodableToken),
                    ("pageSize", "abc"),
                    ("limit", "10"),
                    ("offset", "-1"),
                    ("totalCount", "true")
                )
                .Should()
                .Be(CursorRequestValidator.InvalidPageToken);
        }

        [Test]
        public void It_reports_the_offset_conflict_ahead_of_the_limit_conflict()
        {
            ErrorFrom(("pageToken", ValidToken), ("offset", "1"), ("limit", "10"), ("totalCount", "true"))
                .Should()
                .Be(CursorRequestValidator.OffsetWithPageToken);
        }

        [Test]
        public void It_reports_the_limit_conflict_ahead_of_the_total_count_conflict()
        {
            ErrorFrom(("pageToken", ValidToken), ("limit", "10"), ("totalCount", "true"))
                .Should()
                .Be(CursorRequestValidator.LimitWithPageToken);
        }

        [Test]
        public void It_reports_the_page_size_range_ahead_of_the_total_count_syntax()
        {
            ErrorFrom(("pageToken", ValidToken), ("pageSize", "abc"), ("totalCount", "notabool"))
                .Should()
                .Be(CursorRequestValidator.PageSizeOutOfRange(MaximumPageSize));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Present_But_Blank_Page_Token : CursorRequestValidatorTests
    {
        [Test]
        public void It_reports_the_token_as_invalid_rather_than_absent()
        {
            ErrorFrom(("pageToken", "")).Should().Be(CursorRequestValidator.InvalidPageToken);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Total_Count_Value_That_Is_Not_True : CursorRequestValidatorTests
    {
        [Test]
        public void It_rejects_a_non_boolean_value_in_the_syntax_phase()
        {
            ErrorFrom(("pageToken", ValidToken), ("totalCount", "notabool"))
                .Should()
                .Be(CursorRequestValidator.TotalCountNotBoolean);
        }

        [TestCase("TRUE")]
        [TestCase("True")]
        public void It_treats_case_variant_true_as_the_mixed_mode_conflict(string totalCount)
        {
            ErrorFrom(("pageToken", ValidToken), ("totalCount", totalCount))
                .Should()
                .Be(CursorRequestValidator.TotalCountWithPageToken);
        }

        [TestCase("false")]
        [TestCase("False")]
        public void It_accepts_an_explicit_false(string totalCount)
        {
            Validate(("pageToken", ValidToken), ("totalCount", totalCount))
                .Should()
                .BeOfType<CursorValidationResult.Valid>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Cursor_Request : CursorRequestValidatorTests
    {
        private static CollectionPaging.Cursor PagingFrom(
            params (string Key, string Value)[] queryParameters
        ) => Validate(queryParameters).Should().BeOfType<CursorValidationResult.Valid>().Subject.Paging;

        [Test]
        public void It_defaults_the_page_size_to_the_configured_maximum_when_omitted()
        {
            PagingFrom(("pageToken", ValidToken)).PageSize.Value.Should().Be(MaximumPageSize);
        }

        [Test]
        public void It_carries_the_decoded_range()
        {
            PagingFrom(("pageToken", ValidToken)).Range.Should().Be(new CursorRange(1, 100));
        }

        [Test]
        public void It_never_requests_a_total_count()
        {
            PagingFrom(("pageToken", ValidToken)).IncludesTotalCount.Should().BeFalse();
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(MaximumPageSize)]
        public void It_accepts_a_page_size_within_the_inclusive_bounds(int pageSize)
        {
            PagingFrom(("pageToken", ValidToken), ("pageSize", pageSize.ToString()))
                .PageSize.Value.Should()
                .Be(pageSize);
        }

        [Test]
        public void It_rejects_a_page_size_one_above_the_maximum()
        {
            ErrorFrom(("pageToken", ValidToken), ("pageSize", (MaximumPageSize + 1).ToString()))
                .Should()
                .Be(CursorRequestValidator.PageSizeOutOfRange(MaximumPageSize));
        }

        [Test]
        public void It_rejects_a_present_but_blank_page_size()
        {
            ErrorFrom(("pageToken", ValidToken), ("pageSize", ""))
                .Should()
                .Be(CursorRequestValidator.PageSizeOutOfRange(MaximumPageSize));
        }

        [Test]
        public void It_leaves_change_version_filters_to_their_own_validator()
        {
            Validate(("pageToken", ValidToken), ("minChangeVersion", "1"), ("maxChangeVersion", "2"))
                .Should()
                .BeOfType<CursorValidationResult.Valid>();
        }

        [Test]
        public void It_accepts_a_range_that_is_unbounded_above()
        {
            PagingFrom(("pageToken", PageTokenCodec.Encode(CursorRange.From(42))))
                .Range.Should()
                .Be(new CursorRange(42, long.MaxValue));
        }
    }
}
