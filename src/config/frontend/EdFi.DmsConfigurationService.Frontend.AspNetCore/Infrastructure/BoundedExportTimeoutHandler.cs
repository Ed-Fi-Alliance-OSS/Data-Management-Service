// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Enforces an upper bound on each outgoing OTLP export request regardless of protocol. The
/// sink's gRPC exporter issues calls without a deadline, so a stalled collector would otherwise
/// hold the batch worker and block logger disposal indefinitely; HTTP would be bounded only by
/// HttpClient's 100-second default.
/// </summary>
internal sealed class BoundedExportTimeoutHandler : DelegatingHandler
{
    private readonly TimeSpan _timeout;

    public BoundedExportTimeoutHandler(TimeSpan timeout, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _timeout = timeout;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        return await base.SendAsync(request, timeoutSource.Token);
    }
}
