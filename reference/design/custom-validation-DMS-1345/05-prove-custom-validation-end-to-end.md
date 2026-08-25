---
jira: DMS-1436
jira_url: https://edfi.atlassian.net/browse/DMS-1436
epic: DMS-1345
source_spike: DMS-1346
---

# Story: Prove Custom Validation End-to-End with a Fixture Validator

## Description

The design shows on paper that the contract and composition seam are sufficient to express three real-world driving scenarios, but it is explicit that none of the three is implemented by the spike or shipped by DMS Core. This story proves with running tests, against the live write pipeline and through the real operator-facing path, that the delivered mechanism actually works, per:

- `reference/design/custom-validation-DMS-1345/design.md` ("## Driving Scenarios", "## Failure Surfacing", "### Registration and Composition")

A neutral, no-I/O sample validator, shaped like Scenario 2 in the design's driving-scenarios table (document-only, no constructor dependencies) but not framed as an implementation of that scenario's business rule, is delivered as a test fixture. It never lives inside `EdFi.DataManagementService.Core` or `EdFi.DataManagementService.Core.External`.

The validator reaches the host the way a real one does: a fixture assembly referencing the abstractions contract, exposing its own registration extension, wired into the test host's composition. The proof asserts on HTTP status codes and JSON body shape, not on internal pipeline state.

This story also carries the field-level scenario grounding behind the design's driving-scenarios table, in "## Scenario Grounding" below. That grounding is fixture-specific, which is why it lives here rather than in the design document.

This story depends on the abstractions-contract, fan-in-step, and composition-seam stories.

## Acceptance Criteria

- A POST to a resource the fixture validator's `AppliesTo` matches, with a document that fails the fixture's check, returns HTTP 400 with the `validationErrors`/`errors` shape the design specifies.
- The same POST with a document that passes the fixture's check returns the normal success response, proving the validator does not block a valid request.
- A PUT exercises the same fixture validator on the update pipeline and produces the same 400 shape on failure.
- A POST to a resource the fixture validator's `AppliesTo` does not match is unaffected by the validator being registered.
- The 400 body from the fixture validator is byte-identical in its `detail`, `type`, `title`, and `status` members to the corresponding core schema-validation 400, asserted over real HTTP rather than in a unit test, so a divergence introduced anywhere in the composed stack is caught. The member set matches the fan-in story's unit-level parity criterion, so the two do not drift apart. The fixture returns an `OnPath` failure for this assertion, since that is the arm core schema validation actually produces; `DocumentValidator` never emits an `errors`-arm 400 to compare an `OnResource` failure against.
- With the fixture validator's registration removed and nothing else changed, the same failing POST returns the normal success response. This proves the validator was actually reached through the registration the story added, mirroring the one line a real deployment adds at the composition root, rather than through some other path.
- The fixture validator lives in its own assembly whose only reference is the abstractions contract, so the test proves what an external implementer can actually build rather than what a project with access to Core internals can build.
- `dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration` passes.

## Tasks

1. Use `EdFi.DataManagementService.Tests.Integration`'s existing `WebApplicationFactory<Program>`-based harness, which already drives real HTTP requests through the actual ASP.NET `Program` and its real, DI-composed pipeline.
2. Build a minimal fixture class library referencing only the abstractions contract, containing the sample validator and its own registration extension.
3. Register the fixture's extension into the test host's composition, mirroring the one line a real deployment adds at its composition root.
4. Exercise the validator through actual `HttpClient.PostAsync`/`PutAsync` calls against an existing routed-resource fixture, asserting on status code and JSON body shape.
5. Add the negative control: a run with the registration removed, asserting the same POST now succeeds.

## Scenario Grounding

The design's "## Driving Scenarios" table states what each scenario needs from the contract. The field-level facts behind that table are recorded here because they are fixture-specific and because the fixture validator this story builds is shaped like Scenario 2. Every line number was verified against the Data Standard 5.2 ApiSchema fixture at `src/dms/backend/Fixtures/authoritative/ds-5.2/inputs/ds-5.2-api-schema-authoritative.json`, where `projectName` is `"Ed-Fi"` at line 1369. Anyone implementing one of these rules as a real validator should re-verify against the data standard version that deployment serves.

