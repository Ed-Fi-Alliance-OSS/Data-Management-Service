// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// A syntactically safe database name on the target server: the selected target, this operation's
/// staging database, or an engine maintenance database. Only the grammar is enforced;
/// reservation and protection are the business of <see cref="DmsTargetDatabaseName"/>, from which this
/// type converts implicitly. A default-initialized value has no name and throws when read.
/// </summary>
public readonly record struct DmsDatabaseName
{
    private readonly string? _value;

    private DmsDatabaseName(string value) => _value = value;

    public string Value =>
        _value ?? throw new InvalidOperationException("The database name was not created through TryCreate.");

    public static bool TryCreate(string? candidate, out DmsDatabaseName name)
    {
        if (DmsTargetDatabaseName.CheckGrammar(candidate) == DmsTargetNameValidation.Valid)
        {
            name = new DmsDatabaseName(candidate!);
            return true;
        }

        name = default;
        return false;
    }

    public static DmsDatabaseName From(DmsTargetDatabaseName target) => new(target.Value);

    public static implicit operator DmsDatabaseName(DmsTargetDatabaseName target) => From(target);

    public override string ToString() => _value ?? string.Empty;
}
