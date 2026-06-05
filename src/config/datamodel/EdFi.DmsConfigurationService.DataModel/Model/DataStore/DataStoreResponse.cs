// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.DataStore;

public class DataStoreResponse
{
    public long Id { get; set; }
    public required string DataStoreType { get; set; }
    public required string Name { get; set; }
    public string? ConnectionString { get; set; }
    public IEnumerable<DataStoreContextItem> DataStoreContexts { get; set; } = [];
    public IEnumerable<DataStoreDerivativeItem> DataStoreDerivatives { get; set; } = [];
    public long? TenantId { get; set; }
}

public record DataStoreContextItem(long Id, long DataStoreId, string ContextKey, string ContextValue);

public record DataStoreDerivativeItem(
    long Id,
    long DataStoreId,
    string DerivativeType,
    string? ConnectionString
);
