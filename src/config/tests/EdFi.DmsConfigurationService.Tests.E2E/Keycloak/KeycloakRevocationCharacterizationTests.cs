// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.DmsConfigurationService.Tests.E2E.Keycloak;

// DMS-1327 P1.1: characterization of the pinned Keycloak image's token revocation endpoint.
//
// These fixtures record what Keycloak actually answers (status, body shape, `error`,
// `error_description`) and the token's introspection state before and after each call: access
// tokens through a dedicated confidential observer client, refresh tokens through their owning
// client (D-16 as corrected after P1.1). Each row asserts exact values; a deviation fails the
// fixture so it is recorded in §9.1 and reviewed before any normalization (D-10, D-11) is coded.
// The only skipped checks are ones that do not exist for a row (for example the before-state of a
// request that carried no live token). Nothing here touches the Configuration Service.

/// <summary>Creates the run's realm resources once and removes them (and writes the evidence table) at the end.</summary>
[SetUpFixture]
public class KeycloakCharacterizationRealmSetup
{
    private static CharacterizationRealm? _realm;
    private static EvidenceLog? _evidence;

    public static CharacterizationRealm Realm =>
        _realm
        ?? throw new InvalidOperationException("The characterization realm resources were not created.");

    public static EvidenceLog Evidence =>
        _evidence
        ?? throw new InvalidOperationException("The characterization evidence log was not created.");

    [OneTimeSetUp]
    public async Task CreateRealmResources()
    {
        if (KeycloakCharacterizationEnvironment.IsKeycloakProvider)
        {
            SensitiveValueRegistry sensitive = new();
            _evidence = new EvidenceLog(sensitive);
            _realm = await CharacterizationRealm.CreateAsync(sensitive);
        }
    }

