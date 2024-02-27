// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.ApiSchema.Model;

namespace EdFi.DataManagementService.Api.Core.Model;

/// <summary>
/// Base API resource information for passing along to backends.
/// </summary>
public record BaseResourceInfo(
    /// <summary>
    /// The project name the API document resource is defined in e.g. "EdFi" for a data standard entity
    /// </summary>
    MetaEdProjectName ProjectName,
    /// <summary>
    /// The name of the resource. Typically, this is the same as the corresponding MetaEd entity name. However,
    /// there are exceptions, for example descriptors have a "Descriptor" suffix on their resource name.
    /// </summary>
    MetaEdResourceName ResourceName,
    /// <summary>
    /// Whether this resource is a descriptor. Descriptors are treated differently from other documents
    /// </summary>
    bool IsDescriptor
);

/// <summary>
/// API resource information including version
/// </summary>
public record ResourceInfo(
    MetaEdProjectName ProjectName,
    MetaEdResourceName ResourceName,
    bool IsDescriptor,
    /// <summary>
    /// The project version the resource belongs to.
    /// </summary>
    SemVer ResourceVersion,
    /// <summary>
    /// Whether the resource allows the identity fields of a document to be updated (changed)
    /// </summary>
    bool AllowIdentityUpdates
) : BaseResourceInfo(ProjectName, ResourceName, IsDescriptor);
