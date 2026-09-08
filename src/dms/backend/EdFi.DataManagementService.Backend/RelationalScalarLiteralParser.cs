// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalScalarLiteralParser
{
    private const string DateOnlyFormat = "yyyy-MM-dd";
    private const string UtcDateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss";
    private const string OffsetDateTimeFormat = "yyyy-MM-dd'T'HH:mm:sszzz";
    private const string TimeOnlyFormat = "HH:mm:ss";

    public static bool TryParse(string scalarLiteral, RelationalScalarType scalarType, out object? value)
    {
        ArgumentNullException.ThrowIfNull(scalarLiteral);
        ArgumentNullException.ThrowIfNull(scalarType);

        switch (scalarType.Kind)
        {
            case ScalarKind.String:
                value = scalarLiteral;
                return true;
            case ScalarKind.Int32
                when int.TryParse(
                    scalarLiteral,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var int32Value
                ):
                value = int32Value;
                return true;
            case ScalarKind.Int64
                when long.TryParse(
                    scalarLiteral,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var int64Value
                ):
                value = int64Value;
                return true;
            case ScalarKind.Decimal
                when decimal.TryParse(
                    scalarLiteral,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var decimalValue
                ):
                value = decimalValue;
                return true;
            case ScalarKind.Boolean when bool.TryParse(scalarLiteral, out var boolValue):
                value = boolValue;
                return true;
            case ScalarKind.Date
                when DateOnly.TryParseExact(
                    scalarLiteral,
                    DateOnlyFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var dateOnlyValue
                ):
                value = dateOnlyValue;
                return true;
            case ScalarKind.DateTime when TryReadDateTimeValue(scalarLiteral, out var dateTimeValue):
                value = dateTimeValue;
                return true;
            case ScalarKind.Time
                when TimeOnly.TryParseExact(
                    scalarLiteral,
                    TimeOnlyFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var timeOnlyValue
                ):
                value = timeOnlyValue;
                return true;
            default:
                value = null;
                return false;
        }
    }

    private static bool TryReadDateTimeValue(string rawValue, out DateTime dateTimeValue)
    {
        if (
            rawValue.EndsWith('Z')
            && DateTime.TryParseExact(
                rawValue[..^1],
                UtcDateTimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var utcDateTimeValue
            )
        )
        {
            dateTimeValue = DateTime.SpecifyKind(utcDateTimeValue, DateTimeKind.Utc);
            return true;
        }

        if (
            DateTimeOffset.TryParseExact(
                rawValue,
                OffsetDateTimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var offsetDateTimeValue
            )
        )
        {
            dateTimeValue = offsetDateTimeValue.UtcDateTime;
            return true;
        }

        dateTimeValue = default;
        return false;
    }
}
