// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Model;

/// <summary>
/// The important parts of the request URL in object form
/// </summary>

internal record PathComponents(
    /// <summary>
    /// Project namespace, all lowercased
    /// </summary>
    ProjectNamespace ProjectNamespace,
    /// <summary>
    /// Endpoint name, which is always decapitalized and plural
    /// </summary>
    EndpointName EndpointName,
    /// <summary>
    /// The optional resource identifier, which is a document uuid
    /// </summary>
    DocumentUuid DocumentUuid
)
{
    /// <summary>
    /// Return the path of the resource such as for location headers
    /// </summary>
    /// <param name="pathComponents"></param>
    /// <param name="documentUuid"></param>
    /// <returns>The path of the resource</returns>
    public static string ToResourcePath(PathComponents pathComponents, DocumentUuid documentUuid)
    {
        return $"/{pathComponents.ProjectNamespace.Value}/{pathComponents.EndpointName.Value}/{documentUuid.Value}";
    }
};
