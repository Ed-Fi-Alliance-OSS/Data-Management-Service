// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Backend;

public interface ITrackedChangeQueryRequest
{
    ResourceInfo ResourceInfo { get; }

    ChangeQueryEndpointOperation Operation { get; }

    PaginationParameters PaginationParameters { get; }

    ChangeVersionRange ChangeVersionRange { get; }

    TraceId TraceId { get; }
}

public sealed record TrackedChangeQueryResult(JsonArray Items, long? TotalCount);
