// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Identity;

/// <summary>
/// The contract a third-party implementer provides to back the five DMS identity operations:
/// <c>CreateAsync</c>, <c>GetByIdAsync</c>, <c>FindAsync</c>, <c>SearchAsync</c>, and
/// <c>ResultsAsync</c>.
/// <para>
/// <b>Lifetime and resolution.</b> All three service lifetimes - singleton, scoped, and transient -
/// are supported. DMS resolves the registered provider once per request, from its own per-request
/// scope, and never disposes the instance itself: a scoped or transient registration is disposed
/// with the request scope, and a singleton lives for the process.
/// The same resolved instance serves both the <see cref="Capabilities"/> gate and the invocation for
/// that request, so a provider can never observe a capability set that differs from the one its call
/// was gated on. A provider must be safe for concurrent calls across requests - a singleton or
/// transient registration gives DMS no per-request isolation, and even a scoped registration, though
/// called at most once per request, may share captured dependencies with other concurrent requests.
/// </para>
/// <para>
/// <b>Namespace authorization.</b> Every operation - create, get, find, search, and results - requires
/// the provider to authorize the authenticated <see cref="IdentityRequestContext.ClientId"/> against
/// its own namespace-access policy before doing any identity work; host tenant binding and
/// service-claim authorization are necessary but not sufficient. The provider documents its policy
/// source (for example deployment-managed client/namespace grants or an upstream authorization
/// service), how grants are administered, which operations they permit, and their cache/invalidation
/// behavior. A missing or denied grant, and an unknown namespace, both return
/// <see cref="IdentityResultStatus.NotFound"/> without exposing namespace or person details; a
/// policy-source failure must not become an implicit grant and instead throws, producing the sanitized
/// upstream-failure problem described below. A deployment may configure an explicit tenant-wide grant
/// for clients already authorized by the host for the relevant action, but that grant must be explicit
/// and documented - it is never inferred from the tenant's existence or the absence of client-specific
/// rules - and it never waives the per-job ownership check <c>ResultsAsync</c> applies. This requires
/// no datastore entitlement system and no additional DMS authorization API.
/// </para>
/// <para>
/// <b>Failure and cancellation.</b> Failures of an operation call to obtain an answer are signaled by
/// throwing, never by a result status: DMS logs the exception and returns a sanitized <c>502</c>
/// identity-upstream-failure problem with no provider detail in the client response, and establishes
/// no terminal job state. This is distinct from resolving, constructing, or activating the provider
/// itself, and from reading <see cref="Capabilities"/>: a failure at either of those returns a
/// sanitized <c>500</c> identity-provider-configuration problem instead, and no operation is invoked.
/// Request cancellation observed before or during activation, the capability read, or an invocation
/// propagates as an <see cref="OperationCanceledException"/> with no replacement problem response, and
/// the host restricts its own full exception and message logging for all of these stages to
/// <c>Debug</c>. That restriction binds only what the host itself writes. A plugin runs fully trusted
/// inside the host process, so this contract cannot constrain what the provider logs on its own
/// account; keeping provider detail out of operator-visible logs at higher levels is the provider's
/// obligation, not something the host can enforce.
/// </para>
/// <para>
/// <b>Timeout and retry.</b> The identity pipeline wraps no resilience pipeline, so DMS never retries
/// any call on this interface and imposes no timeout of its own on one. That applies to every
/// operation below, not only to issuance. A provider owns its upstream timeouts, and one it does not
/// handle itself surfaces as a thrown exception and therefore as the sanitized <c>502</c> described
/// above. Where an operation below notes that a client may retry it, that means the client reissuing
/// the HTTP request; it never means DMS re-invoking the provider on its own account.
/// <c>CreateAsync</c> documents what this rule costs when an issuance response is lost.
/// </para>
/// <para>
/// <b>Request and response payloads.</b> DMS treats these payloads as opaque JSON and validates
/// nothing about their contents at runtime, but their shape is defined rather than provider-chosen:
/// the deployment's OpenAPI document pins it, so a provider returning a different shape serves a
/// response that does not conform to the API it is backing. DMS does reject duplicate property names
/// and structurally malformed arrays before invoking a provider.
/// </para>
/// <para>
/// Create and search request objects, and every returned identity, share one set of standard
/// identifying properties, all of them nullable: <c>LastSurname</c>, <c>FirstName</c>,
/// <c>MiddleName</c>, <c>GenerationCodeSuffix</c>, <c>SexType</c>, <c>BirthDate</c> as a
/// <c>date-time</c> string, <c>BirthOrder</c> as an integer, and <c>BirthLocation</c> as an object
/// with nullable <c>City</c>, <c>StateAbbreviation</c>, <c>InternationalProvince</c>, and
/// <c>Country</c> string children.
/// </para>
/// <para>
/// On a request, a client expresses an unknown value either as <c>null</c> or by omitting the
/// property, and a provider should treat the two alike unless its own validation requires the field;
/// a request never carries <c>UniqueId</c> or <c>Score</c>. DMS does not require the standard
/// properties, validate their values, or reject custom ones, so the provider decides which its
/// integration needs and answers <see cref="IdentityResultStatus.InvalidProperties"/> when the
/// supplied data is insufficient.
/// </para>
/// <para>
/// On a response, a returned identity is an <c>IdentityResponse</c> object carrying every standard
/// property above plus a required non-empty <c>UniqueId</c> and a required <c>Score</c>. A provider
/// represents a standard attribute its upstream system does not support as <c>null</c> rather than
/// omitting the property, and still sends <c>BirthLocation</c> itself with null children when
/// birth-location data is unsupported. <c>Score</c> is the confidence indicator: null for an identity
/// returned outside a search, and a number from 0 through 100 for every search match. DMS neither
/// reads <c>Score</c> nor applies any threshold to it.
/// </para>
/// <para>
/// A provider may add custom properties to request objects, to <c>IdentityResponse</c>, and to
/// <c>BirthLocation</c>; DMS passes them through without inspecting them.
/// <see cref="IdentityResult.Payload"/> and <see cref="IdentityAsyncResult.Payload"/> document which
/// of these shapes each operation and status must carry.
/// </para>
/// </summary>
public interface IIdentityService
{
    /// <summary>
    /// The identity operations this registration supports. See <see cref="IdentityCapabilities"/> for
    /// the getter's own inexpensive, no-I/O, stable-per-deployment contract.
    /// DMS reads this getter once per request, immediately after provider activation and before any
    /// operation is invoked, and gates the requested route's operation against the captured value: an
    /// unsupported operation returns operation-unsupported <c>404</c> before body parsing, and no
    /// method below is called. The same captured value also gates whether a token returned by
    /// <c>FindAsync</c> or <c>SearchAsync</c> may later be redeemed through <c>ResultsAsync</c>, via
    /// <see cref="IdentityCapabilities.Results"/>; a provider returning a
    /// <see cref="IdentityAsyncResult.RequestToken"/> while that capability is absent is provider
    /// contract misuse.
    /// A getter failure is a request-time activation failure: it returns the sanitized <c>500</c>
    /// identity-provider-configuration problem and invokes no operation, exactly as a throwing
    /// constructor or registration factory does.
    /// This first contract exposes capabilities deployment-wide rather than per-tenant or
    /// per-route-qualifier; a later context-aware capability method must arrive as a default interface
    /// member or a new versioned interface/package, because adding a required member to this
    /// published, plugin-implemented interface is a breaking change.
    /// </summary>
    IdentityCapabilities Capabilities { get; }

