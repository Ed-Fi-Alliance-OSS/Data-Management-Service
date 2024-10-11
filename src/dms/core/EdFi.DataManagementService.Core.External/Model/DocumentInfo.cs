// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// Complete information on a validated API document
/// </summary>
public record DocumentInfo(
    /// <summary>
    /// The identity elements extracted from the API document
    /// </summary>
    DocumentIdentity DocumentIdentity,
    /// <summary>
    /// The identity of the API document expressed as a ReferentialId
    /// </summary>
    ReferentialId ReferentialId,
    /// <summary>
    /// A list of the document references extracted from the API document
    /// </summary>
    DocumentReference[] DocumentReferences,
    /// <summary>
    /// A list of the non-reference (meaning top-level only) descriptor values of the entity extracted from the API document
    /// </summary>
    DescriptorReference[] DescriptorReferences,
    /// <summary>
    /// If this document is a subclass, this provides the document superclass identity information.
    /// </summary>
    SuperclassIdentity? SuperclassIdentity
);
