// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// A handler to determine if the client is authorized for the resource.
/// </summary>
public interface IResourceAuthorizationHandler
{
    ResourceAuthorizationResult Authorize(string[] namespaces, long[] educationOrganizationIds);

    bool IsRelationshipWithEdOrg { get; }
}
