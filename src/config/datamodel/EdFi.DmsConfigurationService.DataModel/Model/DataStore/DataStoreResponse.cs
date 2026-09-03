// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.DataStore;

public class DataStoreResponse
{
    public int Id { get; set; }
    public required string DataStoreType { get; set; }
    public required string Name { get; set; }
    public string? ConnectionString { get; set; }
    public string? Provider { get; set; }
    public IEnumerable<DataStoreContextItem> DataStoreContexts { get; set; } = [];
    public IEnumerable<DataStoreDerivativeItem> DataStoreDerivatives { get; set; } = [];
    public long? TenantId { get; set; }
}

public record DataStoreContextItem(int Id, int DataStoreId, string ContextKey, string ContextValue);

public record DataStoreDerivativeItem(
    int Id,
    int DataStoreId,
    string DerivativeType,
    string? ConnectionString
);
