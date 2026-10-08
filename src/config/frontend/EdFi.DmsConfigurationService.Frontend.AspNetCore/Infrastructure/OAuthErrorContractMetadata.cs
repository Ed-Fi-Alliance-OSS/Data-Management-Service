// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Endpoint metadata marking a route whose exception responses follow the RFC 6749 §5.2 error format
/// instead of the Ed-Fi problem-details contract. <see cref="GlobalExceptionHandler"/> reads it from the
/// original endpoint and writes the response with <see cref="OAuthErrorResponseWriter"/>. Only
/// <c>/connect/revoke</c> carries it (DMS-1327 D-17); every other route keeps the Ed-Fi contract.
/// </summary>
internal sealed class OAuthErrorContractMetadata
{
    public static readonly OAuthErrorContractMetadata Instance = new();

    private OAuthErrorContractMetadata() { }
}
