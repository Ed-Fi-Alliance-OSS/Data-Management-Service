// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdFi.DmsConfigurationService.Tests.E2E.Keycloak;

/// <summary>
/// Environment wiring for the DMS-1327 Keycloak characterization run (spec P1.1). The fixtures
/// talk to Keycloak directly on the published host port and never through the Configuration
/// Service, so the evidence they record is the identity provider's own behavior.
/// </summary>
public static class KeycloakCharacterizationEnvironment
{
    public const string Category = "KeycloakCharacterization";

    private const string IdentityProviderVariable = "DMS_CONFIG_IDENTITY_PROVIDER";

    // The compose stack publishes Keycloak with values from the active environment file; the
    // fallbacks match eng/docker-compose/.env.config.e2e so a bare `dotnet test` against the
    // standard stack reaches the right instance. KEYCLOAK_PORT is also how the 26.7 compatibility
    // run on an isolated port is addressed.
    public static string KeycloakUrl => $"http://localhost:{EnvOrDefault("KEYCLOAK_PORT", "8045")}";

    public static string Realm => "edfi";

    public static string AdminRealm => "master";

    public static string AdminUsername => EnvOrDefault("KEYCLOAK_ADMIN", "admin");

    public static string AdminPassword => EnvOrDefault("KEYCLOAK_ADMIN_PASSWORD", "admin");

    public static bool IsKeycloakProvider =>
        string.Equals(
            Environment.GetEnvironmentVariable(IdentityProviderVariable),
            "keycloak",
            StringComparison.OrdinalIgnoreCase
        );

    public static void RequireKeycloakProvider()
    {
        if (!IsKeycloakProvider)
        {
            Assert.Ignore(
                $"Keycloak characterization runs only when {IdentityProviderVariable}=keycloak; "
                    + $"current value is '{Environment.GetEnvironmentVariable(IdentityProviderVariable) ?? "unset"}'."
            );
        }
    }

    private static string EnvOrDefault(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}

/// <summary>
/// Every secret and token this run has seen. Diagnostics never include request or response
/// content, and the evidence log refuses any row that contains one of these values, so a provider
/// that echoed a credential could not get it into test output or CI artifacts (AC8).
/// </summary>
public sealed class SensitiveValueRegistry
{
    // Shorter strings are not credentials in this run and would produce false matches.
    private const int MinimumLength = 8;

    private readonly HashSet<string> _values = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    public void Register(string? value)
    {
        if (value is { Length: >= MinimumLength })
        {
            lock (_sync)
            {
                _values.Add(value);
            }
        }
    }

    public bool ContainsAny(string text)
    {
        lock (_sync)
        {
            return _values.Any(value => text.Contains(value, StringComparison.Ordinal));
        }
    }
}

/// <summary>A Keycloak client created for one characterization run.</summary>
public sealed record CharacterizationClient(string Uuid, string ClientId, string? Secret)
{
    public ClientCredentials Credentials => new(ClientId, Secret);
}

/// <summary>A realm user created for one characterization run (held by the public client).</summary>
public sealed record CharacterizationUser(string Uuid, string Username, string Password);

/// <summary>Credentials as a caller would present them. <see cref="Secret"/> is null for "no secret".</summary>
public sealed record ClientCredentials(string ClientId, string? Secret)
{
    public AuthenticationHeaderValue ToBasicHeader() =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{Secret}")));
}

/// <summary>The tokens one grant returned.</summary>
public sealed record TokenGrant(string AccessToken, string? RefreshToken, string? IdToken, int ExpiresIn);

/// <summary>Coarse, content-free description of a response body, safe to put in a diagnostic.</summary>
public enum BodyKind
{
    Empty,
    JsonObject,
    JsonArray,
    JsonValue,
    NotJson,
}

/// <summary>
/// What Keycloak answered to one request, reduced to the fields the evidence table records. The
/// body is parsed for <c>error</c> and <c>error_description</c> and otherwise discarded; no token,
/// secret or header value is ever kept.
/// </summary>
public sealed record HttpOutcome(
    int Status,
    string? Error,
    string? ErrorDescription,
    int BodyLength,
    BodyKind Body,
    string? ContentType,
    string? WwwAuthenticate
)
{
    public bool BodyIsJsonObject => Body == BodyKind.JsonObject;

    public static async Task<HttpOutcome> FromResponseAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        (BodyKind kind, JsonObject? json) = Classify(body);

        return new HttpOutcome(
            (int)response.StatusCode,
            json is null ? null : StringMember(json, "error"),
            json is null ? null : StringMember(json, "error_description"),
            body.Length,
            kind,
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.WwwAuthenticate.Count > 0
                ? string.Join(", ", response.Headers.WwwAuthenticate)
                : null
        );
    }

