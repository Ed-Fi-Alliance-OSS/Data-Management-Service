// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Canonicalizes decimal values to fixed-point form with no insignificant trailing fractional zeros.
/// </summary>
public static class DecimalValueCanonicalizer
{
    private const string FixedPointFormat = "0.############################";

    private const NumberStyles DecimalTextStyles =
        NumberStyles.Float
        | NumberStyles.AllowLeadingSign
        | NumberStyles.AllowDecimalPoint
        | NumberStyles.AllowExponent;

    public static string CanonicalizeText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return decimal.TryParse(value, DecimalTextStyles, CultureInfo.InvariantCulture, out var parsed)
            ? ToCanonicalText(parsed)
            : value;
    }

    public static string ToCanonicalText(decimal value)
    {
        return value == 0m ? "0" : value.ToString(FixedPointFormat, CultureInfo.InvariantCulture);
    }

    public static decimal NormalizeScale(decimal value)
    {
        return decimal.Parse(ToCanonicalText(value), NumberStyles.Number, CultureInfo.InvariantCulture);
    }
}
