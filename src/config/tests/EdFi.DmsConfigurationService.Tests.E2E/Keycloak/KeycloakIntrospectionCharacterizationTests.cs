// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Tests.E2E.Keycloak;
using FluentAssertions;

// The namespace sits outside EdFi.DmsConfigurationService.Tests.E2E.Keycloak on purpose: NUnit applies a
// [SetUpFixture] to every fixture in its namespace, and the revocation characterization's realm setup
// there would otherwise provision its own resources and overwrite its evidence file whenever this
// fixture runs.
namespace EdFi.DmsConfigurationService.Tests.E2E.KeycloakIntrospectionCharacterization;

// Characterization of Keycloak's OAuth 2.0 token introspection endpoint, so that a Configuration
// Service manager that delegates introspection to Keycloak maps only answers recorded here. Every row
// talks to Keycloak directly and asserts exact, version-aware predictions: status, body kind,
// Content-Type, Cache-Control, the OAuth error members, `active`, the exact top-level member names and
// the shapes of azp, client_id, aud, scope, token_type, typ, iss, sub, exp and iat. Unlike the
// revocation harness, the per-run clients carry no observer audience scope, so a token's `aud` holds
// the introspecting client only where a row says so (DMS-1327 §9.1.4 and A-06: from upstream 26.6.2,
// the Red Hat build's 26.4.12, Keycloak answers active:false to a client outside the token's audience).