    public static (BodyKind Kind, JsonObject? Json) Classify(string body)
    {
        if (body.Length == 0)
        {
            return (BodyKind.Empty, null);
        }

        try
        {
            return JsonNode.Parse(body) switch
            {
                JsonObject json => (BodyKind.JsonObject, json),
                JsonArray => (BodyKind.JsonArray, null),
                _ => (BodyKind.JsonValue, null),
            };
        }
        catch (JsonException)
        {
            return (BodyKind.NotJson, null);
        }
    }

    private static string? StringMember(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}

/// <summary>Reads the unverified payload of a JWT. Used only to learn <c>exp</c>, <c>iat</c> and <c>azp</c>.</summary>
public static class JwtPayload
{
    public static JsonObject Read(string token)
    {
        string[] segments = token.Split('.');
        if (segments.Length < 2)
        {
            throw new InvalidOperationException("The token is not a JWT (fewer than two segments).");
        }

        string payload = segments[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        byte[] bytes = Convert.FromBase64String(payload);
        return JsonNode.Parse(bytes)?.AsObject()
            ?? throw new InvalidOperationException("The JWT payload is not a JSON object.");
    }

    public static long GetNumber(JsonObject payload, string claim) =>
        payload[claim]?.GetValue<long>()
        ?? throw new InvalidOperationException($"The JWT payload has no numeric '{claim}' claim.");
}

/// <summary>Builds <c>application/x-www-form-urlencoded</c> bodies, including deliberately duplicated keys.</summary>
public sealed class FormBody
{
    private readonly List<KeyValuePair<string, string>> _fields = [];

    public static FormBody Revocation(
        ClientCredentials? credentials,
        string? token,
        string? tokenTypeHint = null
    )
    {
        FormBody body = new();
        if (credentials is not null)
        {
            body.Add("client_id", credentials.ClientId);
            if (credentials.Secret is not null)
            {
                body.Add("client_secret", credentials.Secret);
            }
        }

        if (token is not null)
        {
            body.Add("token", token);
        }

        if (tokenTypeHint is not null)
        {
            body.Add("token_type_hint", tokenTypeHint);
        }

        return body;
    }

    public FormBody Add(string key, string value)
    {
        _fields.Add(new KeyValuePair<string, string>(key, value));
        return this;
    }

    public FormUrlEncodedContent ToContent() => new(_fields);
}

/// <summary>
/// Direct HTTP access to one Keycloak instance: the admin REST API for creating and deleting the
/// run's clients, user and client scope, and the realm's token, introspection and revocation
/// endpoints. Every diagnostic it raises is built from fixed text, the operation label, the HTTP
/// status and a <see cref="BodyKind"/>; request and response content never reach an exception.
/// </summary>
public sealed class KeycloakCharacterizationApi : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _realm;
    private readonly string _adminRealm;

