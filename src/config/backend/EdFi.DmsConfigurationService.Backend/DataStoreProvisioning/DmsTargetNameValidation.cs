// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The verdict of <see cref="DmsTargetDatabaseName.TryCreate"/>. Checks run in this order and
/// the first failing rule is reported; a candidate is never trimmed or otherwise repaired.
/// </summary>
public enum DmsTargetNameValidation
{
    Valid,

    /// <summary>Null or empty.</summary>
    Empty,

    /// <summary>Leading or trailing whitespace of any kind, including line breaks.</summary>
    SurroundingWhitespace,

    /// <summary>Longer than <see cref="DmsTargetDatabaseName.MaxLength"/> characters.</summary>
    TooLong,

    /// <summary>The first character is not an ASCII letter.</summary>
    InvalidFirstCharacter,

    /// <summary>A character other than an ASCII letter, ASCII digit, or underscore.</summary>
    InvalidCharacter,

    /// <summary>An engine system database of either engine, compared case-insensitively.</summary>
    ReservedName,

    /// <summary>The CMS catalog database or an operator-listed protected name, compared case-insensitively.</summary>
    ProtectedName,

    /// <summary>A name the provisioner generates for its own resources.</summary>
    ReservedPrefix,
}
