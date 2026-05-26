# Suggested Spike Boundary Update: Resource Claim Metadata Seeding

## Context

**Peer Review Finding:** 🟡 MEDIUM — Seeding ResourceClaim rows (incl. homograph) contradicts spike §6 out-of-scope boundary

**Evidence:**
- Spike §6 (spike-resource-claims-endpoints.md:183-192) explicitly states resource-claim metadata seeding is "out of scope"
- Specifically names "homograph or dynamically composed claim metadata" as a documented gap
- PR implementation adds rows 422-437 (homograph/sample claims) to 0009_Insert_ResourceClaim.sql

**Dev Answer:** "It is ok. Create a .plans/suggestedchange.md to update the story to allow this if worthy"

---

## Rationale for Divergence

The spike's out-of-scope boundary was crossed for valid technical reasons:

1. **Complete-or-fail projection contract:** Story line 102-104 requires endpoints to fail explicitly if any hierarchy node cannot resolve to resource-claim metadata. Without the homograph/sample rows, endpoints return 500 (projection integrity failure) against the authoritative hierarchy.

2. **Test requirement coverage:** `ResourceClaimSeedDataTests.cs` asserts every claim URI in `authoritative-composition.json` (including homograph/sample) must have a seed row. Without these rows, the projection is incomplete.

3. **Pre-production database state:** All existing databases include the authoritative hierarchy with homograph/sample nodes. Shipping read endpoints that 500 on these nodes would break the primary feature in exactly the environments users run.

4. **Technically "outside the read endpoints":** The spike states known gaps "must be documented as data limitations or fixed outside these read endpoints." A deploy script is arguably "outside the read endpoints" — it's data bootstrapping, not endpoint logic.

---

## Recommendation

**Update spike §6** to reflect the reality that foundational resource-claim metadata seeding was necessary to deliver working read endpoints:

### Proposed Change

**Original (spike-resource-claims-endpoints.md:183-192):**

> Seeding or maintaining resource-claim metadata rows is explicitly out of scope. Homograph or dynamically composed claim metadata will not be added... Known gaps must be documented as data limitations or fixed outside these read endpoints.

**Revised:**

> This story includes foundational resource-claim metadata seeding required for the complete-or-fail projection contract. Rows for all claim URIs in the authoritative hierarchy JSON — including homograph and sample domain claims — are seeded via migration scripts to ensure endpoints do not fail with projection integrity errors on standard deployments.
>
> Ongoing maintenance or dynamic generation of resource-claim metadata (e.g., extension claims, custom domains) remains out of scope and must be handled separately.

---

## Impact

- **Story acceptance criteria:** No change needed. AC already requires complete-or-fail behavior, which necessitates the seeded rows.
- **Implementation:** No code change. The 16 rows (422-437) are already in 0009_Insert_ResourceClaim.sql (or should be moved to a new 0028 script per the HIGH finding on journaled migrations, if upgradeability is a concern).
- **Documentation:** Spike §6 boundary statement updated to match delivered scope.
- **Testing:** Existing `ResourceClaimSeedDataTests.cs` codifies the requirement that all authoritative hierarchy claims have metadata rows.

---

## Open Questions

1. Should the spike remain a historical record of initial scoping, or should it reflect delivered reality?
   - **Recommendation:** Update it. The spike serves as architecture documentation for future maintainers. Inaccurate scope statements cause confusion.

2. Is the DbUp journaled migration concern (HIGH finding) independent of this boundary divergence?
   - **Yes.** Even if the spike explicitly scoped-in seeding, the rows must still ship via a new script (e.g., 0028) rather than editing 0009 if upgradeability matters. The dev team indicated "not in production yet" accepts the current approach.

---

## Decision

- [x] Divergence from spike §6 is accepted and justified
- [ ] Spike §6 should be updated to reflect delivered scope
- [ ] No action — spike remains unchanged as historical scoping artifact

_Dev team to confirm preferred documentation approach._
