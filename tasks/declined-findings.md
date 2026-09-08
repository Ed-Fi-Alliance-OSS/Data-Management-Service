# Declined Findings Ledger — DMS-1457 / FR-LOG-3..6

Every reviewer sub-agent (functionality and clean-code) **must read this file before
reviewing** and must **not re-raise** anything recorded here. The team lead appends to
it in Step 4 of each round (see `tasks/plan.md` §9.5).

Format: one entry per declined finding, with the severity as reported and the
rationale for declining.

---

## Pre-seeded before round 1

These are known-likely false positives, declined in advance with rationale. They are
pre-recorded because a reviewer reading only the FR text will very plausibly report
them.

### D-1 — "FR-LOG-4 is unmet: CMS enforces no correlation ID max length"

- **Reported severity (anticipated):** High / Critical
- **Decision:** Declined — out of scope by design.
- **Rationale:** `docs/PRD-v8.1.md` is the **Ed-Fi API (DMS)** product PRD. CMS is
  governed by `docs/PRD-CMS-v1.0.md`, whose **FR-ERROR-2 requires** `correlationId`
  to equal `HttpContext.TraceIdentifier`, with **NFR-OBS-4** and **OUT-9** recording
  the lack of client-supplied correlation IDs as a deliberate, documented asymmetry.
  CMS has no `CorrelationIdHeader` setting and reads no correlation header at all, so
  no client-controlled value can reach it; `HttpContext.TraceIdentifier` is a bounded
  server-generated token. See plan AD-4. **`src/config` must not be modified.**

### D-2 — "The response-body outliers still use `traceId.Value` directly"

Refers to `FailureResponse.cs:332` (`ForAuthenticationFailure`),
`Middleware/ProfileResolutionMiddleware.cs:175`,
`Middleware/JwtRoleAuthenticationMiddleware.cs:174`, and `Handler/Utility.cs:104`.

- **Reported severity (anticipated):** High
- **Decision:** Declined — already correct.
- **Rationale:** Under plan AD-1, normalization happens at `TraceId` construction, so
  the `traceId.Value` these sites read is **already normalized**. Editing them adds
  churn without changing behavior. The only sites needing change are those that
  construct a `TraceId` while bypassing `ExtractTraceIdFrom`.

### D-3 — "`Core/Model/No.cs:106` constructs an un-normalized `TraceId`"

- **Reported severity (anticipated):** Medium
- **Decision:** Declined — inert code path.
- **Rationale:** `No.CreateFrontendRequest` is a null-object factory. Its only caller
  is `No.RequestInfo` (`No.cs:130`), which has no production callers. It is not
  reachable from an HTTP request, so it cannot carry client input.

### D-4 — "`LoggingMiddleware` emits `traceId` while everything else emits `correlationId`"

- **Reported severity (anticipated):** Medium
- **Decision:** **Declined by product owner** — out of scope; filed as
  **[DMS-1518](https://edfi.atlassian.net/browse/DMS-1518)**.
- **Rationale:** Real inconsistency, but renaming the field in the 500 response body
  is a client-visible contract change beyond FR-LOG-3..6, and FR-LOG-6 explicitly
  tolerates either key name. DMS-1518 covers the broader defect this is a symptom of:
  DMS emits **two** competing 500 body shapes — the Ed-Fi problem-details shape via
  `FailureResponse.ForSystemError`, and an ad-hoc `{message, traceId}` via
  `FailureResponse.ForServerErrorMessageBody` (4 call sites). **Do not rename the
  field, and do not consolidate the 500 shapes, in this work.** Plan §8 decision 5.

### D-5 — "The strict allowlist is duplicated in three places"

- **Reported severity (anticipated):** Medium
- **Decision:** **Accepted as-is by product owner** — not a defect, and **no follow-up
  ticket is required**.
- **Rationale:** Pre-existing condition, not introduced by this work. The three copies
  (`Backend.External/LogSanitizer.cs`, `src/config/datamodel/.../LoggingUtility.cs`,
  and the test-only `EdFi.InstanceManagement.Tests.E2E/Infrastructure/LogSanitizer.cs`)
  stay as they are, kept in sync by their existing comments. Consolidating would
  require touching `src/config`, forbidden by AD-4. **Do not report this.** Plan §8
  decision 6.

### D-7 — "The allowlist is too permissive — it admits quotes, angle brackets, or non-ASCII"

- **Reported severity (anticipated):** High / Medium
- **Decision:** **Declined by product owner** — the allowlist definition is settled.
- **Rationale:** Plan §8 decision 2 fixes the allowlist as "all printable non-control
  characters" (`!char.IsControl(c)`, no positive enumeration). The value reaches log
  sinks through structured-log parameters and response bodies through
  `JsonSerializer`/`JsonObject`, both of which escape their own output — character
  exclusion is not the control protecting those sinks, and narrowing the set would
  re-break FR-LOG-3's explicit "SHALL NOT be limited to only alphanumeric characters"
  requirement. See AD-3.

### D-8 — "Public/client documentation of the new behavior is missing"

- **Reported severity (anticipated):** Medium
- **Decision:** **Deliberately out of scope for this repository.**
- **Rationale:** Plan §8 decision 4. Client-facing documentation of correlation ID
  behavior (max length, truncation, character removal) belongs in the **separate Ed-Fi
  documentation repository**. The only in-repo obligation is a `TODO` note at the end
  of the PR description. `docs/LOGGING.md` and `docs/CONFIGURATION.md` (Task 7) cover
  the host-facing side and are sufficient here.

### D-6 — "XML doc comments still say 'whitelist' instead of 'allowlist'"

- **Reported severity (anticipated):** Low / Nit
- **Decision:** Optional. Not a defect; do not block on it.
- **Rationale:** Terminology cleanup in code comments is welcome if the file is already
  being edited, but must not expand the diff or consume review rounds. Only the PRD and
  host-facing docs were required to use "allowlist".

---

## Round-by-round declines

*(Team lead: append below, one section per round.)*
