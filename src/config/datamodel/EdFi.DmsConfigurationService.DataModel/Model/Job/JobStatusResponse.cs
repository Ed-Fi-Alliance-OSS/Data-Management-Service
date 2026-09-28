// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.Job;

/// <summary>
/// The body of <c>GET /v3/jobs/{jobId}</c> (spec §1.4, §5): exactly these five properties. Times are UTC.
/// </summary>
public class JobStatusResponse
{
    public required string JobId { get; set; }
    public required string Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