    [OneTimeTearDown]
    public async Task WriteEvidenceAndRemoveRealmResources()
    {
        if (_realm is null)
        {
            return;
        }

        CharacterizationRealm realm = _realm;
        _realm = null;
        Exception? evidenceFailure = null;
        try
        {
            string evidencePath = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                $"keycloak-characterization-{realm.ServerVersion}.md"
            );
            await File.WriteAllTextAsync(
                evidencePath,
                Evidence.RenderMarkdown(realm.ServerVersion, realm.SslRequired, realm.RunId)
            );
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
/// One evidence row: the subclass arranges tokens, performs exactly one characterized call and
/// reports what it observed; the shared tests compare that against the row's prediction.
/// </summary>
[Category(KeycloakCharacterizationEnvironment.Category)]
public abstract class RevocationCharacterizationRow
{
    private RowObservation? _observation;

    protected static CharacterizationRealm Realm => KeycloakCharacterizationRealmSetup.Realm;

    protected RowObservation Observation =>
        _observation ?? throw new InvalidOperationException("The row has not been observed.");

    protected abstract string RowId { get; }

    protected abstract string Scenario { get; }

    protected abstract RowPrediction Prediction { get; }

    [OneTimeSetUp]
    public async Task ArrangeAndAct()
    {
        KeycloakCharacterizationEnvironment.RequireKeycloakProvider();
        _observation = await ObserveAsync();
        KeycloakCharacterizationRealmSetup.Evidence.Record(
            new EvidenceRow(RowId, Scenario, Prediction, _observation)
        );
    }

    protected abstract Task<RowObservation> ObserveAsync();

    [Test]
    public void It_started_from_a_token_the_observer_reported_active()
    {
        if (Observation.ActiveBefore is null)
        {
            Assert.Ignore("Not applicable: this row does not start from a live token.");
        }

        Observation.ActiveBefore.Should().BeTrue("the observer must prove the token live before the call");
    }

    [Test]
    public void It_answers_the_predicted_http_status() =>
        Observation.Outcome.Status.Should().Be(Prediction.Status.Value);

    [Test]
    public void It_answers_with_the_predicted_body_shape()
    {
        HttpOutcome outcome = Observation.Outcome;
        switch (Prediction.Body.Value)
        {
            case BodyShape.Empty:
                outcome.BodyLength.Should().Be(0, "a successful revocation answers with no body at all");
                break;
            case BodyShape.OAuthErrorJson:
                outcome.Body.Should().Be(BodyKind.JsonObject, "an OAuth error is a JSON object");
                outcome.ContentType.Should().Be("application/json");
                break;
            default:
                throw new InvalidOperationException("Every row predicts a body shape.");
        }
    }

    [Test]
    public void It_answers_the_predicted_error() =>
        Observation.Outcome.Error.Should().Be(Prediction.Error.Value);

    [Test]
    public void It_answers_the_predicted_error_description() =>
        Observation.Outcome.ErrorDescription.Should().Be(Prediction.ErrorDescription.Value);

    [Test]
    public void It_leaves_the_token_in_the_predicted_state_afterwards()
    {
        if (!Prediction.ActiveAfter.IsApplicable)
        {
            Assert.Ignore("Not applicable: this row has no token whose state could change.");
        }

        Observation.ActiveAfter.Should().Be(Prediction.ActiveAfter.Value);
    }

    /// <summary>The observer proves an access token live; anything else is a failed prerequisite, not "inactive".</summary>
    protected static async Task<bool> ObserveLiveAsync(string token)
    {
        bool active = await Realm.ObserveAsync(token);
        if (!active)
        {
            throw new InvalidOperationException(
                "The observer reported a freshly issued access token inactive; the characterization prerequisite failed."
            );
        }

        return active;
    }

    /// <summary>Same prerequisite rule for a refresh token, observed by its owning client with the refresh-token hint.</summary>
    protected static async Task<bool> ObserveLiveRefreshTokenAsync(
        CharacterizationClient owner,
        string refreshToken
    )
    {
        bool active = await Realm.ObserveRefreshTokenAsOwnerAsync(owner, refreshToken);
        if (!active)
        {
            throw new InvalidOperationException(
                "The owning client reported its freshly issued refresh token inactive; the characterization prerequisite failed."
            );
        }

        return active;
    }

    protected static string Render(bool? state) =>
        state switch
        {
            true => "active",
            false => "inactive",
            null => "not observed",
        };
}

[TestFixture]
[Category(KeycloakCharacterizationEnvironment.Category)]
public class Given_the_characterization_realm
{
    private string? _azp;
    private string? _subjectClientId;
    private bool _observerSeesSubjectToken;
    private bool _observerSeesPublicUserToken;
    private bool _refreshSubjectSeesOwnRefreshToken;
    private int _publicUserRefreshGrantStatus;

    [OneTimeSetUp]
    public async Task ArrangeAndAct()
    {
        KeycloakCharacterizationEnvironment.RequireKeycloakProvider();

        TokenGrant subject = await Realm.ServiceAccountGrantAsync(Realm.SubjectA);
        _subjectClientId = Realm.SubjectA.ClientId;
        _azp = JwtPayload.Read(subject.AccessToken)["azp"]?.GetValue<string>();
        _observerSeesSubjectToken = await Realm.ObserveAsync(subject.AccessToken);

        TokenGrant publicUser = await Realm.PublicUserGrantAsync();
        _observerSeesPublicUserToken = await Realm.ObserveAsync(publicUser.AccessToken);
        _publicUserRefreshGrantStatus = publicUser.RefreshToken is null
            ? 0
            : (await Realm.PublicUserRefreshGrantProbeAsync(publicUser.RefreshToken)).Status;

        TokenGrant refresh = await Realm.ServiceAccountGrantAsync(Realm.RefreshSubject);
        _refreshSubjectSeesOwnRefreshToken =
            refresh.RefreshToken is not null
            && await Realm.ObserveRefreshTokenAsOwnerAsync(Realm.RefreshSubject, refresh.RefreshToken);

        KeycloakCharacterizationRealmSetup.Evidence.RecordNote(
            "K-00",
            "Preconditions (A-02, A-04, A-06 as corrected)",
            $"server {Realm.ServerVersion}; sslRequired={Realm.SslRequired ?? "(absent)"}; "
                + $"azp={(string.Equals(_azp, _subjectClientId, StringComparison.Ordinal) ? "client id" : _azp ?? "(absent)")}; "
                + $"observer (in aud via the per-run scope) sees subject access token {Render(_observerSeesSubjectToken)} and public user access token {Render(_observerSeesPublicUserToken)}; "
                + $"refresh subject sees its own refresh token (hint refresh_token) {Render(_refreshSubjectSeesOwnRefreshToken)}; "
                + $"public user refresh grant probe HTTP {_publicUserRefreshGrantStatus}; "
                + $"refresh token issued for client_credentials: {(refresh.RefreshToken is not null ? "yes" : "no")}"
        );
    }

    private static CharacterizationRealm Realm => KeycloakCharacterizationRealmSetup.Realm;

    [Test]
    public void It_runs_against_a_realm_whose_ssl_requirement_admits_the_in_network_http_url() =>
        Realm
            .SslRequired.Should()
            .NotBe(
                "all",
                "every delegated request from http://dms-keycloak:8080 would otherwise be 403 (A-02)"
            );

    [Test]
    public void It_issues_service_account_tokens_whose_azp_is_the_client_id() =>
        _azp.Should()
            .Be(
                _subjectClientId,
                "Keycloak's own ownership check compares azp with the authenticated client (A-04)"
            );

    [Test]
    public void It_lets_the_observer_see_a_fresh_subject_access_token_as_active() =>
        _observerSeesSubjectToken.Should().BeTrue();

    [Test]
    public void It_lets_the_observer_see_a_public_client_user_access_token_as_active() =>
        _observerSeesPublicUserToken
            .Should()
            .BeTrue("a public client's access token must be observable by the confidential observer");

    [Test]
    public void It_lets_the_public_client_refresh_its_fresh_user_session() =>
        _publicUserRefreshGrantStatus
            .Should()
            .Be(200, "a public client cannot introspect, so the refresh grant is its liveness probe");

    [Test]
    public void It_lets_the_refresh_subject_see_its_own_fresh_refresh_token_as_active() =>
        _refreshSubjectSeesOwnRefreshToken
            .Should()
            .BeTrue("the refresh subject enables refresh tokens for client_credentials");

    private static string Render(bool state) => state ? "active" : "inactive";
}

[TestFixture]
public class Given_an_owner_revoking_its_own_access_token : RevocationCharacterizationRow
{
    protected override string RowId => "K-01";

    protected override string Scenario => "Owner revokes its own access token (form credentials) †";

    protected override RowPrediction Prediction => RowPrediction.EmptySuccess(Predicted<bool>.Of(false));

    protected override async Task<RowObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.SubjectA);
        bool before = await ObserveLiveAsync(grant.AccessToken);
        HttpOutcome outcome = await Realm.Api.RevokeAsync(
            FormBody.Revocation(Realm.SubjectA.Credentials, grant.AccessToken)
        );
        bool after = await Realm.ObserveAsync(grant.AccessToken);
        return new RowObservation(outcome, before, after, "client_secret_post");
    }
}

