// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// Marks a payload string, or each string of an <c>IReadOnlyList&lt;string&gt;</c>, as an identifier (spec
/// D-13). The only strings a payload may carry are identifiers: an ASCII letter or digit, then ASCII letters,
/// digits, <c>_</c>, <c>.</c>, <c>:</c>, or <c>-</c>, at most <see cref="MaxLength"/> characters. The contract
/// requires <see cref="MaxLength"/> to be between 1 and <see cref="JobPayloadContract.MaxIdentifierLength"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed partial class JobIdentifierAttribute(int maxLength) : Attribute
{
    public int MaxLength { get; } = maxLength;

    /// <summary>Whether <paramref name="value"/> is an identifier no longer than <see cref="MaxLength"/>.</summary>
    public bool Accepts(string value) => value.Length <= MaxLength && IdentifierPattern().IsMatch(value);

    // \z rather than $: $ also matches before a final newline.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.:\-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
