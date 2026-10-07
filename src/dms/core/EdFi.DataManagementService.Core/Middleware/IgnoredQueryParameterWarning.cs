// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Globalization;
using System.Text;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Validation;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Reports the query parameters an operation ignored, in the <c>X-EdFi-Warning</c> response header and
/// one Debug log event, under the contract in <c>reference/adr-unknown-query-parameters-DMS-1589.md</c>.
/// </summary>
/// <remarks>
/// One rendering serves both sinks, so the header and the log cannot disagree about what was ignored,
/// and there is no second sanitizer to drift. The rendering is printable ASCII by construction, which is
/// what makes it safe as a header value; a log sanitizer keeps non-ASCII text and punctuation and so is
/// not used here.
/// </remarks>
internal static class IgnoredQueryParameterWarning
{
    internal const string HeaderName = "X-EdFi-Warning";

    internal const string HeaderPrefix = "Ignored query parameters: ";

    internal const int MaximumListedNames = 10;

    internal const int MaximumEncodedNameLength = 64;

    internal const string EmptyNameMarker = "(empty)";

    internal const string TruncatedNameMarker = "(truncated)";

    private const string Separator = ", ";

    /// <summary>
    /// The query parameters an operation does not consume, in request order: every name that is not one
    /// the operation owns or, where it filters, a query field.
    /// </summary>
    /// <param name="ordinalOwnedNames">
    /// The owned names matched case-sensitively, the way the operation parses them. A name the
    /// operation rejects by name belongs here, because a rejected name is not reported as ignored.
    /// </param>
    /// <param name="ignoreCaseOwnedNames">The owned names matched case-insensitively.</param>
    /// <param name="queryFields">
    /// The resource's query fields, or <c>null</c> when the operation does not filter. A name is a
    /// consumed filter only where it would be matched as one, so an owned name is never one. Read only
    /// when a name has to be matched against it, so a request whose names are all owned never touches
    /// the resource's query field mapping before validation answers.
    /// </param>
    internal static string[] IgnoredNames(
        IReadOnlyDictionary<string, string> queryParameters,
        IReadOnlyList<string> ordinalOwnedNames,
        IReadOnlyList<string> ignoreCaseOwnedNames,
        Lazy<QueryField[]>? queryFields
    )
    {
        bool IsConsumed(string name) =>
            ordinalOwnedNames.Contains(name, StringComparer.Ordinal)
            || ignoreCaseOwnedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || (
                queryFields is not null
                && ResourceQueryFilterValidator.MatchesQueryField(name, queryFields.Value)
            );

        return [.. queryParameters.Keys.Where(name => !IsConsumed(name))];
    }

    /// <summary>
    /// Runs the rest of the step, then adds the warning to whatever response the request ended with:
    /// the step's own rejection or the response a later step produced. A request that ignored nothing
    /// is left exactly as it was, and nothing is logged for it.
    /// </summary>
    /// <param name="ignoredNames">
    /// The names the operation ignored, in request order. Decided before the step can answer, so every
    /// response the step produces carries the warning.
    /// </param>
    internal static async Task ReportAround(
        RequestInfo requestInfo,
        string[] ignoredNames,
        ILogger logger,
        Func<Task> validateAndContinue
    )
    {
        if (ignoredNames.Length == 0)
        {
            await validateAndContinue();
            return;
        }

        string renderedNames = RenderNames(ignoredNames);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "Ignored {IgnoredQueryParameterCount} query parameter(s): {IgnoredQueryParameters} - {TraceId}",
                ignoredNames.Length,
                renderedNames,
                requestInfo.FrontendRequest.TraceId.Value
            );
        }

        await validateAndContinue();

        // A pipeline that ended without assigning a response leaves the shared sentinel in place.
        // Copying it would turn "no response" into a response, so it is left alone.
        IFrontendResponse response = requestInfo.FrontendResponse;
        if (ReferenceEquals(response, No.FrontendResponse))
        {
            return;
        }

        // Copied rather than mutated: a response's header dictionary is not this step's to change, and
        // may be shared with another response.
        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: response.StatusCode,
            Body: response.Body,
            Headers: new Dictionary<string, string>(response.Headers, response.Headers.Comparer)
            {
                [HeaderName] = HeaderPrefix + renderedNames,
            },
            LocationHeaderPath: response.LocationHeaderPath,
            ContentType: response.ContentType
        );
    }

    /// <summary>
    /// Renders the ignored names as the list that follows the header prefix: at most
    /// <see cref="MaximumListedNames"/> names, then a count of the rest.
    /// </summary>
    internal static string RenderNames(IReadOnlyList<string> ignoredNames)
    {
        StringBuilder builder = new();
        int listedCount = Math.Min(ignoredNames.Count, MaximumListedNames);

        for (int index = 0; index < listedCount; index++)
        {
            if (index > 0)
            {
                builder.Append(Separator);
            }

            AppendName(builder, ignoredNames[index]);
        }

        if (ignoredNames.Count > listedCount)
        {
            builder
                .Append(Separator)
                .Append("(and ")
                .Append((ignoredNames.Count - listedCount).ToString(CultureInfo.InvariantCulture))
                .Append(" more)");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Appends one name: control and format characters, the line and paragraph separators, and unpaired
    /// surrogates removed; every other character outside the RFC 3986 unreserved set percent-encoded as
    /// UTF-8; and the encoded form stopped at <see cref="MaximumEncodedNameLength"/> characters.
    /// </summary>
    /// <remarks>
    /// Encoded one code point at a time and stopped at the budget, so a long name is never encoded in
    /// full only to be cut, and a cut never splits a percent triplet or a multi-byte character.
    /// Parentheses, commas and spaces are always encoded, so the markers and the separator cannot be
    /// read as part of a name.
    /// </remarks>
    private static void AppendName(StringBuilder builder, string name)
    {
        Span<byte> utf8 = stackalloc byte[4];
        int encodedLength = 0;
        int index = 0;

        while (index < name.Length)
        {
            if (
                Rune.DecodeFromUtf16(name.AsSpan(index), out Rune rune, out int consumed)
                != OperationStatus.Done
            )
            {
                // An unpaired surrogate is not a character, so it is removed rather than encoded. Both
                // failing statuses consume exactly one code unit.
                index += consumed;
                continue;
            }

            index += consumed;

            if (IsRemoved(rune))
            {
                continue;
            }

            int byteCount = rune.EncodeToUtf8(utf8);
            int runeLength = IsUnreserved(rune) ? 1 : byteCount * 3;

            if (encodedLength + runeLength > MaximumEncodedNameLength)
            {
                builder.Append(TruncatedNameMarker);
                return;
            }

            if (runeLength == 1)
            {
                builder.Append((char)rune.Value);
            }
            else
            {
                foreach (byte b in utf8[..byteCount])
                {
                    builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }

            encodedLength += runeLength;
        }

        if (encodedLength == 0)
        {
            builder.Append(EmptyNameMarker);
        }
    }

    private static bool IsRemoved(Rune rune) =>
        Rune.IsControl(rune)
        || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format
        || rune.Value is 0x2028 or 0x2029;

    private static bool IsUnreserved(Rune rune) =>
        rune.Value
            is (>= 'A' and <= 'Z')
                or (>= 'a' and <= 'z')
                or (>= '0' and <= '9')
                or '-'
                or '.'
                or '_'
                or '~';
}
