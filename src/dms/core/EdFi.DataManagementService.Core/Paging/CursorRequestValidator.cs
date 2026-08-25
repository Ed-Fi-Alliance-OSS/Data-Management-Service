// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Paging;

/// <summary>
/// The outcome of validating the cursor paging parameters of a request.
/// </summary>
internal abstract record CursorValidationResult
{
    private CursorValidationResult() { }

    /// <summary>
    /// Neither cursor parameter was supplied, so the request pages traditionally and this validator
    /// has nothing to say about it.
    /// </summary>
    public sealed record NotCursorRequest : CursorValidationResult
    {
        internal static NotCursorRequest Instance { get; } = new();
    }

    /// <summary>
    /// The request is a cursor request and is rejected.
    /// </summary>
    /// <param name="Error">
    /// The one message the client is told. A cursor request never reports more than one.
    /// </param>
    public sealed record Invalid(string Error) : CursorValidationResult;

    /// <summary>
    /// The request is a well-formed cursor request.
    /// </summary>
    /// <param name="Paging">The decoded range and the page size the request will select with.</param>
    public sealed record Valid(CollectionPaging.Cursor Paging) : CursorValidationResult;
}

/// <summary>
/// Validates the cursor paging parameters of a GET-many request against the approved four-phase
/// precedence, returning exactly one error.
/// </summary>
/// <remarks>
/// Pure: it reads the supplied parameters and returns a result, never touching request state, so a
/// rejected request cannot leave partially applied paging behind. Which operations recognize these
/// parameters at all is the caller's decision, not this validator's.
/// </remarks>
internal static class CursorRequestValidator
{
    internal const string PageTokenParameter = "pageToken";
    internal const string PageSizeParameter = "pageSize";
    internal const string LimitParameter = "limit";
    internal const string OffsetParameter = "offset";
    internal const string TotalCountParameter = "totalCount";

    /// <summary>
    /// The parameters that select cursor paging, in the canonical order they are reported when an
    /// operation does not support them.
    /// </summary>
    /// <remarks>
    /// One definition, so the names an operation recognizes and the names it rejects cannot diverge.
    /// </remarks>
    internal static readonly string[] CursorParameters = [PageTokenParameter, PageSizeParameter];

    internal const string InvalidPageToken = "The page token provided was invalid.";

    internal const string OffsetWithPageToken =
        "Both offset and pageToken parameters were provided, but they support alternative paging "
        + "approaches and cannot be used together.";

    internal const string LimitWithPageToken =
        "Use pageSize instead of limit when using cursor paging with pageToken.";

    internal const string TotalCountWithPageToken =
        "The totalCount parameter cannot be set to true when using cursor paging with pageToken.";

    internal const string PageSizeWithOffset =
        "Use limit instead of pageSize when using limit/offset paging.";

    internal const string PageTokenRequired = "PageToken is required when pageSize is specified.";

    internal const string TotalCountNotBoolean = "TotalCount must be a boolean value.";

    internal static string PageSizeOutOfRange(int maximumPageSize) =>
        $"PageSize must be a value between 0 and {maximumPageSize}.";

    /// <summary>
    /// Validates the cursor parameters of a request.
    /// </summary>
    /// <param name="queryParameters">
    /// The request's query parameters, already canonicalized at the HTTP boundary. Lookups here are
    /// ordinal on the canonical spelling: matching case-insensitively a second time would duplicate
    /// the boundary's job and hide a canonicalization regression rather than fail a test.
    /// </param>
    /// <param name="maximumPageSize">The configured maximum page size.</param>
    internal static CursorValidationResult Validate(
        IReadOnlyDictionary<string, string> queryParameters,
        int maximumPageSize
    )
    {
        ArgumentNullException.ThrowIfNull(queryParameters);

        // Presence of the query key selects the cursor path, whatever its value. A client that sent
        // "pageSize=" meant to send a page size and should be told what is actually wrong with the
        // request, not have the parameter it typed silently ignored.
        bool hasPageToken = queryParameters.ContainsKey(PageTokenParameter);
        bool hasPageSize = queryParameters.ContainsKey(PageSizeParameter);

        if (!hasPageToken && !hasPageSize)
        {
            return CursorValidationResult.NotCursorRequest.Instance;
        }

        bool hasOffset = queryParameters.ContainsKey(OffsetParameter);
        bool hasLimit = queryParameters.ContainsKey(LimitParameter);
        bool hasTotalCount = queryParameters.TryGetValue(TotalCountParameter, out string? totalCountValue);

        if (!hasPageToken)
        {
            // Phase 2, required relationships. Reached only when pageSize is present, because that is
            // the only other parameter that selects this path, so one of these two rules always fires.
            return hasOffset && !hasLimit
                ? new CursorValidationResult.Invalid(PageSizeWithOffset)
                : new CursorValidationResult.Invalid(PageTokenRequired);
        }

        // Phase 0, token decode. An undecodable token makes every rule that reasons about a valid
        // token meaningless.
        if (
            !PageTokenCodec.TryDecode(queryParameters[PageTokenParameter], out CursorRange? range, out _)
            || range is null
        )
        {
            return new CursorValidationResult.Invalid(InvalidPageToken);
        }

        // Phase 1, mixed-mode conflicts. A parameter that should not have been sent at all makes the
        // individual parameters' ranges irrelevant.
        if (hasOffset)
        {
            return new CursorValidationResult.Invalid(OffsetWithPageToken);
        }

        if (hasLimit)
        {
            return new CursorValidationResult.Invalid(LimitWithPageToken);
        }

        if (
            hasTotalCount
            && bool.TryParse(totalCountValue, out bool requestsTotalCount)
            && requestsTotalCount
        )
        {
            return new CursorValidationResult.Invalid(TotalCountWithPageToken);
        }

        // Phase 3, syntax and range, in the canonical order pageSize, limit, offset, totalCount.
        //
        // The limit and offset rules of this phase are unreachable from the cursor path and are
        // therefore not written here. Reaching phase 3 requires phases 0 through 2 to pass. Phase 2
        // returns unconditionally whenever pageToken is absent, so phase 3 is only reached with a
        // present, valid pageToken; and phase 1 has already rejected any present limit or offset.
        // Traditional limit and offset parsing, and their existing messages, remain on the
        // traditional path.
        int pageSize = maximumPageSize;

        if (
            hasPageSize
            && (
                !int.TryParse(
                    queryParameters[PageSizeParameter],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out pageSize
                )
                || pageSize < 0
                || pageSize > maximumPageSize
            )
        )
        {
            return new CursorValidationResult.Invalid(PageSizeOutOfRange(maximumPageSize));
        }

        if (hasTotalCount && !bool.TryParse(totalCountValue, out _))
        {
            return new CursorValidationResult.Invalid(TotalCountNotBoolean);
        }

        return new CursorValidationResult.Valid(new CollectionPaging.Cursor(range, new PageSize(pageSize)));
    }
}
