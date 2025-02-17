// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// A handler to determine if the edFiDoc is authorized to be deleted.
/// </summary>
public interface IDeleteAuthorizationHandler
{
    DeleteAuthorizationResult Authorize(JsonNode securityElements);
}
