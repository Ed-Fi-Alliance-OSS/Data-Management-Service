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

/// <summary>
/// What Keycloak answered to one request, reduced to the fields the evidence table records. The
/// body itself is parsed for <c>error</c> and <c>error_description</c> and otherwise discarded;
/// no token, secret or header value is ever kept.
/// </summary>
public sealed record HttpOutcome(
    int Status,
    string? Error,
    string? ErrorDescription,
    int BodyLength,
    bool BodyIsJsonObject,
    string? ContentType,
    string? WwwAuthenticate
)
{
    public static async Task<HttpOutcome> FromResponseAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        string? error = null;
        string? errorDescription = null;
        bool isJsonObject = false;

        if (body.Length > 0)
        {
            try
            {
                if (JsonNode.Parse(body) is JsonObject json)
                {
                    isJsonObject = true;
                    error = StringMember(json, "error");
                    errorDescription = StringMember(json, "error_description");
                }
            }
            catch (JsonException)
            {
                // Recorded as a non-JSON body through BodyIsJsonObject = false.
            }
        }

        return new HttpOutcome(
            (int)response.StatusCode,
            error,
            errorDescription,
            body.Length,
            isJsonObject,
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.WwwAuthenticate.Count > 0
                ? string.Join(", ", response.Headers.WwwAuthenticate)
                : null
        );
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
/// run's clients and user, and the realm's token, introspection and revocation endpoints.
/// </summary>
public sealed class KeycloakCharacterizationApi(string baseUrl, string realm, string adminRealm) : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string RealmUrl => $"{baseUrl}/realms/{realm}";

    public string TokenEndpoint => $"{RealmUrl}/protocol/openid-connect/token";

    public string IntrospectionEndpoint => $"{RealmUrl}/protocol/openid-connect/token/introspect";

    public string RevocationEndpoint => $"{RealmUrl}/protocol/openid-connect/revoke";

    public void Dispose() => _http.Dispose();

    // The master realm's admin token lives 60 seconds by default and the expiry row waits longer
    // than that, so every admin call fetches its own token instead of caching one.
    public async Task<string> GetAdminTokenAsync(string username, string password)
    {
        FormBody form = new FormBody()
            .Add("grant_type", "password")
            .Add("client_id", "admin-cli")
            .Add("username", username)
            .Add("password", password);
        using HttpResponseMessage response = await _http.PostAsync(
            $"{baseUrl}/realms/{adminRealm}/protocol/openid-connect/token",
            form.ToContent()
        );
        JsonObject json = await ReadJsonObjectAsync(response, "admin token request");
        return json["access_token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The admin token response carries no access_token.");
    }

    public async Task<string> GetServerVersionAsync(string adminToken)
    {
        using HttpRequestMessage request = AdminRequest(
            HttpMethod.Get,
            $"{baseUrl}/admin/serverinfo",
            adminToken
        );
        using HttpResponseMessage response = await _http.SendAsync(request);
        JsonObject json = await ReadJsonObjectAsync(response, "GET /admin/serverinfo");
        return json["systemInfo"]?["version"]?.GetValue<string>()
            ?? throw new InvalidOperationException("GET /admin/serverinfo carries no systemInfo.version.");
    }

    public async Task<string?> GetRealmSslRequiredAsync(string adminToken)
    {
        using HttpRequestMessage request = AdminRequest(
            HttpMethod.Get,
            $"{baseUrl}/admin/realms/{realm}",
            adminToken
        );
        using HttpResponseMessage response = await _http.SendAsync(request);
        JsonObject json = await ReadJsonObjectAsync(response, $"GET /admin/realms/{realm}");
        return json["sslRequired"]?.GetValue<string>();
    }

    public async Task<string> CreateClientAsync(string adminToken, JsonObject representation)
    {
        using HttpRequestMessage request = AdminRequest(
            HttpMethod.Post,
            $"{baseUrl}/admin/realms/{realm}/clients",
            adminToken
        );
        request.Content = new StringContent(representation.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.SendAsync(request);
        return await ReadCreatedIdAsync(response, "client");
    }

    public async Task<string> CreateUserAsync(string adminToken, JsonObject representation)
    {
        using HttpRequestMessage request = AdminRequest(
            HttpMethod.Post,
            $"{baseUrl}/admin/realms/{realm}/users",
            adminToken
        );
        request.Content = new StringContent(representation.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.SendAsync(request);
        return await ReadCreatedIdAsync(response, "user");
    }

    public async Task<string> CreateClientScopeAsync(string adminToken, JsonObject representation)
    {
        using HttpRequestMessage request = AdminRequest(
            HttpMethod.Post,
            $"{baseUrl}/admin/realms/{realm}/client-scopes",
            adminToken
        );
        request.Content = new StringContent(representation.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.SendAsync(request);
        return await ReadCreatedIdAsync(response, "client scope");
    }

    public async Task AddDefaultClientScopeAsync(string adminToken, string clientUuid, string scopeUuid)
    {
        using HttpRequestMessage request = AdminRequest(
            HttpMethod.Put,
            $"{baseUrl}/admin/realms/{realm}/clients/{clientUuid}/default-client-scopes/{scopeUuid}",
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
        DeleteAsync(adminToken, $"{baseUrl}/admin/realms/{realm}/client-scopes/{uuid}", "client scope");

    public Task DeleteClientAsync(string adminToken, string uuid) =>
        DeleteAsync(adminToken, $"{baseUrl}/admin/realms/{realm}/clients/{uuid}", "client");

    public Task DeleteUserAsync(string adminToken, string uuid) =>
        DeleteAsync(adminToken, $"{baseUrl}/admin/realms/{realm}/users/{uuid}", "user");

    /// <summary>Requests tokens from the realm and fails loudly on anything but a 200 with an access token.</summary>
    public async Task<TokenGrant> RequestTokenAsync(FormBody form)
    {
        using HttpResponseMessage response = await _http.PostAsync(TokenEndpoint, form.ToContent());
        JsonObject json = await ReadJsonObjectAsync(response, "token request");
        return new TokenGrant(
            json["access_token"]?.GetValue<string>()
                ?? throw new InvalidOperationException("The token response carries no access_token."),
            json["refresh_token"]?.GetValue<string>(),
            json["id_token"]?.GetValue<string>(),
            json["expires_in"]?.GetValue<int>() ?? 0
        );
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
    /// The observer's view of a token. A failed introspection is a failed prerequisite and throws;
    /// it is never reported as "inactive".
    /// </summary>
    public async Task<bool> IntrospectAsObserverAsync(
        ClientCredentials observer,
        string token,
        string? tokenTypeHint
    )
    {
        using HttpRequestMessage request = new(HttpMethod.Post, IntrospectionEndpoint);
        request.Headers.Authorization = observer.ToBasicHeader();
        FormBody form = new FormBody().Add("token", token);
        if (tokenTypeHint is not null)
        {
            form.Add("token_type_hint", tokenTypeHint);
        }

        request.Content = form.ToContent();
        using HttpResponseMessage response = await _http.SendAsync(request);
        JsonObject json = await ReadJsonObjectAsync(response, "observer introspection");
        return json["active"] is JsonValue active && active.TryGetValue(out bool isActive)
            ? isActive
            : throw new InvalidOperationException(
                "The observer's introspection response has no boolean 'active' member."
            );
    }

    public async Task<HttpOutcome> RevokeAsync(FormBody form, AuthenticationHeaderValue? authorization = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, RevocationEndpoint);
        request.Headers.Authorization = authorization;
        request.Content = form.ToContent();
        using HttpResponseMessage response = await _http.SendAsync(request);
        return await HttpOutcome.FromResponseAsync(response);
    }

    private static HttpRequestMessage AdminRequest(HttpMethod method, string url, string adminToken)
    {
        HttpRequestMessage request = new(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        return request;
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

    private static async Task<string> ReadCreatedIdAsync(HttpResponseMessage response, string kind)
    {
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Creating the characterization {kind} answered HTTP {(int)response.StatusCode}: {body}"
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

    private static async Task<JsonObject> ReadJsonObjectAsync(HttpResponseMessage response, string operation)
    {
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The {operation} answered HTTP {(int)response.StatusCode}; the characterization prerequisite failed. Body: {Sanitize(body)}"
            );
        }

        try
        {
            return JsonNode.Parse(body)?.AsObject()
                ?? throw new InvalidOperationException($"The {operation} returned a non-object JSON body.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"The {operation} returned a non-JSON body.", exception);
        }
    }

    // Error bodies from Keycloak's admin/token endpoints carry no secrets, but they are bounded here
    // so an unexpected large body never floods the test log.
    private static string Sanitize(string body) => body.Length <= 500 ? body : body[..500] + "…";
}

/// <summary>
/// The per-run realm resources (spec P1.1 and D-16): two confidential subject clients, one
/// confidential subject that also issues refresh tokens, one confidential subject with a short
/// access-token lifespan for the expiry row only, one confidential observer used solely for
/// introspection, and one public client with a user so it can hold a user-flow token. Every name
/// carries the run id, and <see cref="DisposeAsync"/> removes everything through the admin API.
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

    public static async Task<CharacterizationRealm> CreateAsync()
    {
        KeycloakCharacterizationApi api = new(
            KeycloakCharacterizationEnvironment.KeycloakUrl,
            KeycloakCharacterizationEnvironment.Realm,
            KeycloakCharacterizationEnvironment.AdminRealm
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

    /// <summary>The observer's view of a token (D-16). Observer credentials are never submitted to revocation.</summary>
    public Task<bool> ObserveAsync(string token) =>
        Api.IntrospectAsObserverAsync(Observer.Credentials, token, tokenTypeHint: null);

    /// <summary>
    /// The observer's view of a refresh token. The hint is required on Keycloak 26.4.12 and later: without
    /// it the access-token introspection provider applies its audience check, and a refresh token's
    /// audience is the issuer, so every refresh token would read as inactive (observed in P1.1).
    /// </summary>
    public Task<bool> ObserveRefreshTokenAsync(string refreshToken) =>
        Api.IntrospectAsObserverAsync(Observer.Credentials, refreshToken, "refresh_token");

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

        // Keycloak 26.4.12 and later deny introspection unless the introspecting client is in the
        // token's audience claim. The pinned 26.1 image has no such check. A per-run client scope adds
        // the observer to the audience of every subject and public-client token so the observer works
        // on both versions. The scope changes the audience claim only and is removed with the other
        // run resources.
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

/// <summary>A value §2.1 either predicts (possibly as "absent") or leaves open.</summary>
public readonly record struct Predicted<T>(bool IsPredicted, T? Value)
{
    public static Predicted<T> Unpredicted => new(false, default);

    public static Predicted<T> Of(T? value) => new(true, value);

    public string Render()
    {
        if (!IsPredicted)
        {
            return "n/p";
        }

        return Value switch
        {
            null => "(absent)",
            true => "active",
            false => "inactive",
            _ => Value.ToString() ?? "(absent)",
        };
    }
}

/// <summary>The §2.1 prediction for one evidence row.</summary>
public sealed record RowPrediction(
    Predicted<int> Status,
    Predicted<string> Error,
    Predicted<string> ErrorDescription,
    Predicted<bool> ActiveAfter
)
{
    public static RowPrediction EmptySuccess(Predicted<bool> activeAfter) =>
        new(Predicted<int>.Of(200), Predicted<string>.Of(null), Predicted<string>.Of(null), activeAfter);

    public static RowPrediction ErrorBody(
        int status,
        string error,
        string description,
        Predicted<bool> activeAfter
    ) =>
        new(
            Predicted<int>.Of(status),
            Predicted<string>.Of(error),
            Predicted<string>.Of(description),
            activeAfter
        );

    public static RowPrediction Open(Predicted<bool> activeAfter) =>
        new(
            Predicted<int>.Unpredicted,
            Predicted<string>.Unpredicted,
            Predicted<string>.Unpredicted,
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

/// <summary>Collects the rows of one run and renders the §9.1 table.</summary>
public static class EvidenceLog
{
    private static readonly List<EvidenceRow> _rows = [];
    private static readonly Lock _sync = new();

    public static void Record(EvidenceRow row)
    {
        lock (_sync)
        {
            _rows.Add(row);
        }

        TestContext.Progress.WriteLine($"[evidence] {RenderRow(row)}");
    }

    public static string RenderMarkdown(CharacterizationRealm realm)
    {
        List<EvidenceRow> rows;
        lock (_sync)
        {
            rows = [.. _rows.OrderBy(row => row.Id, StringComparer.Ordinal)];
        }

        StringBuilder markdown = new();
        markdown.AppendLine(
            $"Keycloak `{realm.ServerVersion}` at `{KeycloakCharacterizationEnvironment.KeycloakUrl}`, realm `{KeycloakCharacterizationEnvironment.Realm}` (`sslRequired` = `{realm.SslRequired ?? "(absent)"}`), run `{realm.RunId}`, {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC."
        );
        markdown.AppendLine();
        markdown.AppendLine(
            "Columns show `predicted / observed`; `n/p` = §2.1 made no prediction; `(absent)` = no such member in the body."
        );
        markdown.AppendLine();
        markdown.AppendLine(
            "| ID | Scenario | HTTP | `error` | `error_description` | active before | active after | Notes |"
        );
        markdown.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (EvidenceRow row in rows)
        {
            markdown.AppendLine(RenderRow(row));
        }

        return markdown.ToString();
    }

    private static string RenderRow(EvidenceRow row)
    {
        HttpOutcome outcome = row.Observation.Outcome;
        bool hasCall = outcome.Status != 0;
        string? body = null;
        if (hasCall)
        {
            body = outcome switch
            {
                { BodyLength: 0 } => "empty body",
                { BodyIsJsonObject: true } => $"JSON, {outcome.BodyLength} chars",
                _ => $"non-JSON, {outcome.BodyLength} chars",
            };
        }
        string notes = string.Join(
            "; ",
            new string?[]
            {
                row.Observation.Notes,
                body,
                outcome.WwwAuthenticate is null ? null : $"WWW-Authenticate: {outcome.WwwAuthenticate}",
            }.Where(note => !string.IsNullOrEmpty(note))
        );
        return $"| {row.Id} | {row.Scenario} | {row.Prediction.Status.Render()} / {(hasCall ? outcome.Status.ToString() : "—")} "
            + $"| {RenderCode(row.Prediction.Error)} / {RenderCode(Predicted<string>.Of(outcome.Error))} "
            + $"| {RenderText(row.Prediction.ErrorDescription)} / {RenderText(Predicted<string>.Of(outcome.ErrorDescription))} "
            + $"| {RenderState(row.Observation.ActiveBefore)} "
            + $"| {row.Prediction.ActiveAfter.Render()} / {RenderState(row.Observation.ActiveAfter)} "
            + $"| {notes.Replace("|", "\\|")} |";
    }

    private static string RenderCode(Predicted<string> value) =>
        value is { IsPredicted: true, Value: not null } ? $"`{value.Value}`" : value.Render();

    private static string RenderText(Predicted<string> value) =>
        value is { IsPredicted: true, Value: not null }
            ? $"\"{value.Value.Replace("|", "\\|")}\""
            : value.Render();

    private static string RenderState(bool? state) =>
        state switch
        {
            true => "active",
            false => "inactive",
            null => "—",
        };
}
