// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Endpoint metadata marking a route whose failures are logged by exception type names only.
/// <c>RequestLoggingMiddleware</c> writes the route's <c>HttpRequestFailed</c> event without the exception
/// object, and replaces an exception it rethrows, so no exception message, inner exception or
/// <c>Data</c> reaches a logger. Only <c>/connect/revoke</c> carries it (DMS-1327 D-15, D-17): its
/// handler resolves implementations a plugin can supply, and their exceptions can carry anything.
/// </summary>
internal sealed class ExceptionTypeOnlyLoggingMetadata
{
    public static readonly ExceptionTypeOnlyLoggingMetadata Instance = new();

    private ExceptionTypeOnlyLoggingMetadata() { }
}
