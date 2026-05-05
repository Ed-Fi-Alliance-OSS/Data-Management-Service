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
    ///
    /// Output must match the SQL identity-text formatters in <c>DialectIdentityTextFormatter</c>
    /// (PG <c>::text</c> + regexp trimming, MSSQL <c>CAST AS nvarchar(max)</c> + trim CASE).
    /// Both DB engines render <c>numeric</c>/<c>decimal</c> in fixed-point form, so this
    /// canonicalizer must also stay fixed-point — never scientific notation.
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

        // Fixed-point custom format: integer digits always shown, up to 28 fractional
        // digits with trailing zeros and a lone trailing decimal point trimmed. 28 is the
        // maximum scale of System.Decimal. The 'G' standard specifier is intentionally
        // avoided because it can switch to scientific notation for very small decimals
        // (e.g., 1e-28 → "1E-28" with G29), which would diverge from the trigger and
        // lookup-verification SQL output.
        return parsed.ToString("0.############################", CultureInfo.InvariantCulture);
    }
}
