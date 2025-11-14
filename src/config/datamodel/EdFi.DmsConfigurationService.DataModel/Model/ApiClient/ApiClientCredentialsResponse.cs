// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.ApiClient;

public class ApiClientCredentialsResponse
{
    public long Id { get; set; }
    public required string Key { get; set; }
    public required string Secret { get; set; }
}
