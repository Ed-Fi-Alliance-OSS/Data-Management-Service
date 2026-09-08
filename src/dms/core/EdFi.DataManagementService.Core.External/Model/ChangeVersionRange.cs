// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// The parsed and validated change-version window from the minChangeVersion /
/// maxChangeVersion query parameters. Either bound may be null when that
/// parameter was not supplied. Bounds are long-width to match the relational
/// bigint change-version storage.
/// </summary>
public sealed record ChangeVersionRange(long? MinChangeVersion, long? MaxChangeVersion)
{
    /// <summary>
    /// The no-bounds range used when neither change-version parameter is supplied.
    /// </summary>
    public static ChangeVersionRange None { get; } = new(null, null);
}
