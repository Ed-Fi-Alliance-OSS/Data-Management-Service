// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

public class TestHttpMessageHandler(HttpStatusCode httpStatusCode, string? responseContent = null)
    : HttpMessageHandler
{
    private readonly HttpStatusCode _httpStatusCode = httpStatusCode;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        if (_httpStatusCode.Equals(HttpStatusCode.OK))
        {
            var content = new StringContent(responseContent!, Encoding.UTF8, "application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        return _httpStatusCode switch
        {
            HttpStatusCode.BadRequest => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)),
            HttpStatusCode.Unauthorized => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Unauthorized)
            ),
            HttpStatusCode.Forbidden => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)),
            HttpStatusCode.NotFound => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)),
        };
    }
}