**Scenario 1 - external identity systems.** `AppliesTo` is `[new ValidatedResource("Ed-Fi", "Student")]`, where `"Student"` is the top-level `resourceName` of the resource exposed at `/ed-fi/students` (line 240294). The validator reads `$.studentUniqueId`, the field the external lookup keys on and a required field of the resource (`required: ["studentUniqueId", "firstName", "lastSurname", "birthDate"]`, lines 239292-239297; the field itself at line 239266); it may additionally read `$.firstName` and `$.lastSurname` to raise match confidence. A lookup miss returns `[new CustomValidationFailure.OnResource("No matching record was found for this student in the external identity system.")]`, which lands in `errors` and selects `FailureResponse.ForBadRequest`.

**Scenario 2 - optional collections made required.** `AppliesTo` is `[new ValidatedResource("Ed-Fi", "StudentEducationOrganizationAssociation")]`, the top-level `resourceName` of the resource at `/ed-fi/studentEducationOrganizationAssociations` (line 213837). `$.races` (line 212250) and `$.languages` (line 212182) are both top-level array properties on `jsonSchemaForInsert` with `minItems: 0`, and both are absent from the schema's top-level `required` array, which lists only `studentReference` and `educationOrganizationReference` (lines 212496-212499); that confirms the scenario's premise against the shipped schema rather than by convention. Each `races` item requires `raceDescriptor` and each `languages` item requires `languageDescriptor`, so the check is for a non-empty array rather than for the key's presence. Failures are `[new CustomValidationFailure.OnPath("$.races", "At least one race must be reported."), new CustomValidationFailure.OnPath("$.languages", "At least one language must be reported.")]`, or only the one that is missing, landing in `validationErrors` and selecting `FailureResponse.ForDataValidation`. One deployment interaction is worth stating for whoever implements this for real: because the fan-in step passes the profile-effective body, assigning a client a writable profile that hides `$.races` or `$.languages` makes this validator reject that client's every write. That is a genuine policy contradiction between the profile and the validator, resolved by not deploying them together.

**Scenario 3 - subclass program associations.** `AppliesTo` is `[new ValidatedResource("Ed-Fi", "StudentProgramAssociation")]`, the top-level `resourceName` of the resource at `/ed-fi/studentProgramAssociations` (line 224327), which itself carries `isSubclass: true` (line 223508). No resource-schema key anywhere in the fixture matches "general" (checked programmatically against every key under `projectSchema.resourceSchemas`), so there is no separately exposed `generalStudentProgramAssociation` resource to hook; the generic association a client actually posts to is `studentProgramAssociations`. `StudentSpecialEducationProgramAssociation` (`resourceName` at line 235494, `isSubclass: true` at line 234094) and `StudentTitleIPartAProgramAssociation` (`resourceName` at line 237772, `isSubclass: true` at line 236938) are separate resources with separate endpoints and are deliberately absent from `AppliesTo`, so the validator never runs for a document already posted to the correct subclass endpoint. The validator reads `$.programReference.programTypeDescriptor`, a required field of that reference (`required: ["educationOrganizationId", "programName", "programTypeDescriptor"]` at lines 223593-223597; the field itself at line 223587), and compares the URI's fragment against its own configured list. A match returns `[new CustomValidationFailure.OnPath("$.programReference.programTypeDescriptor", "Special Education program associations must be posted to /ed-fi/studentSpecialEducationProgramAssociations, not to the generic studentProgramAssociations resource.")]`, or the equivalent Title I message. The out-of-scope variant the design records needs descriptor content rather than the URI: `ProgramTypeDescriptor` is a descriptor resource (`isDescriptor: true` at line 150388) whose schema carries `codeValue` (line 150397), `namespace` (line 150419), and human-readable description fields, none of which the URI fragment alone provides.
