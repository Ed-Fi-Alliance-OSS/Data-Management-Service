// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

public class TestHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _httpStatusCode;
    private readonly Dictionary<string, string> _responses = [];

    public TestHttpMessageHandler(HttpStatusCode httpStatusCode, string? responseContent = null)
    {
        _httpStatusCode = httpStatusCode;
        if (responseContent != null)
        {
            _responses["default"] = responseContent;
        }
    }

    public void SetResponse(string url, object responseObject)
    {
        var content = System.Text.Json.JsonSerializer.Serialize(responseObject);
        _responses[url] = content;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        if (
            _httpStatusCode.Equals(HttpStatusCode.OK)
            && _responses.TryGetValue(request.RequestUri!.ToString(), out var content)
        )
        {
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content, Encoding.UTF8, "application/json"),
                }
            );
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
