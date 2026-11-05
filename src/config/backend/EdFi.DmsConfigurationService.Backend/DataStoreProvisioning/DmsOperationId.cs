// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The identity of one provisioning operation: twelve lowercase hexadecimal characters drawn from
/// <see cref="RandomNumberGenerator"/>. Every resource the operation creates is named from it (spec 6):
/// the staging database, the holder session, the <c>psql</c> session, the SQL Server physical-file tag,
/// and the claim comment. A default-initialized value has no id and throws when read.
/// </summary>
public readonly record struct DmsOperationId
{
    public const int Length = 12;

    public const string StagingDatabaseNamePrefix = "edfi_dms_provision_stage_";

    public const string HolderApplicationNamePrefix = "dmsprov:";

    public const string RestoreApplicationNamePrefix = "dms-provision:";

    private readonly string? _value;

    private DmsOperationId(string value) => _value = value;

    public string Value =>
        _value
        ?? throw new InvalidOperationException("The operation id was not created through New or TryParse.");

    /// <summary>The generated staging database name <c>edfi_dms_provision_stage_&lt;op&gt;</c> (D-6).</summary>
    public DmsDatabaseName StagingDatabaseName =>
        DmsDatabaseName.TryCreate(StagingDatabaseNamePrefix + Value, out DmsDatabaseName name)
            ? name
            : throw new InvalidOperationException("The staging database name did not satisfy the grammar.");

    /// <summary>The holder session's <c>application_name</c> / <c>Application Name</c>, 20 characters (D-12).</summary>
    public string HolderApplicationName => HolderApplicationNamePrefix + Value;

    /// <summary>The <c>PGAPPNAME</c> of the <c>psql</c> replay session (D-7b).</summary>
    public string RestoreApplicationName => RestoreApplicationNamePrefix + Value;

    /// <summary>The tag embedded in every SQL Server physical file name of the operation's database (D-11, D-13).</summary>
    public string PhysicalFileTag => "__" + Value;

    public static DmsOperationId New() =>
        new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(Length / 2)));

    /// <summary>Accepts exactly twelve lowercase hexadecimal characters.</summary>
    public static bool TryParse(string? value, out DmsOperationId operationId)
    {
        if (value is { Length: Length } && value.All(IsLowercaseHex))
        {
            operationId = new DmsOperationId(value);
            return true;
        }

        operationId = default;
        return false;
    }

    private static bool IsLowercaseHex(char character) =>
        char.IsAsciiDigit(character) || character is >= 'a' and <= 'f';

    public override string ToString() => _value ?? string.Empty;
}
