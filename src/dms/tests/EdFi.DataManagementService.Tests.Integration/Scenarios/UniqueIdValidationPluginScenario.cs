// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Tests.Integration.Plugins;
using FluentAssertions;
using Serilog.Events;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Drives the reference UniqueId validator plugin over real HTTP against the composed write pipeline,
/// a leased database, and an in-process stub of the external unique-id system it calls.
/// </summary>
/// <remarks>
/// Every assertion here is about an HTTP status code, a JSON body, or the stub's own request log,
/// never about internal pipeline state, because what this proves is what a client of a real
/// deployment - and the external system the validator calls - observes. Hard-coded for
/// <c>FixtureKey.AuthoritativeDs52</c>'s shapes: project endpoint <c>ed-fi</c>, <c>students</c>
/// requiring <c>studentUniqueId</c>, <c>firstName</c>, <c>lastSurname</c>, and <c>birthDate</c>;
/// <c>staffs</c> and <c>contacts</c> requiring their own UniqueId, <c>firstName</c>, and
/// <c>lastSurname</c>.
/// </remarks>
internal static class UniqueIdValidationPluginScenario
{
    /// <summary>The staged plugin directory name, which is also the plugin's own Name.</summary>
    public const string PluginName = "Acme.UniqueIdValidation";

    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string StaffsEndpoint = "/data/ed-fi/staffs";
    private const string ContactsEndpoint = "/data/ed-fi/contacts";

    /// <summary>
    /// A descriptor endpoint, chosen as the non-person control because a descriptor needs no other
    /// resource to already exist, unlike a School, which needs its category and grade-level
    /// descriptors seeded first.
    /// </summary>
    private const string NonPersonEndpoint = "/data/ed-fi/absenceEventCategoryDescriptors";

    private const string NonPersonDescriptorNamespace =
        "uri://ed-fi.org/AbsenceEventCategoryDescriptor";

    private const string JsonContentType = "application/json";

    private const string StudentResourceName = "Student";
    private const string StaffResourceName = "Staff";
    private const string ContactResourceName = "Contact";

    private const string StudentLastSurname = "Uidv";
    private const string StaffLastSurname = "Uidv";
    private const string ContactLastSurname = "Uidv";
    private const string StudentBirthDate = "2010-01-01";

    /// <summary>The identity B1's Student case knows and creates.</summary>
    private const string KnownStudentUniqueId = "uidv-known-student-001";

    /// <summary>The identity B1's Staff case knows and creates.</summary>
    private const string KnownStaffUniqueId = "uidv-known-staff-001";

    /// <summary>The identity B1's Contact case knows and creates.</summary>
    private const string KnownContactUniqueId = "uidv-known-contact-001";

    /// <summary>
    /// The identity B2's Student case never teaches the stub, and the one the not-allowlisted twin
    /// (a later fixture) sends unchanged, so the two cases put the same bytes on the wire.
    /// </summary>
    private const string UnknownStudentUniqueId = "uidv-unknown-student-001";
    private const string UnknownStudentFirstName = "Uidv";

    private const string UnknownStaffUniqueId = "uidv-unknown-staff-001";
    private const string UnknownContactUniqueId = "uidv-unknown-contact-001";

    /// <summary>The identity B3 and B4 first teach the stub and then make it forget.</summary>
    private const string ForgottenStudentUniqueId = "uidv-forgotten-001";
    private const string StoredFirstName = "Grace";
    private const string ChangedFirstName = "Ada";

    /// <summary>The identity a key-change PUT starts from in B6 and B7, always known.</summary>
    private const string KeyChangeSourceUniqueId = "uidv-keychange-a-001";

    /// <summary>B6's target identity: known to nothing, so the custom validator itself rejects it.</summary>
    private const string KeyChangeUnknownTargetUniqueId = "uidv-keychange-b-unk-001";

    /// <summary>B7's target identity: known to the stub, so DMS's own immutable-identity guard rejects it.</summary>
    private const string KeyChangeKnownTargetUniqueId = "uidv-keychange-b-know-001";

    private const string ServerErrorStudentUniqueId = "uidv-servererror-001";
    private const string UnreachableStudentUniqueId = "uidv-unreachable-001";

    /// <summary>The identity F1's redirect case knows, so a followed redirect would look like success.</summary>
    private const string RedirectStudentUniqueId = "uidv-redirect-001";

