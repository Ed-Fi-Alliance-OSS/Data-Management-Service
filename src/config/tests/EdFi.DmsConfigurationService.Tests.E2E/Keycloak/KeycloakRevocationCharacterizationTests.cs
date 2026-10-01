// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.DmsConfigurationService.Tests.E2E.Keycloak;

// DMS-1327 P1.1: characterization of the pinned Keycloak image's token revocation endpoint.
//
// These fixtures record what Keycloak actually answers (status, `error`, `error_description`) and,
// through a dedicated confidential observer client, the token's introspection state before and
// after each call. Each row asserts the exact values §2.1 of the spec predicts from the Keycloak
// source; a deviation fails the fixture so it is recorded in §9.1 and reviewed before any
// normalization (D-10, D-11) is coded. Fields §2.1 leaves open are reported as skipped tests with
// the observed value, never as passes. Nothing here touches the Configuration Service.

/// <summary>Creates the run's realm resources once and removes them (and writes the evidence table) at the end.</summary>
[SetUpFixture]
public class KeycloakCharacterizationRealmSetup
{
    private static CharacterizationRealm? _realm;

    public static CharacterizationRealm Realm =>
        _realm
        ?? throw new InvalidOperationException("The characterization realm resources were not created.");

    [OneTimeSetUp]
    public async Task CreateRealmResources()
    {
        if (KeycloakCharacterizationEnvironment.IsKeycloakProvider)
        {
            _realm = await CharacterizationRealm.CreateAsync();
        }
    }

    [OneTimeTearDown]
    public async Task RemoveRealmResourcesAndWriteEvidence()
    {
        if (_realm is null)
        {
            return;
        }

        string evidencePath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"keycloak-characterization-{_realm.ServerVersion}.md"
        );
        await File.WriteAllTextAsync(evidencePath, EvidenceLog.RenderMarkdown(_realm));
        await TestContext.Progress.WriteLineAsync($"[evidence] table written to {evidencePath}");

        await _realm.DisposeAsync();
        _realm = null;
    }
}

