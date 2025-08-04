// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DmsConfigurationService.Backend.Claims.Models;

/// <summary>
/// Container for claims JSON nodes including claim sets and claims hierarchy
/// </summary>
public record ClaimsDocumentNodes(JsonNode ClaimSetsNode, JsonNode ClaimsHierarchyNode);
