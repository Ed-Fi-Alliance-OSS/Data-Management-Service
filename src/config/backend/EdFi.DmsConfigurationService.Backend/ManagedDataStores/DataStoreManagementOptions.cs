// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.ManagedDataStores;

/// <summary>The restart-scoped management switch under AppSettings.</summary>
public sealed class DataStoreManagementOptions
{
    public bool EnableDataStoreManagement { get; set; } = true;
}
