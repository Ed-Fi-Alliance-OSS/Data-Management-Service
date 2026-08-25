// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Infrastructure;

/// <summary>
/// Validates a submitted data store connection string before it is encrypted and persisted.
/// Implementations are provider-aware and live in the provider backends, so this project stays
/// free of provider dependencies.
/// </summary>
public interface IDataStoreConnectionStringValidator
{
    /// <summary>
    /// Validates a connection string submitted through the API.
    /// </summary>
    /// <param name="connectionString">
    /// The submitted value. A null value means "not provided" and is valid here, because whether an
    /// absent connection string is acceptable belongs to the caller: an insert stores no value and
    /// an update leaves the stored value alone. An empty or whitespace-only value is not valid.
    /// </param>
    ConnectionStringValidationResult Validate(string? connectionString);
}

public record ConnectionStringValidationResult
{
    public record Valid() : ConnectionStringValidationResult();

    public record Invalid(string ErrorMessage) : ConnectionStringValidationResult();
}