[TestFixture]
public class Given_an_owner_revoking_its_own_access_token_with_basic_authentication
    : RevocationCharacterizationRow
{
    protected override string RowId => "K-01b";

    protected override string Scenario => "Owner revokes its own access token (HTTP Basic) †";

    protected override RowPrediction Prediction => RowPrediction.EmptySuccess(Predicted<bool>.Of(false));

    protected override async Task<RowObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.SubjectA);
        bool before = await ObserveLiveAsync(grant.AccessToken);
        HttpOutcome outcome = await Realm.Api.RevokeAsync(
            FormBody.Revocation(credentials: null, grant.AccessToken),
            Realm.SubjectA.Credentials.ToBasicHeader()
        );
        bool after = await Realm.ObserveAsync(grant.AccessToken);
        return new RowObservation(outcome, before, after, "client_secret_basic");
    }
}

[TestFixture]
public class Given_a_client_revoking_another_clients_access_token : RevocationCharacterizationRow
{
    protected override string RowId => "K-02";

    protected override string Scenario => "Cross-client: B presents A's access token †";

    protected override RowPrediction Prediction =>
        RowPrediction.ErrorBody(400, "invalid_request", "Unmatching clients", Predicted<bool>.Of(true));

    protected override async Task<RowObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.SubjectA);
        bool before = await ObserveLiveAsync(grant.AccessToken);
        HttpOutcome outcome = await Realm.Api.RevokeAsync(
            FormBody.Revocation(Realm.SubjectB.Credentials, grant.AccessToken)
        );
        bool after = await Realm.ObserveAsync(grant.AccessToken);
        return new RowObservation(outcome, before, after, "B authenticated with its own correct secret");
    }
}