    public KeycloakCharacterizationApi(
        string baseUrl,
        string realm,
        string adminRealm,
        SensitiveValueRegistry sensitive,
        HttpMessageHandler? handler = null
    )
    {
        _baseUrl = baseUrl;
        _realm = realm;
        _adminRealm = adminRealm;
        Sensitive = sensitive;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public SensitiveValueRegistry Sensitive { get; }

    public string RealmUrl => $"{_baseUrl}/realms/{_realm}";

    public string TokenEndpoint => $"{RealmUrl}/protocol/openid-connect/token";

    public string IntrospectionEndpoint => $"{RealmUrl}/protocol/openid-connect/token/introspect";

    public string RevocationEndpoint => $"{RealmUrl}/protocol/openid-connect/revoke";

    public void Dispose() => _http.Dispose();

    // The master realm's admin token lives 60 seconds by default and the expiry row waits longer
    // than that, so every admin call fetches its own token instead of caching one.
    public async Task<string> GetAdminTokenAsync(string username, string password)
    {
        Sensitive.Register(password);
        FormBody form = new FormBody()
            .Add("grant_type", "password")
            .Add("client_id", "admin-cli")
            .Add("username", username)
            .Add("password", password);
        using HttpResponseMessage response = await _http.PostAsync(
            $"{_baseUrl}/realms/{_adminRealm}/protocol/openid-connect/token",
            form.ToContent()
        );
        JsonObject json = await ReadJsonObjectAsync(response, "admin token request");
        string token = RequireString(json, "access_token", "admin token request");
        Sensitive.Register(token);
        Sensitive.Register(OptionalString(json, "refresh_token"));
        return token;
    }

    public async Task<string> GetServerVersionAsync(string adminToken)
    {
        using HttpRequestMessage request = AdminRequest(
            HttpMethod.Get,
            $"{_baseUrl}/admin/serverinfo",
            adminToken
        );
        using HttpResponseMessage response = await _http.SendAsync(request);
        JsonObject json = await ReadJsonObjectAsync(response, "server info request");
        return json["systemInfo"]?["version"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The server info request carries no systemInfo.version.");
    }

    public async Task<string?> GetRealmSslRequiredAsync(string adminToken)
    {
        using HttpRequestMessage request = AdminRequest(
            HttpMethod.Get,
            $"{_baseUrl}/admin/realms/{_realm}",
            adminToken
        );
        using HttpResponseMessage response = await _http.SendAsync(request);
        JsonObject json = await ReadJsonObjectAsync(response, "realm representation request");
        return OptionalString(json, "sslRequired");
    }

    public Task<string> CreateClientAsync(string adminToken, JsonObject representation)
    {
        Sensitive.Register(OptionalString(representation, "secret"));
        return CreateAsync(adminToken, $"{_baseUrl}/admin/realms/{_realm}/clients", representation, "client");
    }

    public Task<string> CreateUserAsync(string adminToken, JsonObject representation)
    {
        if (representation["credentials"] is JsonArray credentials)
        {
            foreach (JsonNode? credential in credentials)
            {
                Sensitive.Register(credential is JsonObject item ? OptionalString(item, "value") : null);
            }
        }

        return CreateAsync(adminToken, $"{_baseUrl}/admin/realms/{_realm}/users", representation, "user");
    }

    public Task<string> CreateClientScopeAsync(string adminToken, JsonObject representation) =>
        CreateAsync(
            adminToken,
            $"{_baseUrl}/admin/realms/{_realm}/client-scopes",
            representation,
            "client scope"
        );

    public async Task AddDefaultClientScopeAsync(string adminToken, string clientUuid, string scopeUuid)
    {
        using HttpRequestMessage request = AdminRequest(
            HttpMethod.Put,
            $"{_baseUrl}/admin/realms/{_realm}/clients/{clientUuid}/default-client-scopes/{scopeUuid}",
            adminToken
        );
        using HttpResponseMessage response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Attaching the observer audience scope answered HTTP {(int)response.StatusCode}."
            );
        }
    }

    public Task DeleteClientScopeAsync(string adminToken, string uuid) =>
        DeleteAsync(adminToken, $"{_baseUrl}/admin/realms/{_realm}/client-scopes/{uuid}", "client scope");

    public Task DeleteClientAsync(string adminToken, string uuid) =>
        DeleteAsync(adminToken, $"{_baseUrl}/admin/realms/{_realm}/clients/{uuid}", "client");

    public Task DeleteUserAsync(string adminToken, string uuid) =>
        DeleteAsync(adminToken, $"{_baseUrl}/admin/realms/{_realm}/users/{uuid}", "user");

    /// <summary>Requests tokens from the realm and fails loudly on anything but a 200 with an access token.</summary>
    public async Task<TokenGrant> RequestTokenAsync(FormBody form)
    {
        using HttpResponseMessage response = await _http.PostAsync(TokenEndpoint, form.ToContent());
        JsonObject json = await ReadJsonObjectAsync(response, "token request");
        TokenGrant grant = new(
            RequireString(json, "access_token", "token request"),
            OptionalString(json, "refresh_token"),
            OptionalString(json, "id_token"),
            json["expires_in"]?.GetValue<int>() ?? 0
        );
        Sensitive.Register(grant.AccessToken);
        Sensitive.Register(grant.RefreshToken);
        Sensitive.Register(grant.IdToken);
        return grant;
    }

    /// <summary>
    /// A token-endpoint call whose outcome, not its tokens, is the observation (for example a
    /// refresh grant used as a liveness probe). Any tokens in the body are discarded unread.
    /// </summary>
    public async Task<HttpOutcome> PostTokenRequestRawAsync(FormBody form)
    {
        using HttpResponseMessage response = await _http.PostAsync(TokenEndpoint, form.ToContent());
        return await HttpOutcome.FromResponseAsync(response);
    }

