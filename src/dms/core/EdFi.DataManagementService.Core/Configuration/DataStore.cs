// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Represents a data store configuration fetched from the Configuration Service
/// </summary>
public record DataStore
{
    public DataStore(
        long Id,
        string DataStoreType,
        string Name,
        string? ConnectionString,
        Dictionary<RouteQualifierName, RouteQualifierValue> RouteContext,
        RelationalProviderToken? RelationalProviderToken = null,
        RelationalProviderMetadataStatus RelationalProviderMetadataStatus =
            RelationalProviderMetadataStatus.Missing,
        IEnumerable<KeyValuePair<DataStoreDerivativeType, string>>? DerivativeConnectionStrings = null
    )
    {
        this.Id = Id;
        this.DataStoreType = DataStoreType;
        this.Name = Name;
        this.ConnectionString = ConnectionString;
        this.RouteContext = RouteContext;
        this.RelationalProviderToken = RelationalProviderToken;
        this.RelationalProviderMetadataStatus = RelationalProviderMetadataStatus;
        Derivatives = DerivativeConnectionStrings is null
            ? ImmutableDictionary<DataStoreDerivativeType, string>.Empty
            : ImmutableDictionary.CreateRange(DerivativeConnectionStrings);
    }

    /// <summary>
    /// The unique identifier for the data store
    /// </summary>
    public long Id { get; init; }

    /// <summary>
    /// The type/category of the data store
    /// </summary>
    public string DataStoreType { get; init; }

    /// <summary>
    /// The name of the data store
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// The database connection string for this data store
    /// </summary>
    public string? ConnectionString { get; init; }

    /// <summary>
    /// Route qualifier context for this data store, mapping qualifier names to values
    /// (e.g., "district" -> "255901", "schoolYear" -> "2024")
    /// </summary>
    public Dictionary<RouteQualifierName, RouteQualifierValue> RouteContext { get; init; }

    /// <summary>
    /// Normalized explicit relational provider metadata for this data store when CMS exposes it.
    /// </summary>
    public RelationalProviderToken? RelationalProviderToken { get; init; }

    /// <summary>
    /// Indicates whether explicit relational provider metadata was missing, unknown, or supported.
    /// </summary>
    public RelationalProviderMetadataStatus RelationalProviderMetadataStatus { get; init; }

    /// <summary>
    /// Configured derivative connection strings by type, decrypted and non-blank; empty when none is
    /// configured. These are the strings the Configuration Service stores, never a provider-realized
    /// form. The map is copied at construction rather than aliased, so mutating the collection supplied
    /// to the constructor cannot change any observable state, and a request that has already selected a
    /// target keeps the configuration it selected from even after the tenant cache is replaced.
    /// </summary>
    public ImmutableDictionary<DataStoreDerivativeType, string> Derivatives { get; init; }

    /// <summary>
    /// Gets the configured connection string for a derivative type. Returns false when that derivative
    /// is not configured, which covers a missing row and a null, empty, whitespace, or undecryptable
    /// connection string alike.
    /// </summary>
    public bool TryGetDerivative(
        DataStoreDerivativeType type,
        [NotNullWhen(true)] out string? connectionString
    ) => Derivatives.TryGetValue(type, out connectionString);
}
