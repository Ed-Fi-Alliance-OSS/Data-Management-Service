// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// A delete request to a document repository
/// </summary>
public interface IDeleteRequest
{
    /// <summary>
    /// The document UUID to delete
    /// </summary>
    DocumentUuid DocumentUuid { get; }

    /// <summary>
    /// The ResourceInfo for the resource being deleted
    /// </summary>
    ResourceInfo ResourceInfo { get; }

    /// <summary>
    /// If true, validates there are no references to the document
    /// </summary>
    bool validateNoReferencesToDocument { get; }

    /// <summary>
    /// The request TraceId
    /// </summary>
    TraceId TraceId { get; }
}
