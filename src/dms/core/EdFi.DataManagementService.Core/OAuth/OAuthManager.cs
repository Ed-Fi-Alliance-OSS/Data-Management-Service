// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
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
    /// The fixed client-facing description of every 502 this class produces.
    /// </summary>
    /// <remarks>
    /// <c>/oauth/token</c> is unauthenticated, so anything placed here is readable by any caller
    /// that can reach the endpoint. The upstream identity service's own response body must
    /// therefore never appear in it: its wording, error taxonomy, internal hostnames, realm names
    /// and any stack trace it carries are all disclosure, and its length is bounded only by what
    /// the upstream chooses to send. Decode the <c>X-Correlation-ID</c> response header once
    /// to find the normalized trace value on the corresponding log entry.
    /// </remarks>
    private const string GatewayErrorDetail = OAuthErrorResponse.GatewayDescription;

    /// <summary>
    /// The client-facing description of a 401 whose upstream body carried no usable
    /// <c>error_description</c>.
    /// </summary>
    /// <remarks>
    /// Same disclosure argument as <see cref="GatewayErrorDetail"/>. The parsed OAuth
    /// description is passed through only when usable; the error is always invalid_client.
    /// What this replaces is summarized on the log under
    /// <see cref="DiscardedUnauthorizedDetailTemplate"/> rather than dropped, so the response's
    /// decoded <c>X-Correlation-ID</c> still reaches it - see <c>SummarizeUpstreamFieldsForLogging</c> for
    /// what that summary keeps and what it deliberately does not.
    /// </remarks>
    private const string UnauthorizedFallbackDetail = OAuthErrorResponse.UnauthorizedFallbackDescription;

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

    private const string DiscardedProviderDescriptionTemplate =
        "Upstream identity service returned an unusable error_description; its fields are "
        + "summarized here while the fixed DMS description is returned - {TraceId} - standard "
        + "OAuth error fields: {StandardFields} - other field names present, their values "
        + "withheld: {OtherFieldNames}";

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
    /// <c>error</c> is eligible for scalar summary even when the wire code is normalized.
    /// <c>error_description</c> reaches this summary only when its value was not a usable string
    /// - including invalid decoded characters, a JSON object or array, or a JSON null - because a usable one
    /// is precisely what suppresses this event. <c>error_uri</c> is never logged by value; see
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
    /// The <c>type</c> a problem-details token-limit rejection from the Configuration Service
    /// carries. The Configuration Service has answered in three shapes: the Management API's
    /// <c>urn:ed-fi:management-api</c> problem-details type, an earlier <c>urn:ed-fi:api</c>
    /// problem-details type, and an OAuth envelope (see
    /// <see cref="TemporarilyUnavailableOAuthError"/>). Each type is matched exactly, so a DMS
    /// deployed beside any version still recognizes the rejection. DMS returns an OAuth error
    /// rather than forwarding any of them.
    /// </summary>
    private const string LegacyTooManyTokensType = "urn:ed-fi:api:security:authentication:too-many-tokens";

    private const string ManagementApiTooManyTokensType =
        "urn:ed-fi:management-api:security:authentication:too-many-tokens";

    /// <summary>
    /// The <c>error</c> value of both OAuth-envelope 429 answers, the token limit and the rate limit.
    /// Each is only believed together with its canonical <c>error_description</c>.
    /// </summary>
    private const string TemporarilyUnavailableOAuthError = "temporarily_unavailable";

    /// <summary>
    /// The <c>error_description</c> of the Configuration Service's OAuth-envelope rate-limit answer,
    /// the same text DMS uses for its own rate-limit rejection. The CMS DataModel's
    /// <c>FailureResponse.TooManyRequestsMessage</c> is its source, and <c>OAuthManagerTests</c> builds
    /// the envelope from that constant.
    /// </summary>
    private const string RateLimitOAuthErrorDescription =
        "The number of allowed requests has been exceeded. Retry the request later.";

    /// <summary>
    /// The canonical token-limit message, split either side of the limit it carries. Each half is
    /// used twice - as the shape an upstream body must match to be believed, and as a piece the
    /// returned message is rebuilt from - so the parse and the format cannot drift apart from one
    /// another. The same sentence is carried by the problem-details <c>errors</c> entry and by the
    /// OAuth envelope's <c>error_description</c>. Staying in step with the CMS formatter that
    /// writes the upstream body is a separate obligation, held by unit tests that feed this parser
    /// the sentence the CMS DataModel's <c>FailureResponse.TooManyTokensMessage</c> produces (the
    /// older problem-details shapes are covered by hand-built bodies): a reworded CMS message
    /// fails those tests rather than silently degrading every token-limit rejection to the generic
    /// 429. Separately deployed DMS and CMS versions can still disagree, which is what the
    /// fallback's warning is for.
    /// </summary>
    private const string TokenLimitMessagePrefix = "Too many access tokens have been requested (limit is ";

    private const string TokenLimitMessageSuffix = "). Access tokens should be reused until they expire.";

    // RFC 6749 section 5.2 codes for local authentication and request failures. The corresponding
    // fixed descriptions never echo anything the caller sent.
    private const string InvalidClientError = "invalid_client";

    private const string InvalidRequestError = "invalid_request";

    /// <summary>
    /// Upper bound, in characters, on each of a body <c>client_id</c> and <c>client_secret</c>.
    /// </summary>
    /// <remarks>
    /// A credential in a header was always bounded by the server's request-header limit, but a body
    /// value is bounded only by the request-body limit, which defaults to megabytes. Percent-encoded
    /// and base64-encoded, such a value becomes an upstream header that the identity service rejects
    /// or resets, and the caller's input error would surface as a 502 and an upstream-failure log
    /// entry on every unauthenticated attempt. 2048 is double the longest secret the Configuration
    /// Service accepts (<c>ClientSecretValidationOptions.MaximumAllowedLength</c> is 1024), so no
    /// legitimate credential comes near it. The cap bounds the work and the header size rather than
    /// guaranteeing the upstream accepts the header: an ASCII credential pair at the cap encodes to
    /// at most about 16 KB, but a pair made entirely of non-ASCII characters, at up to nine escaped
    /// characters each, can still reach about 50 KB and be refused upstream. Counted as
    /// <see cref="string.Length"/> counts, in UTF-16 code units.
    /// </remarks>
    private const int MaxBodyCredentialLength = 2048;

    /// <summary>
    /// States the cap and never the value, which may be a secret.
    /// </summary>
    private static readonly string _oversizedBodyCredentialDetail =
        $"client_id and client_secret must each be at most {MaxBodyCredentialLength.ToString(CultureInfo.InvariantCulture)} characters";

    /// <summary>
    /// The upstream <c>Authorization</c> value for credentials the caller sent in the request body,
    /// built as RFC 6749 section 2.3.1 defines it: each value encoded, joined with a colon, then
    /// base64-encoded.
    /// </summary>
    /// <remarks>
    /// Percent-encoding (<see cref="Uri.EscapeDataString"/>) rather than form encoding, because
    /// form encoding writes a space as <c>+</c> and the Configuration Service's lenient
    /// <c>/connect/token</c> parser decodes with <c>Uri.UnescapeDataString</c>, which leaves a
    /// <c>+</c> as a literal plus. Percent-escapes decode the same under that parser, under its
    /// strict one and under Keycloak, and encoding also stops a colon in the client ID from moving
    /// the split. The result is a credential: it is never logged.
    /// </remarks>
    private static string BasicAuthorizationFrom(string clientId, string clientSecret) =>
        "Basic "
        + Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(clientSecret)}")
        );

    public async Task<HttpResponseMessage> GetAccessTokenAsync(
        IHttpClientWrapper httpClient,
        string grantType,
        string? authHeaderString,
        TokenRequestCredentials bodyCredentials,
        string upstreamUri,
        TraceId traceId
    )
    {
        logger.LogInformation("GetAccessTokenAsync - {TraceId}", traceId.Value);
        bool callerUsedAuthorization = authHeaderString is not null;

        // The checks run in the Ed-Fi ODS/API's order: the Authorization header's shape and scheme,
        // then mixed mechanisms, then missing credentials, all before the grant type. The length
        // cap, which the ODS/API does not have, comes last among the credential checks.
        string upstreamAuthorization;
        if (authHeaderString is not null)
        {
            // A header that is present but empty is a malformed attempt, not an absent header: the
            // ODS/API answers it with 400 invalid_request before it looks at the body. A
            // whitespace-only value reaches here trimmed to empty.
            if (string.IsNullOrWhiteSpace(authHeaderString))
            {
                return OAuthErrorResponse.Create(
                    HttpStatusCode.BadRequest,
                    InvalidRequestError,
                    OAuthErrorResponse.EmptyAuthorizationDescription,
                    traceId
                );
            }

            // The same lenient test DMS has always applied, so a caller that authenticates with a
            // Basic header sees no change. Known limitation, kept deliberately: a header of another
            // scheme that merely contains "basic" anywhere passes it, and is treated as Basic
            // rather than rejected (see the DMS-1604 ADR). Any other scheme is not client
            // authentication, and body credentials do not rescue the request.
            if (!authHeaderString.Contains("basic", StringComparison.InvariantCultureIgnoreCase))
            {
                return OAuthErrorResponse.Create(
                    HttpStatusCode.Unauthorized,
                    InvalidClientError,
                    OAuthErrorResponse.NonBasicSchemeDescription,
                    traceId
                );
            }

            // RFC 6749 section 2.3 allows one mechanism per request, and the Ed-Fi ODS/API rejects
            // the combination too. One non-blank body field is enough, so a partial combination is
            // still mixed. An empty field counts as omitted (RFC 6749 section 3.2) and a
            // whitespace-only one as blank, as in the ODS/API, so neither makes the request mixed.
            if (bodyCredentials.HasNonBlankField)
            {
                return OAuthErrorResponse.Create(
                    HttpStatusCode.BadRequest,
                    InvalidRequestError,
                    OAuthErrorResponse.MixedMechanismsDescription,
                    traceId
                );
            }

            // Forwarded verbatim, as before body credentials were accepted.
            upstreamAuthorization = authHeaderString;
        }
        else if (
            string.IsNullOrWhiteSpace(bodyCredentials.ClientId)
            || string.IsNullOrWhiteSpace(bodyCredentials.ClientSecret)
        )
        {
            bool hasAnyCredential = bodyCredentials.HasNonBlankField;
            return OAuthErrorResponse.Create(
                hasAnyCredential ? HttpStatusCode.BadRequest : HttpStatusCode.Unauthorized,
                hasAnyCredential ? InvalidRequestError : InvalidClientError,
                OAuthErrorResponse.MissingCredentialsDescription,
                traceId
            );
        }
        else if (
            bodyCredentials.ClientId.Length > MaxBodyCredentialLength
            || bodyCredentials.ClientSecret.Length > MaxBodyCredentialLength
        )
        {
            return OAuthErrorResponse.Create(
                HttpStatusCode.BadRequest,
                InvalidRequestError,
                _oversizedBodyCredentialDetail,
                traceId
            );
        }
        else
        {
            upstreamAuthorization = BasicAuthorizationFrom(
                bodyCredentials.ClientId,
                bodyCredentials.ClientSecret
            );
        }

        // The static form, because a caller can bind a JSON null into this parameter, and a null
        // grant type is missing rather than a fault.
        if (string.IsNullOrEmpty(grantType))
        {
            return OAuthErrorResponse.Create(
                HttpStatusCode.BadRequest,
                InvalidRequestError,
                OAuthErrorResponse.MissingGrantDescription,
                traceId
            );
        }

        if (!string.Equals(grantType, "client_credentials", StringComparison.InvariantCultureIgnoreCase))
        {
            return OAuthErrorResponse.Create(
                HttpStatusCode.BadRequest,
                "unsupported_grant_type",
                "Unsupported grant type",
                traceId
            );
        }

        HttpRequestMessage upstreamRequest = new(HttpMethod.Post, upstreamUri);
        try
        {
            upstreamRequest.Headers.Add("Authorization", upstreamAuthorization);
        }
        catch (FormatException)
        {
            // A caller-supplied header that passed the lenient Basic test above but is not a valid
            // Authorization value, such as "Basicxyz:secret" or two headers joined with a comma.
            // The exception's message quotes the whole header, so it is neither logged nor passed
            // on; the ODS/API answers such a header 400 as well.
            return OAuthErrorResponse.Create(
                HttpStatusCode.BadRequest,
                InvalidRequestError,
                OAuthErrorResponse.MalformedAuthorizationDescription,
                traceId
            );
        }

        // Only grant_type goes in the body. Body credentials travel upstream in the Basic header
        // built above and never as form fields, so the upstream sees one contract whichever way
        // the caller authenticated.
        // Use FormUrlEncodedContent for proper URL encoding of user-provided values
        upstreamRequest.Content = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("grant_type", grantType),
        ]);

        // Keep upstream classification and its safe client-facing description at the token boundary.
        try
        {
            logger.LogInformation("Forwarding token request to upstream service - {TraceId}", traceId.Value);
            var response = await httpClient.SendAsync(upstreamRequest);

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    return response;
                case HttpStatusCode.Unauthorized:
                    return await GenerateProviderUnauthorizedResponse(logger, traceId, response);
                case HttpStatusCode.BadRequest:
                    return await GenerateProviderBadRequestResponse(
                        logger,
                        traceId,
                        response,
                        callerUsedAuthorization
                    );
                case HttpStatusCode.TooManyRequests:
                    return await GenerateTooManyTokensResponse(logger, traceId, response);
                case HttpStatusCode.Conflict:
                    // The Configuration Service answers 409 when a token grant timed out waiting
                    // for a database lock or was chosen as a deadlock victim: contention, which
                    // retrying resolves. Without this arm it would fall through to the 502 branch
                    // below, which tells the caller the upstream failed. 503 is DMS's own answer
                    // to a transient upstream condition, and the one standard retry policies act
                    // on. The status is the whole signal, so the body is neither parsed nor
                    // logged, and no Retry-After is sent because nothing upstream supplies one.
                    logger.LogInformation(
                        "Upstream identity service reported lock contention on the token grant; "
                            + "answered 503 so the client retries - {TraceId}",
                        traceId.Value
                    );
                    return OAuthErrorResponse.Create(
                        HttpStatusCode.ServiceUnavailable,
                        "temporarily_unavailable",
                        OAuthErrorResponse.TemporarilyUnavailableDescription,
                        traceId
                    );
                default:
                    var content = await response.Content.ReadAsStringAsync();
                    logger.LogWarning(
                        "Error from upstream identity service - {TraceId} - {Content}",
                        traceId.Value,
                        LoggingSanitizer.SanitizeFreeTextForLogging(content, MaxLoggedUpstreamContentLength)
                    );
                    return OAuthErrorResponse.Create(
                        HttpStatusCode.BadGateway,
                        "server_error",
                        GatewayErrorDetail,
                        traceId
                    );
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error from upstream identity service - {TraceId}", traceId.Value);
            return OAuthErrorResponse.Create(
                HttpStatusCode.BadGateway,
                "server_error",
                GatewayErrorDetail,
                traceId
            );
        }

        static async Task<HttpResponseMessage> GenerateProviderUnauthorizedResponse(
            ILogger logger,
            TraceId traceId,
            HttpResponseMessage response
        )
        {
            string body = await ReadStatusClassifiedBody(response.Content);
            JsonObject obj = JsonObjectOrNull(body) ?? [];
            string description = GetStringOrNull(obj["error_description"]) ?? string.Empty;
            bool hasUsableDescription = IsUsableOAuthDescription(description);

            if (!hasUsableDescription)
            {
                (string standardFields, string otherFieldNames) = SummarizeUpstreamFieldsForLogging(obj);

                logger.LogInformation(
                    DiscardedUnauthorizedDetailTemplate,
                    traceId.Value,
                    standardFields,
                    otherFieldNames
                );
            }

            return OAuthErrorResponse.Create(
                HttpStatusCode.Unauthorized,
                InvalidClientError,
                hasUsableDescription ? description : UnauthorizedFallbackDetail,
                traceId
            );
        }

        static async Task<HttpResponseMessage> GenerateProviderBadRequestResponse(
            ILogger logger,
            TraceId traceId,
            HttpResponseMessage response,
            bool callerUsedAuthorization
        )
        {
            string body = await ReadStatusClassifiedBody(response.Content);
            if (
                JsonObjectOrNull(body) is not JsonObject obj
                || GetStringOrNull(obj["error"]) is not string error
                || !IsRecognizedTokenError(error)
            )
            {
                logger.LogWarning(
                    "Error from upstream identity service - {TraceId} - {Content}",
                    traceId.Value,
                    LoggingSanitizer.SanitizeFreeTextForLogging(body, MaxLoggedUpstreamContentLength)
                );
                return OAuthErrorResponse.Create(
                    HttpStatusCode.BadGateway,
                    "server_error",
                    GatewayErrorDetail,
                    traceId
                );
            }

            string suppliedDescription = GetStringOrNull(obj["error_description"]) ?? string.Empty;
            bool hasUsableDescription = IsUsableOAuthDescription(suppliedDescription);
            if (!hasUsableDescription)
            {
                (string standardFields, string otherFieldNames) = SummarizeUpstreamFieldsForLogging(obj);
                logger.LogInformation(
                    DiscardedProviderDescriptionTemplate,
                    traceId.Value,
                    standardFields,
                    otherFieldNames
                );
            }

            bool unauthorized = callerUsedAuthorization && error is InvalidClientError;
            string fallbackDescription =
                error is InvalidClientError
                    ? UnauthorizedFallbackDetail
                    : OAuthErrorResponse.ProviderRejectedDescription;
            return OAuthErrorResponse.Create(
                unauthorized ? HttpStatusCode.Unauthorized : HttpStatusCode.BadRequest,
                error,
                hasUsableDescription ? suppliedDescription : fallbackDescription,
                traceId
            );
        }

        // Answers an upstream 429. /oauth/token is what the Discovery document advertises, so
        // without this arm a client that hit its token limit would fall through to the 502 branch
        // and receive neither the correct status nor reuse guidance.
        //
        // The upstream body is evidence, never content: the limit is parsed out of it and the
        // message is rebuilt here, so nothing the upstream authored reaches the caller. Relaying
        // its `errors` strings verbatim would reopen the same unauthenticated disclosure that
        // provider-401 fallback is designed to close.
        static async Task<HttpResponseMessage> GenerateTooManyTokensResponse(
            ILogger logger,
            TraceId traceId,
            HttpResponseMessage response
        )
        {
            string body = await ReadStatusClassifiedBody(response.Content);
            if (TokenLimitFromUpstreamBody(body) is not int limit)
            {
                // A non-object response has no fields to summarize.
                JsonObject rateLimit = JsonObjectOrNull(body) ?? new();
                if (IsConfigurationServiceRateLimit(rateLimit))
                {
                    // CMS source/client throttling is expected. Rebuild the generic answer without
                    // relaying upstream content or warning on every rejected authentication attempt.
                    return OAuthErrorResponse.Create(
                        HttpStatusCode.TooManyRequests,
                        "temporarily_unavailable",
                        OAuthErrorResponse.RateLimitDescription,
                        traceId
                    );
                }

                // Still a 429: the upstream *status* is trustworthy even when its body is not,
                // and the generic rate-limit contract is also the honest answer when the 429 came
                // from a gateway limiter rather than from the token limit.
                //
                // The log gets the same selected summary the 401 fallback writes, never the body
                // (reference/adr-oauth-upstream-error-disclosure.md): the values of the RFC 6749
                // error fields and the bare names of every other member, under the trace id the
                // client was given. A body that is not a JSON object has no members to name.
                (string standardFields, string otherFieldNames) = SummarizeUpstreamFieldsForLogging(
                    rateLimit
                );

                logger.LogWarning(
                    "Unrecognized 429 body from upstream identity service; its values are withheld "
                        + "- {TraceId} - standard OAuth error fields: {StandardFields} - other field "
                        + "names present: {OtherFieldNames}",
                    traceId.Value,
                    standardFields,
                    otherFieldNames
                );

                return OAuthErrorResponse.Create(
                    HttpStatusCode.TooManyRequests,
                    "temporarily_unavailable",
                    OAuthErrorResponse.RateLimitDescription,
                    traceId
                );
            }

            // Rebuilt from a DMS-side template and the parsed integer alone, so the limit survives
            // while not one character of upstream-authored text reaches the response body. The
            // correlation header encodes DMS's own trace id, as on every other error branch.
            string message =
                TokenLimitMessagePrefix
                + limit.ToString(CultureInfo.InvariantCulture)
                + TokenLimitMessageSuffix;

            return OAuthErrorResponse.Create(
                HttpStatusCode.TooManyRequests,
                "temporarily_unavailable",
                message,
                traceId
            );
        }

        // The Configuration Service's /connect/* rate-limit answer, matched exactly: the OAuth error
        // its /connect/token sends, or the problem details an earlier Configuration Service sent.
        static bool IsConfigurationServiceRateLimit(JsonObject body) =>
            (
                ScalarValueOrNull(body["error"]) == TemporarilyUnavailableOAuthError
                && ScalarValueOrNull(body["error_description"]) == RateLimitOAuthErrorDescription
            )
            || (
                ScalarValueOrNull(body["type"]) == "urn:ed-fi:management-api:too-many-requests"
                && body["status"] is JsonValue status
                && status.TryGetValue<int>(out int statusCode)
                && statusCode == 429
            );

        static async Task<string> ReadStatusClassifiedBody(HttpContent content)
        {
            // Complete I/O outside the decoding guard: an interrupted body remains a gateway
            // failure, even if its declared charset is also unsupported.
            await content.LoadIntoBufferAsync();
            try
            {
                return await content.ReadAsStringAsync();
            }
            catch (InvalidOperationException ex) when (ex.InnerException is ArgumentException)
            {
                // HttpContent wraps an unsupported charset this way. Treat the received body
                // as unusable so the provider-error branch emits its safe fallback and diagnostics.
                return string.Empty;
            }
            catch (NotSupportedException)
            {
                // A recognized but unavailable encoding (such as disabled UTF-7) is not wrapped
                // by HttpContent. Buffering already succeeded, so this is also unusable content.
                return string.Empty;
            }
        }

        // The limit carried by a canonical token-limit body, or null when the body deviates from
        // either accepted shape in any way at all. The OAuth envelope is an `error` of
        // temporarily_unavailable and an `error_description` that is the canonical message. The
        // problem-details shape is one of the two accepted types, a 429 status, and an errors
        // array holding exactly one string that is the canonical message. In both the message
        // must be canonical and its limit present, numeric, positive and small enough for an int.
        // Every deviation falls back to the generic 429 rather than being partially believed.
        static int? TokenLimitFromUpstreamBody(string body)
        {
            if (JsonObjectOrNull(body) is not JsonObject obj)
            {
                return null;
            }

            if (obj["error"] is not null)
            {
                return TokenLimitFromOAuthEnvelope(obj);
            }

            return TokenLimitFromProblemDetails(obj);
        }

        static int? TokenLimitFromOAuthEnvelope(JsonObject obj)
        {
            if (
                ScalarValueOrNull(obj["error"]) is not string error
                || !string.Equals(error, TemporarilyUnavailableOAuthError, StringComparison.Ordinal)
            )
            {
                return null;
            }

            // TryGetValue<string> succeeds only for a JSON string, so an array or a nested object
            // is rejected rather than stringified.
            if (
                obj["error_description"] is not JsonValue description
                || !description.TryGetValue(out string? message)
            )
            {
                return null;
            }

            return LimitFromCanonicalMessage(message);
        }

        static int? TokenLimitFromProblemDetails(JsonObject obj)
        {
            if (ScalarValueOrNull(obj["type"]) is not string type)
            {
                return null;
            }

            if (
                !string.Equals(type, LegacyTooManyTokensType, StringComparison.Ordinal)
                && !string.Equals(type, ManagementApiTooManyTokensType, StringComparison.Ordinal)
            )
            {
                return null;
            }

            if (
                obj["status"] is not JsonValue statusValue
                || !statusValue.TryGetValue(out int status)
                || status != (int)HttpStatusCode.TooManyRequests
            )
            {
                return null;
            }

            if (obj["errors"] is not JsonArray errors || errors.Count != 1)
            {
                return null;
            }

            // Only a JSON string is accepted, so a number or a nested object in the errors array
            // is rejected rather than stringified.
            if (GetStringOrNull(errors[0]) is not string message)
            {
                return null;
            }

            return LimitFromCanonicalMessage(message);
        }

        // The limit in a message that is exactly the canonical template, or null.
        static int? LimitFromCanonicalMessage(string message)
        {
            if (
                !message.StartsWith(TokenLimitMessagePrefix, StringComparison.Ordinal)
                || !message.EndsWith(TokenLimitMessageSuffix, StringComparison.Ordinal)
                || message.Length <= TokenLimitMessagePrefix.Length + TokenLimitMessageSuffix.Length
            )
            {
                return null;
            }

            string limitText = message[TokenLimitMessagePrefix.Length..^TokenLimitMessageSuffix.Length];

            // NumberStyles.None rejects a sign, surrounding whitespace and group separators, and
            // TryParse itself rejects a value that overflows int.
            if (
                !int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out int limit)
                || limit < 1
            )
            {
                return null;
            }

            return limit;
        }

        // The body as a usable root object, or null for malformed/non-object/duplicate-member bodies.
        static JsonObject? JsonObjectOrNull(string body)
        {
            try
            {
                if (JsonNode.Parse(body) is not JsonObject obj)
                {
                    return null;
                }

                // JsonObject materializes its property dictionary lazily. Force that work inside
                // this guard so duplicate root names cannot escape later field lookup or summary
                // enumeration and change a status-driven 401/429 into a transport-style 502.
                _ = obj.Count;
                return obj;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                // Decoding an escaped, unpaired surrogate in a property name is also lazy.
                return null;
            }
        }

        // Splits the top-level members of an upstream 401 body into the only two things the log
        // is allowed to carry: the values of the RFC 6749 section 5.2 error fields, and the bare
        // *names* of every other member.
        //
        // Names-only is not a hedge, it is what keeps the split usable. The member that explains
        // the rejection is often a non-standard one - an upstream answering with an `error` of
        // "invalid_client" and a `reason` of "client disabled" puts the whole answer in `reason`
        // - so allowlisted fields alone would leave an operator holding a correlation ID and no
        // explanation. Logging the body instead is closed off by docs/LOGGING.md, which forbids
        // request and response bodies, credentials and personal information in Information-level
        // logs. Sanitizing and bounding answer log injection and log volume, and redact nothing.
        //
        // The residual loss is deliberate. The log shows that `reason` was present, never that it
        // read "client disabled", and the operator takes that name to the identity provider's own
        // logs. Do not "restore" full-body logging here to close the gap.
        //
        // Being on the allowlist buys a field nothing beyond eligibility: an allowlisted name on
        // a nested object is arbitrary payload by another route, so what a value contributes is
        // decided per value, by StandardFieldValueForLogging.
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
                    // Keep summary extraction guarded like body parsing so malformed fields cannot
                    // turn a classified provider response into a transport failure. Bind the result
                    // as a parameter so it cannot disturb the message template.
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
                LoggingSanitizer.SanitizeFreeTextForLogging(
                    standard.Count == 0 ? NoUpstreamFieldsMarker : string.Join(", ", standard),
                    MaxLoggedUpstreamContentLength
                ),
                LoggingSanitizer.SanitizeFreeTextForLogging(
                    otherNames.Count == 0 ? NoUpstreamFieldsMarker : string.Join(", ", otherNames),
                    MaxLoggedUpstreamContentLength
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
        static string? ScalarValueOrNull(JsonNode? value)
        {
            try
            {
                return value is JsonValue scalar ? scalar.ToString() : null;
            }
            catch (InvalidOperationException)
            {
                // Invalid Unicode escapes cannot be decoded even though the JSON parsed.
                return null;
            }
        }
    }

    private static bool IsRecognizedTokenError(string error) =>
        error
            is "invalid_request"
                or "invalid_client"
                or "invalid_grant"
                or "unauthorized_client"
                or "unsupported_grant_type"
                or "invalid_scope";

    private static bool IsUsableOAuthDescription(string description)
    {
        if (description.Length is 0)
        {
            return false;
        }

        foreach (char character in description)
        {
            if (character < '\u0020' || character > '\u007e' || character is '"' or '\\')
            {
                return false;
            }
        }

        return true;
    }

    private static string? GetStringOrNull(JsonNode? value)
    {
        try
        {
            return value is JsonValue jsonValue && jsonValue.GetValueKind() is JsonValueKind.String
                ? jsonValue.GetValue<string>()
                : null;
        }
        catch (InvalidOperationException)
        {
            // String decoding is lazy too; unusable Unicode needs the same safe fallback.
            return null;
        }
    }
}