/// <summary>A Keycloak server version reduced to major.minor.patch.</summary>
public readonly record struct KeycloakServerVersion(int Major, int Minor, int Patch)
{
    /// <summary>
    /// True from 26.6.2, the upstream release whose migration guide lists the introspection audience
    /// check (the Red Hat build of Keycloak ships it as 26.4.12); absent on 26.1.4 and present on
    /// 26.7.5 in DMS-1327 §9.1.4.
    /// </summary>
    public bool AudienceChecked => (Major, Minor, Patch).CompareTo((26, 6, 2)) >= 0;

    /// <summary>
    /// True from 26.7.0. Answers that differ between the observed 26.1.4 and 26.7.5 for reasons other
    /// than the audience check key on this. No release between the two was observed, so this is the
    /// observed release line, not a verified change point; a run on an unobserved release that fails
    /// needs its own characterization.
    /// </summary>
    public bool On267ReleaseLine => (Major, Minor).CompareTo((26, 7)) >= 0;

    public static KeycloakServerVersion Parse(string version)
    {
        string[] parts = version.Split('.');
        if (
            parts.Length < 3
            || !TryLeadingNumber(parts[0], out int major)
            || !TryLeadingNumber(parts[1], out int minor)
            || !TryLeadingNumber(parts[2], out int patch)
        )
        {
            throw new InvalidOperationException($"The server version '{version}' is not major.minor.patch.");
        }

        return new KeycloakServerVersion(major, minor, patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    private static bool TryLeadingNumber(string part, out int number) =>
        int.TryParse(
            new string([.. part.TakeWhile(char.IsAsciiDigit)]),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out number
        );
}

/// <summary>
/// The per-run realm resources of the introspection characterization. Every client is confidential
/// with a random secret and a service account unless noted:
/// A, B and B2 are plain; A2, R and E carry a per-run scope whose audience mapper puts the client itself
/// into its tokens' <c>aud</c>; C carries one that puts B2 into C's tokens; CMS mirrors a client the
/// Configuration Service creates (its only default scope, the claim-set scope, carries an
/// <c>oidc-audience-resolve-mapper</c>, and its own audience mapper puts the client itself into
/// <c>aud</c>); CMS-legacy mirrors one created before the Configuration Service added that mapper and
/// shares CMS's claim-set scope, as two clients of one claim set do; S names no default client scope,
/// so its tokens carry an empty <c>scope</c>, and carries the same own audience mapper as CMS;
/// R also issues refresh tokens for <c>client_credentials</c>; E's access tokens live 10 seconds; P is
/// public and holds a per-run user; BO is bearer-only with a secret; D is disabled after creation. The
/// audience scopes set <c>include.in.token.scope=false</c> so they change <c>aud</c> only. Every name
/// carries the run id, and <see cref="DisposeAsync"/> removes the user, the clients and the scopes.
/// </summary>
public sealed class IntrospectionCharacterizationRealm : IAsyncDisposable
{
    public const int ExpiryClientAccessTokenLifespanSeconds = 10;

    // The role claim type the Configuration Service compose files configure, passed unescaped to the
    // realm-role protocol mapper exactly as KeycloakClientRepository does.
    public const string CmsRoleClaimType = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";

    private readonly string _adminUsername;
    private readonly string _adminPassword;
    private readonly List<CharacterizationClient> _clients = [];
    private readonly Dictionary<string, CharacterizationClient> _clientsByLabel = new(StringComparer.Ordinal);
    private readonly List<string> _scopeUuids = [];
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _audienceScopeNames = new(StringComparer.Ordinal);
    private CharacterizationUser? _publicUser;

    private IntrospectionCharacterizationRealm(
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

    public KeycloakServerVersion Version { get; private set; }

    public bool AudienceChecked => Version.AudienceChecked;

    public bool On267ReleaseLine => Version.On267ReleaseLine;

    public string? SslRequired { get; private set; }

    public CharacterizationClient A => ClientLabelled("A");

    public CharacterizationClient A2 => ClientLabelled("A2");

    public CharacterizationClient B => ClientLabelled("B");

    public CharacterizationClient B2 => ClientLabelled("B2");

    public CharacterizationClient C => ClientLabelled("C");

    public CharacterizationClient Cms => ClientLabelled("CMS");

    public CharacterizationClient LegacyCms => ClientLabelled("CMS-legacy");

    public CharacterizationClient Scopeless => ClientLabelled("S");

    public CharacterizationClient R => ClientLabelled("R");

    public CharacterizationClient E => ClientLabelled("E");

    public CharacterizationClient P => ClientLabelled("P");

    public CharacterizationClient BearerOnly => ClientLabelled("BO");

    public CharacterizationClient Disabled => ClientLabelled("D");

    public CharacterizationUser PublicUser =>
        _publicUser ?? throw new InvalidOperationException("The public client's user was not created.");

    public string CmsScopeName { get; private set; } = "";

    /// <summary>The fixed-text diagnostic of a refused bearer-only creation, or null when Keycloak created it.</summary>
    public string? BearerOnlyCreationFailure { get; private set; }

    public bool? BearerOnlyStored { get; private set; }

    public bool? DisabledClientEnabledAfterUpdate { get; private set; }

    public IReadOnlyList<string> CmsDefaultClientScopes { get; private set; } = [];

    public IReadOnlyList<string> LegacyCmsDefaultClientScopes { get; private set; } = [];

    public IReadOnlyList<string> ScopelessDefaultClientScopes { get; private set; } = [];

    /// <summary>The evidence label of a per-run name or of the realm's issuer URL, or null.</summary>
    public string? Alias(string value) => _aliases.GetValueOrDefault(value);

    /// <summary>The name of the per-run audience scope attached to <paramref name="holder"/>.</summary>
    public string AudienceScopeOf(CharacterizationClient holder) =>
        _audienceScopeNames.GetValueOrDefault(holder.ClientId)
        ?? throw new InvalidOperationException("The client holds no characterization audience scope.");

    public static async Task<IntrospectionCharacterizationRealm> CreateAsync(SensitiveValueRegistry sensitive)
    {
        KeycloakCharacterizationApi api = new(
            KeycloakCharacterizationEnvironment.KeycloakUrl,
            KeycloakCharacterizationEnvironment.Realm,
            KeycloakCharacterizationEnvironment.AdminRealm,
            sensitive
        );
        IntrospectionCharacterizationRealm realm = new(
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

    /// <summary>A fresh client-credentials grant for a confidential client.</summary>
    public Task<TokenGrant> ServiceAccountGrantAsync(CharacterizationClient client) =>
        Api.RequestTokenAsync(
            new FormBody()
                .Add("grant_type", "client_credentials")
                .Add("client_id", client.ClientId)
                .Add("client_secret", client.Secret ?? "")
        );

    /// <summary>The refresh token of a fresh client-credentials grant for R.</summary>
    public async Task<string> RefreshTokenGrantedToRAsync()
    {
        TokenGrant grant = await ServiceAccountGrantAsync(R);
        return grant.RefreshToken
            ?? throw new InvalidOperationException(
                "R issued no refresh token; the row cannot be characterized."
            );
    }

    /// <summary>A fresh password grant for the public client's user.</summary>
    public Task<TokenGrant> PublicUserGrantAsync() =>
        Api.RequestTokenAsync(
            new FormBody()
                .Add("grant_type", "password")
                .Add("client_id", P.ClientId)
                .Add("username", PublicUser.Username)
                .Add("password", PublicUser.Password)
        );

    /// <summary>
    /// Proves a token live through a client that is in its audience (or, for a refresh token, its
    /// owner). Anything but <c>active:true</c> is a failed prerequisite, never an observation.
    /// </summary>
    public async Task<bool> ProveLiveAsync(
        CharacterizationClient introspector,
        string token,
        string? tokenTypeHint
    )
    {
        if (!await Api.IntrospectAsync(introspector.Credentials, token, tokenTypeHint))
        {
            throw new InvalidOperationException(
                "A client in the token's audience reported a freshly issued token inactive; the characterization prerequisite failed."
            );
        }

        return true;
    }

    /// <summary>A secret that no client holds, registered so it can never reach the evidence.</summary>
    public string UnheldSecret()
    {
        string secret = $"Unheld-{Guid.NewGuid():N}";
        Api.Sensitive.Register(secret);
        return secret;
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

            foreach (string scopeUuid in _scopeUuids)
            {
                await TryAsync(() => Api.DeleteClientScopeAsync(adminToken, scopeUuid), failures);
            }
        }

        Api.Dispose();

        if (failures.Count > 0)
        {
            throw new AggregateException(
                $"Cleanup of the introspection characterization run {RunId} left resources in realm '{KeycloakCharacterizationEnvironment.Realm}'.",
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

    private CharacterizationClient ClientLabelled(string label) =>
        _clientsByLabel.GetValueOrDefault(label)
        ?? throw new InvalidOperationException($"The characterization client {label} was not created.");

    private void Track(string label, CharacterizationClient client)
    {
        _clients.Add(client);
        _clientsByLabel[label] = client;
        _aliases[client.ClientId] = label;
    }

    private async Task ProvisionAsync()
    {
        string adminToken = await GetAdminTokenAsync();
        ServerVersion = await Api.GetServerVersionAsync(adminToken);
        Version = KeycloakServerVersion.Parse(ServerVersion);
        SslRequired = await Api.GetRealmSslRequiredAsync(adminToken);
        _aliases[Api.RealmUrl] = "realm URL";

        await CreateConfidentialAsync(adminToken, "A", "a", attributes: null);
        await CreateConfidentialAsync(adminToken, "A2", "a2", attributes: null);
        await AttachAudienceScopeAsync(adminToken, holder: A2, audience: A2);
        await CreateConfidentialAsync(adminToken, "B", "b", attributes: null);
        await CreateConfidentialAsync(adminToken, "B2", "b2", attributes: null);
        await CreateConfidentialAsync(adminToken, "C", "c", attributes: null);
        await AttachAudienceScopeAsync(adminToken, holder: C, audience: B2);
        await CreateCmsClaimSetScopeAsync(adminToken);
        CmsDefaultClientScopes = await CreateCmsShapedAsync(adminToken, "CMS", "cms", ownAudience: true);
        LegacyCmsDefaultClientScopes = await CreateCmsShapedAsync(
            adminToken,
            "CMS-legacy",
            "cms-legacy",
            ownAudience: false
        );
        ScopelessDefaultClientScopes = await CreateScopelessAsync(adminToken);
        await CreateConfidentialAsync(
            adminToken,
            "R",
            "refresh",
            new JsonObject { ["client_credentials.use_refresh_token"] = "true" }
        );
        await AttachAudienceScopeAsync(adminToken, holder: R, audience: R);
        await CreateConfidentialAsync(
            adminToken,
            "E",
            "expiry",
            new JsonObject { ["access.token.lifespan"] = ExpiryClientAccessTokenLifespanSeconds.ToString() }
        );
        await AttachAudienceScopeAsync(adminToken, holder: E, audience: E);
        await CreatePublicClientWithUserAsync(adminToken);
        await CreateBearerOnlyAsync(adminToken);
        await CreateConfidentialAsync(adminToken, "D", "disabled", attributes: null);
        DisabledClientEnabledAfterUpdate = await Api.SetClientEnabledAsync(
            adminToken,
            Disabled.Uuid,
            enabled: false
        );
    }

    private async Task CreateConfidentialAsync(
        string adminToken,
        string label,
        string role,
        JsonObject? attributes
    )
    {
        string clientId = $"cs-char-introspect-{role}-{RunId}";
        string secret = $"Secret-{Guid.NewGuid():N}!Aa1";
        JsonObject representation = new()
        {
            ["clientId"] = clientId,
            ["name"] = $"Introspection characterization {label} {RunId}",
            ["secret"] = secret,
            ["protocol"] = "openid-connect",
            ["publicClient"] = false,
            ["serviceAccountsEnabled"] = true,
            ["standardFlowEnabled"] = false,
            ["directAccessGrantsEnabled"] = false,
        };
        if (attributes is not null)
        {
            representation["attributes"] = attributes;
        }

        string uuid = await Api.CreateClientAsync(adminToken, representation);
        Track(label, new CharacterizationClient(uuid, clientId, secret));
    }

    private async Task AttachAudienceScopeAsync(
        string adminToken,
        CharacterizationClient holder,
        CharacterizationClient audience
    )
    {
        string holderLabel = _aliases[holder.ClientId];
        string scopeName = $"cs-char-introspect-{holderLabel.ToLowerInvariant()}-audience-{RunId}";
        string scopeUuid = await Api.CreateClientScopeAsync(
            adminToken,
            new JsonObject
            {
                ["name"] = scopeName,
                ["protocol"] = "openid-connect",
                ["attributes"] = new JsonObject { ["include.in.token.scope"] = "false" },
                ["protocolMappers"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "characterization audience",
                        ["protocol"] = "openid-connect",
                        ["protocolMapper"] = "oidc-audience-mapper",
                        ["config"] = new JsonObject
                        {
                            ["included.client.audience"] = audience.ClientId,
                            ["access.token.claim"] = "true",
                            ["introspection.token.claim"] = "true",
                            ["id.token.claim"] = "false",
                        },
                    }
                ),
            }
        );
        _scopeUuids.Add(scopeUuid);
        _aliases[scopeName] = $"{holderLabel} audience scope";
        _audienceScopeNames[holder.ClientId] = scopeName;
        await Api.AddDefaultClientScopeAsync(
            adminToken,
            holder.Uuid,
            scopeUuid,
            "characterization audience scope"
        );
    }

    // Mirrors KeycloakClientRepository.CheckAndCreateClientScopeAsync: the claim-set scope, created once
    // and named as the only default client scope of every CMS-shaped client.
    private async Task CreateCmsClaimSetScopeAsync(string adminToken)
    {
        CmsScopeName = $"cs-char-introspect-cms-scope-{RunId}";
        string scopeUuid = await Api.CreateClientScopeAsync(
            adminToken,
            new JsonObject
            {
                ["name"] = CmsScopeName,
                ["protocol"] = "openid-connect",
                ["protocolMappers"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "audience resolve",
                        ["protocol"] = "openid-connect",
                        ["protocolMapper"] = "oidc-audience-resolve-mapper",
                        ["consentRequired"] = false,
                        ["config"] = new JsonObject
                        {
                            ["introspection.token.claim"] = "true",
                            ["access.token.claim"] = "true",
                        },
                    }
                ),
                ["attributes"] = new JsonObject { ["include.in.token.scope"] = "true" },
            }
        );
        _scopeUuids.Add(scopeUuid);
        _aliases[CmsScopeName] = "CMS scope";
    }

    // Mirrors KeycloakClientRepository.CreateClientAsync: a client whose representation names the
    // claim-set scope as its only default client scope and carries, in the repository's order, the
    // realm-role mapper, the Namespace Prefixes and Education Organization Ids hardcoded-claim mappers,
    // the "Client's own audience" mapper and the Data Store IDs mapper (placeholder claim values).
    // Without ownAudience it is the shape CreateClientAsync built before it added the audience mapper.
    // The realm-role assignment to the service account is not mirrored. Returns the client's default
    // client scopes as Keycloak stored them.
    private async Task<IReadOnlyList<string>> CreateCmsShapedAsync(
        string adminToken,
        string label,
        string role,
        bool ownAudience
    )
    {
        string clientId = $"cs-char-introspect-{role}-{RunId}";
        string secret = $"Secret-{Guid.NewGuid():N}!Aa1";
        JsonArray protocolMappers = new(
            new JsonObject
            {
                ["name"] = "Configuration service role mapper",
                ["protocol"] = "openid-connect",
                ["protocolMapper"] = "oidc-usermodel-realm-role-mapper",
                ["config"] = new JsonObject
                {
                    ["claim.name"] = CmsRoleClaimType,
                    ["jsonType.label"] = "String",
                    ["user.attribute"] = "roles",
                    ["multivalued"] = "true",
                    ["id.token.claim"] = "true",
                    ["access.token.claim"] = "true",
                    ["userinfo.token.claim"] = "true",
                },
            },
            HardcodedClaimMapper("Namespace Prefixes", "namespacePrefixes", "uri://ed-fi.org"),
            HardcodedClaimMapper("Education Organization Ids", "educationOrganizationIds", "255901")
        );
        if (ownAudience)
        {
            protocolMappers.Add(OwnAudienceMapper(clientId));
        }

        protocolMappers.Add(HardcodedClaimMapper("Data Store IDs", "dataStoreIds", "1"));

        string uuid = await Api.CreateClientAsync(
            adminToken,
            new JsonObject
            {
                ["clientId"] = clientId,
                ["enabled"] = true,
                ["secret"] = secret,
                ["name"] = $"Introspection characterization {label} {RunId}",
                ["serviceAccountsEnabled"] = true,
                ["defaultClientScopes"] = new JsonArray(CmsScopeName),
                ["protocolMappers"] = protocolMappers,
            }
        );
        Track(label, new CharacterizationClient(uuid, clientId, secret));
        return await Api.GetDefaultClientScopeNamesAsync(adminToken, uuid);
    }

    // A client whose tokens carry an empty scope: naming no default client scope leaves only the
    // service-account scope Keycloak adds, which puts nothing into scope. Its own audience mapper lets it
    // introspect its own tokens from 26.6.2. Returns the client's default client scopes as Keycloak
    // stored them.
    private async Task<IReadOnlyList<string>> CreateScopelessAsync(string adminToken)
    {
        string clientId = $"cs-char-introspect-scopeless-{RunId}";
        string secret = $"Secret-{Guid.NewGuid():N}!Aa1";
        string uuid = await Api.CreateClientAsync(
            adminToken,
            new JsonObject
            {
                ["clientId"] = clientId,
                ["name"] = $"Introspection characterization S {RunId}",
                ["secret"] = secret,
                ["protocol"] = "openid-connect",
                ["publicClient"] = false,
                ["serviceAccountsEnabled"] = true,
                ["standardFlowEnabled"] = false,
                ["directAccessGrantsEnabled"] = false,
                ["defaultClientScopes"] = new JsonArray(),
                ["protocolMappers"] = new JsonArray(OwnAudienceMapper(clientId)),
            }
        );
        Track("S", new CharacterizationClient(uuid, clientId, secret));
        return await Api.GetDefaultClientScopeNamesAsync(adminToken, uuid);
    }

    // Mirrors KeycloakClientRepository.SelfAudienceProtocolMapper.
    private static JsonObject OwnAudienceMapper(string clientId) =>
        new()
        {
            ["name"] = "Client's own audience",
            ["protocol"] = "openid-connect",
            ["protocolMapper"] = "oidc-audience-mapper",
            ["config"] = new JsonObject
            {
                ["included.client.audience"] = clientId,
                ["id.token.claim"] = "false",
                ["access.token.claim"] = "true",
                ["introspection.token.claim"] = "true",
                ["lightweight.claim"] = "false",
            },
        };

    private static JsonObject HardcodedClaimMapper(string name, string claimName, string value) =>
        new()
        {
            ["name"] = name,
            ["protocol"] = "openid-connect",
            ["protocolMapper"] = "oidc-hardcoded-claim-mapper",
            ["config"] = new JsonObject
            {
                ["access.token.claim"] = "true",
                ["claim.name"] = claimName,
                ["claim.value"] = value,
                ["id.token.claim"] = "true",
                ["introspection.token.claim"] = "true",
                ["jsonType.label"] = "String",
                ["lightweight.claim"] = "false",
                ["userinfo.token.claim"] = "true",
            },
        };

    private async Task CreatePublicClientWithUserAsync(string adminToken)
    {
        string clientId = $"cs-char-introspect-public-{RunId}";
        string uuid = await Api.CreateClientAsync(
            adminToken,
            new JsonObject
            {
                ["clientId"] = clientId,
                ["name"] = $"Introspection characterization P {RunId}",
                ["protocol"] = "openid-connect",
                ["publicClient"] = true,
                ["directAccessGrantsEnabled"] = true,
                ["standardFlowEnabled"] = false,
                ["serviceAccountsEnabled"] = false,
            }
        );
        Track("P", new CharacterizationClient(uuid, clientId, Secret: null));

        string username = $"cs-char-introspect-user-{RunId}";
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
    }

    // A bearer-only client never obtains a token, so it has no service account.
    private async Task CreateBearerOnlyAsync(string adminToken)
    {
        string clientId = $"cs-char-introspect-bearer-only-{RunId}";
        string secret = $"Secret-{Guid.NewGuid():N}!Aa1";
        string uuid;
        try
        {
            uuid = await Api.CreateClientAsync(
                adminToken,
                new JsonObject
                {
                    ["clientId"] = clientId,
                    ["name"] = $"Introspection characterization BO {RunId}",
                    ["secret"] = secret,
                    ["protocol"] = "openid-connect",
                    ["publicClient"] = false,
                    ["bearerOnly"] = true,
                    ["serviceAccountsEnabled"] = false,
                    ["standardFlowEnabled"] = false,
                    ["directAccessGrantsEnabled"] = false,
                }
            );
        }
        catch (InvalidOperationException refused)
        {
            BearerOnlyCreationFailure = refused.Message;
            return;
        }

        Track("BO", new CharacterizationClient(uuid, clientId, secret));
        BearerOnlyStored = await Api.GetClientFlagAsync(adminToken, uuid, "bearerOnly");
    }
}

/// <summary>The prediction for one introspection row. Member names are kept in ordinal order.</summary>
public sealed record IntrospectionPrediction(
    int Status,
    BodyKind Body,
    string? ContentType,
    string? CacheControl,
    string? Error,
    string? ErrorDescription,
    bool? Active,
    IReadOnlyList<string> MemberNames,
    IReadOnlyDictionary<string, MemberShape> Members,
    bool? ExpAfterIat
)
{
    public const string JsonContentType = "application/json";

    // Observed on both versions: every 200 answer carries this header, and no error answer carries
    // any Cache-Control header.
    public const string NoCache = "no-cache";

    public static IReadOnlyDictionary<string, MemberShape> NoRecordedMembers { get; } =
        IntrospectionOutcome.RecordedMembers.ToDictionary(
            static name => name,
            static _ => MemberShape.Absent,
            StringComparer.Ordinal
        );

    /// <summary>200 with a JSON object whose only member is <c>active:false</c>.</summary>
    public static IntrospectionPrediction Inactive() =>
        new(
            200,
            BodyKind.JsonObject,
            JsonContentType,
            NoCache,
            null,
            null,
            false,
            ["active"],
            NoRecordedMembers,
            null
        );

    /// <summary>200 with <c>active:true</c>, exactly these member names and these recorded shapes; <c>exp</c> after <c>iat</c>.</summary>
    public static IntrospectionPrediction ActiveAnswer(
        IEnumerable<string> memberNames,
        IReadOnlyDictionary<string, MemberShape> members
    ) =>
        new(
            200,
            BodyKind.JsonObject,
            JsonContentType,
            NoCache,
            null,
            null,
            true,
            [.. memberNames.Order(StringComparer.Ordinal)],
            members,
            true
        );

    /// <summary>The given status with a JSON object carrying exactly <c>error</c> and <c>error_description</c>, and no Cache-Control header.</summary>
    public static IntrospectionPrediction ErrorBody(int status, string error, string description) =>
        new(
            status,
            BodyKind.JsonObject,
            JsonContentType,
            null,
            error,
            description,
            null,
            ["error", "error_description"],
            NoRecordedMembers,
            null
        );

    /// <summary>
    /// 413 with a JSON object whose only member is <c>error</c>, and no Cache-Control header: a form
    /// value over Keycloak's form attribute limit, refused before the caller is authenticated.
    /// </summary>
    public static IntrospectionPrediction FormAttributeTooLarge() =>
        new(
            413,
            BodyKind.JsonObject,
            JsonContentType,
            null,
            "HTTP 413 Request Entity Too Large",
            null,
            null,
            ["error"],
            NoRecordedMembers,
            null
        );

    public static IReadOnlyDictionary<string, MemberShape> TokenMembers(
        string azp,
        string clientId,
        MemberShape aud,
        MemberShape scope,
        string tokenType,
        string typ,
        string iss,
        MemberShape sub
    ) =>
        new Dictionary<string, MemberShape>(StringComparer.Ordinal)
        {
            ["azp"] = MemberShape.OfString(azp),
            ["client_id"] = MemberShape.OfString(clientId),
            ["aud"] = aud,
            ["scope"] = scope,
            ["token_type"] = MemberShape.OfString(tokenType),
            ["typ"] = MemberShape.OfString(typ),
            ["iss"] = MemberShape.OfString(iss),
            ["sub"] = sub,
            ["exp"] = MemberShape.NumberKind,
            ["iat"] = MemberShape.NumberKind,
        };
}

/// <summary>Predictions shared by several rows, as observed on 26.1.4 and 26.7.5.</summary>
public static class ExpectedIntrospection
{
    public const string InvalidClientDescription = "Invalid client or Invalid client credentials";

    /// <summary>
    /// The longest form value Keycloak accepts by default (128 KiB); one character more is answered
    /// 413 before the caller is authenticated.
    /// </summary>
    public const int FormAttributeLimit = 131072;

    // The members of an active access-token answer for a client holding the realm's default client
    // scopes, identical on both versions. clientHost and clientAddress are not among them, although
    // the refresh-token answer (I-16) carries both.
    private static readonly string[] _defaultScopeAccessTokenMembers =
    [
        "acr",
        "active",
        "aud",
        "azp",
        "client_id",
        "email_verified",
        "exp",
        "iat",
        "iss",
        "jti",
        "preferred_username",
        "realm_access",
        "resource_access",
        "scope",
        "sub",
        "token_type",
        "typ",
        "username",
    ];

    private static IntrospectionCharacterizationRealm Realm =>
        KeycloakIntrospectionCharacterizationSetup.Realm;

    /// <summary>An access token from <c>client_credentials</c> for a client holding the realm's default client scopes.</summary>
    public static IntrospectionPrediction DefaultScopeAccessToken(
        CharacterizationClient issuedTo,
        MemberShape aud
    ) =>
        IntrospectionPrediction.ActiveAnswer(
            _defaultScopeAccessTokenMembers,
            IntrospectionPrediction.TokenMembers(
                issuedTo.ClientId,
                issuedTo.ClientId,
                aud,
                MemberShape.OfSpaceDelimited("profile email"),
                "Bearer",
                "Bearer",
                Realm.Api.RealmUrl,
                MemberShape.StringKind
            )
        );

    /// <summary>
    /// An access token from <c>client_credentials</c> for a CMS-shaped client, answered active; the
    /// two shapes differ only in <c>aud</c> (CMS-legacy's is answered inactive from 26.6.2, I-05). It
    /// carries <c>sub</c> although the client holds no <c>basic</c> scope.
    /// </summary>
    public static IntrospectionPrediction CmsShapedAccessToken(
        CharacterizationClient issuedTo,
        MemberShape aud
    ) =>
        IntrospectionPrediction.ActiveAnswer(
            [
                "active",
                "aud",
                "azp",
                "client_id",
                "dataStoreIds",
                "educationOrganizationIds",
                "exp",
                "http://schemas",
                "iat",
                "iss",
                "jti",
                "namespacePrefixes",
                "scope",
                "sub",
                "token_type",
                "typ",
                "username",
            ],
            IntrospectionPrediction.TokenMembers(
                issuedTo.ClientId,
                issuedTo.ClientId,
                aud,
                MemberShape.OfSpaceDelimited(Realm.CmsScopeName),
                "Bearer",
                "Bearer",
                Realm.Api.RealmUrl,
                MemberShape.StringKind
            )
        );

    /// <summary>CMS's access token: its own audience mapper puts CMS beside <c>account</c> in <c>aud</c>.</summary>
    public static IntrospectionPrediction CmsAccessToken() =>
        CmsShapedAccessToken(Realm.Cms, MemberShape.OfArray(Realm.Cms.ClientId, "account"));

    /// <summary>CMS-legacy's access token: the claim-set scope's audience-resolve mapper yields only <c>account</c>.</summary>
    public static IntrospectionPrediction LegacyCmsAccessToken() =>
        CmsShapedAccessToken(Realm.LegacyCms, MemberShape.OfString("account"));

    /// <summary>
    /// S's access token, answered active without a <c>scope</c> member: Keycloak leaves it out when the
    /// token's scope is empty. S's own audience mapper makes S the only audience.
    /// </summary>
    public static IntrospectionPrediction ScopelessAccessToken() =>
        IntrospectionPrediction.ActiveAnswer(
            [
                "active",
                "aud",
                "azp",
                "client_id",
                "exp",
                "iat",
                "iss",
                "jti",
                "sub",
                "token_type",
                "typ",
                "username",
            ],
            IntrospectionPrediction.TokenMembers(
                Realm.Scopeless.ClientId,
                Realm.Scopeless.ClientId,
                MemberShape.OfString(Realm.Scopeless.ClientId),
                MemberShape.Absent,
                "Bearer",
                "Bearer",
                Realm.Api.RealmUrl,
                MemberShape.StringKind
            )
        );

    /// <summary>
    /// R's refresh token answered active, identical on both versions: the answer carries
    /// access-token claims, an <c>aud</c> of the issuer, R and <c>account</c> although the refresh
    /// token's own <c>aud</c> is the issuer alone, and a <c>scope</c> that also names scopes the
    /// access token's <c>scope</c> omits (for example <c>basic</c>, <c>roles</c> and R's audience scope).
    /// </summary>
    public static IntrospectionPrediction OwnRefreshToken() =>
        IntrospectionPrediction.ActiveAnswer(
            [
                "acr",
                "active",
                "aud",
                "azp",
                "clientAddress",
                "clientHost",
                "client_id",
                "email_verified",
                "exp",
                "iat",
                "iss",
                "jti",
                "preferred_username",
                "realm_access",
                "resource_access",
                "scope",
                "sid",
                "sub",
                "token_type",
                "typ",
                "username",
            ],
            IntrospectionPrediction.TokenMembers(
                Realm.R.ClientId,
                Realm.R.ClientId,
                MemberShape.OfArray(Realm.Api.RealmUrl, Realm.R.ClientId, "account"),
                MemberShape.OfSpaceDelimited(
                    $"acr basic email profile roles service_account web-origins {Realm.AudienceScopeOf(Realm.R)}"
                ),
                "Refresh",
                "Refresh",
                Realm.Api.RealmUrl,
                MemberShape.StringKind
            )
        );

    /// <summary>A2's access token, answered active.</summary>
    public static IntrospectionPrediction A2AccessToken() =>
        DefaultScopeAccessToken(Realm.A2, MemberShape.OfArray(Realm.A2.ClientId, "account"));

    /// <summary>
    /// A client that Keycloak does not authenticate (unknown, case-variant or disabled client, or no
    /// credentials): 26.1.4 wraps every client-authentication failure in <c>invalid_request</c>
    /// "Authentication failed."; 26.7.5 passes the client authenticator's answer through.
    /// </summary>
    public static IntrospectionPrediction UnauthenticatedClient() =>
        Realm.On267ReleaseLine
            ? IntrospectionPrediction.ErrorBody(401, "invalid_client", InvalidClientDescription)
            : AuthenticationFailed();

    /// <summary>The 26.1.4 answer to every client-authentication failure at introspection.</summary>
    public static IntrospectionPrediction AuthenticationFailed() =>
        IntrospectionPrediction.ErrorBody(401, "invalid_request", "Authentication failed.");
}

/// <summary>What one row observed. <see cref="ActiveBefore"/> is null for rows that do not start from a token proven live.</summary>
public sealed record IntrospectionObservation(IntrospectionOutcome Outcome, bool? ActiveBefore, string Notes);

public sealed record IntrospectionEvidenceRow(
    string Id,
    string Scenario,
    IntrospectionPrediction Prediction,
    IntrospectionObservation Observation
);

/// <summary>Renders introspection evidence. <c>{X}</c> stands for the per-run name labelled X.</summary>
public static class IntrospectionRendering
{
    public static string Row(IntrospectionEvidenceRow row, Func<string, string?> alias)
    {
        IntrospectionPrediction predicted = row.Prediction;
        IntrospectionObservation observation = row.Observation;
        IntrospectionOutcome outcome = observation.Outcome;
        string notes = string.Join(
            "; ",
            new[]
            {
                observation.Notes,
                outcome.WwwAuthenticate is null ? null : $"WWW-Authenticate: {outcome.WwwAuthenticate}",
            }.Where(static note => !string.IsNullOrEmpty(note))
        );

        return $"| {row.Id} | {Escape(row.Scenario)} | {predicted.Status} / {outcome.Status} "
            + $"| {Kind(predicted.Body)} / {Kind(outcome.Body)}, {outcome.BodyLength} chars "
            + $"| {Code(predicted.Error)} / {Code(outcome.Error)} "
            + $"| {Text(predicted.ErrorDescription)} / {Text(outcome.ErrorDescription)} "
            + $"| {Flag(predicted.Active)} / {Flag(outcome.Active)} "
            + $"| {Flag(observation.ActiveBefore)} "
            + $"| {Escape(MemberNames(predicted, outcome))} "
            + $"| {Escape(RecordedMembers(predicted, outcome, alias))} "
            + $"| {Escape(Headers(predicted, outcome))} "
            + $"| {Escape(notes)} |";
    }

    /// <summary>Every recorded member whose observed shape differs from the prediction, rendered for an assertion message.</summary>
    public static IReadOnlyList<string> MemberMismatches(
        IntrospectionPrediction predicted,
        IntrospectionOutcome outcome,
        Func<string, string?> alias
    ) =>
        [
            .. IntrospectionOutcome
                .RecordedMembers.Where(name => !outcome.Members[name].Matches(predicted.Members[name]))
                .Select(name =>
                    $"{name}: predicted {Shape(predicted.Members[name], alias)}, observed {Shape(outcome.Members[name], alias)}"
                ),
        ];

    public static string Shape(MemberShape shape, Func<string, string?> alias) =>
        shape.Kind switch
        {
            JsonValueKind.Undefined => "(absent)",
            JsonValueKind.String when shape.Values.Count > 0 => Value(string.Join(' ', shape.Values), alias),
            JsonValueKind.Array =>
                $"[{string.Join(", ", shape.Values.Select(value => Value(value, alias)))}]",
            _ => shape.Kind.ToString().ToLowerInvariant(),
        };

    /// <summary>A string value with every per-run name (whole value or space-separated part) replaced by its label.</summary>
    public static string Value(string value, Func<string, string?> alias)
    {
        if (alias(value) is { } whole)
        {
            return $"{{{whole}}}";
        }

        string[] parts = value.Split(' ');
        string rendered = string.Join(
            ' ',
            parts.Select(part => alias(part) is { } label ? $"{{{label}}}" : part)
        );
        return $"\"{rendered}\"";
    }

    public static string Escape(string text) => text.Replace("|", "\\|");

    /// <summary>A stored boolean flag as the admin API reported it.</summary>
    public static string Stored(bool? value) =>
        value switch
        {
            true => "true",
            false => "false",
            null => "(absent)",
        };

    private static string MemberNames(IntrospectionPrediction predicted, IntrospectionOutcome outcome)
    {
        string observed = outcome.MemberNames.Count == 0 ? "(none)" : string.Join(", ", outcome.MemberNames);
        return outcome.MemberNames.SequenceEqual(predicted.MemberNames, StringComparer.Ordinal)
            ? observed
            : $"{observed} (predicted: {string.Join(", ", predicted.MemberNames)})";
    }

    private static string RecordedMembers(
        IntrospectionPrediction predicted,
        IntrospectionOutcome outcome,
        Func<string, string?> alias
    )
    {
        List<string> rendered = [];
        foreach (string name in IntrospectionOutcome.RecordedMembers)
        {
            MemberShape observed = outcome.Members[name];
            MemberShape expected = predicted.Members[name];
            if (observed.Kind == JsonValueKind.Undefined && expected.Kind == JsonValueKind.Undefined)
            {
                continue;
            }

            string text = $"{name}={Shape(observed, alias)}";
            rendered.Add(observed.Matches(expected) ? text : $"{text} (predicted: {Shape(expected, alias)})");
        }

        if (outcome.ExpAfterIat is not null || predicted.ExpAfterIat is not null)
        {
            string text = $"exp>iat={Flag(outcome.ExpAfterIat)}";
            rendered.Add(
                outcome.ExpAfterIat == predicted.ExpAfterIat
                    ? text
                    : $"{text} (predicted: {Flag(predicted.ExpAfterIat)})"
            );
        }

        return rendered.Count == 0 ? "(none)" : string.Join("; ", rendered);
    }

    private static string Headers(IntrospectionPrediction predicted, IntrospectionOutcome outcome)
    {
        string contentType = Header("Content-Type", predicted.ContentType, outcome.ContentType);
        string cacheControl = Header("Cache-Control", predicted.CacheControl, outcome.CacheControl);
        return $"{contentType}; {cacheControl}";

        static string Header(string name, string? expected, string? observed)
        {
            string text = $"{name}: {observed ?? "(absent)"}";
            return string.Equals(expected, observed, StringComparison.Ordinal)
                ? text
                : $"{text} (predicted: {expected ?? "(absent)"})";
        }
    }

    private static string Kind(BodyKind kind) =>
        kind switch
        {
            BodyKind.Empty => "empty",
            BodyKind.JsonObject => "JSON object",
            BodyKind.JsonArray => "JSON array",
            BodyKind.JsonValue => "JSON value",
            _ => "non-JSON",
        };

    private static string Code(string? value) => value is null ? "(absent)" : $"`{value}`";

    private static string Text(string? value) => value is null ? "(absent)" : $"\"{Escape(value)}\"";

    private static string Flag(bool? value) =>
        value switch
        {
            true => "true",
            false => "false",
            null => "n/a",
        };
}

/// <summary>
/// Collects the rows of one run and renders the evidence table. A row whose rendering contains any
/// secret or token the run has seen is refused and the row fails, so provider content that echoed a
/// credential could not reach the evidence.
/// </summary>
public sealed class IntrospectionEvidenceLog(SensitiveValueRegistry sensitive)
{
    private readonly List<string> _renderedRows = [];
    private readonly Lock _sync = new();

    public void Record(IntrospectionEvidenceRow row, Func<string, string?> alias) =>
        Add(row.Id, IntrospectionRendering.Row(row, alias));

    /// <summary>A row that records facts rather than one characterized call (the preconditions row).</summary>
    public void RecordNote(string id, string scenario, string notes) =>
        Add(
            id,
            $"| {id} | {IntrospectionRendering.Escape(scenario)} | - | - | - | - | - | - | - | - | - | {IntrospectionRendering.Escape(notes)} |"
        );

    public string RenderMarkdown(IntrospectionCharacterizationRealm realm)
    {
        List<string> rows;
        lock (_sync)
        {
            rows = [.. _renderedRows.OrderBy(static rendered => rendered, StringComparer.Ordinal)];
        }

        StringBuilder markdown = new();
        markdown.AppendLine(
            $"Keycloak `{realm.ServerVersion}` at `{KeycloakCharacterizationEnvironment.KeycloakUrl}`, realm `{KeycloakCharacterizationEnvironment.Realm}` (`sslRequired` = `{realm.SslRequired ?? "(absent)"}`), "
                + $"introspection audience check predicted: {(realm.AudienceChecked ? "yes" : "no")} (version {realm.Version} {(realm.AudienceChecked ? ">=" : "<")} 26.6.2, the Red Hat build's 26.4.12), "
                + $"26.7 release-line answers predicted: {(realm.On267ReleaseLine ? "yes" : "no")}, run `{realm.RunId}`, {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC."
        );
        markdown.AppendLine();
        markdown.AppendLine(
            "HTTP, Body, `error`, `error_description` and `active` show `predicted / observed`; the member, recorded-member and header columns show the observed value and append `(predicted: ...)` where the prediction differs. "
                + "`{X}` is the per-run client id of client X (or the per-run scope name, or the realm's issuer URL); `(absent)` = no such member; `n/a` = not applicable. "
                + "`sub`, `exp` and `iat` are recorded by kind only; `aud` arrays and the space-delimited `scope` compare as sets and are rendered in the observed order (Keycloak's `scope` order changes between runs of the same version). "
                + "Clients: A, B, B2 plain confidential; A2, R, E carry themselves in `aud`; C carries B2 in `aud`; CMS mirrors a client the Configuration Service creates (its own audience mapper puts it in `aud`); CMS-legacy one created before that mapper existed; R issues refresh tokens; E's access tokens live 10 s; P public with a user; BO bearer-only with a secret; D disabled."
        );
        markdown.AppendLine();
        markdown.AppendLine(
            "| ID | Scenario | HTTP | Body | `error` | `error_description` | `active` | proven live before | Members | Recorded members | Headers | Notes |"
        );
        markdown.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
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
}

/// <summary>Creates the run's realm resources once and removes them (and writes the evidence table) at the end.</summary>
[SetUpFixture]
public class KeycloakIntrospectionCharacterizationSetup
{
    private static IntrospectionCharacterizationRealm? _realm;
    private static IntrospectionEvidenceLog? _evidence;

    public static IntrospectionCharacterizationRealm Realm =>
        _realm
        ?? throw new InvalidOperationException(
            "The introspection characterization realm resources were not created."
        );

    public static IntrospectionEvidenceLog Evidence =>
        _evidence
        ?? throw new InvalidOperationException(
            "The introspection characterization evidence log was not created."
        );

    [OneTimeSetUp]
    public async Task CreateRealmResources()
    {
        if (KeycloakCharacterizationEnvironment.IsKeycloakProvider)
        {
            SensitiveValueRegistry sensitive = new();
            _evidence = new IntrospectionEvidenceLog(sensitive);
            _realm = await IntrospectionCharacterizationRealm.CreateAsync(sensitive);
        }
    }

    [OneTimeTearDown]
    public async Task WriteEvidenceAndRemoveRealmResources()
    {
        if (_realm is null)
        {
            return;
        }

        IntrospectionCharacterizationRealm realm = _realm;
        _realm = null;
        Exception? evidenceFailure = null;
        try
        {
            string evidencePath = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                $"keycloak-introspection-characterization-{realm.ServerVersion}.md"
            );
            await File.WriteAllTextAsync(evidencePath, Evidence.RenderMarkdown(realm));
            await TestContext.Progress.WriteLineAsync($"[evidence] table written to {evidencePath}");
        }
        catch (Exception exception)
        {
            evidenceFailure = exception;
        }
        finally
        {
            // Realm cleanup must happen even when the evidence file could not be written. If both
            // fail, both are reported: the cleanup failure names what was left behind and the
            // evidence failure says why the table is missing.
            try
            {
                await realm.DisposeAsync();
            }
            catch (Exception cleanupFailure) when (evidenceFailure is not null)
            {
                throw new AggregateException(
                    "Writing the evidence table and cleaning up the realm resources both failed.",
                    evidenceFailure,
                    cleanupFailure
                );
            }
        }

        if (evidenceFailure is not null)
        {
            throw new InvalidOperationException(
                "The evidence table could not be written; the realm resources were cleaned up.",
                evidenceFailure
            );
        }
    }
}

/// <summary>
/// One evidence row: the subclass arranges tokens, performs exactly one characterized introspection
/// and reports what it observed; the shared tests compare that against the row's prediction.
/// </summary>
[Category(KeycloakCharacterizationEnvironment.Category)]
public abstract class IntrospectionCharacterizationRow
{
    private IntrospectionObservation? _observation;
    private IntrospectionPrediction? _prediction;

    protected static IntrospectionCharacterizationRealm Realm =>
        KeycloakIntrospectionCharacterizationSetup.Realm;

    protected IntrospectionObservation Observation =>
        _observation ?? throw new InvalidOperationException("The row has not been observed.");

    protected abstract string RowId { get; }

    protected abstract string Scenario { get; }

    /// <summary>Evaluated once, after provisioning, so it can depend on the server version and the run's clients.</summary>
    protected abstract IntrospectionPrediction Prediction { get; }

    private IntrospectionPrediction Predicted =>
        _prediction ?? throw new InvalidOperationException("The row has no prediction.");

    private IntrospectionOutcome Outcome => Observation.Outcome;

    [OneTimeSetUp]
    public async Task ArrangeAndAct()
    {
        KeycloakCharacterizationEnvironment.RequireKeycloakProvider();
        _prediction = Prediction;
        _observation = await ObserveAsync();
        KeycloakIntrospectionCharacterizationSetup.Evidence.Record(
            new IntrospectionEvidenceRow(RowId, Scenario, _prediction, _observation),
            Realm.Alias
        );
    }

    protected abstract Task<IntrospectionObservation> ObserveAsync();

    [Test]
    public void It_started_from_a_token_proven_live()
    {
        if (Observation.ActiveBefore is null)
        {
            Assert.Ignore(
                "Not applicable: this row does not start from a token proven live by another call."
            );
        }

        Observation.ActiveBefore.Should().BeTrue();
    }

    [Test]
    public void It_answers_the_predicted_http_status() => Outcome.Status.Should().Be(Predicted.Status);

    [Test]
    public void It_answers_with_the_predicted_body_kind() => Outcome.Body.Should().Be(Predicted.Body);

    [Test]
    public void It_answers_the_predicted_content_type() =>
        Outcome.ContentType.Should().Be(Predicted.ContentType);

    [Test]
    public void It_answers_the_predicted_cache_control() =>
        Outcome.CacheControl.Should().Be(Predicted.CacheControl);

    [Test]
    public void It_answers_the_predicted_error() => Outcome.Error.Should().Be(Predicted.Error);

    [Test]
    public void It_answers_the_predicted_error_description() =>
        Outcome.ErrorDescription.Should().Be(Predicted.ErrorDescription);

    [Test]
    public void It_answers_the_predicted_active_value() => Outcome.Active.Should().Be(Predicted.Active);

    [Test]
    public void It_answers_exactly_the_predicted_member_names() =>
        Outcome.MemberNames.Should().Equal(Predicted.MemberNames);

    [Test]
    public void It_answers_the_predicted_shape_for_every_recorded_member() =>
        IntrospectionRendering.MemberMismatches(Predicted, Outcome, Realm.Alias).Should().BeEmpty();

    [Test]
    public void It_answers_the_predicted_exp_and_iat_order() =>
        Outcome.ExpAfterIat.Should().Be(Predicted.ExpAfterIat);

    /// <summary>The token's own <c>aud</c> claim (unverified JWT payload), rendered for the notes.</summary>
    protected static string TokenAudience(string token) =>
        $"token aud {IntrospectionRendering.Shape(MemberShape.Of(JwtPayload.Read(token), "aud"), Realm.Alias)}";

    protected static Task<IntrospectionOutcome> IntrospectWithFormCredentialsAsync(
        ClientCredentials? credentials,
        string? token,
        string? tokenTypeHint = null
    ) => Realm.Api.IntrospectDetailedAsync(FormBody.Revocation(credentials, token, tokenTypeHint));
}

[TestFixture]
[Category(KeycloakCharacterizationEnvironment.Category)]
public class Given_the_introspection_characterization_realm
{
    private MemberShape _audA = MemberShape.Absent;
    private MemberShape _audA2 = MemberShape.Absent;
    private MemberShape _audC = MemberShape.Absent;
    private MemberShape _audCms = MemberShape.Absent;
    private MemberShape _audLegacyCms = MemberShape.Absent;
    private MemberShape _audS = MemberShape.Absent;
    private MemberShape _scopeS = MemberShape.Absent;
    private MemberShape _audR = MemberShape.Absent;
    private MemberShape _audE = MemberShape.Absent;
    private MemberShape _audP = MemberShape.Absent;
    private bool _refreshTokenIssuedToR;

    private static IntrospectionCharacterizationRealm Realm =>
        KeycloakIntrospectionCharacterizationSetup.Realm;

    [OneTimeSetUp]
    public async Task ArrangeAndAct()
    {
        KeycloakCharacterizationEnvironment.RequireKeycloakProvider();

        _audA = await AudienceOfServiceAccountTokenAsync(Realm.A);
        _audA2 = await AudienceOfServiceAccountTokenAsync(Realm.A2);
        _audC = await AudienceOfServiceAccountTokenAsync(Realm.C);
        _audCms = await AudienceOfServiceAccountTokenAsync(Realm.Cms);
        _audLegacyCms = await AudienceOfServiceAccountTokenAsync(Realm.LegacyCms);
        JsonObject scopelessToken = JwtPayload.Read(
            (await Realm.ServiceAccountGrantAsync(Realm.Scopeless)).AccessToken
        );
        _audS = MemberShape.Of(scopelessToken, "aud");
        _scopeS = MemberShape.Of(scopelessToken, "scope");
        _audE = await AudienceOfServiceAccountTokenAsync(Realm.E);
        TokenGrant refresh = await Realm.ServiceAccountGrantAsync(Realm.R);
        _audR = MemberShape.Of(JwtPayload.Read(refresh.AccessToken), "aud");
        _refreshTokenIssuedToR = refresh.RefreshToken is not null;
        _audP = MemberShape.Of(JwtPayload.Read((await Realm.PublicUserGrantAsync()).AccessToken), "aud");

        KeycloakIntrospectionCharacterizationSetup.Evidence.RecordNote(
            "I-00",
            "Preconditions: server, client setup and the `aud` each client's access tokens carry (unverified JWT payload)",
            $"server {Realm.ServerVersion}; audience check predicted {(Realm.AudienceChecked ? "yes" : "no")}; sslRequired={Realm.SslRequired ?? "(absent)"}; "
                + $"aud of A {Render(_audA)}, A2 {Render(_audA2)}, C {Render(_audC)}, CMS {Render(_audCms)}, CMS-legacy {Render(_audLegacyCms)}, S {Render(_audS)}, R {Render(_audR)}, E {Render(_audE)}, P's user {Render(_audP)}; "
                + $"scope of S's tokens {Render(_scopeS)}; "
                + $"CMS default client scopes [{RenderScopes(Realm.CmsDefaultClientScopes)}]; "
                + $"CMS-legacy default client scopes [{RenderScopes(Realm.LegacyCmsDefaultClientScopes)}]; "
                + $"S default client scopes [{RenderScopes(Realm.ScopelessDefaultClientScopes)}]; "
                + $"BO {(Realm.BearerOnlyCreationFailure is null ? $"created, stored bearerOnly={IntrospectionRendering.Stored(Realm.BearerOnlyStored)}" : $"refused: {Realm.BearerOnlyCreationFailure}")}; "
                + $"D enabled after the update: {IntrospectionRendering.Stored(Realm.DisabledClientEnabledAfterUpdate)}; "
                + $"refresh token issued to R for client_credentials: {(_refreshTokenIssuedToR ? "yes" : "no")}"
        );
    }

    [Test]
    public void It_issues_A_tokens_whose_aud_excludes_A() =>
        _audA.Values.Should().NotContain(Realm.A.ClientId);

    [Test]
    public void It_issues_A2_tokens_whose_aud_includes_A2() =>
        _audA2.Values.Should().Contain(Realm.A2.ClientId);

    [Test]
    public void It_issues_A2_tokens_whose_aud_excludes_B_and_BO() =>
        _audA2.Values.Should().NotContain([Realm.B.ClientId, Realm.BearerOnly.ClientId]);

    [Test]
    public void It_issues_C_tokens_whose_aud_includes_B2_but_not_C()
    {
        _audC.Values.Should().Contain(Realm.B2.ClientId);
        _audC.Values.Should().NotContain(Realm.C.ClientId);
    }

    [Test]
    public void It_issues_CMS_tokens_whose_aud_is_exactly_the_CMS_client_and_account() =>
        _audCms.Matches(MemberShape.OfArray(Realm.Cms.ClientId, "account")).Should().BeTrue(Render(_audCms));

    [Test]
    public void It_issues_CMS_legacy_tokens_whose_aud_is_exactly_account() =>
        _audLegacyCms.Matches(MemberShape.OfString("account")).Should().BeTrue(Render(_audLegacyCms));

    [Test]
    public void It_issues_R_and_E_tokens_whose_aud_includes_the_client_itself()
    {
        _audR.Values.Should().Contain(Realm.R.ClientId);
        _audE.Values.Should().Contain(Realm.E.ClientId);
    }

    [Test]
    public void It_issues_a_refresh_token_to_R_for_client_credentials() =>
        _refreshTokenIssuedToR.Should().BeTrue();

    // Naming defaultClientScopes in the creation representation replaces the realm's default scopes, and
    // Keycloak adds its service-account scope because service accounts are enabled.
    [Test]
    public void It_gives_CMS_exactly_its_own_scope_and_the_service_account_scope_as_default_scopes() =>
        Realm.CmsDefaultClientScopes.Should().Equal(Realm.CmsScopeName, "service_account");

    [Test]
    public void It_gives_CMS_legacy_exactly_the_same_default_scopes_as_CMS() =>
        Realm.LegacyCmsDefaultClientScopes.Should().Equal(Realm.CmsScopeName, "service_account");

    [Test]
    public void It_gives_S_only_the_service_account_scope_as_a_default_scope() =>
        Realm.ScopelessDefaultClientScopes.Should().Equal("service_account");

    [Test]
    public void It_issues_S_tokens_whose_scope_is_empty() =>
        _scopeS.Matches(MemberShape.OfString("")).Should().BeTrue(Render(_scopeS));

    [Test]
    public void It_issues_S_tokens_whose_aud_is_exactly_S() =>
        _audS.Matches(MemberShape.OfString(Realm.Scopeless.ClientId)).Should().BeTrue(Render(_audS));

    [Test]
    public void It_stores_BO_as_bearer_only() => Realm.BearerOnlyStored.Should().BeTrue();

    [Test]
    public void It_stores_D_as_disabled() => Realm.DisabledClientEnabledAfterUpdate.Should().BeFalse();

    private static async Task<MemberShape> AudienceOfServiceAccountTokenAsync(
        CharacterizationClient client
    ) => MemberShape.Of(JwtPayload.Read((await Realm.ServiceAccountGrantAsync(client)).AccessToken), "aud");

    private static string Render(MemberShape shape) => IntrospectionRendering.Shape(shape, Realm.Alias);

    private static string RenderScopes(IReadOnlyList<string> scopes) =>
        string.Join(", ", scopes.Select(scope => IntrospectionRendering.Value(scope, Realm.Alias)));
}

[TestFixture]
public class Given_an_owner_outside_aud_introspecting_its_own_token_with_form_credentials
    : IntrospectionCharacterizationRow
{
    protected override string RowId => "I-01";

    protected override string Scenario =>
        "A introspects A's own access token (A not in `aud`), client_secret_post";

    protected override IntrospectionPrediction Prediction =>
        Realm.AudienceChecked
            ? IntrospectionPrediction.Inactive()
            : ExpectedIntrospection.DefaultScopeAccessToken(Realm.A, MemberShape.OfString("account"));

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.A);
        IntrospectionOutcome outcome = await IntrospectWithFormCredentialsAsync(
            Realm.A.Credentials,
            grant.AccessToken
        );
        return new IntrospectionObservation(outcome, ActiveBefore: null, TokenAudience(grant.AccessToken));
    }
}

[TestFixture]
public class Given_an_owner_outside_aud_introspecting_its_own_token_with_basic_credentials
    : IntrospectionCharacterizationRow
{
    protected override string RowId => "I-01b";

    protected override string Scenario =>
        "A introspects A's own access token (A not in `aud`), client_secret_basic";

    protected override IntrospectionPrediction Prediction =>
        Realm.AudienceChecked
            ? IntrospectionPrediction.Inactive()
            : ExpectedIntrospection.DefaultScopeAccessToken(Realm.A, MemberShape.OfString("account"));

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.A);
        IntrospectionOutcome outcome = await Realm.Api.IntrospectDetailedAsync(
            FormBody.Revocation(credentials: null, grant.AccessToken),
            Realm.A.Credentials.ToBasicHeader()
        );
        return new IntrospectionObservation(outcome, ActiveBefore: null, TokenAudience(grant.AccessToken));
    }
}

[TestFixture]
public class Given_an_owner_in_aud_introspecting_its_own_token : IntrospectionCharacterizationRow
{
    protected override string RowId => "I-02";

    protected override string Scenario => "A2 introspects A2's own access token (A2 in `aud`)";

    protected override IntrospectionPrediction Prediction => ExpectedIntrospection.A2AccessToken();

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.A2);
        IntrospectionOutcome outcome = await IntrospectWithFormCredentialsAsync(
            Realm.A2.Credentials,
            grant.AccessToken
        );
        return new IntrospectionObservation(outcome, ActiveBefore: null, TokenAudience(grant.AccessToken));
    }
}

