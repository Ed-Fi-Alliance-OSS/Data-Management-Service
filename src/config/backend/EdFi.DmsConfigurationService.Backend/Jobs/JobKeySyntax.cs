// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// The syntax of a job type and a schedule type (spec D-13, D-14): an ASCII letter, then up to 99 ASCII
/// letters, digits, <c>_</c>, <c>.</c>, or <c>-</c>. Matching is ordinal and case-sensitive, and no whitespace
/// is allowed anywhere, so a key never depends on trailing-space comparison rules.
/// </summary>
public static partial class JobKeySyntax
{
    public const int MaxLength = 100;

    public static bool IsValid(string? key) => key is not null && KeyPattern().IsMatch(key);

    // \z rather than $: $ also matches before a final newline.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.\-]{0,99}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
