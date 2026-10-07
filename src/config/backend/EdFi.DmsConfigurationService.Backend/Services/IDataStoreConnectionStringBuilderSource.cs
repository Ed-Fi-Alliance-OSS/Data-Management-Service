// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;

namespace EdFi.DmsConfigurationService.Backend.Services;

/// <summary>
/// Hands out the configured engine's connection string builder. It is implemented by
/// <see cref="DataStoreConnectionStringValidator"/> and registered as the same instance, so the
/// parser a submitted connection string was validated by is the builder its stored text is later
/// rewritten through, and the two cannot disagree about the engine.
/// </summary>
public interface IDataStoreConnectionStringBuilderSource
{
    /// <summary>
    /// Parses the value with the configured provider's own connection string builder, throwing when
    /// the provider does not accept it.
    /// </summary>
    DbConnectionStringBuilder CreateBuilder(string connectionString);
}