[TestFixture]
public class Given_another_client_outside_aud_introspecting_a_token : IntrospectionCharacterizationRow
{
    protected override string RowId => "I-03";

    protected override string Scenario => "B introspects A2's access token (B not in `aud`)";

    protected override IntrospectionPrediction Prediction =>
        Realm.AudienceChecked ? IntrospectionPrediction.Inactive() : ExpectedIntrospection.A2AccessToken();

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.A2);
        bool before = await Realm.ProveLiveAsync(Realm.A2, grant.AccessToken, tokenTypeHint: null);
        IntrospectionOutcome outcome = await IntrospectWithFormCredentialsAsync(
            Realm.B.Credentials,
            grant.AccessToken
        );
        return new IntrospectionObservation(
            outcome,
            before,
            $"{TokenAudience(grant.AccessToken)}; Keycloak checks no ownership, so an active answer discloses A2's token to B"
        );
    }
}

[TestFixture]
public class Given_another_client_in_aud_introspecting_a_token : IntrospectionCharacterizationRow
{
    protected override string RowId => "I-04";

    protected override string Scenario => "B2 introspects C's access token (B2 in `aud`, C not)";

    protected override IntrospectionPrediction Prediction =>
        ExpectedIntrospection.DefaultScopeAccessToken(
            Realm.C,
            MemberShape.OfArray(Realm.B2.ClientId, "account")
        );

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.C);
        IntrospectionOutcome outcome = await IntrospectWithFormCredentialsAsync(
            Realm.B2.Credentials,
            grant.AccessToken
        );
        return new IntrospectionObservation(
            outcome,
            ActiveBefore: null,
            $"{TokenAudience(grant.AccessToken)}; an active answer discloses C's token to B2 on every version, so a delegating Configuration Service must filter ownership itself"
        );
    }
}

