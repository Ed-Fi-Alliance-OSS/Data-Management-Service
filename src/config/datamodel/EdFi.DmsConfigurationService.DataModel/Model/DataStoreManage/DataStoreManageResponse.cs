// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;

namespace EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;

public sealed record DataStoreManageResponse(
    int Id,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? DataStoreId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DataStoreName,
    string Status,
    string DatabaseTemplate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DatabaseName,
    DateTimeOffset LastRefreshed,
    DateTimeOffset LastModifiedDate
);