[TestFixture]
public class Given_a_caller_whose_client_id_differs_from_the_owner_only_by_case
    : RevocationCharacterizationRow
{
    protected override string RowId => "K-03";

    protected override string Scenario =>
        "Owner's token presented with the owner's secret under a case-variant client_id †";

    // Observed in P1.1: Keycloak client ids are case-sensitive, so this is an unknown client.
    protected override RowPrediction Prediction =>
        RowPrediction.ErrorBody(
            401,
            "invalid_client",
            "Invalid client or Invalid client credentials",
            Predicted<bool>.Of(true)
        );

    protected override async Task<RowObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.SubjectA);
        bool before = await ObserveLiveAsync(grant.AccessToken);
        ClientCredentials caseVariant = new(
            Realm.SubjectA.ClientId.ToUpperInvariant(),
            Realm.SubjectA.Secret
        );
        HttpOutcome outcome = await Realm.Api.RevokeAsync(
            FormBody.Revocation(caseVariant, grant.AccessToken)
        );
        bool after = await Realm.ObserveAsync(grant.AccessToken);
        return new RowObservation(
            outcome,
            before,
            after,
            "client_id upper-cased; answered as an unknown client"
        );
    }
}

[TestFixture]
public class Given_an_invalid_secret_and_an_unknown_token : RevocationCharacterizationRow
{
    protected override string RowId => "K-04";

    protected override string Scenario => "Invalid secret + unknown token";

    // Observed in P1.1 (approved correction to §2.1): a wrong secret on a known confidential client
    // is unauthorized_client, while an unknown client is invalid_client. D-10 row 4 maps both.
    protected override RowPrediction Prediction =>
        RowPrediction.ErrorBody(
            401,
            "unauthorized_client",
            "Invalid client or Invalid client credentials",
            Predicted<bool>.NotApplicable
        );

    protected override async Task<RowObservation> ObserveAsync()
    {
        ClientCredentials wrongSecret = new(Realm.SubjectA.ClientId, "not-the-secret");
        HttpOutcome outcome = await Realm.Api.RevokeAsync(FormBody.Revocation(wrongSecret, "not-a-token"));
        return new RowObservation(outcome, ActiveBefore: null, ActiveAfter: null, "no live token involved");
    }
}

[TestFixture]
public class Given_an_invalid_secret_and_the_callers_own_valid_token : RevocationCharacterizationRow
{
    protected override string RowId => "K-05";

    protected override string Scenario => "Invalid secret + the caller's own valid token †";

    protected override RowPrediction Prediction =>
        RowPrediction.ErrorBody(
            401,
            "unauthorized_client",
            "Invalid client or Invalid client credentials",
            Predicted<bool>.Of(true)
        );

    protected override async Task<RowObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.SubjectA);
        bool before = await ObserveLiveAsync(grant.AccessToken);
        ClientCredentials wrongSecret = new(Realm.SubjectA.ClientId, "not-the-secret");
        HttpOutcome outcome = await Realm.Api.RevokeAsync(
            FormBody.Revocation(wrongSecret, grant.AccessToken)
        );
        bool after = await Realm.ObserveAsync(grant.AccessToken);
        return new RowObservation(outcome, before, after, "token must stay active");
    }
}

[TestFixture]
public class Given_an_unknown_client_id : RevocationCharacterizationRow
{
    protected override string RowId => "K-06";

    protected override string Scenario => "Unknown client_id presenting A's live token";

    protected override RowPrediction Prediction =>
        RowPrediction.ErrorBody(
            401,
            "invalid_client",
            "Invalid client or Invalid client credentials",
            Predicted<bool>.Of(true)
        );

    protected override async Task<RowObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.SubjectA);
        bool before = await ObserveLiveAsync(grant.AccessToken);
        ClientCredentials unknown = new($"cs-char-unknown-{Realm.RunId}", "irrelevant");
        HttpOutcome outcome = await Realm.Api.RevokeAsync(FormBody.Revocation(unknown, grant.AccessToken));
        bool after = await Realm.ObserveAsync(grant.AccessToken);
        return new RowObservation(
            outcome,
            before,
            after,
            "A's token is live only to prove it is left untouched"
        );
    }
}

