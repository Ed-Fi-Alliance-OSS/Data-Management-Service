// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.Job;

/// <summary>
/// The Admin API <c>jobQueuedResult</c> body of an accepted asynchronous request: exactly these two properties.
/// <see cref="JobId"/> is the durable job's identifier, readable through <c>GET /v3/jobs/{jobId}</c>.
/// </summary>
public class JobQueuedResponse
{
    public required string JobId { get; set; }
    public required string Message { get; set; }
}
