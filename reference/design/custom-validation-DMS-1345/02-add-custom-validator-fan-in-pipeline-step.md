---
jira: TBD
jira_url: TBD
epic: DMS-1345
source_spike: DMS-1346
---

# Story: Add the Custom-Validator Fan-In Pipeline Step and Failure Surfacing

## Description

The abstractions contract is inert until something in the write pipeline resolves registered `ICustomResourceValidator` instances and invokes them. This story adds that step and maps the failures it collects onto DMS's existing 400 response shape, per:

- `reference/design/custom-validation-DMS-1345/design.md` ("### Pipeline Placement", "### Per-Invocation Inputs", "### Lifetime and Resolution", and all of "## Failure Surfacing")

The step is an internal `IPipelineStep` named `CustomResourceValidationMiddleware`. It resolves `IEnumerable<ICustomResourceValidator>` from the current request's own DI scope, filters by `AppliesTo`, invokes `Validate` sequentially against a deep-cloned copy of the profile-effective document, and merges every returned `CustomValidationFailure` into the same `validationErrors`/`errors` buckets and the same factory-selection rule `ValidateDocumentMiddleware` already uses. It is added to both write pipelines immediately after `ProvideAuthorizationFiltersMiddleware` and immediately before the terminal handler, so resource and action authorization is already decided and the request's authorization context is already populated before any validator runs. That boundary is narrower than "fully authorized": namespace and relationship authorization is evaluated inside the backend write, downstream of the terminal handler and therefore downstream of every custom validator, so a request that will ultimately be refused on those grounds still runs its validators first and can receive a validator 400 in place of the 403 it would otherwise have received. See design.md "### Pipeline Placement", "**Authorization boundary**". GET and DELETE are untouched.

A validator that throws needs no new exception handling; DMS's existing catch-all-to-500 middleware already covers it, which is the fail-loud posture the design requires.

This story depends on the abstractions-contract story. It adds no registration for the validator collection: `IEnumerable<T>` resolves to an empty sequence when nothing is registered, so the step is a no-op until an implementer registers a validator through the composition seam, which is a separate story.

## Acceptance Criteria

- `CustomResourceValidationMiddleware` sits immediately after `ProvideAuthorizationFiltersMiddleware` and immediately before the terminal handler in both `CreateUpsertPipeline` and `CreateUpdatePipeline`, proven by a new test case in `PipelineOrderingTests.cs`'s `Given_The_Routed_Resource_Pipelines` fixture.
- A unit test proves `CreateGetByIdPipeline`, `CreateQueryPipeline`, and `CreateDeleteByIdPipeline` contain no `CustomResourceValidationMiddleware` step. The delete pipeline is asserted alongside the two read pipelines because validation on DELETE is a stated non-goal, and without it an implementation that added the step there would satisfy every other criterion in this epic.
- A unit test proves the step invokes `next()` and the request reaches the terminal handler unchanged in all three no-failure cases: no validator registered at all, a registered validator whose `AppliesTo` does not match, and an applicable validator returning an empty list. Without this, an implementation that never calls `next()` passes every other criterion here while leaving the request on `No.FrontendResponse`, whose status is 503, so every write fails. This is also what pins the "no-op until an implementer registers a validator" claim above.
- A unit test proves a registered validator whose `AppliesTo` matches the request's resource runs, that its `OnPath` failure lands in the 400 body's `validationErrors` keyed by that path, and that its `OnResource` failure lands in `errors`.
- A unit test proves a validator whose `AppliesTo` does not match the request's resource is never invoked, asserted on call count.
- A unit test proves that when two validators each return an `OnPath` failure for the same JSON path, both messages appear under that key, matching the per-path grouping `DocumentValidator.ValidationErrorsFrom` already performs. Without this, an implementation that assigns rather than appends passes every other criterion while silently dropping one validator's message.
- A unit test proves two applicable validators both run and their failures accumulate into one response rather than one short-circuiting the other.
- A unit test proves the applicable validators are invoked sequentially, by having each of two fakes record entry and exit and asserting the first has exited before the second is entered. A `Task.WhenAll` implementation must fail this test.
- A unit test proves each applicable validator receives its own document instance, by having the first of two validators mutate the `JsonNode` it was given and asserting the second sees the unmutated document and that the request's own body is unchanged.
- A unit test proves a validator receives the profile-shaped body when a writable profile applied to the request and the raw `ParsedBody` otherwise.
- A unit test proves `operation` is `CustomValidationOperation.Upsert` on the POST pipeline and `Update` on the PUT pipeline, with both cases asserted, so a swapped mapping fails.
- Unit tests prove the `resourceInfo` and `traceId` a validator receives are the request's own, asserted on captured arguments rather than inferred from behaviour.
- A unit test proves the `ValidationScope` a validator receives carries the request's `FrontendRequest.Tenant`, including the null case for a single-tenant deployment.
- A separate unit test proves that same `ValidationScope` carries the request's `FrontendRequest.RouteQualifiers`, asserted for a request with at least two qualifiers. This must be distinct from the tenant assertion: a single-tenant routed deployment has a null tenant and non-empty qualifiers, so an implementation populating only `Tenant` would pass a tenant-only test while leaving that deployment shape unable to scope a rule.
- A unit test proves the scope's qualifier dictionary is a defensive copy, by downcasting `scope.RouteQualifiers` to `Dictionary<,>`, mutating it, and asserting `requestInfo.FrontendRequest.RouteQualifiers` is unchanged. Asserting only that `IReadOnlyDictionary<,>` exposes no `Add` proves nothing, since `Dictionary<,>` implements that interface and a pass-through implementation would pass such a test.
- A unit test proves the cancellation token passed to a validator is the token carried on `RequestInfo`, asserted by a capturing fake validator.
- A test proves `AspNetCoreFrontend.FromRequest` assigns `HttpContext.RequestAborted` to the `FrontendRequest` it builds, and must fail if that assignment is removed, confirmed once by removing it. Adding the property without assigning it would leave every validator on `CancellationToken.None` forever with every other test still green.
- The `FrontendRequest` token addition leaves the record's positional surface unchanged, proven by a test that deconstructs a `FrontendRequest` at its pre-change arity and constructs one positionally without supplying a token. The test carries a comment recording the known carve-out: the record's synthesized `Equals`, `GetHashCode`, and `ToString` do change, and no production code compares `FrontendRequest` by value.
- A unit test proves a validator that throws produces a 500 through the existing `CoreExceptionLoggingMiddleware` catch-all, not a 400 and not an unhandled exception escaping the pipeline.
- A unit test pins the v1 cancellation behaviour: a validator that throws `OperationCanceledException` on an aborted request takes the generic exception arm and produces the same logged 500 as any other exception. Distinct handling may be argued in the pull request, but the behaviour ships pinned either way rather than left to whichever arm the implementation happens to take.
- A unit test proves the fan-in step throws on a `CustomValidationFailure` subtype other than `OnPath`/`OnResource`, constructed via a test-only derived record chaining the copy constructor, surfacing as a 500 through the catch chain.
- A unit test proves a validator returning `null` from `Validate` also throws and surfaces as a 500, rather than being coerced to an empty failure list. Without it, a validator that mistakenly returns `null` is indistinguishable from one that ran and found nothing.
- A unit test proves a custom-validator 400 is byte-identical in its `detail` member to the literals `ValidateDocumentMiddleware` passes on each arm (`Middleware/ValidateDocumentMiddleware.cs:41` for `errors`, `:50` for `validationErrors`), asserted for both arms, so an implementation that substitutes its own `detail` string fails. Assert against those literals rather than against a produced core schema-validation 400: `DocumentValidator` always returns an empty `errors` array (`Validation/DocumentValidator.cs:94`), so core schema validation never produces an `errors`-arm 400 to compare with.
- A unit test proves a validator returning failures produces a log record naming that validator against the request's `TraceId`, and that the record contains no failure message text, since failure messages can quote submitted document values.
- `dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit` and `dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit` both pass.

