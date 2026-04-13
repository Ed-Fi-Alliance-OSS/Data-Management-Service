// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Controls which optional projection work is included in a hydration batch.
/// </summary>
/// <param name="IncludeDescriptorProjection">
/// When <see langword="true"/>, append descriptor URI projection result sets.
/// Session-scoped current-state loads can disable this when they only need storage rows.
/// </param>
public readonly record struct HydrationExecutionOptions(bool IncludeDescriptorProjection = true);
