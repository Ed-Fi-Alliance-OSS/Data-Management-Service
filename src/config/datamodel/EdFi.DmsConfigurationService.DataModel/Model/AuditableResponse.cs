// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model;

/// <summary>
/// Base class for response models that include audit tracking information.
/// </summary>
public abstract class AuditableResponse
{
    /// <summary>
    /// The timestamp when the record was created (UTC).
    /// </summary>
    public DateTime? CreatedAt { get; set; }

    /// <summary>
    /// The identifier of the user or client who created the record.
    /// </summary>
    public string? CreatedBy { get; set; }

    /// <summary>
    /// The timestamp when the record was last modified (UTC).
    /// Null if the record has never been modified.
    /// </summary>
    public DateTime? LastModifiedAt { get; set; }

    /// <summary>
    /// The identifier of the user or client who last modified the record.
    /// Null if the record has never been modified.
    /// </summary>
    public string? ModifiedBy { get; set; }
}
