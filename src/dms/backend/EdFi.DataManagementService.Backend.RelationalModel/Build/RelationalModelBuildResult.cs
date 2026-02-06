// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.Build;

/// <summary>
/// The final output of a relational model build run, including the derived resource model and any extension
/// site metadata discovered during traversal.
/// </summary>
public sealed record RelationalModelBuildResult(
    RelationalResourceModel ResourceModel,
    IReadOnlyList<ExtensionSite> ExtensionSites
);
