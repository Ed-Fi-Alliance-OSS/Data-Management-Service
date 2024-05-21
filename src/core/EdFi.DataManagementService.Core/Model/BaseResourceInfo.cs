// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Model;

/// <summary>
/// Base API resource information for passing along to backends.
/// </summary>
internal record BaseResourceInfo(
    /// <summary>
    /// The project name the API document resource is defined in e.g. "EdFi" for a data standard entity
    /// </summary>
    IMetaEdProjectName ProjectName,
    /// <summary>
    /// The name of the resource. Typically, this is the same as the corresponding MetaEd entity name. However,
    /// there are exceptions, for example descriptors have a "Descriptor" suffix on their resource name.
    /// </summary>
    IMetaEdResourceName ResourceName,
    /// <summary>
    /// Whether this resource is a descriptor. Descriptors are treated differently from other documents
    /// </summary>
    bool IsDescriptor
) : IBaseResourceInfo;
