// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// A handler to modify a referencing document with updates to a
/// child documents identifying values. Returns the modified parent
/// data as well as flag indicating whether the update was itself
/// an identity update
/// </summary>
public interface IUpdateCascadeHandler
{
    UpdateCascadeResult Cascade(
        /// <summary>
        /// The original referenced documents before updates.
        /// Used for locating array elements in the referencing
        /// resource that need updating
        /// </summary>
        JsonElement originalEdFiDoc,
        /// <summary>
        /// The project name of the referenced resource
        /// </summary>
        string originalDocumentProjectName,
        /// <summary>
        /// The name of the referenced resource
        /// </summary>
        string originalDocumentResourceName,
        /// <summary>
        /// The modified edfi doc
        /// </summary>
        JsonNode modifiedEdFiDoc,
        /// <summary>
        /// The referencing EdFiDoc
        /// </summary>
        JsonNode referencingEdFiDoc,
        /// <summary>
        /// The referencing document id
        /// </summary>
        long referencingDocumentId,
        /// <summary>
        /// Rhe referencing document partition key
        /// </summary>
        short referencingDocumentPartitionKey,
        /// <summary>
        /// The referencing documentuuid
        /// </summary>
        Guid referencingDocumentUuid,
        /// <summary>
        /// The project name of the referencing resource
        /// </summary>
        string referencingProjectName,
        /// <summary>
        /// The resource name of the referencing resource
        /// </summary>
        string referencingResourceName
    );
}