[TestFixture]
public class Given_a_legacy_configuration_service_shaped_client_introspecting_its_own_token
    : IntrospectionCharacterizationRow
{
    protected override string RowId => "I-05";

    protected override string Scenario =>
        "CMS-legacy, a client created before the Configuration Service added its own audience mapper (only default scope carries `oidc-audience-resolve-mapper`), introspects its own access token";

    protected override IntrospectionPrediction Prediction =>
        Realm.AudienceChecked
            ? IntrospectionPrediction.Inactive()
            : ExpectedIntrospection.LegacyCmsAccessToken();

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.LegacyCms);
        IntrospectionOutcome outcome = await IntrospectWithFormCredentialsAsync(
            Realm.LegacyCms.Credentials,
            grant.AccessToken
        );
        return new IntrospectionObservation(
            outcome,
            ActiveBefore: null,
            $"{TokenAudience(grant.AccessToken)}; Ed-Fi claim mappers included with placeholder values, realm-role mapper claim name unescaped as the CMS compose files configure it; realm-role assignment not mirrored (service account holds the realm's default roles only)"
        );
    }
}

/// <summary>A client-authentication failure presented with A2's live token, which A2 proves live first.</summary>
public abstract class Given_a_client_authentication_failure_with_a_live_token
    : IntrospectionCharacterizationRow
{
    protected abstract string CredentialNote { get; }

    protected abstract Task<IntrospectionOutcome> IntrospectAsync(string liveToken);

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.A2);
        bool before = await Realm.ProveLiveAsync(Realm.A2, grant.AccessToken, tokenTypeHint: null);
        IntrospectionOutcome outcome = await IntrospectAsync(grant.AccessToken);
        return new IntrospectionObservation(outcome, before, CredentialNote);
    }
}