    /// <summary>
    /// Introspects a token with the given client's credentials and returns the raw outcome. Used for
    /// rows that characterize the introspection endpoint itself (for example the public-client refusal).
    /// </summary>
    public async Task<HttpOutcome> IntrospectRawAsync(ClientCredentials credentials, string token)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, IntrospectionEndpoint);
        request.Content = FormBody.Revocation(credentials, token).ToContent();
        using HttpResponseMessage response = await _http.SendAsync(request);
        return await HttpOutcome.FromResponseAsync(response);
    }

    /// <summary>
    /// A client's view of a token through introspection. A failed introspection is a failed
    /// prerequisite and throws; it is never reported as "inactive".
    /// </summary>
    public async Task<bool> IntrospectAsync(
        ClientCredentials introspector,
        string token,
        string? tokenTypeHint
    )
    {
        using HttpRequestMessage request = new(HttpMethod.Post, IntrospectionEndpoint);
        request.Headers.Authorization = RegisterBasic(introspector.ToBasicHeader());
        FormBody form = new FormBody().Add("token", token);
        if (tokenTypeHint is not null)
        {
            form.Add("token_type_hint", tokenTypeHint);
        }

        request.Content = form.ToContent();
        using HttpResponseMessage response = await _http.SendAsync(request);
        JsonObject json = await ReadJsonObjectAsync(response, "introspection request");
        return json["active"] is JsonValue active && active.TryGetValue(out bool isActive)
            ? isActive
            : throw new InvalidOperationException(
                "The introspection response has no boolean 'active' member; the characterization prerequisite failed."
            );
    }

    public async Task<HttpOutcome> RevokeAsync(FormBody form, AuthenticationHeaderValue? authorization = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, RevocationEndpoint);
        request.Headers.Authorization = authorization is null ? null : RegisterBasic(authorization);
        request.Content = form.ToContent();
        using HttpResponseMessage response = await _http.SendAsync(request);
        return await HttpOutcome.FromResponseAsync(response);
    }

    private AuthenticationHeaderValue RegisterBasic(AuthenticationHeaderValue header)
    {
        Sensitive.Register(header.Parameter);
        return header;
    }

    private static HttpRequestMessage AdminRequest(HttpMethod method, string url, string adminToken)
    {
        HttpRequestMessage request = new(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        return request;
    }

    private async Task<string> CreateAsync(
        string adminToken,
        string url,
        JsonObject representation,
        string kind
    )
    {
        using HttpRequestMessage request = AdminRequest(HttpMethod.Post, url, adminToken);
        request.Content = new StringContent(representation.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Creating the characterization {kind} answered HTTP {(int)response.StatusCode} with {Describe(body)}."
            );
        }

        string? location = response.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(location))
        {
            throw new InvalidOperationException(
                $"Creating the characterization {kind} returned no Location header."
            );
        }

        return location[(location.LastIndexOf('/') + 1)..];
    }

    private async Task DeleteAsync(string adminToken, string url, string kind)
    {
        using HttpRequestMessage request = AdminRequest(HttpMethod.Delete, url, adminToken);
        using HttpResponseMessage response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Deleting the characterization {kind} answered HTTP {(int)response.StatusCode}."
            );
        }
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(HttpResponseMessage response, string operation)
    {
        string body = await response.Content.ReadAsStringAsync();
        (BodyKind kind, JsonObject? json) = HttpOutcome.Classify(body);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The {operation} answered HTTP {(int)response.StatusCode} with {Describe(kind)}; the characterization prerequisite failed."
            );
        }

        return json
            ?? throw new InvalidOperationException(
                $"The {operation} answered HTTP {(int)response.StatusCode} with {Describe(kind)} instead of a JSON object."
            );
    }

    private static string RequireString(JsonObject json, string member, string operation) =>
        OptionalString(json, member)
        ?? throw new InvalidOperationException(
            $"The {operation} response carries no string '{member}' member."
        );

    private static string? OptionalString(JsonObject json, string member) =>
        json[member] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static string Describe(string body) => Describe(HttpOutcome.Classify(body).Kind);

    private static string Describe(BodyKind kind) =>
        kind switch
        {
            BodyKind.Empty => "an empty body",
            BodyKind.JsonObject => "a JSON object body",
            BodyKind.JsonArray => "a JSON array body",
            BodyKind.JsonValue => "a JSON value body",
            _ => "a non-JSON body",
        };
}

/// <summary>
/// The per-run realm resources (spec P1.1 and D-16): two confidential subject clients, one
/// confidential subject that also issues refresh tokens, one confidential subject with a short
/// access-token lifespan for the expiry row only, one confidential observer used solely for
/// introspecting access tokens, one public client with a user so it can hold a user-flow token,
/// and one client scope that puts the observer into the audience of every subject token. Every
/// name carries the run id, and <see cref="DisposeAsync"/> removes everything through the admin API.
/// </summary>
public sealed class CharacterizationRealm : IAsyncDisposable
{
    public const int ExpirySubjectAccessTokenLifespanSeconds = 10;