    /// <summary>The identity F5's trailing-slash case knows and creates.</summary>
    private const string TrailingSlashStudentUniqueId = "uidv-trailing-slash-001";

    /// <summary>The identity F6's missing-BaseAddress case sends; the stub never sees it.</summary>
    private const string MissingBaseAddressStudentUniqueId = "uidv-no-baseaddress-001";

    /// <summary>The identity F7's slow-upstream case knows, so only the timeout explains the failure.</summary>
    private const string SlowUpstreamStudentUniqueId = "uidv-slow-upstream-001";

    /// <summary>
    /// A UniqueId containing a slash, a space, and a percent sign, none of which the identity
    /// pattern forbids, since only leading and trailing whitespace are refused.
    /// </summary>
    private const string SpecialCharactersUniqueId = "a/b c%d-001";

    /// <summary>
    /// The identity G1's logging-proof case knows and creates. Distinctive enough that it would not
    /// appear in any log event this host writes for an unrelated reason.
    /// </summary>
    private const string LoggingProofStudentUniqueId = "uidv-log-proof-001";

    private const string DataValidationFailedType =
        "urn:ed-fi:api:bad-request:data-validation-failed";
    private const string DataValidationFailedTitle = "Data Validation Failed";
    private const string KeyChangeNotSupportedType =
        "urn:ed-fi:api:bad-request:data-validation-failed:key-change-not-supported";
    private const string KeyChangeNotSupportedTitle = "Key Change Not Supported";

    // ---- B1: a write whose UniqueId the stub knows succeeds, and the stub saw exactly one request ----

    public static async Task It_creates_a_student_the_stub_knows(
        ApiIntegrationHarness harness,
        UniqueIdServiceStub stub
    )
    {
        stub.Know(StudentResourceName, KnownStudentUniqueId);

        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            KnownStudentUniqueId,
            "Ada"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        stub.RequestPaths.Should().Equal($"/{StudentResourceName}/{KnownStudentUniqueId}");
    }

