// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using CsCheck;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Paging;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.Paging.CursorPagingPropertyScenarios;

namespace EdFi.DataManagementService.Core.Tests.Unit.Paging;

[TestFixture]
[Category("CursorPagingProperty")]
public class Given_Generated_Cursor_Requests
{
    private sealed record RequestSample(Bounds Bounds, int Maximum, int Fraction);

    [Test]
    public void It_preserves_token_bounds_and_validates_page_size_boundaries(
        [Values] PageOrderingMode mode,
        [Values("arbitrary", "inverted", "minimum", "maximum")] string shape
    )
    {
        var generator =
            from bounds in Generate(shape)
            from maximum in Gen.Int[1, 10000]
            from fraction in Gen.Int[0, 100]
            select new RequestSample(bounds, maximum, fraction);
        Check(
            generator,
            sample =>
            {
                var (bounds, maximum, fraction) = sample;
                foreach (var size in new[] { 0, maximum, maximum * fraction / 100 })
                {
                    var query = new Dictionary<string, string>
                    {
                        ["pageToken"] = Token(bounds, mode),
                        ["pageSize"] = Decimal(size),
                        ["totalCount"] = "false",
                    };
                    var before = query.ToArray();
                    AssertValid(query, maximum, mode, bounds, size);
                    query.Should().Equal(before);
                    // An unresolved change-version window leaves the window error to its own validator.
                    CursorRequestValidator
                        .Validate(query, maximum, null)
                        .Should()
                        .Be(
                            new CursorValidationResult.Valid(
                                new(new(bounds.Minimum, bounds.Maximum), new(size))
                            )
                        );
                    query.Remove("pageSize");
                    AssertValid(query, maximum, mode, bounds, maximum);
                }
                foreach (
                    var invalidSize in new[]
                    {
                        "-1",
                        Decimal((long)maximum + 1),
                        "2147483648",
                        "",
                        "1.5",
                        "word",
                    }
                )
                {
                    var query = new Dictionary<string, string>
                    {
                        ["pageToken"] = Token(bounds, mode),
                        ["pageSize"] = invalidSize,
                    };
                    CursorRequestValidator
                        .Validate(query, maximum, mode)
                        .Should()
                        .Be(
                            new CursorValidationResult.Invalid(
                                CursorRequestValidator.PageSizeOutOfRange(maximum)
                            )
                        );
                }
            },
            $"mode={mode}, shape={shape}, zero/interior/maximum/default and six invalid sizes"
        );
    }

    [Test]
    public void It_reports_the_first_applicable_conflict_for_generated_tokens(
        [Values] PageOrderingMode mode,
        [Values(
            "malformed",
            "mismatched",
            "offset",
            "limit",
            "total-count",
            "page-size",
            "total-count-syntax"
        )]
            string firstFailure
    )
    {
        Check(
            Generate(),
            bounds =>
            {
                // Start with simultaneous errors; peel off earlier phases according to the explicit table.
                var query = new Dictionary<string, string>
                {
                    ["pageToken"] = Token(bounds, mode),
                    ["offset"] = "invalid-offset",
                    ["limit"] = "invalid-limit",
                    ["totalCount"] = "true",
                    ["pageSize"] = "-1",
                };
                var expected = firstFailure switch
                {
                    "malformed" or "mismatched" => CursorRequestValidator.InvalidPageToken,
                    "offset" => CursorRequestValidator.OffsetWithPageToken,
                    "limit" => CursorRequestValidator.LimitWithPageToken,
                    "total-count" => CursorRequestValidator.TotalCountWithPageToken,
                    "page-size" => CursorRequestValidator.PageSizeOutOfRange(500),
                    "total-count-syntax" => CursorRequestValidator.TotalCountNotBoolean,
                    _ => throw new InvalidOperationException($"Unknown case: {firstFailure}"),
                };
                if (firstFailure == "malformed")
                {
                    query["pageToken"] += "!";
                }
                if (firstFailure == "mismatched")
                {
                    query["pageToken"] = Token(
                        bounds,
                        mode == PageOrderingMode.DocumentId
                            ? PageOrderingMode.ContentVersion
                            : PageOrderingMode.DocumentId
                    );
                }
                if (firstFailure is "limit" or "total-count" or "page-size" or "total-count-syntax")
                {
                    query.Remove("offset");
                }
                if (firstFailure is "total-count" or "page-size" or "total-count-syntax")
                {
                    query.Remove("limit");
                }
                if (firstFailure is "page-size" or "total-count-syntax")
                {
                    query["totalCount"] = "not-a-boolean";
                }
                if (firstFailure == "total-count-syntax")
                {
                    query["pageSize"] = "0";
                }
                var before = query.ToArray();
                CursorRequestValidator
                    .Validate(query, 500, mode)
                    .Should()
                    .Be(new CursorValidationResult.Invalid(expected));
                query.Should().Equal(before);
            },
            $"mode={mode}, first failure={firstFailure}"
        );
    }

    [Test]
    public void It_selects_traditional_or_missing_token_validation_by_parameter_presence(
        [Values] bool hasPageSize,
        [Values] bool hasLimit,
        [Values] bool hasOffset
    )
    {
        Check(
            Generate(),
            bounds =>
            {
                // The values do not change routing, even when they are blank or invalid.
                foreach (var value in new[] { "", "invalid", Decimal(bounds.Minimum) })
                {
                    var query = new Dictionary<string, string>();
                    if (hasPageSize)
                    {
                        query["pageSize"] = value;
                    }
                    if (hasLimit)
                    {
                        query["limit"] = value;
                    }
                    if (hasOffset)
                    {
                        query["offset"] = value;
                    }
                    var selectsCursor = hasPageSize && !hasLimit;
                    CursorRequestValidator.SelectsCursorPaging(query).Should().Be(selectsCursor);
                    var missingTokenError = hasOffset
                        ? CursorRequestValidator.PageSizeWithOffset
                        : CursorRequestValidator.PageTokenRequired;
                    CursorValidationResult expected = selectsCursor
                        ? new CursorValidationResult.Invalid(missingTokenError)
                        : CursorValidationResult.NotCursorRequest.Instance;
                    CursorRequestValidator
                        .Validate(query, 500, PageOrderingMode.DocumentId)
                        .Should()
                        .Be(expected);
                    query["pageToken"] = "";
                    CursorRequestValidator.SelectsCursorPaging(query).Should().BeTrue();
                    CursorRequestValidator
                        .Validate(query, 500, PageOrderingMode.DocumentId)
                        .Should()
                        .Be(new CursorValidationResult.Invalid(CursorRequestValidator.InvalidPageToken));
                }
            },
            $"pageSize={hasPageSize}, limit={hasLimit}, offset={hasOffset}"
        );
    }

    private static void AssertValid(
        Dictionary<string, string> query,
        int maximum,
        PageOrderingMode mode,
        Bounds bounds,
        int size
    )
    {
        CursorRequestValidator
            .Validate(query, maximum, mode)
            .Should()
            .Be(new CursorValidationResult.Valid(new(new(bounds.Minimum, bounds.Maximum), new(size))));
    }
}
