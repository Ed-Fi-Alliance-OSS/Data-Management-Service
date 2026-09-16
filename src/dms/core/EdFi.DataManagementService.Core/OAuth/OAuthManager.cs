// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
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
    /// A summary of what this replaces is recorded on the log under
    /// <see cref="DiscardedUnauthorizedDetailTemplate"/> rather than dropped, so the body's
    /// <c>correlationId</c> reaches the explanation here for the same reason it does for
    /// <see cref="GatewayErrorDetail"/>. Summary, not the body: see
    /// <c>SummarizeUpstreamFieldsForLogging</c> for what is kept and what is deliberately not.
    /// </remarks>
    private const string UnauthorizedFallbackDetail =
        "The upstream identity service rejected the request credentials.";

    /// <summary>
    /// The structured-logging template for the single event that preserves what can safely be
    /// kept of an upstream 401 body whose diagnostic content <see cref="UnauthorizedFallbackDetail"/>
    /// is about to replace.
    /// </summary>
    /// <remarks>
    /// Both summaries are bound as parameters and never interpolated into the template: they are
    /// built from a JSON error body, so they can contain braces, which the logging pipeline would
    /// otherwise read as property holes. They are separate parameters rather than one so that a
    /// structured sink can query them apart, and so that an oversized value in one cannot consume
    /// the other's share of <see cref="MaxLoggedUpstreamContentLength"/> - the cost being that the
    /// event's worst-case contribution is two bounded strings rather than one.
    /// </remarks>
    private const string DiscardedUnauthorizedDetailTemplate =
        "Upstream identity service rejected the credentials with no usable error_description; "
        + "what its body carried is recorded here because the body is withheld from the client "
        + "- {TraceId} - standard OAuth error fields: {StandardFields} - other field names "
        + "present, their values withheld: {OtherFieldNames}";

    /// <summary>
    /// RFC 6749 section 5.2 defines <c>error_uri</c> as a URI, which is why it is the one
    /// standard field reported by presence alone.
    /// </summary>
    private const string ErrorUriFieldName = "error_uri";

    /// <summary>
    /// The RFC 6749 section 5.2 error response members: the only upstream fields eligible to
    /// have their <em>values</em> written to the log.
    /// </summary>
    /// <remarks>
    /// Ordinal, because JSON member names are case-sensitive and RFC 6749 spells these lowercase.
    /// <c>error</c> costs nothing at all to log, since it is already forwarded to the client as
    /// the problem-details <c>title</c> (<see cref="Response.FailureResponse.ForUnauthorized"/>).
    /// <c>error_description</c> appears only when it arrived malformed - a well-formed one is
    /// precisely what suppresses this event. <c>error_uri</c> is never logged by value; see
    /// <see cref="ErrorUriFieldName"/>.
    ///
    /// Membership makes a field eligible, not safe: what a member actually contributes to the
    /// log is decided per value by <c>StandardFieldValueForLogging</c>.
    /// </remarks>
    private static readonly HashSet<string> _standardOAuthErrorFields = new(StringComparer.Ordinal)
    {
        "error",
        "error_description",
        ErrorUriFieldName,
    };

    /// <summary>
    /// Stands in for the value of a standard field that arrived as a JSON object or array, so an
    /// operator can tell a malformed upstream body from an absent field.
    /// </summary>
    private const string MalformedFieldValueMarker = "(malformed)";

    /// <summary>
    /// Stands in for the value of a standard field that is reported as present but never by
    /// value.
    /// </summary>
    private const string WithheldFieldValueMarker = "(withheld)";

    /// <summary>
    /// Upper bound on how many non-standard field <em>names</em> from an upstream error body reach
    /// the log.
    /// </summary>
    /// <remarks>
    /// The count is attacker-influenceable just as the length is, and bounding length alone does
    /// not bound it usefully: ten thousand one-character names fit inside
    /// <see cref="MaxLoggedUpstreamContentLength"/> and would still produce an unreadable line.
    /// 20 is roughly an order of magnitude above observed practice - RFC 6749 defines three
    /// members, and the verbose identity providers add a handful more (Okta's <c>errorCode</c>,
    /// <c>errorSummary</c>, <c>errorLink</c>, <c>errorId</c>, <c>errorCauses</c>) - which is the
    /// same headroom argument <see cref="MaxLoggedUpstreamContentLength"/> is chosen on. Exceeding
    /// it is reported rather than silently swallowed, via
    /// <see cref="LoggedFieldNameOverflowFormat"/>.
    /// </remarks>
    private const int MaxLoggedUpstreamFieldNames = 20;

    /// <summary>
    /// Appended when <see cref="MaxLoggedUpstreamFieldNames"/> is applied, so an operator can tell
    /// a body with twenty fields from one with twenty thousand.
    /// </summary>
    private const string LoggedFieldNameOverflowFormat = "...[{0} more]";

    /// <summary>
    /// Stands in for an empty summary, so that an absent group reads as deliberately empty rather
    /// than as a logging defect.
    /// </summary>
    private const string NoUpstreamFieldsMarker = "(none)";

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
            JsonObject? obj = null;
            if (parsed is not null)
            {
                obj = parsed.AsObject();

                // A standard field name does not establish that its contents are safe. RFC 6749
                // section 5.2 defines both of these as strings, and `JsonNode.ToString()` on a
                // member that arrived as an object or an array serializes the whole subtree - so
                // `{"error":{"client_secret":"..."}}` would otherwise be echoed verbatim to a
                // caller that has not authenticated. A malformed member falls back to the same
                // fixed default an absent one uses.
                error = ScalarValueOrNull(obj["error"]) ?? error;

                string? suppliedDescription = ScalarValueOrNull(obj["error_description"]);
                if (suppliedDescription is not null)
                {
                    errorDescription = suppliedDescription;
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
                //
                // `obj` is null only when the body was the JSON literal `null`, which parses to a
                // null JsonNode. There are no fields to summarize in that case, and the event
                // still has to fire: the correlation ID must lead somewhere.
                (string standardFields, string otherFieldNames) = obj is null
                    ? (NoUpstreamFieldsMarker, NoUpstreamFieldsMarker)
                    : SummarizeUpstreamFieldsForLogging(obj);

                logger.LogInformation(
                    DiscardedUnauthorizedDetailTemplate,
                    traceId.Value,
                    standardFields,
                    otherFieldNames
                );
            }

            return GenerateProblemDetailResponse(
                HttpStatusCode.Unauthorized,
                FailureResponse.ForUnauthorized(traceId, error, errorDescription)
            );
        }

        // Splits the top-level members of an upstream 401 body into the only two things the log
        // is allowed to carry: the values of the RFC 6749 section 5.2 error fields, and the bare
        // *names* of every other member.
        //
        // This is the synthesis of two review findings that pull against each other, and it is
        // worth knowing both before changing it.
        //
        // The first finding was that discarding the body outright loses the whole of why the
        // upstream rejected the request whenever that reason lives in a non-standard member -
        // `{"error":"invalid_client","reason":"client disabled"}`, where `reason` is the entire
        // answer. An operator holding a correlation ID would find the request and no explanation.
        //
        // The second finding was that the fix for the first copied the whole body into ordinary
        // production logs, which docs/LOGGING.md forbids outright: Information-level logs must
        // carry no request or response bodies, credentials or personal information. Sanitizing
        // and bounding the body answers log injection and log volume; it does not redact
        // anything. The remedy asked for was "explicitly selected, safe diagnostic fields".
        //
        // Taken literally that remedy reinstates the first finding, because the field carrying
        // the answer is by definition the one nobody knew to put on an allowlist. So: allowlisted
        // fields by value, everything else by name only. The operator learns that the upstream
        // also sent a `reason` and takes that to the identity provider's own logs, and DMS
        // persists no arbitrary payload.
        //
        // The residual loss is real and deliberate. The log shows that `reason` was present; it
        // never shows that it read "client disabled". That is the price of the no-payload policy,
        // paid knowingly. Do not "restore" full-body logging here without reading both findings.
        //
        // Being on the allowlist buys a field nothing beyond eligibility: an allowlisted name on
        // a nested object is a way back to the second finding by another route, so what a value
        // contributes is decided per value, by StandardFieldValueForLogging.
        static (string StandardFields, string OtherFieldNames) SummarizeUpstreamFieldsForLogging(
            JsonObject obj
        )
        {
            List<string> standard = [];
            List<string> otherNames = [];
            int otherCount = 0;

            // Enumeration order is the upstream document's own, which JsonObject preserves, so
            // the summary reads in the order the identity service wrote its body.
            foreach (KeyValuePair<string, JsonNode?> member in obj)
            {
                if (_standardOAuthErrorFields.Contains(member.Key))
                {
                    // No branch of StandardFieldValueForLogging throws - this must not become a
                    // second way out of this method, the first (JsonNode.Parse and AsObject on a
                    // non-object body) being tracked as DMS-1549 - and none can disturb the
                    // message template, because the result is bound as a parameter.
                    standard.Add($"{member.Key}={StandardFieldValueForLogging(member.Key, member.Value)}");
                    continue;
                }

                otherCount++;
                if (otherNames.Count < MaxLoggedUpstreamFieldNames)
                {
                    otherNames.Add(member.Key);
                }
            }

            if (otherCount > otherNames.Count)
            {
                otherNames.Add(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        LoggedFieldNameOverflowFormat,
                        otherCount - otherNames.Count
                    )
                );
            }

            // Sanitized and bounded on the way out, names included: a member name is as
            // attacker-influenceable as a member value, and an upstream is free to return one
            // carrying newlines or a megabyte of padding.
            return (
                SanitizeAndBoundForLogging(
                    standard.Count == 0 ? NoUpstreamFieldsMarker : string.Join(", ", standard)
                ),
                SanitizeAndBoundForLogging(
                    otherNames.Count == 0 ? NoUpstreamFieldsMarker : string.Join(", ", otherNames)
                )
            );
        }

        // What a standard field contributes to the log - its scalar text, or a marker standing
        // in for a value that is never logged. A member name earns a field its place in the
        // summary, and this decides what, if anything, of the value goes with it.
        static string StandardFieldValueForLogging(string name, JsonNode? value)
        {
            if (name == ErrorUriFieldName)
            {
                // A URI carries credentials in its query and userinfo components often enough
                // that no part of one is logged. A client secret in the query string is the
                // obvious case, and a one-time token in the path is no better. Presence is the
                // whole diagnostic, and the operator follows it up in the identity provider's
                // own logs.
                return WithheldFieldValueMarker;
            }

            if (value is null)
            {
                // A JSON null, which JsonObject surfaces as a null node. Kept distinct from the
                // malformed marker so an operator can tell an upstream that sent nothing from
                // one that sent something unloggable.
                return "null";
            }

            return ScalarValueOrNull(value) ?? MalformedFieldValueMarker;
        }

        // The text of a JSON scalar, or null for a JSON null, object or array. The container
        // cases are the point: `JsonNode.ToString()` on an object or an array renders its whole
        // subtree, which for an upstream error body is arbitrary payload - nested credentials
        // included - and neither a log sink nor a client response may receive it. Callers turn
        // the null into whatever their own safe default is.
        static string? ScalarValueOrNull(JsonNode? value) =>
            value is JsonValue scalar ? scalar.ToString() : null;

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
