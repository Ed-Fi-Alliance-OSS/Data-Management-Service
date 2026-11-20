// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;

/// <summary>The frozen DMS-1439 section 3.3 equality policy, independent of culture and database collation.</summary>
public static class CatalogName
{
    private const string EdgeWhitespace =
        "\u0009\u000A\u000B\u000C\u000D\u0020\u0085\u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000";

    public static string Trim(string value) => value.AsSpan().Trim(EdgeWhitespace.AsSpan()).ToString();

    public static string Canonicalize(string value) => FoldAscii(Trim(value));

    public static string FoldAscii(string value) =>
        string.Create(
            value.Length,
            value,
            (destination, source) =>
            {
                for (int i = 0; i < source.Length; i++)
                {
                    destination[i] = source[i] is >= 'A' and <= 'Z'
                        ? (char)(source[i] + ('a' - 'A'))
                        : source[i];
                }
            }
        );

    public static bool IsValidManagedName(string? value) =>
        value is not null
        && Trim(value) is { Length: >= 1 and <= 46 } name
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_');
}