    public static async Task It_creates_a_staff_the_stub_knows(
        ApiIntegrationHarness harness,
        UniqueIdServiceStub stub
    )
    {
        stub.Know(StaffResourceName, KnownStaffUniqueId);

        using HttpResponseMessage response = await PostStaffAsync(
            harness,
            KnownStaffUniqueId,
            "Ada"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        stub.RequestPaths.Should().Equal($"/{StaffResourceName}/{KnownStaffUniqueId}");
    }

    public static async Task It_creates_a_contact_the_stub_knows(
        ApiIntegrationHarness harness,
        UniqueIdServiceStub stub
    )
    {
        stub.Know(ContactResourceName, KnownContactUniqueId);

        using HttpResponseMessage response = await PostContactAsync(
            harness,
            KnownContactUniqueId,
            "Ada"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        stub.RequestPaths.Should().Equal($"/{ContactResourceName}/{KnownContactUniqueId}");
    }

    // ---- B2: a write whose UniqueId the stub does not know is refused, and nothing is persisted ----

    public static async Task It_rejects_a_student_with_an_unknown_unique_id(
        ApiIntegrationHarness harness
    )
    {
        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            UnknownStudentUniqueId,
            UnknownStudentFirstName
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().NotContain(UnknownStudentUniqueId);

        using JsonDocument document = JsonDocument.Parse(body);
        AssertRejectedUniqueId(document.RootElement, StudentResourceName, "studentUniqueId");

        JsonArray students = await GetStudentsByUniqueIdAsync(harness, UnknownStudentUniqueId);
        students.Should().BeEmpty("a rejected write must persist nothing");
    }

    public static async Task It_rejects_a_staff_with_an_unknown_unique_id(
        ApiIntegrationHarness harness
    )
    {
        using HttpResponseMessage response = await PostStaffAsync(
            harness,
            UnknownStaffUniqueId,
            "Uidv"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().NotContain(UnknownStaffUniqueId);

        using JsonDocument document = JsonDocument.Parse(body);
        AssertRejectedUniqueId(document.RootElement, StaffResourceName, "staffUniqueId");
    }

    public static async Task It_rejects_a_contact_with_an_unknown_unique_id(
        ApiIntegrationHarness harness
    )
    {
        using HttpResponseMessage response = await PostContactAsync(
            harness,
            UnknownContactUniqueId,
            "Uidv"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().NotContain(UnknownContactUniqueId);

        using JsonDocument document = JsonDocument.Parse(body);
        AssertRejectedUniqueId(document.RootElement, ContactResourceName, "contactUniqueId");
    }

    // ---- B3: an upsert of an existing document whose UniqueId the stub has since forgotten ----

    public static async Task It_rejects_a_repeat_post_after_the_stub_forgets_the_id(
        ApiIntegrationHarness harness,
        UniqueIdServiceStub stub
    )
    {
        stub.Know(StudentResourceName, ForgottenStudentUniqueId);

        (string locationPath, _) = await CreateStudentAsync(
            harness,
            ForgottenStudentUniqueId,
            StoredFirstName
        );

        stub.Forget(StudentResourceName, ForgottenStudentUniqueId);

        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            ForgottenStudentUniqueId,
            ChangedFirstName
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);

        using JsonDocument document = JsonDocument.Parse(body);
        AssertRejectedUniqueId(document.RootElement, StudentResourceName, "studentUniqueId");

        (await ReadFirstNameAsync(harness, locationPath))
            .Should()
            .Be(StoredFirstName, "a rejected upsert must not have changed what is stored");
    }

    // ---- B4: an update of an existing document whose UniqueId the stub has since forgotten ----

    public static async Task It_rejects_a_put_after_the_stub_forgets_the_id(
        ApiIntegrationHarness harness,
        UniqueIdServiceStub stub
    )
    {
        stub.Know(StudentResourceName, ForgottenStudentUniqueId);

        (string locationPath, string etag) = await CreateStudentAsync(
            harness,
            ForgottenStudentUniqueId,
            StoredFirstName
        );

        stub.Forget(StudentResourceName, ForgottenStudentUniqueId);

        string id = IdFromLocation(locationPath);
        JsonObject payload = StudentBody(ForgottenStudentUniqueId, ChangedFirstName, id);

        using HttpResponseMessage response = await PutAsync(harness, locationPath, etag, payload);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);

        using JsonDocument document = JsonDocument.Parse(body);
        AssertRejectedUniqueId(document.RootElement, StudentResourceName, "studentUniqueId");

        (await ReadFirstNameAsync(harness, locationPath))
            .Should()
            .Be(StoredFirstName, "a rejected update must not have changed what is stored");
    }

    // ---- B5: a non-person resource is untouched by the validator ----

    /// <summary>
    /// A write to a resource that is not a person succeeds and never reaches the upstream.
    /// </summary>
    /// <remarks>
    /// This does not prove that the host filters validators by <c>AppliesTo</c>: were that filter
    /// broken, this validator would still make no call here, because a descriptor carries no member
    /// named after its resource plus <c>UniqueId</c>. Filtering is proven by the proof plugin's own
    /// non-matching-resource case, whose rule would reject this resource if filtering regressed.
    /// </remarks>
    public static async Task It_creates_a_non_person_resource_without_calling_the_stub(
        ApiIntegrationHarness harness,
        UniqueIdServiceStub stub
    )
    {
        using HttpResponseMessage response = await PostNonPersonResourceAsync(harness);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        stub.RequestPaths.Should()
            .BeEmpty("a non-person write gives this validator nothing to look up");
    }

    // ---- B6: a key change to an identity the custom validator itself rejects ----

    public static async Task It_rejects_a_key_change_to_an_unknown_unique_id_on_the_data_validation_arm(
        ApiIntegrationHarness harness,
        UniqueIdServiceStub stub
    )
    {
        stub.Know(StudentResourceName, KeyChangeSourceUniqueId);

        (string locationPath, string etag) = await CreateStudentAsync(
            harness,
            KeyChangeSourceUniqueId,
            "Ada"
        );

        // KeyChangeUnknownTargetUniqueId is deliberately never taught to the stub, so the custom
        // validator rejects the document before DMS's own immutable-identity guard - which runs
        // later, in the handler - ever sees the key change.
        string id = IdFromLocation(locationPath);
        JsonObject payload = StudentBody(KeyChangeUnknownTargetUniqueId, "Ada", id);

        using HttpResponseMessage response = await PutAsync(harness, locationPath, etag, payload);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);

        using JsonDocument document = JsonDocument.Parse(body);

        // Exactly the plain arm, not the key-change subtype, which is the distinction this case
        // exists to prove: the custom validator's rejection reaches the client first. This helper
        // already asserts "type" equals the plain arm exactly, so a regression to the key-change
        // subtype would fail here.
        AssertRejectedUniqueId(document.RootElement, StudentResourceName, "studentUniqueId");
    }

    // ---- B7: a key change to an identity the custom validator accepts, but DMS itself refuses ----

    public static async Task It_rejects_a_key_change_to_a_known_unique_id_as_key_change_not_supported(
        ApiIntegrationHarness harness,
        UniqueIdServiceStub stub
    )
    {
        stub.Know(StudentResourceName, KeyChangeSourceUniqueId);
        stub.Know(StudentResourceName, KeyChangeKnownTargetUniqueId);

        (string locationPath, string etag) = await CreateStudentAsync(
            harness,
            KeyChangeSourceUniqueId,
            "Ada"
        );

        string id = IdFromLocation(locationPath);
        JsonObject payload = StudentBody(KeyChangeKnownTargetUniqueId, "Ada", id);

        using HttpResponseMessage response = await PutAsync(harness, locationPath, etag, payload);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        // The custom validator has nothing to say about either identity here - both are known - so
        // this is DMS's own guard, reached only because the validator let the write through.
        root.GetProperty("type").GetString().Should().Be(KeyChangeNotSupportedType);
        root.GetProperty("title").GetString().Should().Be(KeyChangeNotSupportedTitle);
        root.GetProperty("status").GetInt32().Should().Be(400);
    }

    // ---- B8a: the stub itself faults ----

    public static async Task It_fails_the_write_when_the_stub_answers_server_error(
        ApiIntegrationHarness harness,
        UniqueIdServiceStub stub
    )
    {
        stub.AnswerServerError = true;

        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            ServerErrorStudentUniqueId,
            "Uidv"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, body);

        stub.AnswerServerError = false;

        JsonArray students = await GetStudentsByUniqueIdAsync(harness, ServerErrorStudentUniqueId);
        students.Should().BeEmpty("a write the dependency faulted on must persist nothing");
    }

