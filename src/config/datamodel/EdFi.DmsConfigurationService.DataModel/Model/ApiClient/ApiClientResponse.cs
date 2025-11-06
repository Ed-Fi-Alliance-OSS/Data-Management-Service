// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.ApiClient;

public class ApiClientResponse
{
    public long Id { get; set; }
    public long ApplicationId { get; set; }
    public required string ClientId { get; set; }
    public required Guid ClientUuid { get; set; }
    public required string Name { get; set; }
    public required bool IsApproved { get; set; }
    public List<long> DmsInstanceIds { get; set; } = [];
}