[TestFixture]
public class Given_no_client_credentials : RevocationCharacterizationRow
{
    protected override string RowId => "K-07";

    protected override string Scenario => "No credentials at all, A's live token †";

    protected override RowPrediction Prediction =>
        RowPrediction.ErrorBody(
            401,
            "invalid_client",
            "Invalid client or Invalid client credentials",
            Predicted<bool>.Of(true)
        );

    protected override async Task<RowObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.SubjectA);
        bool before = await ObserveLiveAsync(grant.AccessToken);
        HttpOutcome outcome = await Realm.Api.RevokeAsync(
            FormBody.Revocation(credentials: null, grant.AccessToken)
        );
        bool after = await Realm.ObserveAsync(grant.AccessToken);
        return new RowObservation(
            outcome,
            before,
            after,
            "no Authorization header, no client_id, no client_secret"
        );
    }
}

[TestFixture]
public class Given_basic_and_form_credentials_for_different_clients : RevocationCharacterizationRow
{
    protected override string RowId => "K-08";

    protected override string Scenario => "Mixed mechanisms: Basic = A, form = B, token = A's †";

    // §2.1: the form value overrides the header, so Keycloak authenticates B and the token is A's.
    protected override RowPrediction Prediction =>
        RowPrediction.ErrorBody(400, "invalid_request", "Unmatching clients", Predicted<bool>.Of(true));

    protected override async Task<RowObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.SubjectA);
        bool before = await ObserveLiveAsync(grant.AccessToken);
        HttpOutcome outcome = await Realm.Api.RevokeAsync(
            FormBody.Revocation(Realm.SubjectB.Credentials, grant.AccessToken),
            Realm.SubjectA.Credentials.ToBasicHeader()
        );
        bool after = await Realm.ObserveAsync(grant.AccessToken);
        string winner = outcome.Status == 200 ? "header (A) won" : "form (B) won";
        return new RowObservation(outcome, before, after, $"identity that Keycloak used: {winner}");
    }
}

[TestFixture]
public class Given_a_duplicated_token_parameter : RevocationCharacterizationRow
{
    protected override string RowId => "K-09";

    protected override string Scenario => "Duplicated `token` parameter (same live token twice) †";

    protected override RowPrediction Prediction =>
        RowPrediction.ErrorBody(400, "invalid_request", "duplicated parameter", Predicted<bool>.Of(true));

    protected override async Task<RowObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.SubjectA);
        bool before = await ObserveLiveAsync(grant.AccessToken);
        HttpOutcome outcome = await Realm.Api.RevokeAsync(
            FormBody.Revocation(Realm.SubjectA.Credentials, grant.AccessToken).Add("token", grant.AccessToken)
        );
        bool after = await Realm.ObserveAsync(grant.AccessToken);
        return new RowObservation(outcome, before, after, "owner credentials");
    }
}

public abstract class Given_an_owner_revoking_with_a_token_type_hint(string hint, string rowId)
    : RevocationCharacterizationRow
{
    protected override string RowId => rowId;

    protected override string Scenario => $"Owner revokes its access token with token_type_hint=\"{hint}\" †";

    protected override RowPrediction Prediction => RowPrediction.EmptySuccess(Predicted<bool>.Of(false));

    protected override async Task<RowObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.SubjectA);
        bool before = await ObserveLiveAsync(grant.AccessToken);
        HttpOutcome outcome = await Realm.Api.RevokeAsync(
            FormBody.Revocation(Realm.SubjectA.Credentials, grant.AccessToken, hint)
        );
        bool after = await Realm.ObserveAsync(grant.AccessToken);
        return new RowObservation(outcome, before, after, "the endpoint source does not read the hint");
    }
}

[TestFixture]
public class Given_an_owner_revoking_with_hint_access_token()
    : Given_an_owner_revoking_with_a_token_type_hint("access_token", "K-10");

