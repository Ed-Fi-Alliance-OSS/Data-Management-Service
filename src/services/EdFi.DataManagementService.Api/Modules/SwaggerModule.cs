// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.


using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Api.Configuration;
using EdFi.DataManagementService.Api.Content;
using EdFi.DataManagementService.Api.Core;
using EdFi.DataManagementService.Api.Infrastructure.Extensions;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Api.Modules
{
    public partial class SwaggerModule : IModule
    {
        [GeneratedRegex(@"v1\/(?<section>[^-]+)-spec.json?")]
        private static partial Regex PathExpression();

        private readonly string[] Sections = ["resources", "descriptors", "discovery"];
        private readonly string ErrorResourcePath = "Invalid resource path";

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/v1/{section}-spec.json", GetMetaDataDocument);
        }

        internal async Task GetMetaDataDocument(
            HttpContext httpContext,
            IApiSchemaTranslator translator,
            IOptions<AppSettings> options
        )
        {
            var request = httpContext.Request;
            Match match = PathExpression().Match(request.Path);
            if (!match.Success)
            {
                httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await httpContext.Response.WriteAsync(ErrorResourcePath);
                return;
            }

            string section = match.Groups["section"].Value.ToLower();
            string? rootUrl = request.RootUrl();
            if (
                Array.Exists(
                    Sections,
                    x => x.ToLowerInvariant().Equals(section, StringComparison.InvariantCultureIgnoreCase)
                )
            )
            {
                var content = translator.TranslateToSwagger(rootUrl, "/data");
                if (content != null)
                {
                    content["openapi"] = "3.0.1";
                }
                await httpContext.Response.WriteAsSerializedJsonAsync(content);
            }
            else
            {
                httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await httpContext.Response.WriteAsync(ErrorResourcePath);
            }
        }
    }
}