/// <summary>
/// One evidence row: the subclass arranges tokens, performs exactly one characterized call and
/// reports what it observed; the shared tests compare that against the §2.1 prediction.
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
        EvidenceLog.Record(new EvidenceRow(RowId, Scenario, Prediction, _observation));
    }

    protected abstract Task<RowObservation> ObserveAsync();

    [Test]
    public void It_started_from_a_token_the_observer_reported_active()
    {
        if (Observation.ActiveBefore is null)
        {
            Assert.Ignore("This row does not start from a live token.");
        }

        Observation.ActiveBefore.Should().BeTrue("the observer must prove the token live before the call");
    }

    [Test]
    public void It_answers_the_predicted_http_status() =>
        AssertPredicted(Prediction.Status, Observation.Outcome.Status, "HTTP status");

    [Test]
    public void It_answers_the_predicted_error() =>
        AssertPredicted(Prediction.Error, Observation.Outcome.Error, "error");

    [Test]
    public void It_answers_the_predicted_error_description() =>
        AssertPredicted(
            Prediction.ErrorDescription,
            Observation.Outcome.ErrorDescription,
            "error_description"
        );

    [Test]
    public void It_leaves_the_token_in_the_predicted_state_afterwards()
    {
        if (!Prediction.ActiveAfter.IsPredicted)
        {
            Assert.Ignore(
                $"§2.1 makes no prediction about the token's state afterwards; observed '{Render(Observation.ActiveAfter)}'."
            );
        }

        Observation.ActiveAfter.Should().Be(Prediction.ActiveAfter.Value);
    }

    /// <summary>The observer proves a token live; anything else is a failed prerequisite, not "inactive".</summary>
    protected static async Task<bool> ObserveLiveAsync(string token)
    {
        bool active = await Realm.ObserveAsync(token);
        if (!active)
        {
            throw new InvalidOperationException(
                "The observer reported a freshly issued token inactive; the characterization prerequisite failed."
            );
        }

        return active;
    }

    /// <summary>Same prerequisite rule for a refresh token, observed with the refresh-token hint.</summary>
    protected static async Task<bool> ObserveLiveRefreshTokenAsync(string refreshToken)
    {
        bool active = await Realm.ObserveRefreshTokenAsync(refreshToken);
        if (!active)
        {
            throw new InvalidOperationException(
                "The observer reported a freshly issued refresh token inactive; the characterization prerequisite failed."
            );
        }

        return active;
    }

    private static void AssertPredicted<T>(Predicted<T> predicted, T? observed, string field)
    {
        if (!predicted.IsPredicted)
        {
            Assert.Ignore(
                $"§2.1 makes no prediction for the {field}; observed '{observed?.ToString() ?? "(absent)"}'."
            );
        }

        observed
            .Should()
            .Be(predicted.Value, $"the {field} is the value §2.1 predicts from the Keycloak source");
    }

    private static string Render(bool? state) =>
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
    private bool _observerSeesRefreshToken;
    private bool _observerSeesPublicUserRefreshToken;

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
        _observerSeesPublicUserRefreshToken =
            publicUser.RefreshToken is not null
            && await Realm.ObserveRefreshTokenAsync(publicUser.RefreshToken);

        TokenGrant refresh = await Realm.ServiceAccountGrantAsync(Realm.RefreshSubject);
        _observerSeesRefreshToken =
            refresh.RefreshToken is not null && await Realm.ObserveRefreshTokenAsync(refresh.RefreshToken);

        EvidenceLog.Record(
            new EvidenceRow(
                "K-00",
                "Preconditions (A-02, A-04, A-06)",
                RowPrediction.Open(Predicted<bool>.Unpredicted),
                new RowObservation(
                    new HttpOutcome(0, null, null, 0, false, null, null),
                    ActiveBefore: null,
                    ActiveAfter: null,
                    $"server {Realm.ServerVersion}; sslRequired={Realm.SslRequired ?? "(absent)"}; azp={(string.Equals(_azp, _subjectClientId, StringComparison.Ordinal) ? "client id" : _azp ?? "(absent)")}; "
                        + $"observer sees subject access token {Render(_observerSeesSubjectToken)}, public user access token {Render(_observerSeesPublicUserToken)}, "
                        + $"public user refresh token {Render(_observerSeesPublicUserRefreshToken)}, service-account refresh token {Render(_observerSeesRefreshToken)}; "
                        + $"refresh token issued for client_credentials: {(refresh.RefreshToken is not null ? "yes" : "no")}; "
                        + "observer is in every subject token's aud via a per-run client scope; refresh tokens observed with token_type_hint=refresh_token"
                )
            )
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
            .BeTrue("a public client's token must be observable by the confidential observer (A-06)");

    [Test]
    public void It_lets_the_observer_see_a_public_client_user_refresh_token_as_active() =>
        _observerSeesPublicUserRefreshToken.Should().BeTrue();

    [Test]
    public void It_lets_the_observer_see_a_service_account_refresh_token_as_active() =>
        _observerSeesRefreshToken
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

    protected override RowPrediction Prediction => RowPrediction.Open(Predicted<bool>.Of(true));

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
            "client_id upper-cased; §2.2 lists unknown client as open"
        );
    }
}

[TestFixture]
public class Given_an_invalid_secret_and_an_unknown_token : RevocationCharacterizationRow
{
    protected override string RowId => "K-04";

    protected override string Scenario => "Invalid secret + unknown token";

    protected override RowPrediction Prediction =>
        RowPrediction.ErrorBody(
            401,
            "invalid_client",
            "Invalid client or Invalid client credentials",
            Predicted<bool>.Unpredicted
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
            "invalid_client",
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

    protected override RowPrediction Prediction => RowPrediction.Open(Predicted<bool>.Of(true));

    protected override async Task<RowObservation> ObserveAsync()
    {
        TokenGrant grant = await Realm.ServiceAccountGrantAsync(Realm.SubjectA);
        bool before = await ObserveLiveAsync(grant.AccessToken);
        ClientCredentials unknown = new($"cs-char-unknown-{Realm.RunId}", "irrelevant");
        HttpOutcome outcome = await Realm.Api.RevokeAsync(FormBody.Revocation(unknown, grant.AccessToken));
        bool after = await Realm.ObserveAsync(grant.AccessToken);
        return new RowObservation(outcome, before, after, "§2.2 lists unknown client as open");
    }
}

[TestFixture]
public class Given_no_client_credentials : RevocationCharacterizationRow
{
    protected override string RowId => "K-07";

