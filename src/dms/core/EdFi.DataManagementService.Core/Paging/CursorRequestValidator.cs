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
    /// <param name="orderingMode">
    /// The anchor this request resolved from its change-version window and the data store serving it,
    /// or <see langword="null"/> when that window did not parse and the request therefore resolves no
    /// anchor. A token carries the anchor it was issued for, and replaying it under a request that
    /// resolves a different one would read its bounds against the wrong column, so the two have to
    /// agree. The resolved value already accounts for the legacy ordering kill switch, which is what
    /// keeps tokens issued under that setting replayable instead of failing mid-walk.
    /// </param>
    internal static CursorValidationResult Validate(
        IReadOnlyDictionary<string, string> queryParameters,
        int maximumPageSize,
        PageOrderingMode? orderingMode
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
            !PageTokenCodec.TryDecode(
                queryParameters[PageTokenParameter],
                out CursorRange? range,
                out PageOrderingMode tokenOrderingMode
            ) || range is null
        )
        {
            return new CursorValidationResult.Invalid(InvalidPageToken);
        }

        // Still phase 0: a token whose anchor disagrees with the one this request resolved decodes
        // cleanly but names bounds in the wrong units, which makes it no more replayable than a
        // malformed one. The window is not the only way the two can disagree: the anchor is resolved
        // from the window and the data store serving the request, so a min-only token also stops
        // matching when the request changes data source. The same answer in every direction - a
        // windowed token replayed without the window, an unwindowed token replayed with one, and a
        // min-only token replayed against a different source - because a token is opaque and none of
        // them tells the client anything it could act on beyond "start over".
        //
        // A request whose window did not parse resolves no anchor, so there is nothing to disagree
        // with and this comparison is skipped. Reporting a perfectly replayable token as invalid
        // because of a typo in maxChangeVersion would name the one piece of state the client cannot
        // rebuild, and the natural response - discard the token and restart the walk - is the
        // expensive one. The window's own fault is reported instead, and the token survives the fix.
        // Only the comparison is skipped: an undecodable token is still rejected above, because that
        // fault is real whatever the window says.
        if (orderingMode is { } resolvedOrderingMode && tokenOrderingMode != resolvedOrderingMode)
        {
            return new CursorValidationResult.Invalid(InvalidPageToken);
        }

        // The decoded anchor is deliberately not carried past here. Once the check above passes it
        // equals the request-level anchor by construction, and that is what the SQL compiler and the
        // token emitter read; a second copy on the paging record would be one more defaultable value.
        // When the check was skipped there is no request-level anchor to equal, but the caller is
        // about to reject the request for its window, so nothing downstream reads either one.

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
