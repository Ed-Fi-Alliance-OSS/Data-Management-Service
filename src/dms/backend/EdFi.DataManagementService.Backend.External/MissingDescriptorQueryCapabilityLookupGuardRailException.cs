// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Identifies the request-scoped descriptor query guard rail where a descriptor resource is missing its compiled descriptor query capability metadata.
/// </summary>
public sealed class MissingDescriptorQueryCapabilityLookupGuardRailException(string message)
    : InvalidOperationException(message);
