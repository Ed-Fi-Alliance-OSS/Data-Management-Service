// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Postgresql.Model;

/// <summary>
/// ReferentialId Guids and parallel partition keys extracted from
/// a set of DocumentReferences
/// </summary>
public record DocumentReferenceIds(
    /// <summary>
    /// The ReferentialIds for all the references on this document
    /// </summary>
    Guid[] ReferentialIds,
    /// <summary>
    /// The ReferentialPartitionKeys for all the references on this document
    /// </summary>
    short[] ReferentialPartitionKeys
);
