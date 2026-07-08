// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.External.Logging;

// The event id values are defined in docs/LOGGING.md and must stay in sync with
// EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware.RequestLoggingEventIds. CMS and DMS
// build as separate solutions, so each application pins the documented values with its own unit test.
public static class RequestLoggingEventIds
{
    public static readonly EventId HttpRequestCompleted = new(1228001, "HttpRequestCompleted");

    public static readonly EventId HttpRequestFailed = new(1228002, "HttpRequestFailed");
}
