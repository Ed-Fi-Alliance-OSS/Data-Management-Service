// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.DmsInstance;

public class DmsInstanceResponse
{
    public long Id { get; set; }
    public required string InstanceType { get; set; }
    public required string InstanceName { get; set; }
    public string? ConnectionString { get; set; }
    public IEnumerable<DmsInstanceRouteContextItem> DmsInstanceRouteContexts { get; set; } = [];
}

public record DmsInstanceRouteContextItem(string ContextKey, string ContextValue);
