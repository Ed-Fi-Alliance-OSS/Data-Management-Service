// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Extraction;

internal static class IdentityValueCanonicalizer
{
    public static DocumentIdentityElement CreateDocumentIdentityElement(
        JsonPath identityJsonPath,
        string identityValue,
        bool isNumeric = false
    )
    {
        return new(identityJsonPath, Canonicalize(identityJsonPath, identityValue, isNumeric));
    }

    private static string Canonicalize(JsonPath identityJsonPath, string identityValue, bool isNumeric)
    {
        if (DocumentIdentity.IsDescriptorIdentityPath(identityJsonPath))
        {
            return identityValue.ToLowerInvariant();
        }

        if (isNumeric)
        {
            return CanonicalizeDecimal(identityValue);
        }

        return identityValue;
    }

    /// <summary>
    /// Canonicalizes a numeric identity-value string to a fixed-point decimal representation.
    /// Parses the input as a decimal (supporting scientific notation and leading sign),
    /// then emits: fixed-point, no exponent, no leading '+', single leading '0' before the
    /// decimal point, no trailing fractional zeros, no trailing decimal point,
    /// and signed-zero collapsed to '0'.
    /// </summary>
    internal static string CanonicalizeDecimal(string identityValue)
    {
        const NumberStyles styles =
            NumberStyles.Float
            | NumberStyles.AllowLeadingSign
            | NumberStyles.AllowDecimalPoint
            | NumberStyles.AllowExponent;

        if (!decimal.TryParse(identityValue, styles, CultureInfo.InvariantCulture, out decimal parsed))
        {
            return identityValue;
        }

        // Collapse signed zero to positive zero
        if (parsed == 0m)
        {
            return "0";
        }

        // G29 produces fixed-point with up to 29 significant digits, no exponent,
        // no trailing fractional zeros, and no trailing decimal point.
        return parsed.ToString("G29", CultureInfo.InvariantCulture);
    }
}
