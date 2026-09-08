# DEPRECATED: Legacy Claimset Files

⚠️ **IMPORTANT: These files are deprecated and no longer used for claimset loading.**

## Status

The `.json` files in this directory were previously used by the CmsHierarchy tool for static claimset generation and SQL file preprocessing. As of the DMS-777 implementation, this approach has been **completely replaced** with a runtime claims loading system.

## What Changed

- **Before**: Static JSON files processed by build-time scripts to generate SQL insertion scripts
- **After**: Runtime claims loading with embedded resources, fragment composition, and dynamic upload/reload capabilities

## Current Usage

These files are retained **only for legacy reference purposes** and are no longer part of the active claimset loading pipeline.

## Removal Schedule

**These files should be deleted after a successful DMS 1.0 production release** to clean up the codebase and avoid confusion.

## New Claims System

For information about the current claims loading system, see:
- `/docs/CLAIMS-LOADING-GUIDE.md` - Comprehensive guide to the new system
- `/src/config/backend/EdFi.DmsConfigurationService.Backend/Claims/` - Runtime claims services
- `/src/config/backend/EdFi.DmsConfigurationService.Backend/Deploy/AdditionalClaimsets/` - Fragment-based claimsets

---
*Created as part of DMS-777 migration from static to runtime claims loading*