[TestFixture]
public class Given_a_wrong_secret_for_the_token_owner
    : Given_a_client_authentication_failure_with_a_live_token
{
    protected override string RowId => "I-06";

    protected override string Scenario => "A2's client_id with a wrong secret, A2's own live token";

    // Observed: 26.1.4 answers every client-authentication failure alike (ExpectedIntrospection.
    // AuthenticationFailed); 26.7.5 answers a wrong secret on a known client with a description of its
    // own, unlike its unknown-client answer (I-07) and unlike revocation's unauthorized_client (K-05).
    protected override IntrospectionPrediction Prediction =>
        Realm.On267ReleaseLine
            ? IntrospectionPrediction.ErrorBody(401, "invalid_client", "Client authentication failed.")
            : ExpectedIntrospection.AuthenticationFailed();

    protected override string CredentialNote => "client_secret_post with a secret no client holds";

    protected override Task<IntrospectionOutcome> IntrospectAsync(string liveToken) =>
        IntrospectWithFormCredentialsAsync(
            new ClientCredentials(Realm.A2.ClientId, Realm.UnheldSecret()),
            liveToken
        );
}

[TestFixture]
public class Given_an_unknown_client_id_at_introspection
    : Given_a_client_authentication_failure_with_a_live_token
{
    protected override string RowId => "I-07";

    protected override string Scenario => "Unknown client_id with some secret, A2's live token";

    protected override IntrospectionPrediction Prediction => ExpectedIntrospection.UnauthenticatedClient();

    protected override string CredentialNote =>
        "client_secret_post; the client_id names no client in the realm";

    protected override Task<IntrospectionOutcome> IntrospectAsync(string liveToken) =>
        IntrospectWithFormCredentialsAsync(
            new ClientCredentials($"cs-char-introspect-unknown-{Realm.RunId}", Realm.UnheldSecret()),
            liveToken
        );
}

