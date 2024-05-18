// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Model;

/// <summary>
/// Information representing a reference to a document, extracted from the referring document
/// </summary>
internal record DocumentReference(
    /// <summary>
    /// Base API resource information for the referenced document
    /// </summary>
    IBaseResourceInfo ResourceInfo,
    /// <summary>
    /// The document identity representing this reference.
    /// </summary>
    IDocumentIdentity DocumentIdentity
) : IDocumentReference;