    private readonly string _adminUsername;
    private readonly string _adminPassword;
    private readonly List<CharacterizationClient> _clients = [];
    private CharacterizationUser? _publicUser;
    private string? _observerAudienceScopeUuid;

    private CharacterizationRealm(
        KeycloakCharacterizationApi api,
        string runId,
        string adminUsername,
        string adminPassword
    )
    {
        Api = api;
        RunId = runId;
        _adminUsername = adminUsername;
        _adminPassword = adminPassword;
    }

    public KeycloakCharacterizationApi Api { get; }

    public string RunId { get; }

    public string ServerVersion { get; private set; } = "";

    public string? SslRequired { get; private set; }

    public CharacterizationClient SubjectA => _clients[0];

    public CharacterizationClient SubjectB => _clients[1];

    public CharacterizationClient RefreshSubject => _clients[2];

    public CharacterizationClient ExpirySubject => _clients[3];

    public CharacterizationClient Observer => _clients[4];

    public CharacterizationClient PublicClient => _clients[5];

    public CharacterizationUser PublicUser =>
        _publicUser ?? throw new InvalidOperationException("The public client's user was not created.");

    public static async Task<CharacterizationRealm> CreateAsync(SensitiveValueRegistry sensitive)
    {
        KeycloakCharacterizationApi api = new(
            KeycloakCharacterizationEnvironment.KeycloakUrl,
            KeycloakCharacterizationEnvironment.Realm,
            KeycloakCharacterizationEnvironment.AdminRealm,
            sensitive
        );
        CharacterizationRealm realm = new(
            api,
            Guid.NewGuid().ToString("N")[..12],
            KeycloakCharacterizationEnvironment.AdminUsername,
            KeycloakCharacterizationEnvironment.AdminPassword
        );

        try
        {
            await realm.ProvisionAsync();
            return realm;
        }
        catch
        {
            try
            {
                await realm.DisposeAsync();
            }
            catch (Exception cleanupFailure)
            {
                // The provisioning failure is the one to report; the cleanup problem is only logged.
                await TestContext.Progress.WriteLineAsync(
                    $"[evidence] cleanup after a failed provisioning also failed: {cleanupFailure.GetType().FullName}"
                );
            }

            throw;
        }
    }

    public Task<string> GetAdminTokenAsync() => Api.GetAdminTokenAsync(_adminUsername, _adminPassword);

    /// <summary>A fresh client-credentials grant for a confidential subject (realm default lifespan unless the client overrides it).</summary>
    public Task<TokenGrant> ServiceAccountGrantAsync(CharacterizationClient subject) =>
        Api.RequestTokenAsync(
            new FormBody()
                .Add("grant_type", "client_credentials")
                .Add("client_id", subject.ClientId)
                .Add("client_secret", subject.Secret ?? "")
        );

    /// <summary>A fresh password grant for the public client's user, with <c>openid</c> so an ID token is issued.</summary>
    public Task<TokenGrant> PublicUserGrantAsync() =>
        Api.RequestTokenAsync(
            new FormBody()
                .Add("grant_type", "password")
                .Add("client_id", PublicClient.ClientId)
                .Add("username", PublicUser.Username)
                .Add("password", PublicUser.Password)
                .Add("scope", "openid")
        );

    /// <summary>
    /// Liveness probe for a public client's refresh token: a refresh grant. 200 means the token and its
    /// session are still usable; 400 <c>invalid_grant</c> means they are not. The probe may rotate the
    /// token, so it is only ever the last thing a row does with that token, and the new tokens it
    /// returns are discarded unread. A public client cannot introspect (403), so this is the only
    /// first-hand observation available for its refresh token.
    /// </summary>
    public Task<HttpOutcome> PublicUserRefreshGrantProbeAsync(string refreshToken) =>
        Api.PostTokenRequestRawAsync(
            new FormBody()
                .Add("grant_type", "refresh_token")
                .Add("client_id", PublicClient.ClientId)
                .Add("refresh_token", refreshToken)
        );

    /// <summary>The observer's view of an access token (D-16). Observer credentials are never submitted to revocation.</summary>
    public Task<bool> ObserveAsync(string token) =>
        Api.IntrospectAsync(Observer.Credentials, token, tokenTypeHint: null);

    /// <summary>
    /// The owning client's view of its refresh token, with <c>token_type_hint=refresh_token</c> (D-16
    /// as corrected after P1.1). Keycloak's refresh-token introspection provider admits only the
    /// client the token was issued to, so a separate observer cannot see refresh tokens on 26.7.5.
    /// </summary>
    public Task<bool> ObserveRefreshTokenAsOwnerAsync(CharacterizationClient owner, string refreshToken) =>
        Api.IntrospectAsync(owner.Credentials, refreshToken, "refresh_token");

