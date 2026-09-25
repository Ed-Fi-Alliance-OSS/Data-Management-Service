# Claims Test Data

This directory contains reference data for E2E claims management tests.

## Files

- `authoritative-composition.json` - The expected result of hybrid mode fragment composition, created originally
as a composition of all the claims data from `eng/CmsHierarchy/`. 
This file represents how claims should be composed when all additional claimset fragments are applied correctly.

- `Fragments/` - The test-owned claimset fragments `001` through `003c`, which define the six `E2E-*` claim
  sets. They are not shipped with the Configuration Service; the E2E setup stages them with the default
  `004`/`005` extension fragments into `eng/docker-compose/.e2e-claims/`, which is mounted as the claims
  directory.
  - `e2e-readiness-checks.json` - One canonical readiness probe per E2E claim set (claim set, full leaf
    resource-claim URI, action). Bootstrap staging with `-IncludeE2EClaimSets` records these for the
    claims-ready gate. The file is not named `*-claimset.json`, so it is never loaded as a fragment.

## Usage

The test suite uses this data to verify:
1. Initial hybrid mode composition matches the expected structure
2. Claims are properly restored after upload/reload cycles