[TestFixture]
public class Given_no_client_credentials_at_introspection
    : Given_a_client_authentication_failure_with_a_live_token
{
    protected override string RowId => "I-08";

    protected override string Scenario => "No credentials at all, A2's live token";

    protected override IntrospectionPrediction Prediction => ExpectedIntrospection.UnauthenticatedClient();

    protected override string CredentialNote => "no Authorization header, no client_id, no client_secret";

    protected override Task<IntrospectionOutcome> IntrospectAsync(string liveToken) =>
        IntrospectWithFormCredentialsAsync(credentials: null, liveToken);
}

[TestFixture]
public class Given_a_client_id_that_differs_from_the_owner_only_by_case
    : Given_a_client_authentication_failure_with_a_live_token
{
    protected override string RowId => "I-09";

    protected override string Scenario => "A2's secret under an upper-cased A2 client_id, A2's live token";

    protected override IntrospectionPrediction Prediction => ExpectedIntrospection.UnauthenticatedClient();

    protected override string CredentialNote => "client_secret_post; client_id upper-cased";

    protected override Task<IntrospectionOutcome> IntrospectAsync(string liveToken) =>
        IntrospectWithFormCredentialsAsync(
            new ClientCredentials(Realm.A2.ClientId.ToUpperInvariant(), Realm.A2.Secret),
            liveToken
        );
}