    /// <summary>
    /// Polls the observer at one-second intervals until the token reports inactive, with a deadline
    /// of the token's <c>exp</c> plus 60 seconds. Returns a note describing what was observed.
    /// </summary>
    public async Task<string> WaitUntilObservedInactiveAsync(string token)
    {
        JsonObject payload = JwtPayload.Read(token);
        long exp = JwtPayload.GetNumber(payload, "exp");
        long iat = JwtPayload.GetNumber(payload, "iat");
        DateTimeOffset deadline = DateTimeOffset.FromUnixTimeSeconds(exp).AddSeconds(60);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        int polls = 0;

        while (true)
        {
            polls++;
            if (!await ObserveAsync(token))
            {
                double waited = (DateTimeOffset.UtcNow - started).TotalSeconds;
                return $"token lifespan exp-iat={exp - iat}s; observer reported inactive after {polls} polls ({waited:F0}s)";
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new InvalidOperationException(
                    $"The observer still reported the token active at exp+60s (exp-iat={exp - iat}s, {polls} polls)."
                );
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Exception> failures = [];
        string? adminToken = null;
        try
        {
            adminToken = await GetAdminTokenAsync();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (adminToken is not null)
        {
            if (_publicUser is not null)
            {
                await TryAsync(() => Api.DeleteUserAsync(adminToken, _publicUser.Uuid), failures);
            }

            foreach (CharacterizationClient client in _clients)
            {
                await TryAsync(() => Api.DeleteClientAsync(adminToken, client.Uuid), failures);
            }

            if (_observerAudienceScopeUuid is not null)
            {
                await TryAsync(
                    () => Api.DeleteClientScopeAsync(adminToken, _observerAudienceScopeUuid),
                    failures
                );
            }
        }

        Api.Dispose();

        if (failures.Count > 0)
        {
            throw new AggregateException(
                $"Cleanup of the characterization run {RunId} left resources in realm '{KeycloakCharacterizationEnvironment.Realm}'.",
                failures
            );
        }

        static async Task TryAsync(Func<Task> action, List<Exception> failures)
        {
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
    }

    private async Task ProvisionAsync()
    {
        string adminToken = await GetAdminTokenAsync();
        ServerVersion = await Api.GetServerVersionAsync(adminToken);
        SslRequired = await Api.GetRealmSslRequiredAsync(adminToken);

        // Index order matters: the SubjectA..PublicClient accessors read _clients by position.
        await CreateConfidentialAsync(adminToken, "subject-a", attributes: null);
        await CreateConfidentialAsync(adminToken, "subject-b", attributes: null);
        await CreateConfidentialAsync(
            adminToken,
            "subject-refresh",
            new JsonObject { ["client_credentials.use_refresh_token"] = "true" }
        );
        await CreateConfidentialAsync(
            adminToken,
            "subject-expiry",
            new JsonObject { ["access.token.lifespan"] = ExpirySubjectAccessTokenLifespanSeconds.ToString() }
        );
        await CreateConfidentialAsync(
            adminToken,
            "observer",
            attributes: null,
            serviceAccountsEnabled: false
        );

        string publicClientId = $"cs-char-public-{RunId}";
        string publicUuid = await Api.CreateClientAsync(
            adminToken,
            new JsonObject
            {
                ["clientId"] = publicClientId,
                ["name"] = $"DMS-1327 characterization public client {RunId}",
                ["protocol"] = "openid-connect",
                ["publicClient"] = true,
                ["directAccessGrantsEnabled"] = true,
                ["standardFlowEnabled"] = false,
                ["serviceAccountsEnabled"] = false,
            }
        );
        _clients.Add(new CharacterizationClient(publicUuid, publicClientId, Secret: null));

        string username = $"cs-char-user-{RunId}";
        string password = $"Pw-{Guid.NewGuid():N}!Aa1";
        string userUuid = await Api.CreateUserAsync(
            adminToken,
            new JsonObject
            {
                ["username"] = username,
                ["enabled"] = true,
                ["emailVerified"] = true,
                ["email"] = $"{username}@example.invalid",
                ["firstName"] = "Characterization",
                ["lastName"] = RunId,
                ["credentials"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "password",
                        ["value"] = password,
                        ["temporary"] = false,
                    }
                ),
            }
        );
        _publicUser = new CharacterizationUser(userUuid, username, password);

        // Observed in P1.1: Keycloak 26.7.5 denies introspection unless the introspecting client is in
        // the token's audience claim, while the pinned 26.1.4 image has no such check (the Red Hat
        // build of Keycloak 26.4 migration guide attributes the check to 26.4.12). A per-run client
        // scope adds the observer to the audience of every subject and public-client token so the
        // observer works on both. The scope changes the audience claim only and is removed with the
        // other run resources.
        _observerAudienceScopeUuid = await Api.CreateClientScopeAsync(
            adminToken,
            new JsonObject
            {
                ["name"] = $"cs-char-observer-audience-{RunId}",
                ["protocol"] = "openid-connect",
                ["protocolMappers"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "observer audience",
                        ["protocol"] = "openid-connect",
                        ["protocolMapper"] = "oidc-audience-mapper",
                        ["config"] = new JsonObject
                        {
                            ["included.client.audience"] = Observer.ClientId,
                            ["access.token.claim"] = "true",
                            ["introspection.token.claim"] = "true",
                            ["id.token.claim"] = "false",
                        },
                    }
                ),
            }
        );
        foreach (
            CharacterizationClient subject in new[]
            {
                SubjectA,
                SubjectB,
                RefreshSubject,
                ExpirySubject,
                PublicClient,
            }
        )
        {
            await Api.AddDefaultClientScopeAsync(adminToken, subject.Uuid, _observerAudienceScopeUuid);
        }
    }

    private async Task CreateConfidentialAsync(
        string adminToken,
        string role,
        JsonObject? attributes,
        bool serviceAccountsEnabled = true
    )
    {
        string clientId = $"cs-char-{role}-{RunId}";
        string secret = $"Secret-{Guid.NewGuid():N}!Aa1";
        JsonObject representation = new()
        {
            ["clientId"] = clientId,
            ["name"] = $"DMS-1327 characterization {role} {RunId}",
            ["secret"] = secret,
            ["protocol"] = "openid-connect",
            ["publicClient"] = false,
            ["serviceAccountsEnabled"] = serviceAccountsEnabled,
            ["standardFlowEnabled"] = false,
            ["directAccessGrantsEnabled"] = false,
        };
        if (attributes is not null)
        {
            representation["attributes"] = attributes;
        }

        string uuid = await Api.CreateClientAsync(adminToken, representation);
        _clients.Add(new CharacterizationClient(uuid, clientId, secret));
    }
}

/// <summary>
/// A value the row either predicts exactly (possibly as "absent") or marks as not applicable. After
/// P1.1 every field §2.1 left open has an exact prediction; "not applicable" is reserved for state
/// that genuinely does not exist for the row (for example the after-state of a request that carried
/// no token).
/// </summary>
public readonly record struct Predicted<T>(bool IsApplicable, T? Value)
{
    public static Predicted<T> NotApplicable => new(false, default);

    public static Predicted<T> Of(T? value) => new(true, value);

    public string Render()
    {
        if (!IsApplicable)
        {
            return "n/a";
        }

        return Value switch
        {
            null => "(absent)",
            true => "active",
            false => "inactive",
            BodyShape.Empty => "empty",
            BodyShape.OAuthErrorJson => "OAuth JSON",
            _ => Value.ToString() ?? "(absent)",
        };
    }
}

/// <summary>The body a row predicts: nothing at all, or an <c>application/json</c> object carrying OAuth error members.</summary>
public enum BodyShape
{
    Empty,
    OAuthErrorJson,
}

/// <summary>The prediction for one evidence row.</summary>
public sealed record RowPrediction(
    Predicted<int> Status,
    Predicted<BodyShape> Body,
    Predicted<string> Error,
    Predicted<string> ErrorDescription,
    Predicted<bool> ActiveAfter
)
{
    /// <summary>HTTP 200 with a zero-length body and therefore no error members.</summary>
    public static RowPrediction EmptySuccess(Predicted<bool> activeAfter) =>
        new(
            Predicted<int>.Of(200),
            Predicted<BodyShape>.Of(BodyShape.Empty),
            Predicted<string>.Of(null),
            Predicted<string>.Of(null),
            activeAfter
        );

    /// <summary>The given status with an <c>application/json</c> object carrying exactly these OAuth members.</summary>
    public static RowPrediction ErrorBody(
        int status,
        string error,
        string description,
        Predicted<bool> activeAfter
    ) =>
        new(
            Predicted<int>.Of(status),
            Predicted<BodyShape>.Of(BodyShape.OAuthErrorJson),
            Predicted<string>.Of(error),
            Predicted<string>.Of(description),
            activeAfter
        );
}

/// <summary>What one row observed. <see cref="ActiveBefore"/> is null for rows that do not start from a live token.</summary>
public sealed record RowObservation(HttpOutcome Outcome, bool? ActiveBefore, bool? ActiveAfter, string Notes);

public sealed record EvidenceRow(
    string Id,
    string Scenario,
    RowPrediction Prediction,
    RowObservation Observation
);

/// <summary>
/// Collects the rows of one run and renders the §9.1 table. A row whose rendering contains any
/// secret or token the run has seen is refused and the fixture fails, so provider content that
/// echoed a credential could not reach the evidence.
/// </summary>
public sealed class EvidenceLog(SensitiveValueRegistry sensitive)
{
    private readonly List<string> _renderedRows = [];
    private readonly Lock _sync = new();

    public void Record(EvidenceRow row) => Add(row.Id, RenderRow(row));

    /// <summary>A row that records facts rather than one characterized call (the preconditions row).</summary>
    public void RecordNote(string id, string scenario, string notes) =>
        Add(id, $"| {id} | {scenario} | — | — | — | — | — | — | {Escape(notes)} |");

    public string RenderMarkdown(string serverVersion, string? sslRequired, string runId)
    {
        List<string> rows;
        lock (_sync)
        {
            rows = [.. _renderedRows.OrderBy(static rendered => rendered, StringComparer.Ordinal)];
        }

        StringBuilder markdown = new();
        markdown.AppendLine(
            $"Keycloak `{serverVersion}` at `{KeycloakCharacterizationEnvironment.KeycloakUrl}`, realm `{KeycloakCharacterizationEnvironment.Realm}` (`sslRequired` = `{sslRequired ?? "(absent)"}`), run `{runId}`, {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC."
        );
        markdown.AppendLine();
        markdown.AppendLine(
            "Columns show `predicted / observed`; `n/a` = the field does not exist for the row; `(absent)` = no such member in the body."
        );
        markdown.AppendLine();
        markdown.AppendLine(
            "| ID | Scenario | HTTP | Body | `error` | `error_description` | active before | active after | Notes |"
        );
        markdown.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (string rendered in rows)
        {
            markdown.AppendLine(rendered);
        }

        return markdown.ToString();
    }

    private void Add(string id, string rendered)
    {
        if (sensitive.ContainsAny(rendered))
        {
            throw new InvalidOperationException(
                $"Evidence row {id} would contain a secret or token issued during this run; the row was not recorded."
            );
        }

        lock (_sync)
        {
            _renderedRows.Add(rendered);
        }

        TestContext.Progress.WriteLine($"[evidence] {rendered}");
    }

    private static string RenderRow(EvidenceRow row)
    {
        HttpOutcome outcome = row.Observation.Outcome;
        string body = outcome.Body switch
        {
            BodyKind.Empty => "empty",
            BodyKind.JsonObject =>
                $"JSON object, {outcome.ContentType ?? "no media type"}, {outcome.BodyLength} chars",
            BodyKind.JsonArray => $"JSON array, {outcome.BodyLength} chars",
            BodyKind.JsonValue => $"JSON value, {outcome.BodyLength} chars",
            _ => $"non-JSON, {outcome.ContentType ?? "no media type"}, {outcome.BodyLength} chars",
        };
        string notes = string.Join(
            "; ",
            new string?[]
            {
                row.Observation.Notes,
                outcome.WwwAuthenticate is null ? null : $"WWW-Authenticate: {outcome.WwwAuthenticate}",
            }.Where(static note => !string.IsNullOrEmpty(note))
        );
        return $"| {row.Id} | {row.Scenario} | {row.Prediction.Status.Render()} / {outcome.Status} "
            + $"| {row.Prediction.Body.Render()} / {body} "
            + $"| {RenderCode(row.Prediction.Error)} / {RenderCode(Predicted<string>.Of(outcome.Error))} "
            + $"| {RenderText(row.Prediction.ErrorDescription)} / {RenderText(Predicted<string>.Of(outcome.ErrorDescription))} "
            + $"| {RenderState(row.Observation.ActiveBefore)} "
            + $"| {row.Prediction.ActiveAfter.Render()} / {RenderState(row.Observation.ActiveAfter)} "
            + $"| {Escape(notes)} |";
    }

    private static string Escape(string text) => text.Replace("|", "\\|");

    private static string RenderCode(Predicted<string> value) =>
        value is { IsApplicable: true, Value: not null } ? $"`{value.Value}`" : value.Render();

    private static string RenderText(Predicted<string> value) =>
        value is { IsApplicable: true, Value: not null } ? $"\"{Escape(value.Value)}\"" : value.Render();

    private static string RenderState(bool? state) =>
        state switch
        {
            true => "active",
            false => "inactive",
            null => "n/a",
        };
}
