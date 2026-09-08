// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Result of validating the dms.ResourceKey seed data against expected schema metadata.
/// </summary>
public abstract record ResourceKeyValidationResult
{
    private ResourceKeyValidationResult() { }

    /// <summary>
    /// Validation succeeded: the database resource keys match the expected seed.
    /// </summary>
    public sealed record ValidationSuccess() : ResourceKeyValidationResult;

    /// <summary>
    /// Validation failed: the database resource keys do not match the expected seed.
    /// </summary>
    /// <param name="DiffReport">A deterministic diff report describing the mismatch.</param>
    public sealed record ValidationFailure(string DiffReport) : ResourceKeyValidationResult;
}