    /// <summary>
    /// Issues a new identity for the identifying data in <paramref name="request"/> and returns its
    /// UniqueId in the result payload.
    /// Before issuing, resolve the tenant/route-qualifier namespace from <paramref name="context"/> and
    /// authorize <see cref="IdentityRequestContext.ClientId"/> as described above; a missing or denied
    /// grant, and an unknown namespace, return <see cref="IdentityResultStatus.NotFound"/> rather than
    /// performing issuance - <see cref="IdentityResultStatus.InvalidProperties"/> diagnoses invalid
    /// request data, not missing namespace permission.
    /// <para>The issued UniqueId must:</para>
    /// <list type="bullet">
    /// <item>
    /// fit the deployment's ApiSchema <c>maxLength</c> for person UniqueIds - currently
    /// <b>32 characters</b> across the checked-in core and shipped extension schemas. This is a
    /// property of the deployment's ApiSchema, not a constant of this contract, so a 36-character
    /// hyphenated GUID does not fit it, while the same GUID in 32-character hyphen-free form does.
    /// </item>
    /// <item>
    /// be drawn from the guaranteed repertoire - ASCII digits <c>0</c>-<c>9</c> and ASCII letters
    /// <c>A</c>-<c>Z</c>/<c>a</c>-<c>z</c> - within which <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// agrees exactly with the SQL Server identity collation. That comparer is an in-process
    /// approximation of the collation, exact only within this guaranteed repertoire, and not an
    /// emulation of it: outside the repertoire the two can disagree. DMS does not reject values
    /// outside this repertoire, but doing so moves the uniqueness obligation to the provider under
    /// each backing store's own equality, with no cross-engine equivalence guaranteed by this
    /// contract.
    /// </item>
    /// <item>
    /// be unique, and distinct under <see cref="StringComparer.OrdinalIgnoreCase"/>, within the
    /// provider's documented tenant/route-qualifier-to-authority namespace mapping. Independent
    /// namespaces may reuse the same value; namespaces the operator combines into one person
    /// natural-key domain must be coordinated by the operator to be compatible. No deployment-wide
    /// namespace, host remapping, or person-resource write is implied.
    /// </item>
    /// <item>non-empty and free of leading or trailing whitespace, which DMS does not trim.</item>
    /// <item>a single URL path segment, because <c>GetByIdAsync</c> carries it in the route.</item>
    /// <item>
    /// stable for the life of the identity, because person documents already written reference it as
    /// natural-key data.
    /// </item>
    /// </list>
    /// The host-wide no-timeout, no-retry rule above lands hardest here. If the response is lost - a
    /// connection abort or an upstream <c>502</c> - the outcome is an unknown issuance the client
    /// cannot safely resolve by retrying; the provider must document how a repeated create for the
    /// same identifying data behaves, either idempotent on an upstream key or duplicate issuance with a
    /// documented reconciliation path, since this contract adds no portable idempotency key in v1. A
    /// demographic match, or an unqualified no-match from a documented recovery lookup, is not proof
    /// that retrying create is safe. Where the provider documents an authoritative lookup, the client
    /// reconciles the unknown issuance through it. Where no such lookup exists, or where it cannot
    /// establish whether the identity was issued, the client stops rather than retrying and resolves
    /// the outcome through the provider's documented operator or upstream reconciliation process.
    /// </summary>
    /// <param name="request">
    /// The create request body: a JSON object carrying the standard identifying properties documented
    /// on <see cref="IIdentityService"/>, plus any custom properties this provider supports.
    /// </param>
    /// <param name="context">The tenant, route-qualifier, and client context of the request.</param>
    /// <param name="cancellationToken">
    /// Cancellation observed before or during the call propagates without a replacement problem
    /// response; it never retroactively un-issues an id the provider has already committed to return.
    /// </param>
    /// <returns>
    /// <see cref="IdentityResultStatus.Success"/> with the issued identity,
    /// <see cref="IdentityResultStatus.InvalidProperties"/> with <see cref="IdentityError"/> entries, or
    /// <see cref="IdentityResultStatus.NotFound"/> for an unknown or unauthorized namespace. Returning
    /// <see cref="IdentityResultStatus.Incomplete"/> or <see cref="IdentityResultStatus.JobFailed"/>
    /// here is provider contract misuse.
    /// </returns>
    Task<IdentityResult> CreateAsync(
        JsonObject request,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Retrieves the identity previously issued as <paramref name="uniqueId"/>.
    /// DMS rejects a route value that is present but blank before calling this method, so
    /// <paramref name="uniqueId"/> is never null, empty, or whitespace-only here; it is otherwise
    /// passed through unchanged and need not be drawn from the guaranteed repertoire documented on
    /// <c>CreateAsync</c>, since a value outside that repertoire is still a value the provider itself
    /// issued and must still be able to look up under its own equality.
    /// As with every operation, resolve the tenant/route-qualifier namespace from
    /// <paramref name="context"/> and authorize <see cref="IdentityRequestContext.ClientId"/> before
    /// lookup; a missing or denied grant, an unknown namespace, and no matching identity all return
    /// <see cref="IdentityResultStatus.NotFound"/>.
    /// DMS applies no timeout to this call and never retries it, under the host-wide rule above.
    /// </summary>
    /// <param name="uniqueId">The UniqueId to look up, guaranteed non-blank.</param>
    /// <param name="context">The tenant, route-qualifier, and client context of the request.</param>
    /// <param name="cancellationToken">
    /// Cancellation observed before or during the call propagates without a replacement problem
    /// response.
    /// </param>
    /// <returns>
    /// <see cref="IdentityResultStatus.Success"/> with the matching identity,
    /// <see cref="IdentityResultStatus.InvalidProperties"/> with <see cref="IdentityError"/> entries, or
    /// <see cref="IdentityResultStatus.NotFound"/> for no match or an unknown/unauthorized namespace.
    /// Returning <see cref="IdentityResultStatus.Incomplete"/> or
    /// <see cref="IdentityResultStatus.JobFailed"/> here is provider contract misuse.
    /// </returns>
    Task<IdentityResult> GetByIdAsync(
        string uniqueId,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Resolves <paramref name="uniqueIds"/> to identities, either synchronously or by accepting an
    /// asynchronous job whose outcome is later retrieved through <c>ResultsAsync</c>; see
    /// <see cref="IdentityAsyncResult"/> for the synchronous/pending payload shape and
    /// <see cref="IdentityAsyncResult.RequestToken"/> for the usability rules a returned token must
    /// satisfy. Returning a token while <see cref="IdentityCapabilities.Results"/> is not advertised is
    /// provider contract misuse, because DMS would then have no operation to redeem it with.
    /// As with every operation, resolve the tenant/route-qualifier namespace from
    /// <paramref name="context"/> and authorize <see cref="IdentityRequestContext.ClientId"/> before
    /// accepting the job or performing the lookup, not only when the job is later polled; a missing or
    /// denied grant, and an unknown namespace, both return <see cref="IdentityResultStatus.NotFound"/>.
    /// <para>When this call accepts an asynchronous job, the provider must:</para>
    /// <list type="bullet">
    /// <item>
    /// bind the job to the complete <paramref name="context"/> - <see cref="IdentityRequestContext.Tenant"/>,
    /// <see cref="IdentityRequestContext.RouteQualifiers"/>, and
    /// <see cref="IdentityRequestContext.ClientId"/> - under the equality rules documented on
    /// <see cref="IdentityRequestContext"/>, and treat a poll whose context does not match as
    /// <see cref="IdentityResultStatus.NotFound"/> rather than returning the job.
    /// </item>
    /// <item>
    /// keep the result retrievable for a documented retention period, answering a poll after expiry as
    /// <see cref="IdentityResultStatus.NotFound"/>.
    /// </item>
    /// <item>
    /// answer repeated polls of an unexpired, complete job with the same result, because polling is a
    /// <c>GET</c> and clients may retry it.
    /// </item>
    /// <item>
    /// make the result retrievable from any replica serving the same deployment, or document that the
    /// integration is single-replica only, and document whether accepted jobs survive a provider
    /// restart.
    /// </item>
    /// <item>
    /// scope the result to the issuing client, not the tenant. This is the v1 security boundary: a
    /// provider whose upstream system shares results tenant-wide must still gate the poll on
    /// <see cref="IdentityRequestContext.ClientId"/>, and documentation cannot waive it.
    /// </item>
    /// </list>
    /// Request cancellation does not cancel an accepted job. Once this call has returned
    /// <see cref="IdentityResultStatus.Success"/> with a token, DMS has already answered <c>202</c> and
    /// the client's connection is irrelevant to the job; cancellation applies only to this call while it
    /// is in flight.
    /// DMS applies no timeout to this call and never retries it, under the host-wide rule above; the
    /// client-retry allowance above concerns a client reissuing its own poll, not DMS re-invoking a
    /// provider.
    /// </summary>
    /// <param name="uniqueIds">The UniqueIds to resolve, one entry per requested identity.</param>
    /// <param name="context">The tenant, route-qualifier, and client context of the request.</param>
    /// <param name="cancellationToken">
    /// Cancellation observed before or during the call propagates without a replacement problem
    /// response, and never cancels a job this call has already caused to be accepted.
    /// </param>
    /// <returns>
    /// <see cref="IdentityResultStatus.Success"/> with either a complete payload or a
    /// <see cref="IdentityAsyncResult.RequestToken"/> for a pending job,
    /// <see cref="IdentityResultStatus.InvalidProperties"/>, or <see cref="IdentityResultStatus.NotFound"/>
    /// for an unknown or unauthorized namespace. Returning <see cref="IdentityResultStatus.Incomplete"/>
    /// or <see cref="IdentityResultStatus.JobFailed"/> here is provider contract misuse regardless of any
    /// token supplied alongside it.
    /// </returns>
    Task<IdentityAsyncResult> FindAsync(
        IReadOnlyList<string> uniqueIds,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Resolves <paramref name="requests"/> to scored identity matches, either synchronously or by
    /// accepting an asynchronous job, under exactly the namespace-authorization, job-acceptance,
    /// job-ownership, retention, and result-scoping obligations documented on <c>FindAsync</c>, which
    /// apply identically here.
    /// A request with no matching identity is represented as <see cref="IdentityResultStatus.Success"/>
    /// with an empty match group, not <see cref="IdentityResultStatus.NotFound"/>;
    /// <see cref="IdentityResultStatus.NotFound"/> is reserved for an unknown or unauthorized namespace.
    /// DMS applies no timeout to this call and never retries it, under the host-wide rule above.
    /// </summary>
    /// <param name="requests">
    /// The search criteria, one JSON object per requested match group, each carrying the standard
    /// identifying properties documented on <see cref="IIdentityService"/> plus any custom properties
    /// this provider supports. Each group's matches are returned in the corresponding positional entry.
    /// </param>
    /// <param name="context">The tenant, route-qualifier, and client context of the request.</param>
    /// <param name="cancellationToken">
    /// Cancellation observed before or during the call propagates without a replacement problem
    /// response, and never cancels a job this call has already caused to be accepted.
    /// </param>
    /// <returns>
    /// <see cref="IdentityResultStatus.Success"/> with either a complete payload or a
    /// <see cref="IdentityAsyncResult.RequestToken"/> for a pending job,
    /// <see cref="IdentityResultStatus.InvalidProperties"/>, or <see cref="IdentityResultStatus.NotFound"/>
    /// for an unknown or unauthorized namespace. Returning <see cref="IdentityResultStatus.Incomplete"/>
    /// or <see cref="IdentityResultStatus.JobFailed"/> here is provider contract misuse regardless of any
    /// token supplied alongside it.
    /// </returns>
    Task<IdentityAsyncResult> SearchAsync(
        IReadOnlyList<JsonObject> requests,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Retrieves the outcome of the asynchronous job previously accepted by <c>FindAsync</c> or
    /// <c>SearchAsync</c> under <paramref name="requestToken"/>.
    /// DMS passes the framework-decoded route value through unchanged, performing no second unescape;
    /// see <see cref="IdentityAsyncResult.RequestToken"/> for the usability rules the originally
    /// returned token had to satisfy.
    /// As with every operation, resolve the tenant/route-qualifier namespace and authorize
    /// <see cref="IdentityRequestContext.ClientId"/> before revealing any job state; this check is
    /// layered on top of, not a substitute for, the job-ownership check below. A missing or denied
    /// grant, and an unknown namespace, both return <see cref="IdentityResultStatus.NotFound"/>.
    /// <para>Job ownership and polling:</para>
    /// <list type="bullet">
    /// <item>
    /// a poll whose <paramref name="context"/> does not match the complete context the job was
    /// accepted under - <see cref="IdentityRequestContext.Tenant"/>,
    /// <see cref="IdentityRequestContext.RouteQualifiers"/>, and
    /// <see cref="IdentityRequestContext.ClientId"/> - returns
    /// <see cref="IdentityResultStatus.NotFound"/> rather than the job's state, and so does a poll after
    /// the job's documented retention period has expired.
    /// </item>
    /// <item>
    /// repeated authorized polls of an unexpired, complete job return the same result, because polling
    /// is a <c>GET</c> and clients may retry it.
    /// </item>
    /// <item>
    /// <see cref="IdentityResultStatus.JobFailed"/> is returned only after the provider establishes that
    /// the accepted job has failed permanently; it requires no payload, and DMS ignores any payload or
    /// errors supplied with it, emitting its own fixed, sanitized terminal problem instead: a <c>502</c>
    /// <c>application/problem+json</c> carrying <c>type: urn:ed-fi:api:identities:job-failed</c>, with no
    /// provider payload, error message, token, or <c>Location</c> header.
    /// That <c>type</c>, rather than the status code, is the portable terminal signal. An upstream
    /// failure and a provider-contract violation are <c>502</c> as well, so a client switching on the
    /// status alone cannot tell a permanently failed job from a poll that simply did not reach an
    /// answer. The terminal problem tells the client to stop polling this job; it is not an instruction
    /// to resubmit the original find or search automatically.
    /// The provider retains the terminal state for its documented retention period and returns the same
    /// terminal classification on repeated authorized polls while it remains retrievable, subject to the
    /// same ownership and expiry checks as a complete job.
    /// </item>
    /// <item>
    /// an exception thrown while polling means that poll did not obtain an answer, not that the job
    /// failed: DMS returns the ordinary sanitized upstream-failure problem - also a <c>502</c>, but
    /// carrying <c>type: urn:ed-fi:api:identities:upstream-failure</c> - and the job, including an
    /// already-terminal one whose own record could not be read during the failure, remains retrievable
    /// and unmodified once the dependency recovers. Only a call that itself establishes permanent
    /// failure may return <see cref="IdentityResultStatus.JobFailed"/>. A client may retry the poll
    /// under its own bounded retry policy, but this problem guarantees neither that the failure is
    /// temporary nor that a later poll will succeed, and it never makes resubmitting the original find
    /// or search safe: the job it failed to read may still be running.
    /// </item>
    /// </list>
    /// DMS applies no timeout to this call and never retries it, under the host-wide rule above; the
    /// repeated-poll allowance above concerns a client reissuing its own poll, not DMS re-invoking a
    /// provider.
    /// </summary>
    /// <param name="requestToken">
    /// The token previously returned by <c>FindAsync</c> or <c>SearchAsync</c>, passed through
    /// character-for-character.
    /// </param>
    /// <param name="context">The tenant, route-qualifier, and client context of the request.</param>
    /// <param name="cancellationToken">
    /// Cancellation observed before or during the call propagates without a replacement problem
    /// response.
    /// </param>
    /// <returns>
    /// <see cref="IdentityResultStatus.Success"/> with the completed result,
    /// <see cref="IdentityResultStatus.Incomplete"/> for a job still running,
    /// <see cref="IdentityResultStatus.JobFailed"/> for a definitively failed job,
    /// <see cref="IdentityResultStatus.InvalidProperties"/> with <see cref="IdentityError"/> entries, or
    /// <see cref="IdentityResultStatus.NotFound"/> for an unknown job, a mismatched context, an expired
    /// job, or an unknown/unauthorized namespace.
    /// </returns>
    Task<IdentityResult> ResultsAsync(
        string requestToken,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    );
}
