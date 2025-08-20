# Claims Test Data

This directory contains reference data for E2E claims management tests.

## Files

- `authoritative-composition.json` - The expected result of hybrid mode fragment composition, created originally
as a composition of all the claims data from `eng/CmsHierarchy/`. 
This file represents how claims should be composed when all additional claimset fragments are applied correctly.

## Usage

The test suite uses this data to verify:
1. Initial hybrid mode composition matches the expected structure
2. Claims are properly restored after upload/reload cycles

