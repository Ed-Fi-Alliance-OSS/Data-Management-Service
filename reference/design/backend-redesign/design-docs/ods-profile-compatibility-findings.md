# ODS Profile Compatibility Findings

Verified against the legacy ODS/API codebase.

This memo assumes the DMS support boundary is:

- MetaEd-generated models only
- all relevant MetaEd validations run successfully

## Question

How does the legacy ODS/API handle the hard case of empty-key, profile-scoped collection matching when the API does not expose a child-row identity?

## Findings

### 1. Legacy collection synchronization is equality-based

The core collection write path is `SynchronizeCollectionTo`, which:

- Deletes persisted items that have no equal submitted item.
- Updates persisted items that are equal to a submitted item.
- Adds submitted items that are not equal to any persisted item.

There is no ordinal-based matching, no server-issued child token, and no separate hidden row identity in this path.

Evidence:

- `/home/brad/work/ods/Ed-Fi-ODS/Application/EdFi.Ods.Common/Extensions/CollectionExtensions.cs:20`

### 2. Profiles preserve collection item identity in the writable shape

Legacy profiles do not allow identifying members to disappear from write processing:

- `IncludeOnly` profiles automatically add identifying properties and identifying references.
- `ExcludeOnly` profiles warn and refuse to exclude identifying members.

That is what allows profile-scoped writes to continue matching existing child items without exposing a server-managed row handle.

Evidence:

- `/home/brad/work/ods/Ed-Fi-ODS/Application/EdFi.Ods.Common/Models/Resource/ProfileResourceMembersFilterProvider.cs:37`

### 3. Child item equality is generated from parent plus identifying members

Generated child resource equality uses:

- The parent reference for child items.
- The child item's identifying properties and identifying references.

The mapper also excludes identifying properties from normal synchronization, which reinforces that those fields are used to find the existing row, not treated as ordinary mutable state during merge.

Evidence:

- `/home/brad/work/ods/Ed-Fi-ODS/Utilities/CodeGeneration/EdFi.Ods.CodeGen/Generators/Resources/ResourcePropertyRenderer.cs:61`
- `/home/brad/work/ods/Ed-Fi-ODS/Utilities/CodeGeneration/EdFi.Ods.CodeGen/Mustache/Resources.mustache:483`
- `/home/brad/work/ods/Ed-Fi-ODS/Utilities/CodeGeneration/EdFi.Ods.CodeGen/Generators/EntityMapper.cs:246`

Concrete example:

- `AssessmentContentStandardAuthor` equality matches on parent `AssessmentContentStandard` plus `Author`.
- `/home/brad/work/ods/Ed-Fi-ODS/Utilities/CodeGeneration/EdFi.Ods.CodeGen.Tests/Approval/5.2.0/DataStandard_520_ApprovalTests.Verify.Standard_Std_5.2.0_Resources_Resources.generated.approved.cs:6365`

### 4. Profile collection filters are predicates, not an alternate match strategy

Profile collection item filters compile to value predicates. They determine which child items are in scope for a profile, but they do not introduce a second matching algorithm.

Evidence:

- `/home/brad/work/ods/Ed-Fi-ODS/Application/EdFi.Ods.Common/Models/MappingContractProvider.cs:203`
- `/home/brad/work/ods/Ed-Fi-ODS/Application/EdFi.Ods.Common/Models/ResourceItemPredicateBuilder.cs:22`

### 5. Legacy tests assert merge-and-preserve behavior

The profile test suite explicitly asserts that profile-scoped writes persist submitted in-scope child values while preserving pre-existing values that remain out of scope.

Evidence:

- `/home/brad/work/ods/Ed-Fi-ODS/Postman Test Suite/Ed-Fi ODS-API Profile Test Suite.postman_collection.json:4263`
- `/home/brad/work/ods/Ed-Fi-ODS/Postman Test Suite/Ed-Fi ODS-API Profile Test Suite.postman_collection.json:14004`

### 6. The hard empty-key multi-item case is mostly avoided, not specially solved

I did not find evidence of a legacy fallback based on:

- visible ordinal
- array position
- a hidden child-row token
- a server-managed row identity used for collection matching

The legacy model instead relies on collection-item identity remaining available in the request shape.

I found parent-only equality cases, but the examples inspected were singular embedded children or one-to-one relationships, where parent-only identity is sufficient because there is only one child in that scope.

Examples:

- `AssessmentContentStandard` is a one-to-one relationship on `Assessment`.
- `/home/brad/work/ods/Ed-Fi-ODS/Application/EdFi.Ods.Standard/Standard/5.2.0/Models/Interfaces/EntityInterfaces.generated.cs:1055`
- `/home/brad/work/ods/Ed-Fi-ODS/Utilities/CodeGeneration/EdFi.Ods.CodeGen.Tests/Approval/5.2.0/DataStandard_520_ApprovalTests.Verify.Standard_Std_5.2.0_Resources_Resources.generated.approved.cs:100888`

- `GraduationPlanRequiredAssessmentPerformanceLevel` is a one-to-one relationship on `GraduationPlanRequiredAssessment`.
- `/home/brad/work/ods/Ed-Fi-ODS/Application/EdFi.Ods.Standard/Standard/5.2.0/Models/Interfaces/EntityInterfaces.generated.cs:18679`
- `/home/brad/work/ods/Ed-Fi-ODS/Utilities/CodeGeneration/EdFi.Ods.CodeGen.Tests/Approval/5.2.0/DataStandard_520_ApprovalTests.Verify.Standard_Std_5.2.0_Resources_Resources.generated.approved.cs:82915`