[TestFixture]
public class Given_a_public_client_with_an_arbitrary_secret : IntrospectionCharacterizationRow
{
    protected override string RowId => "I-10";

    protected override string Scenario =>
        "Public client P with an arbitrary client_secret presents its own user access token";

    protected override IntrospectionPrediction Prediction =>
        IntrospectionPrediction.ErrorBody(403, "invalid_request", "Client not allowed.");

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.PublicUserGrantAsync();
        IntrospectionOutcome outcome = await IntrospectWithFormCredentialsAsync(
            new ClientCredentials(Realm.P.ClientId, Realm.UnheldSecret()),
            grant.AccessToken
        );
        return new IntrospectionObservation(
            outcome,
            ActiveBefore: null,
            $"{TokenAudience(grant.AccessToken)}; no per-run confidential client is in this token's audience, so no other call proves it live first"
        );
    }
}

[TestFixture]
public class Given_a_bearer_only_client : IntrospectionCharacterizationRow
{
    protected override string RowId => "I-11";

    protected override string Scenario =>
        "Bearer-only BO with its secret introspects A2's access token (BO not in `aud`)";

    // Observed: 26.1.4 does not authenticate a bearer-only client at introspection; 26.7.5 does, and
    // then answers inactive because BO is outside the token's audience.
    protected override IntrospectionPrediction Prediction =>
        Realm.On267ReleaseLine
            ? IntrospectionPrediction.Inactive()
            : ExpectedIntrospection.AuthenticationFailed();

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        if (Realm.BearerOnlyCreationFailure is not null)
        {
            throw new InvalidOperationException(
                $"Keycloak refused to create the bearer-only client: {Realm.BearerOnlyCreationFailure}"
            );
        }

        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.A2);
        bool before = await Realm.ProveLiveAsync(Realm.A2, grant.AccessToken, tokenTypeHint: null);
        IntrospectionOutcome outcome = await IntrospectWithFormCredentialsAsync(
            Realm.BearerOnly.Credentials,
            grant.AccessToken
        );
        return new IntrospectionObservation(
            outcome,
            before,
            $"client_secret_post; BO stored bearerOnly={IntrospectionRendering.Stored(Realm.BearerOnlyStored)}, no service account; a 200 answer means BO authenticated"
        );
    }
}

[TestFixture]
public class Given_a_disabled_client_with_its_correct_secret
    : Given_a_client_authentication_failure_with_a_live_token
{
    protected override string RowId => "I-12";

    protected override string Scenario => "Disabled client D with its correct secret, A2's live token";

    protected override IntrospectionPrediction Prediction => ExpectedIntrospection.UnauthenticatedClient();

    protected override string CredentialNote =>
        $"client_secret_post; D enabled after the update: {IntrospectionRendering.Stored(Realm.DisabledClientEnabledAfterUpdate)}";

    protected override Task<IntrospectionOutcome> IntrospectAsync(string liveToken) =>
        IntrospectWithFormCredentialsAsync(Realm.Disabled.Credentials, liveToken);
}

[TestFixture]
public class Given_a_garbage_token : IntrospectionCharacterizationRow
{
    protected override string RowId => "I-13";

    protected override string Scenario => "A2 introspects the string \"not-a-jwt\"";

    protected override IntrospectionPrediction Prediction => IntrospectionPrediction.Inactive();

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        IntrospectionOutcome outcome = await IntrospectWithFormCredentialsAsync(
            Realm.A2.Credentials,
            "not-a-jwt"
        );
        return new IntrospectionObservation(outcome, ActiveBefore: null, "no token issued");
    }
}

[TestFixture]
public class Given_an_owner_introspecting_its_revoked_token : IntrospectionCharacterizationRow
{
    private HttpOutcome? _revocation;

    protected override string RowId => "I-14";

    protected override string Scenario => "A2 revokes its own access token, then introspects it";

    protected override IntrospectionPrediction Prediction => IntrospectionPrediction.Inactive();

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.A2);
        bool before = await Realm.ProveLiveAsync(Realm.A2, grant.AccessToken, tokenTypeHint: null);
        _revocation = await Realm.Api.RevokeAsync(
            FormBody.Revocation(Realm.A2.Credentials, grant.AccessToken)
        );
        IntrospectionOutcome outcome = await IntrospectWithFormCredentialsAsync(
            Realm.A2.Credentials,
            grant.AccessToken
        );
        return new IntrospectionObservation(
            outcome,
            before,
            $"revocation answered HTTP {_revocation.Status} with {_revocation.BodyLength} body chars"
        );
    }

    [Test]
    public void It_had_revoked_the_token_with_an_empty_200()
    {
        _revocation.Should().NotBeNull();
        _revocation!.Status.Should().Be(200);
        _revocation.BodyLength.Should().Be(0);
    }
}

[TestFixture]
public class Given_an_owner_introspecting_its_expired_token : IntrospectionCharacterizationRow
{
    protected override string RowId => "I-15";

    protected override string Scenario =>
        "E introspects its own naturally expired access token (E in `aud`, 10 s lifespan, polled until inactive)";

    protected override IntrospectionPrediction Prediction => IntrospectionPrediction.Inactive();

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.E);
        bool before = await Realm.ProveLiveAsync(Realm.E, grant.AccessToken, tokenTypeHint: null);
        JsonObject payload = JwtPayload.Read(grant.AccessToken);
        long exp = JwtPayload.GetNumber(payload, "exp");
        long iat = JwtPayload.GetNumber(payload, "iat");
        DateTimeOffset deadline = DateTimeOffset.FromUnixTimeSeconds(exp).AddSeconds(60);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        int polls = 0;

        while (true)
        {
            polls++;
            IntrospectionOutcome outcome = await IntrospectWithFormCredentialsAsync(
                Realm.E.Credentials,
                grant.AccessToken
            );
            if (outcome.Active is not true)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                return new IntrospectionObservation(
                    outcome,
                    before,
                    $"lifespan exp-iat={exp - iat}s (configured {IntrospectionCharacterizationRealm.ExpiryClientAccessTokenLifespanSeconds}s); "
                        + $"first non-active answer after {polls} polls, {(now - started).TotalSeconds:F0}s after issue, "
                        + $"{(now - DateTimeOffset.FromUnixTimeSeconds(exp)).TotalSeconds:F0}s after exp by the test host clock"
                );
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new InvalidOperationException(
                    $"E still saw its token active at exp+60s (exp-iat={exp - iat}s, {polls} polls)."
                );
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }
}

[TestFixture]
public class Given_an_owner_introspecting_its_refresh_token_with_the_refresh_hint
    : IntrospectionCharacterizationRow
{
    protected override string RowId => "I-16";

    protected override string Scenario =>
        "R introspects its own refresh token with token_type_hint=refresh_token";

    protected override IntrospectionPrediction Prediction => ExpectedIntrospection.OwnRefreshToken();

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        string refreshToken = await Realm.RefreshTokenGrantedToRAsync();
        IntrospectionOutcome outcome = await IntrospectWithFormCredentialsAsync(
            Realm.R.Credentials,
            refreshToken,
            "refresh_token"
        );
        return new IntrospectionObservation(outcome, ActiveBefore: null, TokenAudience(refreshToken));
    }
}

[TestFixture]
public class Given_an_owner_introspecting_its_refresh_token_without_a_hint : IntrospectionCharacterizationRow
{
    protected override string RowId => "I-16b";

    protected override string Scenario => "R introspects its own refresh token with no token_type_hint";

