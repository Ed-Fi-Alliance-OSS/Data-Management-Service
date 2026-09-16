// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.OAuth;

/// <summary>
/// Retrieves an access token via upstream OAuth Service.
/// </summary>
/// <param name="logger"></param>
public class OAuthManager(ILogger<OAuthManager> logger) : IOAuthManager
{
    /// <summary>
    /// The client-facing <c>detail</c> of every 502 this class produces, used by both the
    /// upstream-error branch and the <c>catch</c> branch so that the two are indistinguishable
    /// from outside.
    /// </summary>
    /// <remarks>
    /// <c>/oauth/token</c> is unauthenticated, so anything placed here is readable by any caller
    /// that can reach the endpoint. The upstream identity service's own response body must
    /// therefore never appear in it: its wording, error taxonomy, internal hostnames, realm names
    /// and any stack trace it carries are all disclosure, and its length is bounded only by what
    /// the upstream chooses to send. The body's <c>correlationId</c> is the supported way to
    /// reach the detail - it appears verbatim on the log entry that does record the upstream
    /// content, which is what FR-LOG-6 guarantees.
    /// </remarks>
    private const string GatewayErrorDetail =
        "The upstream identity service did not return a usable response. Contact your system "
        + "administrator with the correlationId from this response, which identifies the "
        + "corresponding server log entry.";

    /// <summary>
    /// The client-facing <c>detail</c> of a 401 whose upstream body carried no
    /// <c>error_description</c>, replacing what used to be the raw upstream body. Same disclosure
    /// argument as <see cref="GatewayErrorDetail"/>; the two parsed OAuth fields are passed
    /// through when present because they are part of the OAuth 2.0 error contract the client is
    /// entitled to, but an arbitrary body that merely failed to contain them is not.
    /// </summary>
    /// <remarks>
    /// Whatever this replaces is recorded on the log under
    /// <see cref="DiscardedUnauthorizedDetailTemplate"/> rather than dropped, so the body's
    /// <c>correlationId</c> reaches the explanation here for the same reason it does for
    /// <see cref="GatewayErrorDetail"/>.
    /// </remarks>
    private const string UnauthorizedFallbackDetail =
        "The upstream identity service rejected the request credentials.";

    /// <summary>
    /// The structured-logging template for the single event that preserves an upstream 401 body
    /// whose diagnostic content <see cref="UnauthorizedFallbackDetail"/> is about to replace.
    /// </summary>
    /// <remarks>
    /// The body is bound to <c>{Content}</c> and never interpolated into the template: a JSON
    /// error body is made of braces, which the logging pipeline would read as property holes.
    /// </remarks>
    private const string DiscardedUnauthorizedDetailTemplate =
        "Upstream identity service rejected the credentials with no usable error_description; "
        + "its body is recorded here because it is withheld from the client - {TraceId} - {Content}";

    /// <summary>
    /// Upper bound, in characters, on how much of an upstream error body reaches the log.
    /// </summary>
    /// <remarks>
    /// The body is attacker-influenceable in size as well as in content - a caller that can
    /// provoke a large upstream error can otherwise write an unbounded amount of text into the
    /// log on every unauthenticated request. 2048 was chosen as roughly an order of magnitude
    /// above a realistic OAuth error payload (an <c>error</c>/<c>error_description</c> pair runs
    /// to a couple of hundred characters, and a verbose identity provider's stack-trace-bearing
    /// body to a few hundred more), so a genuine diagnostic arrives intact while a padded one is
    /// cut off well before it can dominate a log line.
    /// </remarks>
    private const int MaxLoggedUpstreamContentLength = 2048;

    /// <summary>
    /// Appended when <see cref="MaxLoggedUpstreamContentLength"/> is applied, so an operator can
    /// tell a short upstream body from a truncated one.
    /// </summary>
    private const string LoggedContentTruncationSuffix = "...[truncated]";

