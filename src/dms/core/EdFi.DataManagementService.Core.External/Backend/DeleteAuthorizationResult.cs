// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// The result of a DeleteAuthorizationHandler Authorize call to determine
/// whether a document is authorized to be deleted. 
/// </summary>
public record DeleteAuthorizationResult
{
    public record Authorized() : DeleteAuthorizationResult();

    public record NotAuthorizedNamespace() : DeleteAuthorizationResult();
}
