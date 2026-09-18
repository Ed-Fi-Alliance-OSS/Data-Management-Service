// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Drives the custom validation proof plugin over real HTTP against the composed write pipeline and
/// the leased database.
/// </summary>
/// <remarks>
/// <para>
/// Every assertion here is about an HTTP status code and a JSON body, never about internal pipeline
/// state, because what this proves is what a client of a real deployment observes. The validator
/// reaches the host the way a real one does: a published plugin directory named in
/// <c>Plugins:Allowed</c>.
/// </para>
/// <para>
/// Hard-coded for the ProfileRootOnlyMerge fixture's shapes: project endpoint <c>ed-fi</c>,
/// <c>students</c> requiring <c>studentUniqueId</c> and <c>firstName</c>, and
/// <c>profileRootOnlyMergeItems</c> requiring only the integer <c>profileRootOnlyMergeItemId</c>
/// and declaring an optional string <c>displayName</c>.
/// </para>
/// </remarks>
internal static class CustomValidationPluginScenario
{
    /// <summary>The staged plugin directory name, which is also the plugin's own Name.</summary>
    public const string PluginName = "Acme.CustomValidationProof";

    /// <summary>The value the fixture validator answers with a path-level failure.</summary>
    public const string RejectOnPathToken = "custom-validation-proof-reject-path";

    /// <summary>The value the fixture validator answers with a resource-level failure.</summary>
    public const string RejectOnResourceToken = "custom-validation-proof-reject-resource";

    /// <summary>The identity every case that sends the failing POST uses.</summary>
    /// <remarks>
    /// One definition rather than a value per case, and that is what the negative control rests on:
    /// the allowlisted class and the disabled class have to put the same bytes on the wire, so that
    /// the only difference between a request that is refused and one that is accepted is
    /// <c>Plugins:Allowed</c>. A per-case identity would have changed the document as well as the
    /// setting and proved nothing. Every case leases its own database, so one identity reused across
    /// them cannot collide.
    /// </remarks>
    private const string FailingStudentUniqueId = "cvp-failing-student-001";

    /// <summary>The identity both PUT cases create before they update it.</summary>
    private const string PutTargetStudentUniqueId = "cvp-put-target-001";

    /// <summary>The stored value a rejected PUT must leave alone.</summary>
    private const string StoredFirstName = "Grace";

    /// <summary>An ordinary value, which is all the passing control differs by.</summary>
    private const string PassingFirstName = "Ada";

    /// <summary>The identity of the document core schema validation refuses in the parity case.</summary>
    private const string SchemaInvalidStudentUniqueId = "cvp-schema-invalid-001";

    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string ItemsEndpoint = "/data/ed-fi/profileRootOnlyMergeItems";
    private const string JsonContentType = "application/json";

    /// <summary>The <c>validationErrors</c>-arm members a write-path 400 carries.</summary>
    private const string ValidationErrorsArmDetail =
        "Data validation failed. See 'validationErrors' for details.";
    private const string ValidationErrorsArmType = "urn:ed-fi:api:bad-request:data-validation-failed";
    private const string ValidationErrorsArmTitle = "Data Validation Failed";

    /// <summary>The <c>errors</c>-arm members a write-path 400 carries.</summary>
    private const string ErrorsArmDetail = "The request could not be processed. See 'errors' for details.";
    private const string ErrorsArmType = "urn:ed-fi:api:bad-request";
    private const string ErrorsArmTitle = "Bad Request";

    /// <summary>
    /// Every member a DMS write-path 400 body carries, which is what the parity criterion compares
    /// as a set before it compares any value.
    /// </summary>
    private static readonly string[] _clientVisibleMembers =
    [
        "detail",
        "type",
        "title",
        "status",
        "correlationId",
        "validationErrors",
        "errors",
    ];

    /// <summary>
    /// A POST whose document the validator rejects on a path returns the documented 400.
    /// </summary>
    public static async Task It_rejects_a_matching_post_on_the_validation_errors_arm(
        ApiIntegrationHarness harness
    )
    {
        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            FailingStudentUniqueId,
            RejectOnPathToken
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be(JsonContentType);

        using JsonDocument document = JsonDocument.Parse(body);

        ShouldCarryThePathLevelRejection(document.RootElement);

        // The rejection happens ahead of the backend, so the write never reached the database.
        (await StudentCountAsync(harness, FailingStudentUniqueId))
            .Should()
            .Be(0, "a rejected write must persist nothing");
    }