    // Observed: 26.1.4 answers exactly as with the refresh_token hint (I-16); 26.7.5 runs the
    // access-token checks without the hint and answers inactive.
    protected override IntrospectionPrediction Prediction =>
        Realm.On267ReleaseLine ? IntrospectionPrediction.Inactive() : ExpectedIntrospection.OwnRefreshToken();

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        string refreshToken = await Realm.RefreshTokenGrantedToRAsync();
        bool before = await Realm.ProveLiveAsync(Realm.R, refreshToken, "refresh_token");
        IntrospectionOutcome outcome = await IntrospectWithFormCredentialsAsync(
            Realm.R.Credentials,
            refreshToken
        );
        return new IntrospectionObservation(
            outcome,
            before,
            $"{TokenAudience(refreshToken)}; proven live by R with token_type_hint=refresh_token"
        );
    }
}

public abstract class Given_an_owner_introspecting_its_access_token_with_a_hint(string hint, string rowId)
    : IntrospectionCharacterizationRow
{
    protected string Hint => hint;

    protected override string RowId => rowId;

    protected override string Scenario => $"A2 introspects its own access token with token_type_hint={hint}";

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.A2);
        bool before = await Realm.ProveLiveAsync(Realm.A2, grant.AccessToken, tokenTypeHint: null);
        IntrospectionOutcome outcome = await IntrospectWithFormCredentialsAsync(
            Realm.A2.Credentials,
            grant.AccessToken,
            hint
        );
        return new IntrospectionObservation(outcome, before, "proven live by A2 with no hint");
    }
}

[TestFixture]
public class Given_an_access_token_with_the_refresh_token_hint()
    : Given_an_owner_introspecting_its_access_token_with_a_hint("refresh_token", "I-17")
{
    // Observed: 26.1.4 answers the access token active despite the refresh_token hint; 26.7.5's
    // refresh-token checks reject it.
    protected override IntrospectionPrediction Prediction =>
        Realm.On267ReleaseLine ? IntrospectionPrediction.Inactive() : ExpectedIntrospection.A2AccessToken();
}

[TestFixture]
public class Given_an_access_token_with_the_access_token_hint()
    : Given_an_owner_introspecting_its_access_token_with_a_hint("access_token", "I-17b")
{
    protected override IntrospectionPrediction Prediction => ExpectedIntrospection.A2AccessToken();
}

[TestFixture]
public class Given_an_access_token_with_an_unrecognized_hint()
    : Given_an_owner_introspecting_its_access_token_with_a_hint("bogus", "I-18")
{
    // Observed: 26.1.4 names the hint in the description; 26.7.5 does not.
    protected override IntrospectionPrediction Prediction =>
        IntrospectionPrediction.ErrorBody(
            400,
            "invalid_request",
            Realm.On267ReleaseLine ? "Unsupported token type." : $"Unsupported token type [{Hint}]."
        );
}

[TestFixture]
public class Given_a_master_realm_token_at_introspection : IntrospectionCharacterizationRow
{
    protected override string RowId => "I-19";

    protected override string Scenario =>
        "A2 presents a token issued by the master realm (admin-cli password grant for the admin user)";

    protected override IntrospectionPrediction Prediction => IntrospectionPrediction.Inactive();

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        string foreignToken = await Realm.GetAdminTokenAsync();
        IntrospectionOutcome outcome = await IntrospectWithFormCredentialsAsync(
            Realm.A2.Credentials,
            foreignToken
        );
        return new IntrospectionObservation(
            outcome,
            ActiveBefore: null,
            "a master-realm token has no observable state in the edfi realm"
        );
    }
}

[TestFixture]
public class Given_a_duplicated_token_parameter_at_introspection : IntrospectionCharacterizationRow
{
    protected override string RowId => "I-20";

    protected override string Scenario =>
        "Duplicated `token` parameter (A2's live token twice), A2 credentials";

    protected override IntrospectionPrediction Prediction =>
        IntrospectionPrediction.ErrorBody(400, "invalid_request", "duplicated parameter");

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.A2);
        bool before = await Realm.ProveLiveAsync(Realm.A2, grant.AccessToken, tokenTypeHint: null);
        IntrospectionOutcome outcome = await Realm.Api.IntrospectDetailedAsync(
            FormBody.Revocation(Realm.A2.Credentials, grant.AccessToken).Add("token", grant.AccessToken)
        );
        return new IntrospectionObservation(outcome, before, "client_secret_post");
    }
}

[TestFixture]
public class Given_a_missing_token_parameter_at_introspection : IntrospectionCharacterizationRow
{
    protected override string RowId => "I-21";

    protected override string Scenario => "A2 credentials, no `token` parameter";

    protected override IntrospectionPrediction Prediction =>
        IntrospectionPrediction.ErrorBody(400, "invalid_request", "Token not provided.");

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        IntrospectionOutcome outcome = await IntrospectWithFormCredentialsAsync(
            Realm.A2.Credentials,
            token: null
        );
        return new IntrospectionObservation(outcome, ActiveBefore: null, "no token involved");
    }
}

[TestFixture]
public class Given_a_configuration_service_shaped_client_introspecting_its_own_token
    : IntrospectionCharacterizationRow
{
    protected override string RowId => "I-22";

    protected override string Scenario =>
        "CMS, shaped as the Configuration Service creates clients now (its own audience mapper puts it in `aud`), introspects its own access token";

    protected override IntrospectionPrediction Prediction => ExpectedIntrospection.CmsAccessToken();

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.Cms);
        IntrospectionOutcome outcome = await IntrospectWithFormCredentialsAsync(
            Realm.Cms.Credentials,
            grant.AccessToken
        );
        return new IntrospectionObservation(
            outcome,
            ActiveBefore: null,
            $"{TokenAudience(grant.AccessToken)}; the own audience mapper is the only difference from CMS-legacy (I-05)"
        );
    }
}

[TestFixture]
public class Given_another_client_outside_aud_introspecting_a_configuration_service_shaped_token
    : IntrospectionCharacterizationRow
{
    protected override string RowId => "I-23";

    protected override string Scenario => "B introspects CMS's access token (CMS in `aud`, B not)";

    protected override IntrospectionPrediction Prediction =>
        Realm.AudienceChecked ? IntrospectionPrediction.Inactive() : ExpectedIntrospection.CmsAccessToken();

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.Cms);
        bool before = await Realm.ProveLiveAsync(Realm.Cms, grant.AccessToken, tokenTypeHint: null);
        IntrospectionOutcome outcome = await IntrospectWithFormCredentialsAsync(
            Realm.B.Credentials,
            grant.AccessToken
        );
        return new IntrospectionObservation(
            outcome,
            before,
            $"{TokenAudience(grant.AccessToken)}; the own audience mapper adds CMS alone to `aud`, so from 26.6.2 B still learns nothing; before that only the Configuration Service's azp check keeps the token from B"
        );
    }
}

/// <summary>
/// A token of <paramref name="length"/> characters presented with the row's <see cref="Credentials"/>.
/// The token is one letter repeated, which form encoding leaves unchanged, so Keycloak receives a form
/// value of exactly that length.
/// </summary>
public abstract class Given_a_token_of_a_length_at_introspection(string rowId, int length)
    : IntrospectionCharacterizationRow
{
    protected override string RowId => rowId;

    protected int Length => length;

    protected abstract ClientCredentials Credentials { get; }

    protected abstract string CredentialNote { get; }

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        IntrospectionOutcome outcome = await IntrospectWithFormCredentialsAsync(
            Credentials,
            new string('A', length)
        );
        return new IntrospectionObservation(
            outcome,
            ActiveBefore: null,
            $"no token issued; {CredentialNote}; Keycloak's default form attribute limit is {ExpectedIntrospection.FormAttributeLimit} characters"
        );
    }
}

[TestFixture]
public class Given_a_token_at_the_form_attribute_limit()
    : Given_a_token_of_a_length_at_introspection("I-24", ExpectedIntrospection.FormAttributeLimit)
{
    protected override string Scenario =>
        $"A2 introspects a garbage token of {Length} characters (Keycloak's form attribute limit)";

    protected override IntrospectionPrediction Prediction => IntrospectionPrediction.Inactive();

    protected override ClientCredentials Credentials => Realm.A2.Credentials;

    protected override string CredentialNote => "client_secret_post";
}

[TestFixture]
public class Given_a_token_over_the_form_attribute_limit()
    : Given_a_token_of_a_length_at_introspection("I-25", ExpectedIntrospection.FormAttributeLimit + 1)
{
    protected override string Scenario =>
        $"A2 introspects a garbage token of {Length} characters (one over Keycloak's form attribute limit)";

    protected override IntrospectionPrediction Prediction => IntrospectionPrediction.FormAttributeTooLarge();

    protected override ClientCredentials Credentials => Realm.A2.Credentials;

    protected override string CredentialNote => "client_secret_post";
}

[TestFixture]
public class Given_a_wrong_secret_with_a_token_over_the_form_attribute_limit()
    : Given_a_token_of_a_length_at_introspection("I-26", ExpectedIntrospection.FormAttributeLimit + 1)
{
    protected override string Scenario =>
        $"A2's client_id with a wrong secret, a garbage token of {Length} characters";

    // The size check runs before client authentication: the same wrong secret with a token within the
    // limit is I-06's 401.
    protected override IntrospectionPrediction Prediction => IntrospectionPrediction.FormAttributeTooLarge();

    protected override ClientCredentials Credentials => new(Realm.A2.ClientId, Realm.UnheldSecret());

    protected override string CredentialNote => "client_secret_post with a secret no client holds";
}

[TestFixture]
public class Given_a_client_whose_tokens_carry_no_scope_introspecting_its_own_token
    : IntrospectionCharacterizationRow
{
    protected override string RowId => "I-27";

    protected override string Scenario =>
        "S, whose tokens carry an empty `scope` and whose own audience mapper puts it in `aud`, introspects its own access token";

    protected override IntrospectionPrediction Prediction => ExpectedIntrospection.ScopelessAccessToken();

    protected override async Task<IntrospectionObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.Scopeless);
        IntrospectionOutcome outcome = await IntrospectWithFormCredentialsAsync(
            Realm.Scopeless.Credentials,
            grant.AccessToken
        );
        return new IntrospectionObservation(
            outcome,
            ActiveBefore: null,
            $"{TokenAudience(grant.AccessToken)}; token scope {IntrospectionRendering.Shape(MemberShape.Of(JwtPayload.Read(grant.AccessToken), "scope"), Realm.Alias)}; RFC 7662 §2.2 makes `scope` optional"
        );
    }
}
