// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.ApiSchema.Model;

/// <summary>
/// Represents an AbstractResource, such as an EducationOrganization.
/// </summary>
public record struct AbstractResource(
    ResourceName ResourceName,
    IEnumerable<JsonPath> IdentityJsonPaths,
    JsonNode? OpenApiFragment
);