[TestFixture]
public class Given_an_owner_revoking_with_hint_refresh_token()
    : Given_an_owner_revoking_with_a_token_type_hint("refresh_token", "K-11");

[TestFixture]
public class Given_an_owner_revoking_with_hint_bogus()
    : Given_an_owner_revoking_with_a_token_type_hint("bogus", "K-12");

[TestFixture]
public class Given_an_owner_revoking_with_an_empty_hint()
    : Given_an_owner_revoking_with_a_token_type_hint("", "K-13");

[TestFixture]
public class Given_an_owner_revoking_its_refresh_token : RevocationCharacterizationRow
{
    private bool? _pairedAccessTokenActiveBefore;
    private bool? _pairedAccessTokenActiveAfter;

    protected override string RowId => "K-14";

    protected override string Scenario =>
        "Owner revokes its refresh token (client_credentials with refresh enabled); refresh state seen by the owner, paired access token by the observer †";

    protected override RowPrediction Prediction => RowPrediction.EmptySuccess(Predicted<bool>.Of(false));

    protected override async Task<RowObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.RefreshSubject);
        if (grant.RefreshToken is null)
        {
            throw new InvalidOperationException(
                "The refresh subject issued no refresh token; the row cannot be characterized."
            );
        }

        bool before = await ObserveLiveRefreshTokenAsync(Realm.RefreshSubject, grant.RefreshToken);
        _pairedAccessTokenActiveBefore = await ObserveLiveAsync(grant.AccessToken);
        HttpOutcome outcome = await Realm.Api.RevokeAsync(
            FormBody.Revocation(Realm.RefreshSubject.Credentials, grant.RefreshToken)
        );
        bool after = await Realm.ObserveRefreshTokenAsOwnerAsync(Realm.RefreshSubject, grant.RefreshToken);
        _pairedAccessTokenActiveAfter = await Realm.ObserveAsync(grant.AccessToken);
        return new RowObservation(
            outcome,
            before,
            after,
            $"observer's view of the paired access token: {Render(_pairedAccessTokenActiveBefore)} before, {Render(_pairedAccessTokenActiveAfter)} after"
        );
    }

    [Test]
    public void It_started_from_a_paired_access_token_the_observer_reported_active() =>
        _pairedAccessTokenActiveBefore.Should().BeTrue();

    // §2.1: refresh-token revocation detaches the client session, so the paired access token dies with it.
    [Test]
    public void It_invalidates_the_paired_access_token() => _pairedAccessTokenActiveAfter.Should().BeFalse();
}

[TestFixture]
public class Given_an_id_token : RevocationCharacterizationRow
{
    protected override string RowId => "K-15";

    protected override string Scenario =>
        "ID token (public user's, scope=openid) presented by confidential A; state is the paired access token's";

    protected override RowPrediction Prediction =>
        RowPrediction.ErrorBody(
            400,
            "unsupported_token_type",
            "Unsupported token type",
            Predicted<bool>.Of(true)
        );

    protected override async Task<RowObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.PublicUserGrantAsync();
        if (grant.IdToken is null)
        {
            throw new InvalidOperationException("The password grant with scope=openid issued no id_token.");
        }

        bool pairedBefore = await ObserveLiveAsync(grant.AccessToken);
        HttpOutcome outcome = await Realm.Api.RevokeAsync(
            FormBody.Revocation(Realm.SubjectA.Credentials, grant.IdToken)
        );
        bool pairedAfter = await Realm.ObserveAsync(grant.AccessToken);
        return new RowObservation(
            outcome,
            pairedBefore,
            pairedAfter,
            "A is not the ID token's azp, so a 400 unsupported_token_type here shows the type check runs before the ownership check"
        );
    }
}

[TestFixture]
public class Given_an_expired_access_token : RevocationCharacterizationRow
{
    protected override string RowId => "K-16";

    protected override string Scenario =>
        "Owner revokes its own naturally expired access token (short-lifespan client, expiry observed first)";

    // Observed in P1.1: decode accepts the expired token and the owner's revocation path runs to a
    // 200 with an empty body (not 200 invalid_token). Either way CMS answers 200 under D-13.
    protected override RowPrediction Prediction => RowPrediction.EmptySuccess(Predicted<bool>.Of(false));