    // ---- B8b: the upstream is unreachable; shared by the sibling fixture with its own dead address ----

    public static async Task It_fails_the_write_when_the_upstream_is_unreachable(
        ApiIntegrationHarness harness
    )
    {
        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            UnreachableStudentUniqueId,
            "Uidv"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, body);

        JsonArray students = await GetStudentsByUniqueIdAsync(harness, UnreachableStudentUniqueId);
        students
            .Should()
            .BeEmpty("a write whose dependency could not be reached must persist nothing");
    }

    // ---- F1: the stub redirects instead of answering; the validator's HttpClient never follows it ----

    /// <summary>
    /// The stub answers every request with a 302 redirect while <see cref="UniqueIdServiceStub.RedirectMode"/>
    /// is on. The target it redirects to answers 200, so a client that followed it would read this
    /// write as a success; the validator's <c>HttpClient</c> is configured with
    /// <c>AllowAutoRedirect = false</c>, so it never reaches that target at all.
    /// </summary>
    public static async Task It_fails_the_write_when_the_stub_redirects(
        ApiIntegrationHarness harness,
        UniqueIdServiceStub stub
    )
    {
        stub.Know(StudentResourceName, RedirectStudentUniqueId);
        stub.RedirectMode = true;

        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            RedirectStudentUniqueId,
            "Uidv"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, body);

        stub.RedirectMode = false;

        JsonArray students = await GetStudentsByUniqueIdAsync(harness, RedirectStudentUniqueId);
        students
            .Should()
            .BeEmpty("a write the dependency redirected instead of answering must persist nothing");

        stub.RequestPaths.Should()
            .NotContain(
                path =>
                    path.Contains(UniqueIdServiceStub.RedirectTargetPath, StringComparison.Ordinal),
                "the validator's HttpClient must not follow the redirect"
            );
    }

    // ---- F5: BaseAddress carries a path with no trailing slash; the plugin normalizes it ----

    /// <summary>
    /// With <c>UniqueIdValidation:BaseAddress</c> configured as an address with a path and no
    /// trailing slash, the plugin's normalization appends one before building the client, so the
    /// request still lands under that path rather than replacing its last segment the way ordinary
    /// <see cref="Uri"/> combination rules would.
    /// </summary>
    public static async Task It_creates_a_student_when_the_base_address_has_a_path_and_no_trailing_slash(
        ApiIntegrationHarness harness,
        UniqueIdServiceStub stub
    )
    {
        stub.Know(StudentResourceName, TrailingSlashStudentUniqueId);

        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            TrailingSlashStudentUniqueId,
            "Uidv"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        stub.RequestPaths.Should()
            .Equal(
                $"/uid/{StudentResourceName}/{Uri.EscapeDataString(TrailingSlashStudentUniqueId)}"
            );
    }

    // ---- F6: no BaseAddress is configured at all ----

    /// <summary>
    /// With <c>UniqueIdValidation:BaseAddress</c> entirely absent, the plugin's client factory
    /// callback throws on every matching write, which the host answers as a logged 500 and persists
    /// nothing, the same shape as the stub's own 500 and the unreachable-upstream cases.
    /// </summary>
    public static async Task It_fails_the_write_when_no_base_address_is_configured(
        ApiIntegrationHarness harness
    )
    {
        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            MissingBaseAddressStudentUniqueId,
            "Uidv"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, body);

        JsonArray students = await GetStudentsByUniqueIdAsync(
            harness,
            MissingBaseAddressStudentUniqueId
        );
        students
            .Should()
            .BeEmpty("a write with no configured upstream address must persist nothing");
    }

    /// <summary>
    /// A non-person resource is untouched by the validator even when the configuration it would need
    /// is entirely absent, because this validator's <c>AppliesTo</c> filters it out before any client
    /// is ever asked for.
    /// </summary>
    public static async Task It_creates_a_non_person_resource_without_a_configured_base_address(
        ApiIntegrationHarness harness
    )
    {
        using HttpResponseMessage response = await PostNonPersonResourceAsync(harness);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    // ---- F7: the upstream is slow; the plugin's own configured timeout cuts the wait short ----

    /// <summary>
    /// With <c>UniqueIdValidation:Timeout</c> configured to one second and the stub delaying every
    /// answer by ten, the write fails well before either the stub's own delay or the client's
    /// 5-second default timeout would have elapsed, proving the configured value is what is in
    /// effect rather than the default.
    /// </summary>
    public static async Task It_fails_the_write_when_the_upstream_times_out(
        ApiIntegrationHarness harness,
        UniqueIdServiceStub stub
    )
    {
        stub.Know(StudentResourceName, SlowUpstreamStudentUniqueId);
        stub.ResponseDelay = TimeSpan.FromSeconds(10);

        Stopwatch stopwatch = Stopwatch.StartNew();
        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            SlowUpstreamStudentUniqueId,
            "Uidv"
        );
        stopwatch.Stop();
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, body);

        stub.ResponseDelay = TimeSpan.Zero;

        stopwatch
            .Elapsed.Should()
            .BeLessThan(
                TimeSpan.FromSeconds(3),
                "the configured 1-second timeout, not the 5-second default, must be what cuts the wait short"
            );
        stopwatch
            .Elapsed.Should()
            .BeGreaterThanOrEqualTo(
                TimeSpan.FromMilliseconds(900),
                "a wait far shorter than the configured 1-second timeout would mean the timeout never "
                    + "actually applied"
            );

        stub.RequestPaths.Should().Equal($"/{StudentResourceName}/{SlowUpstreamStudentUniqueId}");

        JsonArray students = await GetStudentsByUniqueIdAsync(harness, SlowUpstreamStudentUniqueId);
        students
            .Should()
            .BeEmpty("a write the dependency never answered in time must persist nothing");
    }

    // ---- B9: a UniqueId with characters that need escaping reaches the stub as one path segment ----

    public static async Task It_escapes_a_unique_id_containing_reserved_characters(
        ApiIntegrationHarness harness,
        UniqueIdServiceStub stub
    )
    {
        stub.Know(StudentResourceName, SpecialCharactersUniqueId);

        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            SpecialCharactersUniqueId,
            "Uidv"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        stub.RequestPaths.Should()
            .Equal($"/{StudentResourceName}/{Uri.EscapeDataString(SpecialCharactersUniqueId)}");
        stub.RequestPaths[0].Should().Contain("%2F").And.Contain("%20").And.Contain("%25");
    }

    // ---- B10: the negative control - the plugin is not allowlisted, so B2's request succeeds ----

    /// <summary>
    /// With the plugin's name absent from the allowlist and nothing else changed, the same POST
    /// B2's Student case sends succeeds, and the stub that would have answered it saw nothing.
    /// </summary>
    /// <remarks>
    /// Built from <see cref="UnknownStudentUniqueId"/> and <see cref="UnknownStudentFirstName"/>, the
    /// same definition the allowlisted class's unknown-id case posts, so the two cases serialize the
    /// same bytes. That is what makes this a control on <c>Plugins:Allowed</c> rather than on the
    /// document. With the plugin not allowlisted it is never loaded, so it never calls out to the
    /// stub at all - the stub is started only so every other host setting can be repeated verbatim.
    /// </remarks>
    public static async Task It_accepts_the_failing_post_when_the_plugin_is_not_allowlisted(
        ApiIntegrationHarness harness,
        UniqueIdServiceStub stub
    )
    {
        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            UnknownStudentUniqueId,
            UnknownStudentFirstName
        );
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.Created,
                $"the byte-identical request is refused only when the plugin is allowlisted: {body}"
            );
        stub.RequestPaths.Should().BeEmpty("the plugin is never loaded when it is not allowlisted");
    }

    // ---- G1: a validated write never puts the submitted UniqueId into the host's own logs ----

    /// <summary>
    /// Guards the plugin's own <c>RemoveAllLoggers()</c> call on its named <c>HttpClient</c>: without
    /// it, the factory's default HttpClient logging would have written the lookup request's URI - which
    /// carries the UniqueId in its path - to the host's own logs at Information level.
    /// </summary>
    /// <remarks>
    /// Two things make this an honest proof rather than a vacuous one: <see cref="UniqueIdServiceStub.RequestPaths"/>
    /// is asserted to hold exactly the lookup this case's write should have made, so the id really was
    /// looked up over HTTP, and <paramref name="capture"/> is asserted to have captured at least one
    /// event, so an empty capture - which would trivially contain nothing - cannot pass this case.
    /// </remarks>
    public static async Task It_does_not_log_the_submitted_unique_id(
        ApiIntegrationHarness harness,
        UniqueIdServiceStub stub,
        PluginLogCapture capture
    )
    {
        stub.Know(StudentResourceName, LoggingProofStudentUniqueId);

        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            LoggingProofStudentUniqueId,
            "Uidv"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        stub.RequestPaths.Should().Equal($"/{StudentResourceName}/{LoggingProofStudentUniqueId}");

        IReadOnlyList<LogEvent> events = capture.Events;
        events
            .Should()
            .NotBeEmpty("the host must have logged something else while handling this request");
        events
            .Should()
            .NotContain(
                logEvent => LogEventMentions(logEvent, LoggingProofStudentUniqueId),
                "the submitted UniqueId must never reach the host's logs"
            );
    }

    /// <summary>Whether an event's rendered message, its exception, or any of its properties carry <paramref name="text"/>.</summary>
    /// <remarks>
    /// Properties are checked as well as the rendered message because a property can be attached to an
    /// event by an enricher without appearing in that event's own message template, so a check of the
    /// rendered message alone could miss a leak.
    /// </remarks>
    private static bool LogEventMentions(LogEvent logEvent, string text) =>
        PluginLogCapture.TextOf(logEvent).Contains(text, StringComparison.Ordinal)
        || logEvent.Properties.Values.Any(value =>
            value.ToString().Contains(text, StringComparison.Ordinal)
        );

    private static void AssertRejectedUniqueId(JsonElement root, string resourceName, string member)
    {
        root.GetProperty("type").GetString().Should().Be(DataValidationFailedType);
        root.GetProperty("title").GetString().Should().Be(DataValidationFailedTitle);
        root.GetProperty("status").GetInt32().Should().Be(400);

        JsonElement validationErrors = root.GetProperty("validationErrors");
        validationErrors.ValueKind.Should().Be(JsonValueKind.Object);
        validationErrors
            .EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo($"$.{member}");

        validationErrors
            .GetProperty($"$.{member}")
            .EnumerateArray()
            .Select(message => message.GetString())
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be($"The {resourceName} unique id was not found in the external unique id system.");
    }

    private static JsonObject StudentBody(
        string studentUniqueId,
        string firstName,
        string? id = null
    )
    {
        var body = new JsonObject();
        if (id is not null)
        {
            body["id"] = id;
        }
        body["studentUniqueId"] = studentUniqueId;
        body["firstName"] = firstName;
        body["lastSurname"] = StudentLastSurname;
        body["birthDate"] = StudentBirthDate;
        return body;
    }

    private static JsonObject StaffBody(string staffUniqueId, string firstName) =>
        new()
        {
            ["staffUniqueId"] = staffUniqueId,
            ["firstName"] = firstName,
            ["lastSurname"] = StaffLastSurname,
        };

    private static JsonObject ContactBody(string contactUniqueId, string firstName) =>
        new()
        {
            ["contactUniqueId"] = contactUniqueId,
            ["firstName"] = firstName,
            ["lastSurname"] = ContactLastSurname,
        };

    private static JsonObject NonPersonBody() =>
        new()
        {
            ["codeValue"] = "UidvNonPerson",
            ["description"] = "UidvNonPerson",
            ["namespace"] = NonPersonDescriptorNamespace,
            ["shortDescription"] = "UidvNonPerson",
        };

    // Awaited rather than returned as a Task: the content has to outlive the request, and returning
    // the task from inside a using block disposes it while the request is still in flight.
    private static async Task<HttpResponseMessage> PostStudentAsync(
        ApiIntegrationHarness harness,
        string studentUniqueId,
        string firstName
    )
    {
        using var content = JsonContentOf(StudentBody(studentUniqueId, firstName));
        return await harness.HttpClient.PostAsync(StudentsEndpoint, content);
    }

    private static async Task<HttpResponseMessage> PostStaffAsync(
        ApiIntegrationHarness harness,
        string staffUniqueId,
        string firstName
    )
    {
        using var content = JsonContentOf(StaffBody(staffUniqueId, firstName));
        return await harness.HttpClient.PostAsync(StaffsEndpoint, content);
    }

    private static async Task<HttpResponseMessage> PostContactAsync(
        ApiIntegrationHarness harness,
        string contactUniqueId,
        string firstName
    )
    {
        using var content = JsonContentOf(ContactBody(contactUniqueId, firstName));
        return await harness.HttpClient.PostAsync(ContactsEndpoint, content);
    }

    private static async Task<HttpResponseMessage> PostNonPersonResourceAsync(
        ApiIntegrationHarness harness
    )
    {
        using var content = JsonContentOf(NonPersonBody());
        return await harness.HttpClient.PostAsync(NonPersonEndpoint, content);
    }

    private static async Task<HttpResponseMessage> PutAsync(
        ApiIntegrationHarness harness,
        string locationPath,
        string etag,
        JsonObject payload
    )
    {
        using var content = JsonContentOf(payload);
        using var request = new HttpRequestMessage(HttpMethod.Put, locationPath)
        {
            Content = content,
        };
        // If-Match is a request header, not a content header; setting it on StringContent.Headers
        // would silently no-op and the update would be refused for a reason this case is not about.
        request.Headers.TryAddWithoutValidation("If-Match", etag);

        return await harness.HttpClient.SendAsync(request);
    }

    private static async Task<(string LocationPath, string ETag)> CreateStudentAsync(
        ApiIntegrationHarness harness,
        string studentUniqueId,
        string firstName
    )
    {
        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            studentUniqueId,
            firstName
        );
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(HttpStatusCode.Created, $"the arrange step must create the document: {body}");
        response.TryReadRawEtag(out string etag).Should().BeTrue("a POST create must emit an ETag");

        return (PathOf(response), etag);
    }

    private static async Task<string> ReadFirstNameAsync(
        ApiIntegrationHarness harness,
        string locationPath
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(locationPath);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        return JsonNode.Parse(body)!.AsObject()["firstName"]!.GetValue<string>();
    }

    /// <summary>
    /// How many Students carry a UniqueId, read back over HTTP by the resource's own query field.
    /// </summary>
    private static async Task<JsonArray> GetStudentsByUniqueIdAsync(
        ApiIntegrationHarness harness,
        string studentUniqueId
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            $"{StudentsEndpoint}?studentUniqueId={Uri.EscapeDataString(studentUniqueId)}"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        return JsonNode.Parse(body)!.AsArray();
    }

    private static string PathOf(HttpResponseMessage response) =>
        response.Headers.Location!.IsAbsoluteUri
            ? response.Headers.Location!.AbsolutePath
            : response.Headers.Location!.OriginalString;

    private static string IdFromLocation(string locationPath) => locationPath.Split('/')[^1];

    private static StringContent JsonContentOf(JsonObject payload) =>
        new(payload.ToJsonString(), Encoding.UTF8, JsonContentType);
}
