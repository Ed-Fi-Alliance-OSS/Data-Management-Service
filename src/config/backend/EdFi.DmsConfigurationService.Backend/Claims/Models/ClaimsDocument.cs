// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DmsConfigurationService.Backend.Claims.Models;

/// <summary>
/// A Claims.json document - A combination of ClaimsHierarchy and Claimsets
/// </summary>
public record ClaimsDocument(JsonNode ClaimSetsNode, JsonNode ClaimsHierarchyNode);
