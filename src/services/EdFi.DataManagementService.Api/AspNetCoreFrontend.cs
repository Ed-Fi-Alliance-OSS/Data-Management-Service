// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core;
using EdFi.DataManagementService.Api.Core.Middleware;
using EdFi.DataManagementService.Api.Core.Model;

namespace EdFi.DataManagementService.Api
{
    /// <summary>
    /// A thin static class that converts from ASP.NET Core to the DMS facade.
    /// </summary>
    public static class Frontend
    {
        /// <summary>
        /// Takes an HttpRequest and returns a deserialized request body
        /// </summary>
        private static async Task<JsonNode?> ExtractJsonBodyFrom(HttpRequest request)
        {
            using Stream body = request.Body;
            using StreamReader bodyReader = new(body);
            var requestBodyString = await bodyReader.ReadToEndAsync();

            if (string.IsNullOrEmpty(requestBodyString)) return null;

            return JsonNode.Parse(requestBodyString);
        }

        /// <summary>
        /// ASP.NET Core entry point for API POST requests to DMS
        /// </summary>
        public static async Task<IResult> Upsert(HttpRequest request, ICoreFacade coreFacade)
        {
            JsonNode? body = await ExtractJsonBodyFrom(request);

            FrontendRequest frontendRequest = new(Method: RequestMethod.POST, Body: body, Path: request.Path, TraceId: request.HttpContext.TraceIdentifier);
            FrontendResponse frontendResponse = await coreFacade.Upsert(frontendRequest);

            return Results.Content(statusCode: frontendResponse.StatusCode, content: frontendResponse.Body);
        }

        /// <summary>
        /// ASP.NET Core entry point for all API GET by id requests to DMS
        /// </summary>
        public static async Task<IResult> GetById(HttpRequest request, ICoreFacade coreFacade)
        {
            FrontendRequest frontendRequest = new(Method: RequestMethod.GET, Body: null, Path: request.Path, TraceId: request.HttpContext.TraceIdentifier);
            FrontendResponse frontendResponse = await coreFacade.GetById(frontendRequest);

            return Results.Content(statusCode: frontendResponse.StatusCode, content: frontendResponse.Body);
        }

        /// <summary>
        /// ASP.NET Core entry point for all API PUT requests to DMS, which are "by id"
        /// </summary>
        public static async Task<IResult> UpdateById(HttpRequest request, ICoreFacade coreFacade)
        {
            JsonNode? body = await ExtractJsonBodyFrom(request);

            FrontendRequest frontendRequest = new(Method: RequestMethod.PUT, Body: body, Path: request.Path, TraceId: request.HttpContext.TraceIdentifier);
            FrontendResponse frontendResponse = await coreFacade.GetById(frontendRequest);

            return Results.Content(statusCode: frontendResponse.StatusCode, content: frontendResponse.Body);
        }

        /// <summary>
        /// ASP.NET Core entry point for all API DELETE requests to DMS, which are "by id"
        /// </summary>
        public static async Task<IResult> DeleteById(HttpRequest request, ICoreFacade coreFacade)
        {
            FrontendRequest frontendRequest = new(Method: RequestMethod.DELETE, Body: null, Path: request.Path, TraceId: request.HttpContext.TraceIdentifier);
            FrontendResponse frontendResponse = await coreFacade.GetById(frontendRequest);

            return Results.Content(statusCode: frontendResponse.StatusCode, content: frontendResponse.Body);
        }
    }
}
