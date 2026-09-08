// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// All the extracted document references for an array of references in an API document
/// </summary>
public record DocumentReferenceArray(
    /// <summary>
    /// The JsonPath to the array element the API document
    /// </summary>
    JsonPath arrayPath,
    /// <summary>
    /// An ordered list of the document references extracted from the API document
    /// </summary>
    DocumentReference[] DocumentReferences
);
