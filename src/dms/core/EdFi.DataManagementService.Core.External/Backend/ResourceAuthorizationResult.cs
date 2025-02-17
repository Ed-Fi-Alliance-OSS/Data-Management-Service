// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// The result of a ResourceAuthorizationHandler Authorize call to determine
/// whether a client ia authorized to access the resource.
/// </summary>
public record ResourceAuthorizationResult
{
    public record Authorized() : ResourceAuthorizationResult();

    public record NotAuthorized(string[] ErrorMessages) : ResourceAuthorizationResult();
}
