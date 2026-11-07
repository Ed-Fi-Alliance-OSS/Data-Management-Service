// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// A selected target database name that passed every rule in spec 6.8: the ASCII grammar
/// <c>^[A-Za-z][A-Za-z0-9_]{0,62}$</c> (quotable on both engines and within PostgreSQL's 63-byte
/// limit), no surrounding whitespace, not an engine system database, not in the deployment's
/// protected set, and not carrying a provisioner-generated prefix. The only construction path is
/// <see cref="TryCreate"/>; a default-initialized value has no name and throws when read.
/// </summary>
public readonly record struct DmsTargetDatabaseName
{
    public const int MaxLength = 63;

    private readonly string? _value;

    private DmsTargetDatabaseName(string value) => _value = value;

    /// <summary>The validated name exactly as supplied.</summary>
    public string Value =>
        _value
        ?? throw new InvalidOperationException("The target database name was not created through TryCreate.");

    /// <summary>
    /// Validates <paramref name="candidate"/> against the grammar, the engine system databases of both
    /// engines, the deployment's protected set, and the provisioner's reserved prefixes. Returns the
    /// first failing rule; <paramref name="name"/> is set only on <see cref="DmsTargetNameValidation.Valid"/>.
    /// </summary>
    public static DmsTargetNameValidation TryCreate(
        string? candidate,
        ProtectedDatabaseNames protectedNames,
        out DmsTargetDatabaseName name
    )
    {
        ArgumentNullException.ThrowIfNull(protectedNames);
        name = default;

        DmsTargetNameValidation verdict = CheckGrammar(candidate);
        if (verdict != DmsTargetNameValidation.Valid)
        {
            return verdict;
        }

        if (ProtectedDatabaseNames.IsReserved(candidate!))
        {
            return DmsTargetNameValidation.ReservedName;
        }

        if (protectedNames.IsProtected(candidate!))
        {
            return DmsTargetNameValidation.ProtectedName;
        }

        if (ProtectedDatabaseNames.HasReservedPrefix(candidate!))
        {
            return DmsTargetNameValidation.ReservedPrefix;
        }

        name = new DmsTargetDatabaseName(candidate!);
        return DmsTargetNameValidation.Valid;
    }

    /// <summary>The grammar rules alone, shared with <see cref="DmsDatabaseName"/>.</summary>
    internal static DmsTargetNameValidation CheckGrammar(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return DmsTargetNameValidation.Empty;
        }

        if (char.IsWhiteSpace(candidate[0]) || char.IsWhiteSpace(candidate[^1]))
        {
            return DmsTargetNameValidation.SurroundingWhitespace;
        }

        if (candidate.Length > MaxLength)
        {
            return DmsTargetNameValidation.TooLong;
        }

        if (!char.IsAsciiLetter(candidate[0]))
        {
            return DmsTargetNameValidation.InvalidFirstCharacter;
        }

        return candidate.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            ? DmsTargetNameValidation.Valid
            : DmsTargetNameValidation.InvalidCharacter;
    }

    public override string ToString() => _value ?? string.Empty;
}