    /// <summary>
    /// A POST whose document the validator rejects at the document level returns the other arm.
    /// </summary>
    public static async Task It_rejects_a_matching_post_on_the_errors_arm(ApiIntegrationHarness harness)
    {
        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            FailingStudentUniqueId,
            RejectOnResourceToken
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        MemberNamesOf(root).Should().BeEquivalentTo(_clientVisibleMembers);

        // A different factory from the path arm, which is the distinction this case exists for.
        root.GetProperty("detail").GetString().Should().Be(ErrorsArmDetail);
        root.GetProperty("type").GetString().Should().Be(ErrorsArmType);
        root.GetProperty("title").GetString().Should().Be(ErrorsArmTitle);
        root.GetProperty("status").GetInt32().Should().Be(400);

        JsonElement errors = root.GetProperty("errors");
        errors.ValueKind.Should().Be(JsonValueKind.Array);
        errors
            .EnumerateArray()
            .Select(error => error.GetString())
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("document-level rejection token");

        JsonElement validationErrors = root.GetProperty("validationErrors");
        validationErrors.ValueKind.Should().Be(JsonValueKind.Object);
        validationErrors
            .EnumerateObject()
            .Should()
            .BeEmpty("a document-level failure leaves the other arm empty rather than absent");

        (await StudentCountAsync(harness, FailingStudentUniqueId)).Should().Be(0);
    }

    /// <summary>
    /// The same POST with an ordinary value is not blocked, which is what makes the rejection above
    /// evidence of a rule rather than of a broken write path.
    /// </summary>
    public static async Task It_accepts_a_matching_post_that_passes(ApiIntegrationHarness harness)
    {
        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            FailingStudentUniqueId,
            PassingFirstName
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        response.Headers.Location.Should().NotBeNull();

        using HttpResponseMessage read = await harness.HttpClient.GetAsync(PathOf(response));
        string readBody = await read.Content.ReadAsStringAsync();

        read.StatusCode.Should().Be(HttpStatusCode.OK, readBody);
        JsonNode.Parse(readBody)!.AsObject()["firstName"]!.GetValue<string>().Should().Be(PassingFirstName);
    }

