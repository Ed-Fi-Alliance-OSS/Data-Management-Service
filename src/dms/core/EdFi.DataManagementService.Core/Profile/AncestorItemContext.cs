// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Caller-provided context identifying one concrete collection item on the JSON
/// traversal path to the addressed scope.
/// </summary>
/// <param name="JsonScope">Compiled JsonScope of the ancestor collection.</param>
/// <param name="Item">The concrete JSON collection item on the traversal path.</param>
public sealed record AncestorItemContext(string JsonScope, JsonNode Item);
