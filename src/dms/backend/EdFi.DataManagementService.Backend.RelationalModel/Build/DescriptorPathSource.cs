// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Indicates how descriptor paths should be supplied to the per-resource pipeline.
/// </summary>
public enum DescriptorPathSource
{
    /// <summary>
    /// Infer descriptor paths from the resource schema.
    /// </summary>
    InferFromSchema,

    /// <summary>
    /// Use precomputed descriptor paths supplied by the caller.
    /// </summary>
    Precomputed,
}
