# ADR: Correlation ID normalization contract (FR-LOG-3..6)

**Status:** Accepted — implemented in DMS (`src/dms`). Does not apply to CMS (`src/config`); see
[Scope boundaries](#scope-boundaries). \
**Date:** 2026-09-13 \
**Author:** Stephen Fuqua.

## Executive summary

DMS accepts a client-supplied correlation ID (via the header named by
`AppSettings:CorrelationIdHeader`) and threads it through request logs and error response bodies.
Because this value is client-controlled, it must be normalized before use: capped in length and
stripped of characters that could forge additional log lines or corrupt structured logging. This
ADR fixes the **normalization contract** — what gets normalized, in what order, how broad the
allowlist is, and where the single point of normalization lives — so that future changes don't
silently reintroduce the class of defects found during review (see
[Defects found and their lessons](#defects-found-and-their-lessons)).

## Context

Two sanitizers already existed for different purposes:

- A **strict allowlist** (`LogSanitizer.SanitizeForLog` / `LoggingSanitizer.SanitizeForLogging`):
  letters, digits, spaces, and a small set of safe punctuation (`_ - . : / \`). Used for
  server-controlled values like `Method` and `Path`.
- A new **broader allowlist**, added for correlation IDs: all printable non-control characters —
  effectively `!char.IsControl(c)`, with no positive character enumeration, plus removal of
  `U+2028`/`U+2029` (LINE SEPARATOR / PARAGRAPH SEPARATOR), which are not control characters but
  break line-oriented log consumers the same way a line feed does.

The broader allowlist exists because a client-supplied correlation ID normally originates in an
upstream system's own identifier scheme (base64, W3C `traceparent`, JSON-ish keys, RFC 5322
addresses, etc.) — narrowing it to alphanumerics would defeat the purpose of accepting a
client-supplied value at all.

## Decision

### Normalization order

Normalization is exactly three steps, in this order:

1. **Truncate** to `AppSettings:CorrelationIdMaxLength` (default `255`, constrained to the
   inclusive range `64`–`1024`; see [Bounds on the length cap](#bounds-on-the-length-cap)).
2. **Back the truncation cut off a split UTF-16 surrogate pair.** Truncating at a raw code-unit
   boundary can land between the two halves of a non-BMP character; when it does, the orphaned
   leading half is dropped as well, one character short of the cap. Without this, JSON
   serialization would emit `U+FFFD` in the response body while the raw unpaired unit reached a log
   sink, and the two would no longer be identical.
3. **Remove characters outside the allowlist** (see above).

**Order matters, and it is deliberate:** truncating first means a long hostile value retains less
trailing content than it would if characters were removed first. A consequence teams should
expect, not "fix": an over-length value can normalize to something **shorter** than
`CorrelationIdMaxLength` — either because it also contained excluded characters, or because the
cut landed inside a surrogate pair.

### Bounds on the length cap

`CorrelationIdMaxLength` is constrained to the **inclusive range 64 to 1024**. A value outside that
range is a configuration error, not a value to be clamped at use.

**Why there is a floor at all.** The cap is not applied only to client-supplied values. When no
usable correlation header is present, DMS falls back to the server-generated
`HttpContext.TraceIdentifier`, and that value goes through exactly the same normalization — a
deliberate consequence of having a single point of normalization. Kestrel formats the trace
identifier as a 13-character connection id, a colon, and an 8-hex-digit request number
(`0HNOIG2VLOC0S:00000001`, 22 characters). A cap below 22 truncates DMS's *own* identifier and
drops the request number, so every request on one connection collapses onto a single correlation
ID — the opposite of what a correlation ID is for, reached silently through a setting that reads
as though it only governs hostile input.

**Why the floor is 64 and not 22 or 32.** 64 is the shortest cap that preserves every common
upstream identifier scheme intact, so an operator cannot configure a value that silently mangles
real client IDs:

| Scheme                                | Length |
| ------------------------------------- | -----: |
| Kestrel `HttpContext.TraceIdentifier` |     22 |
| W3C trace-id (bare)                   |     32 |
| AWS X-Ray trace ID                    |     35 |
| UUID                                  |     36 |
| Braced GUID                           |     38 |
| W3C `traceparent` (full)              |     55 |

A floor of 32 was considered and rejected: it sits exactly at a bare W3C trace-id and below UUID,
X-Ray and `traceparent`, so it would still permit a configuration that truncates most real schemes.

**Why there is a ceiling.** A correlation ID is reflected back into every request log event and
into the `correlationId` of every error response body. With no upper bound a cap of, say,
`1000000` is accepted, and one request can then push up to Kestrel's ~32 KB header limit of
client-controlled text into all of those sinks at once. `1024` is generous for any real identifier
scheme while bounding that amplification.

**What an out-of-range value actually does.** It does *not* stop the process from starting, and it
is not a startup crash. `Program.cs` resolves `IOptions<AppSettings>` eagerly, catches the
resulting `OptionsValidationException`, and registers `ReportInvalidConfigurationMiddleware` ahead
of routing and endpoint configuration — which is then skipped in full. The host starts and
listens; every request, `/health` included (it is never mapped), is short-circuited with a
bodiless `500`, and the validation failure is logged at `Critical`. The setting has to be
corrected and the service restarted. That failure message is passed to `ILogger` as the message
*template*, so it is written brace-free: the template is parsed by the logging pipeline, and a
brace in it would be read as a property hole rather than as literal text.

### Single point of normalization

Normalization happens once, at the point the value is first ingested from the request (or falls
back to the server-generated trace identifier). Downstream code should read the already-normalized
value; it must not re-derive or re-sanitize it with a different allowlist. See
[Defects found](#defects-found-and-their-lessons) for what happens when this rule is violated.

### Empty-after-normalization is treated as absent

A header that is present but normalizes to empty (for example, a value made up entirely of control
characters, such as a lone horizontal tab) is treated the same as a header sent empty or omitted:
the server-generated trace identifier is used. This must be checked **after** normalization, not on
the raw header value — checking the raw value lets a client send a technically-non-empty value
(legal over HTTP) that normalizes to the empty string, silently blanking the operational identifier
everywhere it's read.

### Allowlist is deliberately unbounded (not "settled by omission")

The allowlist is "all printable non-control characters," with **no positive enumeration** of
allowed punctuation. This was an explicit product decision (not merely undiscussed): narrowing it
to reject quotes, angle brackets, or non-ASCII would re-violate the requirement that it "SHALL NOT
be limited to only alphanumeric characters." The value reaches log sinks through structured-log
parameters and response bodies through JSON serialization, both of which already escape their own
output — character exclusion from this allowlist is not the mechanism protecting those sinks;
removing line-breaking characters (control chars plus `U+2028`/`U+2029`) is what prevents log
forging.

### Scope boundaries

- **CMS (`src/config`) is out of scope.** CMS has no `CorrelationIdHeader` setting, reads no
  correlation header, and its `correlationId` is always `HttpContext.TraceIdentifier` — a bounded,
  server-generated value. This should be changed in the future, when client-provided correlation
  headers become a requested capability of CMS.
- **Renaming the `traceId` field in the ad-hoc 500 response body to `correlationId`, or
  consolidating DMS's two competing 500 response shapes, is explicitly out of scope for this
  contract.** Both are real, tracked follow-ups (see the project's issue tracker for the
  successor ticket), but are a client-visible contract change beyond normalization itself.
- **Consolidating the three duplicate copies of the strict allowlist** (`Backend.External/LogSanitizer.cs`,
  the CMS-side logging utility, and a test-only copy) is a pre-existing condition, not introduced
  by this contract, and was accepted as-is rather than fixed here because doing so would require
  touching `src/config`.
- **Client-facing documentation** (as opposed to the host-facing `docs/CONFIGURATION.md` and
  `docs/LOGGING.md`) belongs in the separate Ed-Fi documentation repository, not this one.

## Defects found and their lessons

These were found across a multi-round implement/review cycle and are recorded here so the
underlying *class* of mistake is documented, not just the specific line that was fixed.

1. **Re-sanitizing an already-normalized value with the wrong allowlist.** The plan's original
   assumption was "normalize once at construction; downstream consumers, including all log
   events, need no further changes because the value they receive is already correct." That held
   for response bodies but was false for logging: roughly 32 pre-existing log call sites pushed the
   already-normalized correlation ID back through the strict `Method`/`Path` allowlist, so
   Core/Backend log entries silently diverged from the value in the response body the client
   received, for the majority of real failure types (400/403/409/503/500). **Lesson:** when adding
   a second, differently-scoped sanitizer, audit every existing call site that logs the affected
   value type — an inventory of *construction* sites is not the same as an inventory of *use*
   sites.
2. **Logging the wrapper type instead of its value.** One remaining site logged the `TraceId`
   struct itself rather than `.Value`, so its synthesized `ToString()` (`"TraceId { Value = ... }"`)
   diverged from the identical value written to the response body. Found only by a targeted
   grep for every `Log*(...)` call with a trace/correlation-shaped argument *not* followed by
   `.Value`. **Lesson:** never pass a value-wrapping record/struct directly to a logger call;
   always pass its normalized `.Value`.
3. **Testing emptiness before normalization instead of after.** An early implementation checked
   `string.IsNullOrEmpty()` on the *raw* header value before normalizing, so a control-character-only
   header (a legal HTTP field value) took the "client supplied a value" branch and produced an
   empty `correlationId` everywhere, instead of falling back to the server-generated identifier —
   contradicting the shipped documentation in the same change. **Lesson:** normalize first, then
   test for emptiness, when deciding whether a client-supplied value should be honored.
4. **Vacuous tests.** Several early tests would pass even if the feature were reverted:
   `NotBeEmpty()` instead of an exact expected value, an assertion comparing a value against a
   `const` that never varies with the input, and an ordering invariant in a shared test fixture
   enforced only by a code comment rather than an assertion. **Lesson:** for every new test, check
   "would this fail if the change under test were reverted?" before counting it as coverage.
5. **Documentation drifting from the code it describes, within the same change.** Twice, a
   behavior fix landed but the doc sentence describing the old behavior was not updated to match —
   in one case flipping to state the *opposite* of the new, correct behavior. **Lesson:** when a
   PR changes behavior that is already documented, treat "the doc sentence for this exact behavior"
   as part of the diff to re-read, not just "docs were touched somewhere."
