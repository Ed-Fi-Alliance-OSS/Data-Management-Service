// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Indicates that standalone hydration exhausted its bounded attempts to read a stable representation.
/// </summary>
public sealed class HydrationConsistencyException()
    : InvalidOperationException(
        "Relational hydration could not read a stable representation after concurrent writes."
    );
