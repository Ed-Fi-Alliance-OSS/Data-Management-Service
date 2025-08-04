# Claims Test Data

This directory contains reference data for E2E claims management tests.

## Files

- `authoritative-composition.json` - The expected result of hybrid mode fragment composition, copied from `eng/CmsHierarchy/`. This file represents the "gold standard" for how claims should be composed when all fragments are applied correctly.

## Usage

The test suite uses this data to verify:
1. Initial hybrid mode composition matches the expected structure
2. Claims are properly restored after upload/reload cycles
3. Fragment assembly produces valid JSON (no empty arrays)

## Maintenance

If the authoritative composition changes in `eng/CmsHierarchy/`, this file should be updated to match.