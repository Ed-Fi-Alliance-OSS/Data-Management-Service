// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;

namespace EdFi.DataManagementService.Backend.OpenSearch;

/// <summary>
/// A document from OpenSearch
/// </summary>
public record Document(
    /// <summary>
    /// The externally known UUID for this Document
    /// </summary>
    Guid DocumentUuid,
    /// <summary>
    /// The ResourceName for this document
    /// </summary>
    string ResourceName,
    /// <summary>
    /// The ResourceVersion for this document
    /// </summary>
    string ResourceVersion,
    /// <summary>
    /// Whether this resource is a descriptor
    /// </summary>
    bool IsDescriptor,
    /// <summary>
    /// The ProjectName for this document
    /// </summary>
    string ProjectName,
    /// <summary>
    /// The JSON API document itself
    /// </summary>
    JsonElement EdfiDoc
);
