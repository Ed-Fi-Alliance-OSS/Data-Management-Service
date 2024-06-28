// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.ApiSchema
{
    /// <summary>
    /// A pair of JsonPaths representing path information for a single element of a reference in a resource document.
    ///
    /// For example, the referenceJsonPaths section of an ApiSchema.json for a document referencing a Program document
    /// might look like this:
    ///
    /// "referenceJsonPaths": [
    ///   {
    ///     "identityJsonPath": "$.educationOrganizationReference.educationOrganizationId",
    ///     "referenceJsonPath": "$.programs[*].programReference.educationOrganizationId"
    ///   },
    ///   {
    ///     "identityJsonPath": "$.programName",
    ///     "referenceJsonPath": "$.programs[*].programReference.programName"
    ///   },
    ///   {
    ///     "identityJsonPath": "$.programTypeDescriptor",
    ///     "referenceJsonPath": "$.programs[*].programReference.programTypeDescriptor"
    ///   }
    /// ],
    ///
    /// In this example, each array element is a ReferenceJsonPathsElement
    ///
    /// </summary>
    internal record ReferenceJsonPathsElement(
        /// <summary>
        /// The JsonPath to the identity value in the document being referenced
        /// </summary>
        JsonPath IdentityJsonPath,
        /// <summary>
        /// The JsonPath to the identity value in the referencing document
        /// </summary>
        JsonPath ReferenceJsonPath
    );
}
