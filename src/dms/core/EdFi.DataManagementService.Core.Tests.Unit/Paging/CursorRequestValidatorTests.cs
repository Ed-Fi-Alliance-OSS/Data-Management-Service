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
    private static readonly string ValidToken = PageTokenCodec.Encode(
        new CursorRange(1, 100),
        PageOrderingMode.DocumentId
    );

    /// <summary>
    /// A <c>ContentVersion</c>-anchored token over the same bounds, so a marker comparison cannot pass
    /// or fail for any reason other than the marker.
    /// </summary>
    private static readonly string ContentVersionToken = PageTokenCodec.Encode(
        new CursorRange(1, 100),
        PageOrderingMode.ContentVersion
    );

    private const string UndecodableToken = "!!!";

    /// <summary>
    /// The overwhelming majority of the cursor rules are anchor-independent, so they are exercised
    /// against the <c>DocumentId</c> anchor an unwindowed request resolves.
    /// </summary>
    private static CursorValidationResult Validate(params (string Key, string Value)[] queryParameters) =>
        ValidateFor(PageOrderingMode.DocumentId, queryParameters);

    private static CursorValidationResult ValidateFor(
        PageOrderingMode orderingMode,
        params (string Key, string Value)[] queryParameters
    ) =>
        CursorRequestValidator.Validate(
            queryParameters.ToDictionary(
                static parameter => parameter.Key,
                static parameter => parameter.Value,
                StringComparer.Ordinal
            ),
            MaximumPageSize,
            orderingMode
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
    /// The exact wording of every cursor rule is part of the API contract, so each message is pinned
    /// to its literal text here. Asserting against the constants themselves would only prove the
    /// validator is self-consistent and would let a reworded message through unnoticed.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Contractual_Messages : CursorRequestValidatorTests
    {
        [Test]
        public void It_words_the_invalid_page_token_message()
        {
            CursorRequestValidator.InvalidPageToken.Should().Be("The page token provided was invalid.");
        }

        [Test]
        public void It_words_the_offset_conflict_message()
        {
            CursorRequestValidator
                .OffsetWithPageToken.Should()
                .Be(
                    "Both offset and pageToken parameters were provided, but they support alternative paging approaches and cannot be used together."
                );
        }

        [Test]
        public void It_words_the_limit_conflict_message()
        {
            CursorRequestValidator
                .LimitWithPageToken.Should()
                .Be("Use pageSize instead of limit when using cursor paging with pageToken.");
        }

        [Test]
        public void It_words_the_total_count_conflict_message()
        {
            CursorRequestValidator
                .TotalCountWithPageToken.Should()
                .Be(
                    "The totalCount parameter cannot be set to true when using cursor paging with pageToken."
                );
        }

        [Test]
        public void It_words_the_page_size_with_offset_message()
        {
            CursorRequestValidator
                .PageSizeWithOffset.Should()
                .Be("Use limit instead of pageSize when using limit/offset paging.");
        }

        [Test]
        public void It_words_the_page_token_required_message()
        {
            CursorRequestValidator
                .PageTokenRequired.Should()
                .Be("PageToken is required when pageSize is specified.");
        }

        [Test]
        public void It_words_the_non_boolean_total_count_message()
        {
            CursorRequestValidator.TotalCountNotBoolean.Should().Be("TotalCount must be a boolean value.");
        }

        [Test]
        public void It_renders_the_configured_maximum_in_the_page_size_range_message()
        {
            CursorRequestValidator
                .PageSizeOutOfRange(MaximumPageSize)
                .Should()
                .Be("PageSize must be a value between 0 and 500.");
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

    /// <summary>
    /// The wrong-paging-mode message belongs to a request that supplied offset and no limit. A
    /// request that supplied both traditional parameters alongside pageSize is missing its page
    /// token, not using the wrong page-size parameter, so the required-relationship rule answers it.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_Page_Size_With_Both_Offset_And_Limit : CursorRequestValidatorTests
    {
        [Test]
        public void It_requires_a_token_rather_than_reporting_the_wrong_paging_mode()
        {
            ErrorFrom(("pageSize", "5"), ("offset", "3"), ("limit", "10"))
                .Should()
                .Be(CursorRequestValidator.PageTokenRequired);
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

    /// <summary>
    /// A token carries the anchor it was issued for; the request resolves its own from its
    /// change-version window. The two have to agree, because a token stores no filters and its bounds
    /// are only interpretable in the units of one column. Both directions answer the same standard
    /// invalid-token message: a token is opaque, so neither direction tells the client anything it
    /// could act on beyond starting the walk over.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Token_Anchored_Differently_From_The_Request : CursorRequestValidatorTests
    {
        private static string ErrorFor(
            PageOrderingMode orderingMode,
            params (string Key, string Value)[] queryParameters
        ) =>
            ValidateFor(orderingMode, queryParameters)
                .Should()
                .BeOfType<CursorValidationResult.Invalid>()
                .Subject.Error;

        [Test]
        public void It_rejects_a_content_version_token_under_a_document_id_anchor()
        {
            ErrorFor(PageOrderingMode.DocumentId, ("pageToken", ContentVersionToken))
                .Should()
                .Be(CursorRequestValidator.InvalidPageToken);
        }

        [Test]
        public void It_rejects_a_document_id_token_under_a_content_version_anchor()
        {
            ErrorFor(PageOrderingMode.ContentVersion, ("pageToken", ValidToken))
                .Should()
                .Be(CursorRequestValidator.InvalidPageToken);
        }

        [Test]
        public void It_accepts_a_document_id_token_under_a_document_id_anchor()
        {
            ValidateFor(PageOrderingMode.DocumentId, ("pageToken", ValidToken))
                .Should()
                .BeOfType<CursorValidationResult.Valid>()
                .Subject.Paging.Range.Should()
                .Be(new CursorRange(1, 100));
        }

        [Test]
        public void It_accepts_a_content_version_token_under_a_content_version_anchor()
        {
            ValidateFor(PageOrderingMode.ContentVersion, ("pageToken", ContentVersionToken))
                .Should()
                .BeOfType<CursorValidationResult.Valid>()
                .Subject.Paging.Range.Should()
                .Be(new CursorRange(1, 100));
        }

        /// <summary>
        /// The comparison belongs to phase 0, so it answers ahead of every conflict a token that
        /// decoded cleanly would otherwise raise — the same standing an undecodable token has.
        /// </summary>
        [Test]
        public void It_reports_the_anchor_mismatch_ahead_of_every_other_fault()
        {
            ErrorFor(
                    PageOrderingMode.ContentVersion,
                    ("pageToken", ValidToken),
                    ("pageSize", "abc"),
                    ("offset", "-1"),
                    ("limit", "10"),
                    ("totalCount", "true")
                )
                .Should()
                .Be(CursorRequestValidator.InvalidPageToken);
        }

        /// <summary>
        /// The anchor arrives as a parameter, already resolved — and that resolution accounts for the
        /// page-ordering kill switch. A deployment running with legacy ordering therefore keeps
        /// accepting the <c>d</c>-marked tokens it issues, even for a windowed request, instead of
        /// breaking every walk mid-flight.
        /// </summary>
        [Test]
        public void It_accepts_a_document_id_token_for_a_windowed_request_under_legacy_ordering()
        {
            ValidateFor(PageOrderingMode.DocumentId, ("pageToken", ValidToken), ("maxChangeVersion", "200"))
                .Should()
                .BeOfType<CursorValidationResult.Valid>();
        }

        /// <summary>
        /// The mirror of the case above: under legacy ordering a windowed request resolves
        /// <c>DocumentId</c>, so a <c>c</c>-marked token issued before the switch was turned on is no
        /// longer replayable and says so.
        /// </summary>
        [Test]
        public void It_rejects_a_content_version_token_for_a_windowed_request_under_legacy_ordering()
        {
            ErrorFor(
                    PageOrderingMode.DocumentId,
                    ("pageToken", ContentVersionToken),
                    ("maxChangeVersion", "200")
                )
                .Should()
                .Be(CursorRequestValidator.InvalidPageToken);
        }

        /// <summary>
        /// An undecodable token is rejected without any anchor comparison, so the mismatch rule cannot
        /// be what a malformed token is reported by.
        /// </summary>
        [TestCase(PageOrderingMode.DocumentId)]
        [TestCase(PageOrderingMode.ContentVersion)]
        public void It_rejects_an_undecodable_token_under_either_anchor(PageOrderingMode orderingMode)
        {
            ErrorFor(orderingMode, ("pageToken", UndecodableToken))
                .Should()
                .Be(CursorRequestValidator.InvalidPageToken);
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

        /// <summary>
        /// The validator never reads the change-version parameters itself: the anchor they imply is
        /// resolved upstream and arrives as a parameter, so these two names are nothing to this step
        /// but query string it does not own.
        /// </summary>
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
            PagingFrom(
                ("pageToken", PageTokenCodec.Encode(CursorRange.From(42), PageOrderingMode.DocumentId))
            )
                .Range.Should()
                .Be(new CursorRange(42, long.MaxValue));
        }
    }
}