    protected override string Scenario => "No credentials at all, A's live token †";

    protected override RowPrediction Prediction => RowPrediction.Open(Predicted<bool>.Of(true));

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
        "Owner revokes its refresh token (client_credentials with refresh enabled) †";

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

        bool before = await ObserveLiveRefreshTokenAsync(grant.RefreshToken);
        _pairedAccessTokenActiveBefore = await ObserveLiveAsync(grant.AccessToken);
        HttpOutcome outcome = await Realm.Api.RevokeAsync(
            FormBody.Revocation(Realm.RefreshSubject.Credentials, grant.RefreshToken)
        );
        bool after = await Realm.ObserveRefreshTokenAsync(grant.RefreshToken);
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

    private static string Render(bool? state) => state is true ? "active" : "inactive";
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

    // §2.2: either 200 `invalid_token` or 200 empty; both are CMS 200, so only the status is predicted.
    protected override RowPrediction Prediction =>
        new(
            Predicted<int>.Of(200),
            Predicted<string>.Unpredicted,
            Predicted<string>.Unpredicted,
            Predicted<bool>.Of(false)
        );

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

    protected override RowPrediction Prediction =>
        RowPrediction.ErrorBody(200, "invalid_token", "Invalid token", Predicted<bool>.Unpredicted);

    protected override async Task<RowObservation> ObserveAsync()
    {
        string foreignToken = await Realm.GetAdminTokenAsync();
        HttpOutcome outcome = await Realm.Api.RevokeAsync(
            FormBody.Revocation(Realm.SubjectA.Credentials, foreignToken)
        );
        bool observedInRealm = await Realm.ObserveAsync(foreignToken);
        return new RowObservation(
            outcome,
            ActiveBefore: null,
            observedInRealm,
            "the edfi observer's view of a master-realm token is recorded for completeness only"
        );
    }
}

[TestFixture]
public class Given_a_public_client_revoking_its_own_user_token_without_a_secret
    : RevocationCharacterizationRow
{
    private bool? _pairedRefreshTokenActiveAfter;

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
        _pairedRefreshTokenActiveAfter = grant.RefreshToken is not null
            ? await Realm.ObserveRefreshTokenAsync(grant.RefreshToken)
            : null;
        return new RowObservation(
            outcome,
            before,
            after,
            $"observer's view of the paired refresh token after: {(_pairedRefreshTokenActiveAfter is true ? "active" : "inactive")}"
        );
    }

    // §2.1: access-token revocation records the jti and leaves the session alone.
    [Test]
    public void It_leaves_the_paired_refresh_token_active() =>
        _pairedRefreshTokenActiveAfter.Should().BeTrue();
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
        FormBody form = FormBody.Revocation(Realm.SubjectA.Credentials, grant.AccessToken);
        _firstOutcome = await Realm.Api.RevokeAsync(form);
        _activeAfterFirst = await Realm.ObserveAsync(grant.AccessToken);
        HttpOutcome second = await Realm.Api.RevokeAsync(
            FormBody.Revocation(Realm.SubjectA.Credentials, grant.AccessToken)
        );
        bool afterSecond = await Realm.ObserveAsync(grant.AccessToken);
        return new RowObservation(
            second,
            ActiveBefore: null,
            afterSecond,
            $"first call: HTTP {_firstOutcome.Status}, token {(_activeAfterFirst is true ? "active" : "inactive")} afterwards"
        );
    }

    [Test]
    public void It_had_revoked_the_token_on_the_first_call()
    {
        _firstOutcome.Should().NotBeNull();
        _firstOutcome!.Status.Should().Be(200);
        _activeAfterFirst.Should().BeFalse();
    }
}

[TestFixture]
public class Given_a_missing_token_parameter : RevocationCharacterizationRow
{
    protected override string RowId => "K-22";

    protected override string Scenario => "Owner credentials, no `token` parameter";

    protected override RowPrediction Prediction =>
        RowPrediction.ErrorBody(400, "invalid_request", "Token not provided", Predicted<bool>.Unpredicted);

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
