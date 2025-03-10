// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.ResourceLoadOrder;

/// <summary>
/// Represents the fully qualified resource name that includes the project name and the resource name.
/// </summary>
internal record FullResourceName(ProjectName ProjectName, ResourceName ResourceName)
{
    public override string ToString() => $"{ProjectName.Value}.{ResourceName.Value}";
}