### 7. MetaEd modeling and validation close the gap for common-backed collections

The MetaEd model and validator pipeline show that the true empty-key multi-item scenario is not valid for supported generated ODS collections.

In `@edfi/ed-fi-model-5.2/Common`, the only non-`Inline Common` files with no `is part of identity` annotation are:

- `/home/brad/work/dms-root/MetaEd-js/node_modules/@edfi/ed-fi-model-5.2/Common/ClassRanking.metaed:1`
- `/home/brad/work/dms-root/MetaEd-js/node_modules/@edfi/ed-fi-model-5.2/Common/ContentStandard.metaed:1`

Those are both used as singular common properties, not collections:

- `ClassRanking` on `/home/brad/work/dms-root/MetaEd-js/node_modules/@edfi/ed-fi-model-5.2/DomainEntity/StudentAcademicRecord.metaed:11`
- `ContentStandard` on `/home/brad/work/dms-root/MetaEd-js/node_modules/@edfi/ed-fi-model-5.2/DomainEntity/Assessment.metaed:28`
- `ContentStandard` on `/home/brad/work/dms-root/MetaEd-js/node_modules/@edfi/ed-fi-model-5.2/DomainEntity/LearningStandard.metaed:17`

More importantly, `metaed-plugin-edfi-unified-advanced` contains a validator that explicitly forbids using a common as a collection target unless the referenced common, or its base common, has identity properties:

- `/home/brad/work/dms-root/MetaEd-js/packages/metaed-plugin-edfi-unified-advanced/src/validator/CommonProperty/CommonPropertyCollectionTargetMustContainIdentity.ts:8`

That validator:

- only applies to common properties that are collections
- fails validation when the referenced common has no identity
- is registered in the advanced plugin validator list
- is covered by tests for both direct common and common-subclass cases

Evidence:

- `/home/brad/work/dms-root/MetaEd-js/packages/metaed-plugin-edfi-unified-advanced/src/validator/CommonProperty/CommonPropertyCollectionTargetMustContainIdentity.ts:19`
- `/home/brad/work/dms-root/MetaEd-js/packages/metaed-plugin-edfi-unified-advanced/src/index.ts:18`
- `/home/brad/work/dms-root/MetaEd-js/packages/metaed-plugin-edfi-unified-advanced/test/validator/CommonProperty/CommonPropertyCollectionTargetMustContainIdentity.test.ts:24`
- `/home/brad/work/dms-root/MetaEd-js/packages/metaed-plugin-edfi-unified-advanced/test/validator/CommonProperty/CommonPropertyCollectionTargetMustContainIdentity.test.ts:85`

The base unified plugin also reinforces the intended model by forbidding a common property reference itself from being marked as part of the parent identity:

- `/home/brad/work/dms-root/MetaEd-js/packages/metaed-plugin-edfi-unified/src/validator/CommonProperty/CommonPropertyMustNotContainIdentity.ts:11`

## Conclusion

The legacy ODS/API does not appear to have a general-purpose solution for empty-key, multi-item, profile-scoped collection matching without API-visible row identity.

Its actual compatibility model is narrower:

- collection synchronization is equality-based
- profiles preserve identifying members
- generated child equality uses parent plus identifying members
- filtered profile writes merge only the in-scope items and preserve out-of-scope state

In other words, legacy mostly sidesteps the hard empty-key multi-item problem rather than solving it with a hidden mechanism.

Because DMS explicitly supports only MetaEd-generated models with all validations run, the conclusion is stronger for the DMS support scope:

- true empty-key multi-item common-backed collections are not valid when the `metaed-plugin-edfi-unified-advanced` validators run
- the non-identity common types that do exist are used as singular embedded objects, not collections
- generated collection child tables therefore still have a semantic child identity, even when the parent-only one-to-one tables do not

Within the DMS support boundary, that means the specific scenario of a profile-scoped multi-item child collection with no semantic child identity is out of scope.

That conclusion aligns with the normative rule in [profiles.md](profiles.md#data-model-and-compilation-prerequisites): if a persisted multi-item collection scope cannot supply a non-empty compiled semantic identity, validation/compilation must fail before runtime merge execution.

## Implication For DMS Design

This implies that DMS should not let the empty-key fallback case define the main collection write contract.

The legacy-compatible path is:

- use semantic-key matching as the primary collection merge model
- ensure write profiles preserve the fields needed to match a child item, analogous to legacy identifying members
- keep any backend `CollectionItemId` internal
- keep `Ordinal` as order/reconstitution state only, not identity

For MetaEd-generated ODS resources in the DMS support scope, the specific concern of a true empty-key multi-item common-backed collection is not a compatibility requirement at all.

That means the more important design requirement is:

- preserve and use the semantic child key that the model already requires for collections

Any fallback behavior for empty-effective-key multi-item collections would be a DMS-specific contract for scenarios outside the supported MetaEd-generated ODS pattern, not something legacy ODS already solved and that DMS must reproduce exactly.

## Confidence And Limits

Confidence is high on the main conclusion because the relevant legacy synchronization, profile filtering, code generation, profile tests, MetaEd model inspection, generated DDL inspection, and MetaEd validators all align on the same model.

The remaining limit is that this conclusion is intentionally scoped to what DMS supports: valid MetaEd-generated models with the full validator set applied. It does not claim that every conceivable non-MetaEd or validator-bypassed model would obey the same constraint, because those models are outside the stated DMS support boundary.