## Tasks

1. Add `CustomResourceValidationMiddleware` and `new`-construct it directly inside `ApiService.CreateUpsertPipeline` and `ApiService.CreateUpdatePipeline`, following the `new`-ed-middleware pattern the four existing core validators already use in that file. Place it between `ProvideAuthorizationFiltersMiddleware` and the terminal handler in both.
2. Resolve `IEnumerable<ICustomResourceValidator>` from `requestInfo.ScopedServiceProvider` inside `Execute`, following the pattern `UpsertHandler` already uses for its own scoped dependency, rather than from an `ApiService` constructor field.
3. Source the document from `ProfileWriteValidationBody.Effective(requestInfo)` and deep-clone it per validator with `JsonNode.DeepClone()` before calling `Validate`.
4. Add the cancellation token to `FrontendRequest` as a non-positional `init`-only property initialized to `CancellationToken.None`, never as an appended positional parameter, since a positional addition regenerates `Deconstruct` at a new arity and regenerates the primary constructor, breaking external consumers in source and binary respectively. Assign it from `HttpContext.RequestAborted` in `AspNetCoreFrontend.FromRequest`, the single construction site all endpoint call sites go through, and carry it through `RequestInfo`.
5. Build the `ValidationScope` each `Validate` call receives from `FrontendRequest.Tenant` and a copy of `FrontendRequest.RouteQualifiers`. Copy rather than pass the instance, since it is declared `Dictionary<,>` and a validator could otherwise downcast and mutate the live request.
6. Merge returned failures into the `validationErrors` and `errors` buckets, reuse `FailureResponse.ForDataValidation`/`ForBadRequest` with the same `errors.Length > 0` selection rule `ValidateDocumentMiddleware` uses, and pass that middleware's exact `detail` literals rather than inventing new ones.
7. Implement the exhaustive switch over `CustomValidationFailure` that throws on any subtype other than the two constructible cases.
8. Add the per-request observability records: validator type name and elapsed time at Debug, and one record naming the validator and its failure count when it returns failures, both against `TraceId` and both routing validator type names through `LoggingSanitizer.SanitizeForLogging`. Do not log failure message text.
9. Decide and document the handling of `OperationCanceledException` from a cancelled request; the v1 default is the generic exception arm.
10. Add the pinning test to `PipelineOrderingTests.cs`'s `Given_The_Routed_Resource_Pipelines` fixture, following the shape of the existing route-semantics ordering tests.
11. Put tests that need a booted host in `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit`, which already uses `WebApplicationFactory` and needs no database.
