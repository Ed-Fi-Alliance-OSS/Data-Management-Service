// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.Configuration;

namespace EdFi.DataManagementService.Core.Paging;

/// <summary>
/// The outcome of validating the query parameters of a partitions request.
/// </summary>
/// <param name="Errors">
/// The ordered validation errors, empty when the request is valid. Unlike cursor validation this may
/// carry several: unsupported partition parameters are independent mistakes, and a client that sent
/// three of them should learn about all three in one response rather than over three round trips.
/// </param>
/// <param name="RequestedPartitionCount">
/// The validated desired partition count, or null when the request omitted it or was rejected. The
/// partitions pipeline does not exist yet. A caller that comes to serve it must apply the configured
/// default to a null. It is deliberately null whenever <paramref name="Errors"/> is non-empty, so a
/// count from a rejected request cannot be used by mistake.
/// </param>
internal sealed record PartitionValidationResult(IReadOnlyList<string> Errors, int? RequestedPartitionCount);

/// <summary>
/// Validates the query parameters of a partitions request.
/// </summary>
/// <remarks>
/// Pure, and phase-gated separately from cursor validation. The count is validated first because it
/// is the only parameter that controls the partition calculation itself, while the reserved paging
/// parameters have no effect on it at all.
/// </remarks>
internal static class PartitionRequestValidator
{
    internal const string NumberParameter = "number";

    /// <summary>
    /// The paging parameters the partitions operation reserves, in the canonical order they are
    /// reported. They belong to GET-many, so a client that confused the two endpoints is told which
    /// parameter does not apply rather than being given an unknown-query-field answer. Spelled from
    /// the constants the cursor validator reads, so renaming one cannot leave the names that validator
    /// recognizes and the names this operation rejects disagreeing.
    /// </summary>
    internal static readonly string[] ReservedParameters =
    [
        CursorRequestValidator.PageTokenParameter,
        CursorRequestValidator.PageSizeParameter,
        CursorRequestValidator.LimitParameter,
        CursorRequestValidator.OffsetParameter,
        CursorRequestValidator.TotalCountParameter,
    ];

    internal static string NumberOutOfRange =>
        $"Number of partitions must be between {AppSettingsValidator.MinimumDefaultPartitionCount} and "
        + $"{AppSettingsValidator.MaximumDefaultPartitionCount}.";

    internal static string UnsupportedParameter(string parameter) =>
        $"The '{parameter}' parameter is not supported by the partitions endpoint.";

    /// <summary>
    /// Validates the partition parameters of a request.
    /// </summary>
    /// <param name="queryParameters">
    /// The request's query parameters, already canonicalized at the HTTP boundary.
    /// </param>
    internal static PartitionValidationResult Validate(IReadOnlyDictionary<string, string> queryParameters)
    {
        ArgumentNullException.ThrowIfNull(queryParameters);

        int? requestedPartitionCount = null;

        // Phase 1, count syntax and range. A present-but-blank value is a malformed count rather than
        // an absent one: a client that typed "number=" asked for a partition count, and the parameter
        // it typed should not be silently ignored. This phase suppresses the reserved-parameter phase,
        // because the count is the only parameter that controls the calculation. A client-supplied
        // count is bounded by the same constants that bound the configured default, which is what
        // keeps the accepted request range and the accepted configuration range from drifting apart.
        if (queryParameters.TryGetValue(NumberParameter, out string? numberValue))
        {
            if (
                !int.TryParse(numberValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                || number < AppSettingsValidator.MinimumDefaultPartitionCount
                || number > AppSettingsValidator.MaximumDefaultPartitionCount
            )
            {
                return new PartitionValidationResult([NumberOutOfRange], RequestedPartitionCount: null);
            }

            requestedPartitionCount = number;
        }

        // Phase 2, reserved paging parameters. Reported without parsing their values: the complaint is
        // that the parameter does not apply here at all, so whether its value is well formed is beside
        // the point. Resource-property filters and the change-version filters are not reserved and are
        // deliberately not reported. The partitions pipeline does not exist yet. Every other unknown
        // field is left for a caller that comes to serve it to answer with its own
        // unknown-query-field rule.
        string[] errors =
        [
            .. ReservedParameters.Where(queryParameters.ContainsKey).Select(UnsupportedParameter),
        ];

        return errors.Length == 0
            ? new PartitionValidationResult([], requestedPartitionCount)
            : new PartitionValidationResult(errors, RequestedPartitionCount: null);
    }
}
