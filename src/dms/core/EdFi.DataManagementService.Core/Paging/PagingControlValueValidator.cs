// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Paging;

/// <summary>
/// The per-value rules for the five paging controls, shared by the operations that consume a control
/// and the operations that ignore it.
/// </summary>
/// <remarks>
/// An operation that ignores a paging control still answers a malformed value with 400, judged by the
/// same rule and message as an operation that consumes it. One definition keeps the two from drifting.
/// Only per-value rules live here: the rules that relate one control to another, such as pageToken with
/// offset, belong to cursor validation and apply only where the controls are consumed.
/// </remarks>
internal static class PagingControlValueValidator
{
    internal const string OffsetInvalid = "Offset must be a numeric value greater than or equal to 0.";

    internal static string LimitOutOfRange(int maximumPageSize) =>
        $"Limit must be omitted or set to a numeric value between 0 and {maximumPageSize}.";

    internal static bool TryParseOffset(string value, out int offset) =>
        int.TryParse(value, out offset) && offset >= 0;

    internal static bool TryParseLimit(string value, int maximumPageSize, out int limit) =>
        int.TryParse(value, out limit) && limit >= 0 && limit <= maximumPageSize;

    internal static bool TryParsePageSize(string value, int maximumPageSize, out int pageSize) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pageSize)
        && pageSize >= 0
        && pageSize <= maximumPageSize;

    /// <summary>
    /// Validates the values of the paging controls an operation ignores, returning one message per
    /// malformed value in the order <paramref name="ignoredControls" /> lists them.
    /// </summary>
    /// <remarks>
    /// A page token only has to decode. It is not compared with a page anchor, because an operation
    /// that ignores the token resolves no page for it to be replayed against.
    /// </remarks>
    internal static string[] ValidateIgnored(
        IReadOnlyDictionary<string, string> queryParameters,
        IReadOnlyList<string> ignoredControls,
        int maximumPageSize
    ) =>
        [
            .. ignoredControls
                .Where(queryParameters.ContainsKey)
                .Select(control => ErrorFor(control, queryParameters[control], maximumPageSize))
                .OfType<string>(),
        ];

    private static bool IsDecodablePageToken(string value) =>
        PageTokenCodec.TryDecode(value, out CursorRange? range, out _) && range is not null;

    private static string? ErrorFor(string control, string value, int maximumPageSize) =>
        control switch
        {
            CursorRequestValidator.PageTokenParameter => IsDecodablePageToken(value)
                ? null
                : CursorRequestValidator.InvalidPageToken,
            CursorRequestValidator.PageSizeParameter => TryParsePageSize(value, maximumPageSize, out _)
                ? null
                : CursorRequestValidator.PageSizeOutOfRange(maximumPageSize),
            CursorRequestValidator.LimitParameter => TryParseLimit(value, maximumPageSize, out _)
                ? null
                : LimitOutOfRange(maximumPageSize),
            CursorRequestValidator.OffsetParameter => TryParseOffset(value, out _) ? null : OffsetInvalid,
            CursorRequestValidator.TotalCountParameter => bool.TryParse(value, out _)
                ? null
                : CursorRequestValidator.TotalCountNotBoolean,
            _ => throw new ArgumentOutOfRangeException(nameof(control), control, "Not a paging control."),
        };
}
