// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.ChangeQueries;

/// <summary>
/// Shared validation for the minChangeVersion / maxChangeVersion query parameters
/// used by live resource and descriptor GET-many requests and, in later stories,
/// the /deletes and /keyChanges endpoints. Parses each bound as a long greater than
/// or equal to 0 and enforces min &lt;= max when both are supplied. Present-but-empty
/// or whitespace-only values are deliberately treated as absent rather than invalid:
/// ODS binds these parameters as nullable longs, and ASP.NET model binding converts
/// empty and whitespace query values to null before any parse is attempted, so ODS
/// never returns a validation error for them. Parameter lookup
/// is case-insensitive so all callers behave the same regardless of client casing.
/// </summary>
internal static class ChangeVersionParameterValidator
{
    public const string MinChangeVersion = "minChangeVersion";
    public const string MaxChangeVersion = "maxChangeVersion";

    private const string MinError = "MinChangeVersion must be a numeric value greater than or equal to 0.";
    private const string MaxError = "MaxChangeVersion must be a numeric value greater than or equal to 0.";
    private const string InvertedError = "MinChangeVersion must be less than or equal to MaxChangeVersion.";

    /// <summary>
    /// The query parameters this validator owns. Callers exclude these from ordinary
    /// query-field matching (case-insensitively).
    /// </summary>
    public static readonly string[] ReservedParameterNames = [MinChangeVersion, MaxChangeVersion];

    /// <summary>
    /// Parses and validates the change-version parameters from the supplied query parameters.
    /// </summary>
    /// <returns>
    /// A <see cref="ChangeVersionValidationResult"/> carrying the parsed range and the ordered
    /// list of validation errors (empty when the parameters are valid or absent).
    /// </returns>
    public static ChangeVersionValidationResult Validate(IReadOnlyDictionary<string, string> queryParameters)
    {
        List<string> errors = [];

        long? min = ParseBound(queryParameters, MinChangeVersion, MinError, errors);
        long? max = ParseBound(queryParameters, MaxChangeVersion, MaxError, errors);

        if (min is not null && max is not null && min > max)
        {
            errors.Add(InvertedError);
        }

        return new ChangeVersionValidationResult(new ChangeVersionRange(min, max), errors);
    }

    private static long? ParseBound(
        IReadOnlyDictionary<string, string> queryParameters,
        string parameterName,
        string error,
        List<string> errors
    )
    {
        if (!TryGetValueIgnoreCase(queryParameters, parameterName, out string rawValue))
        {
            return null;
        }

        // A present-but-empty or whitespace-only value is treated as absent, matching ODS
        // model binding, which converts such query values to null without a validation error.
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        if (
            long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            && value >= 0
        )
        {
            return value;
        }

        errors.Add(error);
        return null;
    }

    /// <summary>
    /// Case-insensitive dictionary lookup. When multiple case-variant keys are present,
    /// duplicates collapse to a single value: the first match in enumeration order wins.
    /// </summary>
    private static bool TryGetValueIgnoreCase(
        IReadOnlyDictionary<string, string> source,
        string key,
        out string value
    )
    {
        string? found = source
            .Where(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .FirstOrDefault();

        if (found is not null)
        {
            value = found;
            return true;
        }

        value = string.Empty;
        return false;
    }
}

/// <summary>
/// The outcome of change-version parameter validation: the parsed range plus an
/// ordered list of error messages (empty when valid). Ordering is deterministic and
/// request-independent: min parse error, then max parse error, then inverted-range
/// error (only when both bounds parsed successfully).
/// </summary>
internal sealed record ChangeVersionValidationResult(ChangeVersionRange Range, IReadOnlyList<string> Errors);
