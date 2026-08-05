// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// The number of items a cursor page may select. Zero is a valid size that returns an empty page and
/// intentionally cannot advance a cursor walk.
/// </summary>
/// <remarks>
/// Only the negative case is unrepresentable here. Validating a page size against the configured
/// maximum, and reporting that failure to the client, belongs to request validation.
/// </remarks>
public readonly record struct PageSize
{
    public PageSize(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    /// <summary>
    /// The page size. Zero for a default-constructed value, which is itself a valid empty page.
    /// </summary>
    public int Value { get; }
}
