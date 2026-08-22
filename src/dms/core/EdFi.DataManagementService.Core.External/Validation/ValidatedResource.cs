// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Validation;

/// <summary>
/// A single resource that a custom validator declares itself applicable to, identified by the
/// MetaEd project it belongs to and its resource name within that project.
/// </summary>
/// <param name="ProjectName">The MetaEd project name the resource belongs to, e.g. "Ed-Fi".</param>
/// <param name="ResourceName">The resource name within the project.</param>
public sealed record ValidatedResource(ProjectName ProjectName, ResourceName ResourceName);