    protected override async Task<RowObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.ExpirySubject);
        bool liveAtIssue = await ObserveLiveAsync(grant.AccessToken);
        string expiryNote = await Realm.WaitUntilObservedInactiveAsync(grant.AccessToken);
        HttpOutcome outcome = await Realm.Api.RevokeAsync(
            FormBody.Revocation(Realm.ExpirySubject.Credentials, grant.AccessToken)
        );
        bool after = await Realm.ObserveAsync(grant.AccessToken);
        return new RowObservation(
            outcome,
            ActiveBefore: null,
            after,
            $"active at issue: {liveAtIssue}; {expiryNote}; configured lifespan {CharacterizationRealm.ExpirySubjectAccessTokenLifespanSeconds}s"
        );
    }
}

[TestFixture]
public class Given_a_token_from_another_realm : RevocationCharacterizationRow
{
    protected override string RowId => "K-17";

    protected override string Scenario =>
        "Token issued and signed by the master realm, presented by confidential A";

    // The edfi realm cannot introspect a master-realm token, so there is no observable state for this row.
    protected override RowPrediction Prediction =>
        RowPrediction.ErrorBody(200, "invalid_token", "Invalid token", Predicted<bool>.NotApplicable);

    protected override async Task<RowObservation> ObserveAsync()
    {
        string foreignToken = await Realm.GetAdminTokenAsync();
        HttpOutcome outcome = await Realm.Api.RevokeAsync(
            FormBody.Revocation(Realm.SubjectA.Credentials, foreignToken)
        );
        return new RowObservation(
            outcome,
            ActiveBefore: null,
            ActiveAfter: null,
            "a master-realm token has no observable state in the edfi realm"
        );
    }
}

[TestFixture]
public class Given_a_public_client_revoking_its_own_user_token_without_a_secret
    : RevocationCharacterizationRow
{
    private int? _refreshGrantStatusAfter;

    protected override string RowId => "K-18";

    protected override string Scenario => "Public client revokes its own user-flow access token, no secret †";

    // §2.1: Keycloak authenticates a public client without a secret.
    protected override RowPrediction Prediction => RowPrediction.EmptySuccess(Predicted<bool>.Of(false));

    protected override async Task<RowObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.PublicUserGrantAsync();
        bool before = await ObserveLiveAsync(grant.AccessToken);
        HttpOutcome outcome = await Realm.Api.RevokeAsync(
            FormBody.Revocation(Realm.PublicClient.Credentials, grant.AccessToken)
        );
        bool after = await Realm.ObserveAsync(grant.AccessToken);
        // Last use of the refresh token in this row: the probe may rotate it (see D-16).
        _refreshGrantStatusAfter = grant.RefreshToken is null
            ? null
            : (await Realm.PublicUserRefreshGrantProbeAsync(grant.RefreshToken)).Status;
        return new RowObservation(
            outcome,
            before,
            after,
            $"refresh grant probe with the paired refresh token after the call: HTTP {_refreshGrantStatusAfter?.ToString() ?? "no refresh token issued"}"
        );
    }

    // §2.1: access-token revocation records the jti and leaves the session alone, so the paired
    // refresh token still buys a new access token.
    [Test]
    public void It_leaves_the_paired_refresh_token_usable() => _refreshGrantStatusAfter.Should().Be(200);
}

[TestFixture]
public class Given_a_public_client_revoking_its_own_user_token_with_an_arbitrary_secret
    : RevocationCharacterizationRow
{
    protected override string RowId => "K-19";

    protected override string Scenario =>
        "Public client revokes its own user-flow access token with an arbitrary secret †";

    protected override RowPrediction Prediction => RowPrediction.EmptySuccess(Predicted<bool>.Of(false));

    protected override async Task<RowObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.PublicUserGrantAsync();
        bool before = await ObserveLiveAsync(grant.AccessToken);
        ClientCredentials arbitrary = new(Realm.PublicClient.ClientId, "any-secret-at-all");
        HttpOutcome outcome = await Realm.Api.RevokeAsync(FormBody.Revocation(arbitrary, grant.AccessToken));
        bool after = await Realm.ObserveAsync(grant.AccessToken);
        return new RowObservation(
            outcome,
            before,
            after,
            "client_secret ignored for a public client per §2.1"
        );
    }
}

