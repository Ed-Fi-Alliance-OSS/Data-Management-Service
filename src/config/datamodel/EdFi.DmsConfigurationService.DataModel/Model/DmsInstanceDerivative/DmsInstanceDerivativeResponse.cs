// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.DmsInstanceDerivative;

/// <summary>
/// A derivative instance (read replica or snapshot) associated with a DMS instance
/// </summary>
public class DmsInstanceDerivativeResponse
{
    /// <summary>
    /// The unique identifier for the derivative instance
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// The parent DMS instance ID
    /// </summary>
    public long InstanceId { get; set; }

    /// <summary>
    /// The type of derivative: "ReadReplica" or "Snapshot"
    /// </summary>
    public required string DerivativeType { get; set; }

    /// <summary>
    /// The connection string for the derivative instance (decrypted)
    /// </summary>
    public string? ConnectionString { get; set; }
}