    public async Task<HttpResponseMessage> GetAccessTokenAsync(
        IHttpClientWrapper httpClient,
        string grantType,
        string authHeaderString,
        string upstreamUri,
        TraceId traceId
    )
    {
        logger.LogInformation("GetAccessTokenAsync - {TraceId}", traceId.Value);

        if (!authHeaderString.Contains("basic", StringComparison.InvariantCultureIgnoreCase))
        {
            return GenerateProblemDetailResponse(
                HttpStatusCode.BadRequest,
                FailureResponse.ForBadRequest("Malformed Authorization header", traceId, [], [])
            );
        }

        if (!grantType.Equals("client_credentials", StringComparison.InvariantCultureIgnoreCase))
        {
            return GenerateProblemDetailResponse(
                HttpStatusCode.BadRequest,
                FailureResponse.ForBadRequest("Unsupported grant type", traceId, [], [])
            );
        }

        HttpRequestMessage upstreamRequest = new(HttpMethod.Post, upstreamUri);
        upstreamRequest.Headers.Add("Authorization", authHeaderString);

        // Use FormUrlEncodedContent for proper URL encoding of user-provided values
        upstreamRequest.Content = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("grant_type", grantType),
        ]);

        // In case of 5xx Error, pass 503 Service unavailable to client, otherwise forward response directly to client.
        try
        {
            logger.LogInformation("Forwarding token request to upstream service - {TraceId}", traceId.Value);
            var response = await httpClient.SendAsync(upstreamRequest);

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    return response;
                case HttpStatusCode.Unauthorized:
                    return await GenerateUnauthorizedResponse(logger, traceId, response);
                default:
                    var content = await response.Content.ReadAsStringAsync();
                    logger.LogWarning(
                        "Error from upstream identity service - {TraceId} - {Content}",
                        traceId.Value,
                        SanitizeAndBoundForLogging(content)
                    );
                    return GenerateProblemDetailResponse(
                        HttpStatusCode.BadGateway,
                        FailureResponse.ForGatewayError(traceId, GatewayErrorDetail)
                    );
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error from upstream identity service - {TraceId}", traceId.Value);
            return GenerateProblemDetailResponse(
                HttpStatusCode.BadGateway,
                FailureResponse.ForGatewayError(traceId, GatewayErrorDetail)
            );
        }

        // Attempts to read `{ "error": "...", "error_description": "..."}` from the response
        // body, with sensible fallback mechanism if the response is in a different format.
        // The logger is a parameter because this is a `static` local function and so cannot
        // capture the enclosing `logger`; it is kept static deliberately, to keep the accidental
        // capture of anything else out of reach.
        static async Task<HttpResponseMessage> GenerateUnauthorizedResponse(
            ILogger logger,
            TraceId traceId,
            HttpResponseMessage response
        )
        {
            var body = await response.Content.ReadAsStringAsync();
            var error = "Unauthorized";
            // Not `body`: the fallback used to hand the raw upstream body to the caller whenever
            // it parsed as a JSON object but happened not to carry an `error_description`, which
            // is the same unauthenticated disclosure as the 502 branch above.
            var errorDescription = UnauthorizedFallbackDetail;
            var upstreamSuppliedDescription = false;

            JsonNode? parsed = JsonNode.Parse(body);
            if (parsed is not null)
            {
                var obj = parsed.AsObject();
                if (obj.ContainsKey("error"))
                {
                    error = obj["error"]!.ToString();
                }
                if (obj.ContainsKey("error_description"))
                {
                    errorDescription = obj["error_description"]!.ToString();
                    upstreamSuppliedDescription = true;
                }
            }

            if (!upstreamSuppliedDescription)
            {
                // Only on the fallback, not on every 401: a 401 is client-triggered and routine,
                // and when the upstream did supply an `error_description` the client already has
                // it. This is the one point at which information is about to be discarded.
                //
                // Information rather than the Warning its 502 sibling uses, because a rejected
                // credential is not a condition an operator must act on (docs/LOGGING.md) - but
                // not Debug either, since DMS ships at Information and an unemitted event would
                // leave the correlation ID pointing at nothing, the defect being fixed.
                logger.LogInformation(
                    DiscardedUnauthorizedDetailTemplate,
                    traceId.Value,
                    SanitizeAndBoundForLogging(body)
                );
            }

            return GenerateProblemDetailResponse(
                HttpStatusCode.Unauthorized,
                FailureResponse.ForUnauthorized(traceId, error, errorDescription)
            );
        }

        // Sanitize first and truncate second. The order is observable: an upstream body padded
        // with control characters would, under truncate-then-sanitize, spend the whole budget on
        // characters the sanitizer then removes, so the diagnostic content that follows the
        // padding would never reach the log even though the logged value came in far under the
        // cap.
        static string SanitizeAndBoundForLogging(string? content)
        {
            string sanitized = LoggingSanitizer.SanitizeFreeTextForLogging(content);

            if (sanitized.Length <= MaxLoggedUpstreamContentLength)
            {
                return sanitized;
            }

            // The cap counts UTF-16 code units, so the cut can land between the halves of a
            // surrogate pair and manufacture a lone high surrogate that the upstream body never
            // contained - 2047 ASCII characters followed by an emoji is the whole of it. That is
            // the same defect this branch exists to prevent elsewhere, so it is backed off here
            // rather than tolerated: an unpaired surrogate reaches a JSON-formatted log sink as
            // U+FFFD and a plain-text one as the raw code unit, so two sinks reading the same
            // event disagree about what the upstream service said. The astral character is
            // dropped whole rather than half-kept, costing one code unit of a 2048-unit budget.
            //
            // One test, deliberately not the two-part test in CorrelationIdNormalizer.Normalize
            // - do not "restore" the missing half. That method cuts *unsanitized* input, where a
            // lone high surrogate can sit at the boundary. Here the cut happens after
            // sanitization, which drops every unpaired surrogate, so a high surrogate still
            // present is necessarily paired.
            //
            // The index is in bounds because the branch runs only when sanitized.Length exceeds
            // the cap, and `retained` is never driven below cap - 1.
            int retained = MaxLoggedUpstreamContentLength;
            if (char.IsHighSurrogate(sanitized[retained - 1]))
            {
                retained--;
            }

            // The suffix is appended whichever way the boundary moved. It is the operator's only
            // signal that anything was cut at all, and backing off must not cost it.
            return string.Concat(sanitized.AsSpan(0, retained), LoggedContentTruncationSuffix);
        }

        static HttpResponseMessage GenerateProblemDetailResponse(
            HttpStatusCode statusCode,
            JsonNode failureResponse
        )
        {
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    failureResponse.ToString(),
                    Encoding.UTF8,
                    "application/problem+json"
                ),
            };
        }
    }
}
