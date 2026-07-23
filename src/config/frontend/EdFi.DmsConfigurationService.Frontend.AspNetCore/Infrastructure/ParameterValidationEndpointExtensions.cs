// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.AspNetCore.Builder;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

public static class ParameterValidationEndpointExtensions
{
    /// <summary>
    /// Attaches <see cref="ParameterValidationMetadata"/> for the endpoint's <c>[AsParameters]</c> query
    /// DTO. Read by the global exception handler (via <c>IExceptionHandlerFeature.Endpoint</c>) so a
    /// query-binding failure (e.g. offset=abc) can be classified as urn:ed-fi:api:bad-request:parameter
    /// without inspecting the framework exception message. The metadata is built once at endpoint
    /// construction.
    /// </summary>
    public static RouteHandlerBuilder WithQueryParameterValidation<TQuery>(
        this RouteHandlerBuilder builder
    ) => builder.WithMetadata(ParameterValidationMetadata.ForQueryType(typeof(TQuery)));
}