[TestFixture]
public class Given_a_public_client_revoking_another_clients_token : RevocationCharacterizationRow
{
    protected override string RowId => "K-20";

    protected override string Scenario =>
        "Public client (no secret) presents confidential A's access token †";

    protected override RowPrediction Prediction =>
        RowPrediction.ErrorBody(400, "invalid_request", "Unmatching clients", Predicted<bool>.Of(true));

    protected override async Task<RowObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.SubjectA);
        bool before = await ObserveLiveAsync(grant.AccessToken);
        HttpOutcome outcome = await Realm.Api.RevokeAsync(
            FormBody.Revocation(Realm.PublicClient.Credentials, grant.AccessToken)
        );
        bool after = await Realm.ObserveAsync(grant.AccessToken);
        return new RowObservation(
            outcome,
            before,
            after,
            "public client authenticates, then fails Keycloak's ownership check"
        );
    }
}

[TestFixture]
public class Given_a_second_revocation_of_an_already_revoked_token : RevocationCharacterizationRow
{
    private HttpOutcome? _firstOutcome;
    private bool? _activeAfterFirst;

    protected override string RowId => "K-21";

    protected override string Scenario => "Idempotent second revoke of an already revoked access token";

    protected override RowPrediction Prediction => RowPrediction.EmptySuccess(Predicted<bool>.Of(false));

    protected override async Task<RowObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.SubjectA);
        await ObserveLiveAsync(grant.AccessToken);
        _firstOutcome = await Realm.Api.RevokeAsync(
            FormBody.Revocation(Realm.SubjectA.Credentials, grant.AccessToken)
        );
        _activeAfterFirst = await Realm.ObserveAsync(grant.AccessToken);
        HttpOutcome second = await Realm.Api.RevokeAsync(
            FormBody.Revocation(Realm.SubjectA.Credentials, grant.AccessToken)
        );
        bool afterSecond = await Realm.ObserveAsync(grant.AccessToken);
        return new RowObservation(
            second,
            ActiveBefore: null,
            afterSecond,
            $"first call: HTTP {_firstOutcome.Status}, token {Render(_activeAfterFirst)} afterwards"
        );
    }

    [Test]
    public void It_had_revoked_the_token_on_the_first_call()
    {
        _firstOutcome.Should().NotBeNull();
        _firstOutcome!.Status.Should().Be(200);
        _firstOutcome.BodyLength.Should().Be(0);
        _activeAfterFirst.Should().BeFalse();
    }
}

[TestFixture]
public class Given_a_missing_token_parameter : RevocationCharacterizationRow
{
    protected override string RowId => "K-22";

    protected override string Scenario => "Owner credentials, no `token` parameter";

    protected override RowPrediction Prediction =>
        RowPrediction.ErrorBody(400, "invalid_request", "Token not provided", Predicted<bool>.NotApplicable);

    protected override async Task<RowObservation> ObserveAsync()
    {
        HttpOutcome outcome = await Realm.Api.RevokeAsync(
            FormBody.Revocation(Realm.SubjectA.Credentials, token: null)
        );
        return new RowObservation(outcome, ActiveBefore: null, ActiveAfter: null, "no token involved");
    }
}

[TestFixture]
public class Given_introspection_by_the_public_client : RevocationCharacterizationRow
{
    protected override string RowId => "K-23";

    protected override string Scenario =>
        "Introspection endpoint called by the public client with its own user token (observer rationale, A-06)";

    protected override RowPrediction Prediction =>
        RowPrediction.ErrorBody(403, "invalid_request", "Client not allowed.", Predicted<bool>.Of(true));

    protected override async Task<RowObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.PublicUserGrantAsync();
        bool before = await ObserveLiveAsync(grant.AccessToken);
        HttpOutcome outcome = await Realm.Api.IntrospectRawAsync(
            Realm.PublicClient.Credentials,
            grant.AccessToken
        );
        bool after = await Realm.ObserveAsync(grant.AccessToken);
        return new RowObservation(
            outcome,
            before,
            after,
            "not a revocation call; shows why a confidential observer is required"
        );
    }
}
