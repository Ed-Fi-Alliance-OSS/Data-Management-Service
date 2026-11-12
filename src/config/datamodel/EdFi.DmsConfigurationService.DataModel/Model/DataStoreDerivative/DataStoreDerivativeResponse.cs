// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.DataStoreDerivative;

/// <summary>
/// A derivative instance (read replica or snapshot) associated with a data store
/// </summary>
public class DataStoreDerivativeResponse
{
    /// <summary>
    /// The unique identifier for the derivative instance
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The parent data store ID
    /// </summary>
    public int DataStoreId { get; set; }

    /// <summary>
    /// The type of derivative: "ReadReplica" or "Snapshot"
    /// </summary>
    public required string DerivativeType { get; set; }

    /// <summary>
    /// The Base64-encoded encrypted connection string that DMS decrypts, or null when none is
    /// configured or a derivative read as part of its data store cannot be decrypted or resolved.
    /// </summary>
    public string? ConnectionString { get; set; }
}
