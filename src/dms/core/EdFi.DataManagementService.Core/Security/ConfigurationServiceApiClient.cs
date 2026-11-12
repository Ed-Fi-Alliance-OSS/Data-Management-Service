// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EdFi.DataManagementService.Core.Security;

public class ConfigurationServiceApiClient(HttpClient httpClient)
{
    public HttpClient Client { get; } = httpClient;

    /// <summary>
    /// Decodes and parses a protected response without exposing upstream text in failures. Charset
    /// errors happen after SendAsync's buffering; JSON errors can also include response member names.
    /// Keep this outside authentication recovery: a malformed success is never a rejected bearer.
    /// </summary>
    internal static async Task<T> ReadResponseAsync<T>(
        HttpResponseMessage response,
        Func<string, T> deserialize,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return deserialize(body);
        }
        catch (OperationCanceledException failure)
        {
            throw SafeCancellation(failure);
        }
        catch (JsonException)
        {
            throw new JsonException("Configuration Service response contained invalid JSON.");
        }
        catch (Exception)
        {
            throw new HttpRequestException(
                HttpRequestError.InvalidResponse,
                "Configuration Service response could not be decoded."
            );
        }
    }

    /// <summary>
    /// Sends one authenticated Configuration Service read with the current DMS bearer. When the read
    /// is rejected with 401, it obtains a replacement for the generation it actually sent and sends a
    /// fresh request from <paramref name="createRequest" /> once. A second 401 surfaces to the caller;
    /// no third read or further grant is attempted for this call.
    /// </summary>
    /// <remarks>
    /// Only a protected read's 401 starts recovery. It is recognized by the status the registered
    /// response handler carries on its <see cref="HttpRequestException" />, so a failed grant, which
    /// the token handler reports separately, is never mistaken for a rejected read. Each attempt uses
    /// its own request, which is disposed with it; the caller owns and disposes a returned response.
    /// </remarks>
    internal async Task<HttpResponseMessage> SendAuthenticatedAsync(
        IConfigurationServiceTokenHandler tokenHandler,
        ConfigurationServiceContext context,
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken = default
    )
    {
        ConfigurationServiceTokenLease lease = await tokenHandler.GetTokenAsync(
            context.clientId,
            context.clientSecret,
            context.scope,
            cancellationToken
        );

        try
        {
            return await SendWithAsync(lease, createRequest, cancellationToken);
        }
        catch (HttpRequestException rejected) when (rejected.StatusCode is HttpStatusCode.Unauthorized)
        {
            // Recover below, outside this handler, so the replay is not nested in the first failure.
        }

        ConfigurationServiceTokenLease replacement = await tokenHandler.RefreshRejectedTokenAsync(
            lease,
            context.clientId,
            context.clientSecret,
            context.scope,
            cancellationToken
        );

        // A second 401 surfaces as is. The replacement stays current, so reads it still satisfies
        // keep using it. A later call's first 401 can recover independently.
        return await SendWithAsync(replacement, createRequest, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithAsync(
        ConfigurationServiceTokenLease lease,
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken
    )
    {
        using HttpRequestMessage request = createRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lease.AccessToken);

        try
        {
            return await Client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException failure)
        {
            // This boundary includes HttpClient's response buffering, which happens after the
            // response handler returns. A malformed header or body can put echoed credentials in
            // transport exceptions. Preserve status-based recovery and the error category, but
            // never let upstream text or its inner exceptions reach provider logs.
            string message = failure.StatusCode is HttpStatusCode status
                ? $"Configuration Service request failed with status code {(int)status}."
                : $"Configuration Service request failed during transport ({failure.HttpRequestError}).";
            throw new HttpRequestException(
                failure.HttpRequestError,
                message,
                inner: null,
                failure.StatusCode
            );
        }
        catch (OperationCanceledException failure)
        {
            // HttpClient can wrap a transport failure in a timeout or cancellation exception.
            // Keep cancellation and its token while discarding any echoed text and inner failures.
            throw SafeCancellation(failure);
        }
    }

    private static OperationCanceledException SafeCancellation(OperationCanceledException failure) =>
        failure is TaskCanceledException
            ? new TaskCanceledException(
                "Configuration Service request was canceled.",
                innerException: null,
                failure.CancellationToken
            )
            : new OperationCanceledException(
                "Configuration Service request was canceled.",
                innerException: null,
                failure.CancellationToken
            );
}