    /// <summary>
    /// The update pipeline runs the same validator, and a rejected PUT leaves what is stored alone.
    /// </summary>
    public static async Task It_rejects_a_matching_put_and_leaves_the_stored_document_intact(
        ApiIntegrationHarness harness
    )
    {
        (string locationPath, string etag) = await CreateStudentAsync(
            harness,
            PutTargetStudentUniqueId,
            StoredFirstName
        );

        JsonObject payload = FailingPutPayload(locationPath);

        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, JsonContentType);
        using var request = new HttpRequestMessage(HttpMethod.Put, locationPath) { Content = content };
        // If-Match is a request header, not a content header; setting it on StringContent.Headers
        // would silently no-op and the update would be refused for a reason this case is not about.
        request.Headers.TryAddWithoutValidation("If-Match", etag);

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);

        using JsonDocument document = JsonDocument.Parse(body);

        // The same assertion the POST case makes, against the same rejected value, because "the
        // update pipeline surfaces a custom failure the same way the upsert one does" is a claim
        // about the whole body rather than about one member name being present.
        ShouldCarryThePathLevelRejection(document.RootElement);

        using HttpResponseMessage read = await harness.HttpClient.GetAsync(locationPath);
        string readBody = await read.Content.ReadAsStringAsync();

        read.StatusCode.Should().Be(HttpStatusCode.OK, readBody);
        JsonNode.Parse(readBody)!.AsObject()["firstName"]!
            .GetValue<string>()
            .Should()
            .Be(StoredFirstName, "a rejected update must not have changed what is stored");
    }

    /// <summary>
    /// A resource the validator does not declare is untouched, even by a document carrying the
    /// rejection token.
    /// </summary>
    /// <remarks>
    /// The token is what makes this a real control. The fixture's rule is resource-agnostic - it
    /// rejects the token wherever it finds it among the document's top-level string properties - so
    /// <c>AppliesTo</c> is the only thing that can keep this request from being refused. If fan-in
    /// filtering regressed and ran every validator for every resource, this exact POST would start
    /// returning 400. A document with no token could not tell the two worlds apart.
    /// </remarks>
    public static async Task It_leaves_a_non_matching_resource_alone(ApiIntegrationHarness harness)
    {
        // The validator is live in this very host, proven against the resource it does declare.
        // Without this the case would pass just as well against a host where the plugin failed to
        // load, which is the one way a proof about applicability can be satisfied for the wrong
        // reason: each test boots its own host, so the other cases say nothing about this one's.
        using HttpResponseMessage matching = await PostStudentAsync(
            harness,
            "cvp-applies-to-001",
            RejectOnPathToken
        );

        matching
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest, await matching.Content.ReadAsStringAsync());

        using HttpResponseMessage carryingTheToken = await PostMergeItemAsync(
            harness,
            itemId: 14360,
            displayName: RejectOnPathToken
        );
        string carryingBody = await carryingTheToken.Content.ReadAsStringAsync();

        carryingTheToken
            .StatusCode.Should()
            .Be(
                HttpStatusCode.Created,
                $"the validator declares only Ed-Fi/Student, so this document is never shown to it: {carryingBody}"
            );

        using HttpResponseMessage ordinary = await PostMergeItemAsync(
            harness,
            itemId: 14361,
            displayName: "an ordinary display name"
        );
        string ordinaryBody = await ordinary.Content.ReadAsStringAsync();

        ordinary.StatusCode.Should().Be(HttpStatusCode.Created, ordinaryBody);
    }

    /// <summary>
    /// The fixture's 400 and core schema validation's 400 are the same response to a client.
    /// </summary>
    /// <remarks>
    /// Asserted over real HTTP against one running host rather than in a unit test, so a divergence
    /// introduced anywhere in the composed stack is caught. The four members compared for equality
    /// are compared as raw JSON text rather than as coerced values, which is what "byte-identical"
    /// means here, and each is also compared to its literal expected value so the case fails both
    /// when the two bodies diverge and when they drift together.
    /// Two members are deliberately not compared for equality, and both are named rather than
    /// quietly skipped. <c>correlationId</c> is the request's own trace id and cannot be equal
    /// across two requests. The <c>validationErrors</c> messages differ because a custom rule says
    /// something a schema cannot; what is compared there is the arm's shape, on both bodies.
    /// </remarks>
    public static async Task It_matches_the_core_schema_validation_400(ApiIntegrationHarness harness)
    {
        using HttpResponseMessage custom = await PostStudentAsync(
            harness,
            FailingStudentUniqueId,
            RejectOnPathToken
        );
        string customBody = await custom.Content.ReadAsStringAsync();

        // Schema-invalid rather than rule-invalid: firstName is required and absent, so this never
        // reaches the custom validator and is answered by DocumentValidator.
        var schemaInvalid = new JsonObject { ["studentUniqueId"] = SchemaInvalidStudentUniqueId };
        using var content = new StringContent(schemaInvalid.ToJsonString(), Encoding.UTF8, JsonContentType);
        using HttpResponseMessage core = await harness.HttpClient.PostAsync(StudentsEndpoint, content);
        string coreBody = await core.Content.ReadAsStringAsync();

        custom.StatusCode.Should().Be(HttpStatusCode.BadRequest, customBody);
        core.StatusCode.Should().Be(HttpStatusCode.BadRequest, coreBody);
        custom
            .Content.Headers.ContentType?.MediaType.Should()
            .Be(core.Content.Headers.ContentType?.MediaType);

        using JsonDocument customDocument = JsonDocument.Parse(customBody);
        using JsonDocument coreDocument = JsonDocument.Parse(coreBody);
        JsonElement customRoot = customDocument.RootElement;
        JsonElement coreRoot = coreDocument.RootElement;

        // The member set first: a member added to one body and not the other is a divergence no
        // per-member comparison below would notice.
        MemberNamesOf(customRoot).Should().BeEquivalentTo(_clientVisibleMembers);
        MemberNamesOf(coreRoot).Should().BeEquivalentTo(_clientVisibleMembers);

        foreach (string member in new[] { "detail", "type", "title", "status" })
        {
            customRoot
                .GetProperty(member)
                .GetRawText()
                .Should()
                .Be(
                    coreRoot.GetProperty(member).GetRawText(),
                    $"a client cannot tell a custom-validation 400 from a core-validation 400 by its '{member}'"
                );
        }

        customRoot.GetProperty("detail").GetString().Should().Be(ValidationErrorsArmDetail);
        customRoot.GetProperty("type").GetString().Should().Be(ValidationErrorsArmType);
        customRoot.GetProperty("title").GetString().Should().Be(ValidationErrorsArmTitle);
        customRoot.GetProperty("status").GetInt32().Should().Be(400);

        foreach (JsonElement root in new[] { customRoot, coreRoot })
        {
            root.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace();
            root.GetProperty("validationErrors").ValueKind.Should().Be(JsonValueKind.Object);
            root.GetProperty("validationErrors").EnumerateObject().Should().NotBeEmpty();
            root.GetProperty("errors").ValueKind.Should().Be(JsonValueKind.Array);
            root.GetProperty("errors").GetArrayLength().Should().Be(0);
        }

        customRoot
            .GetProperty("correlationId")
            .GetString()
            .Should()
            .NotBe(
                coreRoot.GetProperty("correlationId").GetString(),
                "the correlation id is the request's own trace id, which is why it is excluded from "
                    + "the equality comparison rather than overlooked"
            );
    }

    /// <summary>
    /// With the plugin's name absent from the allowlist and nothing else changed, the same failing
    /// POST succeeds.
    /// </summary>
    /// <remarks>
    /// The request is built from the same identity and the same rejected value as
    /// <see cref="It_rejects_a_matching_post_on_the_validation_errors_arm"/> and goes to the same
    /// endpoint, so the two cases serialize the same bytes. That is what makes this a control on
    /// <c>Plugins:Allowed</c> rather than on the document.
    /// </remarks>
    public static async Task It_accepts_the_failing_post_when_the_plugin_is_not_allowlisted(
        ApiIntegrationHarness harness
    )
    {
        using HttpResponseMessage response = await PostStudentAsync(
            harness,
            FailingStudentUniqueId,
            RejectOnPathToken
        );
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.Created,
                $"the byte-identical request is refused only when the plugin is allowlisted: {body}"
            );
        (await StudentCountAsync(harness, FailingStudentUniqueId)).Should().Be(1);
    }

    /// <summary>The same for the update pipeline.</summary>
    /// <remarks>
    /// Built from <see cref="FailingPutPayload"/>, the same definition the allowlisted PUT case
    /// uses, so every client-controlled member matches. The document's <c>id</c> and the If-Match
    /// ETag necessarily differ, because each case leases its own database and both values are
    /// assigned by the server rather than chosen by the client.
    /// </remarks>
    public static async Task It_accepts_the_failing_put_when_the_plugin_is_not_allowlisted(
        ApiIntegrationHarness harness
    )
    {
        (string locationPath, string etag) = await CreateStudentAsync(
            harness,
            PutTargetStudentUniqueId,
            StoredFirstName
        );

        JsonObject payload = FailingPutPayload(locationPath);

        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, JsonContentType);
        using var request = new HttpRequestMessage(HttpMethod.Put, locationPath) { Content = content };
        request.Headers.TryAddWithoutValidation("If-Match", etag);

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, body);

        using HttpResponseMessage read = await harness.HttpClient.GetAsync(locationPath);
        JsonNode.Parse(await read.Content.ReadAsStringAsync())!.AsObject()["firstName"]!
            .GetValue<string>()
            .Should()
            .Be(RejectOnPathToken, "with no validator registered the token is ordinary text");
    }

    /// <summary>
    /// The whole 400 body a path-level rejection produces, asserted the same way wherever it
    /// arrives.
    /// </summary>
    /// <remarks>
    /// Shared by the POST and the PUT case rather than written twice, because the story's claim is
    /// that both pipelines answer with the <em>same</em> shape: two assertions written separately
    /// can agree that a member exists while disagreeing about everything inside it. Every member is
    /// read to a value, so an empty array, a null, or a message from some other rule fails here.
    /// </remarks>
    private static void ShouldCarryThePathLevelRejection(JsonElement root)
    {
        MemberNamesOf(root).Should().BeEquivalentTo(_clientVisibleMembers);
        root.GetProperty("detail").GetString().Should().Be(ValidationErrorsArmDetail);
        root.GetProperty("type").GetString().Should().Be(ValidationErrorsArmType);
        root.GetProperty("title").GetString().Should().Be(ValidationErrorsArmTitle);
        root.GetProperty("status").GetInt32().Should().Be(400);
        root.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace();

        JsonElement validationErrors = root.GetProperty("validationErrors");
        validationErrors.ValueKind.Should().Be(JsonValueKind.Object);
        validationErrors
            .TryGetProperty("$.firstName", out JsonElement messages)
            .Should()
            .BeTrue("the fixture reported its failure against that path");
        messages.ValueKind.Should().Be(JsonValueKind.Array);
        messages
            .EnumerateArray()
            .Select(message => message.GetString())
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("reserved rejection token");

        JsonElement errors = root.GetProperty("errors");
        errors.ValueKind.Should().Be(JsonValueKind.Array);
        errors
            .GetArrayLength()
            .Should()
            .Be(0, "a path-level failure leaves the other arm empty rather than absent");
    }

    /// <summary>The one failing PUT body, shared by the allowlisted and the disabled case.</summary>
    /// <remarks>
    /// Every client-controlled member is identical between the two: the identity, the rejected
    /// value, and the endpoint. <paramref name="locationPath"/> supplies <c>id</c>, which is
    /// server-assigned and therefore differs between two isolated databases, as does the If-Match
    /// ETag the caller sends with it. Those two are the only differences, and they are not
    /// differences a client chooses.
    /// </remarks>
    private static JsonObject FailingPutPayload(string locationPath) =>
        new()
        {
            ["id"] = locationPath.Split('/')[^1],
            ["studentUniqueId"] = PutTargetStudentUniqueId,
            ["firstName"] = RejectOnPathToken,
        };

    // Awaited rather than returned as a Task: the content has to outlive the request, and returning
    // the task from inside a using block disposes it while the request is still in flight.
    private static async Task<HttpResponseMessage> PostStudentAsync(
        ApiIntegrationHarness harness,
        string uniqueId,
        string firstName
    )
    {
        var payload = new JsonObject { ["studentUniqueId"] = uniqueId, ["firstName"] = firstName };
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, JsonContentType);

        return await harness.HttpClient.PostAsync(StudentsEndpoint, content);
    }

    private static async Task<HttpResponseMessage> PostMergeItemAsync(
        ApiIntegrationHarness harness,
        int itemId,
        string displayName
    )
    {
        var payload = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = itemId,
            ["displayName"] = displayName,
        };
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, JsonContentType);

        return await harness.HttpClient.PostAsync(ItemsEndpoint, content);
    }

    private static async Task<(string LocationPath, string ETag)> CreateStudentAsync(
        ApiIntegrationHarness harness,
        string uniqueId,
        string firstName
    )
    {
        using HttpResponseMessage response = await PostStudentAsync(harness, uniqueId, firstName);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(HttpStatusCode.Created, $"the arrange step must create the document: {body}");
        response.TryReadRawEtag(out string etag).Should().BeTrue("a POST create must emit an ETag");

        return (PathOf(response), etag);
    }

    /// <summary>How many Students carry an identity, read back over HTTP.</summary>
    /// <remarks>
    /// The whole collection is fetched and filtered here rather than queried by
    /// <c>studentUniqueId</c>, because this fixture's schema declares no query field of that name
    /// and the API answers such a request with its own 400. Each test leases its own database, so
    /// the collection holds only what that test wrote.
    /// </remarks>
    private static async Task<int> StudentCountAsync(ApiIntegrationHarness harness, string uniqueId)
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(StudentsEndpoint);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        return JsonNode
            .Parse(body)!
            .AsArray()
            .Count(student =>
                string.Equals(
                    student?["studentUniqueId"]?.GetValue<string>(),
                    uniqueId,
                    StringComparison.Ordinal
                )
            );
    }

    private static string PathOf(HttpResponseMessage response) =>
        response.Headers.Location!.IsAbsoluteUri
            ? response.Headers.Location!.AbsolutePath
            : response.Headers.Location!.OriginalString;

    private static IEnumerable<string> MemberNamesOf(JsonElement body) =>
        body.EnumerateObject().Select(member => member.Name);
}
