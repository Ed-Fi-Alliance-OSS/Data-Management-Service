# DMS-1244 dmscs identifier and naming inventory

This inventory covers the CMS PostgreSQL fresh-install path under
`src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Deploy/Scripts`.
Target names follow the DMS generated naming pattern reviewed in
`src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/Constraints/ConstraintNaming.cs`:
`PK_Table`, `FK_Table_TargetOrRole`, `UX_Table_Column1_Column2`, `IX_Table_Column1_Column2`, and
`CK_Table_Rule`.

Use guarded `ALTER TABLE ... ADD CONSTRAINT` blocks for PK, FK, UX, and CK objects, with
`pg_constraint` checks against quoted table regclass values. Keep PostgreSQL defaults unnamed.

## Script inventory

| File | Category | Work accounted for |
| --- | --- | --- |
| `0000_Create_CMS_Schema.sql` | schema | Creates the `dmscs` schema. Quote as `"dmscs"`. |
| `0001_Create_Vendor_Table.sql` | schema | Creates `Vendor` and `VendorNamespacePrefix`; comments; vendor logical uniqueness. |
| `0002_Create_Application_Table.sql` | schema | Creates `Application` and `ApplicationEducationOrganization`; application logical uniqueness. |
| `0003_Create_ApiClient_Table.sql` | schema | Creates `ApiClient`; FK to `Application`. |
| `0004_Create_ClaimSet_Table.sql` | schema | Creates `ClaimSet`; claim set logical uniqueness. |
| `0005_Create_AuthorizationStrategy_Table.sql` | schema | Creates `AuthorizationStrategy`; authorization strategy logical uniqueness. |
| `0006_Create_ResourceClaim_Table.sql` | schema | Creates `ResourceClaim`; resource claim logical uniqueness. |
| `0007_Create_ClaimsHierarchy_Table.sql` | schema | Creates `ClaimsHierarchy`. |
| `0008_Insert_AuthorizationStrategy.sql` | seed | Seeds default authorization strategies and resets identity sequence. |
| `0009_Insert_ResourceClaim.sql` | seed | Seeds default resource claims and resets identity sequence. |
| `0012_Create_openiddict_Application_Table.sql` | OpenIddict schema | Creates `OpenIddictApplication`; implicit PK and `ClientId` uniqueness. |
| `0013_Create_openiddict_Authorization_Table.sql` | OpenIddict schema | Creates `OpenIddictAuthorization`; implicit PK. |
| `0014_Create_openiddict_Scope_Table.sql` | OpenIddict schema | Creates `OpenIddictScope`; implicit PK and `Name` uniqueness. |
| `0015_Create_openiddict_Application_Scope_Table.sql` | OpenIddict schema | Creates `OpenIddictApplicationScope`; PK and FKs to application/scope. |
| `0016_Create_openiddict_Token_Table.sql` | OpenIddict schema | Creates `OpenIddictToken`; implicit PK and lookup indexes. |
| `0017_Create_openiddict_Role_Table.sql` | OpenIddict schema | Creates `OpenIddictRole`; implicit PK and `Name` uniqueness. |
| `0018_Create_openiddict_Client_Rol_and_Claims.sql` | OpenIddict schema | Creates `OpenIddictClientRole`; implicit PK and inline FKs. |
| `0019_Create_OpenIdKeys_Table.sql` | OpenIddict schema | Creates `OpenIddictKey`; implicit PK. |
| `0020_Create_PgCrypto_Extension.sql` | extension setup | Creates `pgcrypto`; no `dmscs` identifier work except script ordering. |
| `0021_Create_DataStore_Table.sql` | schema | Creates `DataStore`. |
| `0022_Create_ApiClientDataStore_Table.sql` | schema | Creates `ApiClientDataStore`; join PK and FKs. |
| `0022_Create_DataStoreContext_Table.sql` | schema | Creates `DataStoreContext`; FK and logical uniqueness by data store/context key. |
| `0023_Create_DataStoreDerivative_Table.sql` | schema | Creates `DataStoreDerivative`; FK and lookup index. |
| `0024_Create_Tenant_Table.sql` | schema | Creates `Tenant`; tenant name logical uniqueness. |
| `0025_Add_TenantId_To_Tables.sql` | tenant-alter | Adds `TenantId`, tenant FKs, and tenant lookup indexes to tenant-owned tables. |
| `0026_Create_Profile_Table.sql` | schema | Creates `Profile`; profile name logical uniqueness and redundant name index. |
| `0027_Create_ProfileApplication_Table.sql` | schema | Creates `ApplicationProfile`; join PK and FKs. |

## Constraint and index mapping

| Table | Current object | Current columns | Target object | Kind | Notes |
| --- | --- | --- | --- | --- | --- |
| `Vendor` | implicit primary key | `Id` | `PK_Vendor` | PK | Move from inline PK to guarded named constraint. |
| `Vendor` | `uq_Company` | `Company` | `UX_Vendor_TenantId_Company` | UX | Tenant-owned logical uniqueness; use `NULLS NOT DISTINCT` so non-multitenant `TenantId IS NULL` stays unique. |
| `Vendor` | `idx_Company` | `Company` | remove | redundant unique index | Covered by the new UX; no separate lookup need found. |
| `VendorNamespacePrefix` | `pk_VendorNamespacePrefix` | `VendorId`, `NamespacePrefix` | `PK_VendorNamespacePrefix` | PK | Logical uniqueness per vendor. |
| `VendorNamespacePrefix` | `fk_vendor_NamespacePrefix` | `VendorId` -> `Vendor.Id` | `FK_VendorNamespacePrefix_Vendor` | FK | Keep `ON DELETE CASCADE`. |
| `Application` | implicit primary key | `Id` | `PK_Application` | PK | Move from inline PK to guarded named constraint. |
| `Application` | `fk_vendor` | `VendorId` -> `Vendor.Id` | `FK_Application_Vendor` | FK | Keep `ON DELETE CASCADE`. |
| `Application` | `idx_vendor_applicationname` | `VendorId`, `ApplicationName` | `UX_Application_VendorId_ApplicationName` | UX | Logical uniqueness per vendor; replace unique index. |
| `ApplicationEducationOrganization` | `pk_applicationEducationOrganization` | `ApplicationId`, `EducationOrganizationId` | `PK_ApplicationEducationOrganization` | PK | Logical uniqueness per application/education organization. |
| `ApplicationEducationOrganization` | `fk_application_educationOrganization` | `ApplicationId` -> `Application.Id` | `FK_ApplicationEducationOrganization_Application` | FK | Keep `ON DELETE CASCADE`; no table exists for `EducationOrganizationId`. |
| `ApiClient` | implicit primary key | `Id` | `PK_ApiClient` | PK | Move from inline PK to guarded named constraint. |
| `ApiClient` | `fk_apiclient_application` | `ApplicationId` -> `Application.Id` | `FK_ApiClient_Application` | FK | Keep `ON DELETE CASCADE`. |
| `ClaimSet` | `claimset_pkey` | `Id` | `PK_ClaimSet` | PK | Current constraint references folded `id`; move to quoted `Id`. |
| `ClaimSet` | `idx_ClaimSetName` | `ClaimSetName` | `UX_ClaimSet_TenantId_ClaimSetName` | UX | Tenant-owned logical uniqueness; use `NULLS NOT DISTINCT`. Repository `ON CONFLICT (ClaimSetName)` must later change to the constraint name. |
| `AuthorizationStrategy` | implicit primary key | `Id` | `PK_AuthorizationStrategy` | PK | Move from inline PK to guarded named constraint. |
| `AuthorizationStrategy` | `uq_AuthorizationStrategyName` | `AuthorizationStrategyName` | `UX_AuthorizationStrategy_TenantId_AuthorizationStrategyName` | UX | Tenant-owned logical uniqueness; use `NULLS NOT DISTINCT`. |
| `ResourceClaim` | implicit primary key | `Id` | `PK_ResourceClaim` | PK | Move from inline PK to guarded named constraint. |
| `ResourceClaim` | `uq_ClaimName` | `ClaimName` | `UX_ResourceClaim_TenantId_ClaimName` | UX | Tenant-owned logical uniqueness; use `NULLS NOT DISTINCT`. Seed and loader `ON CONFLICT (ClaimName)` must later change to the constraint name. |
| `ClaimsHierarchy` | implicit primary key | `Id` | `PK_ClaimsHierarchy` | PK | No UX, FK, CK, or IX currently present. |
| `OpenIddictApplication` | implicit primary key | `Id` | `PK_OpenIddictApplication` | PK | OpenIddict implicit object. |
| `OpenIddictApplication` | implicit unique constraint | `ClientId` | `UX_OpenIddictApplication_ClientId` | UX | Global OpenIddict uniqueness; no tenant scope. |
| `OpenIddictAuthorization` | implicit primary key | `Id` | `PK_OpenIddictAuthorization` | PK | `ApplicationId` currently has no FK; if enforced, name it `FK_OpenIddictAuthorization_OpenIddictApplication`. |
| `OpenIddictScope` | implicit primary key | `Id` | `PK_OpenIddictScope` | PK | OpenIddict implicit object. |
| `OpenIddictScope` | implicit unique constraint | `Name` | `UX_OpenIddictScope_Name` | UX | Global OpenIddict uniqueness; no tenant scope. |
| `OpenIddictApplicationScope` | `OpenIddictApplicationScope_pkey` | `ApplicationId`, `ScopeId` | `PK_OpenIddictApplicationScope` | PK | Join-table logical uniqueness. |
| `OpenIddictApplicationScope` | `FK_Application` | `ApplicationId` -> `OpenIddictApplication.Id` | `FK_OpenIddictApplicationScope_OpenIddictApplication` | FK | Keep `ON DELETE CASCADE`. |
| `OpenIddictApplicationScope` | `FK_Scope` | `ScopeId` -> `OpenIddictScope.Id` | `FK_OpenIddictApplicationScope_OpenIddictScope` | FK | Keep `ON DELETE CASCADE`. |
| `OpenIddictToken` | implicit primary key | `Id` | `PK_OpenIddictToken` | PK | OpenIddict implicit object. |
| `OpenIddictToken` | `idx_OpenIddictToken_ApplicationId` | `ApplicationId` | `IX_OpenIddictToken_ApplicationId` | IX | Non-unique lookup index. |
| `OpenIddictToken` | `idx_OpenIddictToken_Subject` | `Subject` | `IX_OpenIddictToken_Subject` | IX | Non-unique lookup index. |
| `OpenIddictToken` | `idx_OpenIddictToken_ReferenceId` | `ReferenceId` | `IX_OpenIddictToken_ReferenceId` | IX | Non-unique lookup index; do not make unique without separate behavior change. |
| `OpenIddictToken` | `idx_OpenIddictToken_ExpirationDate` | `ExpirationDate` | `IX_OpenIddictToken_ExpirationDate` | IX | Non-unique lookup index. |
| `OpenIddictToken` | none | `ApplicationId`, `AuthorizationId` | candidate FKs | note | If future OpenIddict cleanup enforces them, use `FK_OpenIddictToken_OpenIddictApplication` and `FK_OpenIddictToken_OpenIddictAuthorization`. |
| `OpenIddictRole` | implicit primary key | `Id` | `PK_OpenIddictRole` | PK | OpenIddict implicit object. |
| `OpenIddictRole` | implicit unique constraint | `Name` | `UX_OpenIddictRole_Name` | UX | Global OpenIddict uniqueness; no tenant scope. |
| `OpenIddictClientRole` | implicit primary key | `ClientId`, `RoleId` | `PK_OpenIddictClientRole` | PK | Join-table logical uniqueness. |
| `OpenIddictClientRole` | implicit FK | `ClientId` -> `OpenIddictApplication.Id` | `FK_OpenIddictClientRole_OpenIddictApplication` | FK | Keep `ON DELETE CASCADE`. |
| `OpenIddictClientRole` | implicit FK | `RoleId` -> `OpenIddictRole.Id` | `FK_OpenIddictClientRole_OpenIddictRole` | FK | Keep `ON DELETE CASCADE`. |
| `OpenIddictKey` | implicit primary key | `Id` | `PK_OpenIddictKey` | PK | `KeyId` has no current uniqueness or lookup constraint. |
| `DataStore` | implicit primary key | `Id` | `PK_DataStore` | PK | Tenant-owned table, but no current logical unique key. |
| `ApiClientDataStore` | `pk_apiClientDataStore` | `ApiClientId`, `DataStoreId` | `PK_ApiClientDataStore` | PK | Join-table logical uniqueness. |
| `ApiClientDataStore` | `fk_apiclient` | `ApiClientId` -> `ApiClient.Id` | `FK_ApiClientDataStore_ApiClient` | FK | Keep `ON DELETE CASCADE`. |
| `ApiClientDataStore` | `fk_datastore` | `DataStoreId` -> `DataStore.Id` | `FK_ApiClientDataStore_DataStore` | FK | Keep `ON DELETE CASCADE`. |
| `DataStoreContext` | implicit primary key | `Id` | `PK_DataStoreContext` | PK | Move from inline PK to guarded named constraint. |
| `DataStoreContext` | `fk_datastorecontext_datastore` | `DataStoreId` -> `DataStore.Id` | `FK_DataStoreContext_DataStore` | FK | Keep `ON DELETE CASCADE`. |
| `DataStoreContext` | `idx_datastore_context_unique` | `DataStoreId`, `ContextKey` | `UX_DataStoreContext_DataStoreId_ContextKey` | UX | Logical uniqueness belongs to the parent data store; parent carries tenant scope. |
| `DataStoreDerivative` | implicit primary key | `Id` | `PK_DataStoreDerivative` | PK | Move from inline PK to guarded named constraint. |
| `DataStoreDerivative` | `fk_datastorederivative_datastore` | `DataStoreId` -> `DataStore.Id` | `FK_DataStoreDerivative_DataStore` | FK | Keep `ON DELETE CASCADE`. |
| `DataStoreDerivative` | `idx_datastorederivative_datastoreid` | `DataStoreId` | `IX_DataStoreDerivative_DataStoreId` | IX | Non-unique lookup index. |
| `Tenant` | implicit primary key | `Id` | `PK_Tenant` | PK | Move from inline PK to guarded named constraint. |
| `Tenant` | `uq_tenant_name` | `Name` | `UX_Tenant_Name` | UX | Global tenant-name uniqueness; repository duplicate handling must use new name. |
| `Vendor` | `fk_vendor_tenant` | `TenantId` -> `Tenant.Id` | `FK_Vendor_Tenant` | FK | Added by tenant-alter script; keep `ON DELETE CASCADE`. |
| `ClaimSet` | `fk_claimset_tenant` | `TenantId` -> `Tenant.Id` | `FK_ClaimSet_Tenant` | FK | Added by tenant-alter script; keep `ON DELETE CASCADE`. |
| `AuthorizationStrategy` | `fk_authorizationstrategy_tenant` | `TenantId` -> `Tenant.Id` | `FK_AuthorizationStrategy_Tenant` | FK | Added by tenant-alter script; keep `ON DELETE CASCADE`. |
| `ResourceClaim` | `fk_resourceclaim_tenant` | `TenantId` -> `Tenant.Id` | `FK_ResourceClaim_Tenant` | FK | Added by tenant-alter script; keep `ON DELETE CASCADE`. |
| `DataStore` | `fk_datastore_tenant` | `TenantId` -> `Tenant.Id` | `FK_DataStore_Tenant` | FK | Added by tenant-alter script; keep `ON DELETE CASCADE`. |
| `Vendor` | `idx_vendor_tenantid` | `TenantId` | `IX_Vendor_TenantId` | IX | Non-unique tenant lookup index. |
| `ClaimSet` | `idx_claimset_tenantid` | `TenantId` | `IX_ClaimSet_TenantId` | IX | Non-unique tenant lookup index. |
| `AuthorizationStrategy` | `idx_authorizationstrategy_tenantid` | `TenantId` | `IX_AuthorizationStrategy_TenantId` | IX | Non-unique tenant lookup index. |
| `ResourceClaim` | `idx_resourceclaim_tenantid` | `TenantId` | `IX_ResourceClaim_TenantId` | IX | Non-unique tenant lookup index. |
| `DataStore` | `idx_datastore_tenantid` | `TenantId` | `IX_DataStore_TenantId` | IX | Non-unique tenant lookup index. |
| `Profile` | implicit primary key | `Id` | `PK_Profile` | PK | Move from inline PK to guarded named constraint. |
| `Profile` | `uq_profile_name` | `ProfileName` | `UX_Profile_ProfileName` | UX | Global profile-name uniqueness. |
| `Profile` | `ix_profile_name` | `ProfileName` | remove | redundant lookup index | Covered by `UX_Profile_ProfileName`; repository duplicate handling must use new name. |
| `ApplicationProfile` | implicit primary key | `ApplicationId`, `ProfileId` | `PK_ApplicationProfile` | PK | Join-table logical uniqueness. |
| `ApplicationProfile` | `fk_applicationprofile_application` | `ApplicationId` -> `Application.Id` | `FK_ApplicationProfile_Application` | FK | Keep `ON DELETE CASCADE`. |
| `ApplicationProfile` | `fk_applicationprofile_profile` | `ProfileId` -> `Profile.Id` | `FK_ApplicationProfile_Profile` | FK | Keep `ON DELETE RESTRICT`. |

No current `CHECK` constraints were found in the deploy scripts.

## Tenant-owned logical uniqueness

The tenant-alter script adds `TenantId` to `Vendor`, `ClaimSet`, `AuthorizationStrategy`, `ResourceClaim`, and
`DataStore`. Repository behavior uses `TenantContext.TenantWhereClause()` for `Vendor`, `ClaimSet`,
`AuthorizationStrategy` reads through `ClaimSetRepository`, `ResourceClaim` global seed reads, and `DataStore`.

Logical UX constraints that must include `TenantId`:

| Table | Logical key | Target UX |
| --- | --- | --- |
| `Vendor` | tenant + company | `UX_Vendor_TenantId_Company` |
| `ClaimSet` | tenant + claim set name | `UX_ClaimSet_TenantId_ClaimSetName` |
| `AuthorizationStrategy` | tenant + authorization strategy name | `UX_AuthorizationStrategy_TenantId_AuthorizationStrategyName` |
| `ResourceClaim` | tenant + claim name | `UX_ResourceClaim_TenantId_ClaimName` |

Because `TenantId` is nullable when multi-tenancy is disabled, these PostgreSQL UX constraints should use
`NULLS NOT DISTINCT`; otherwise multiple `TenantId IS NULL` rows with the same logical key would bypass the unique
constraint. `DataStore` is tenant-owned but has no existing duplicate result or current logical unique constraint to
rename.

Child tables such as `VendorNamespacePrefix`, `Application`, `DataStoreContext`, and `DataStoreDerivative` inherit
tenant ownership through their parent relationships. Current logical uniqueness there uses parent identifiers, not a
separate `TenantId` column.

## Repository follow-up names

Later repository work must update duplicate and FK handling from current names to:

| Current repository check | Target name |
| --- | --- |
| `uq_company` / `uq_Company` | `UX_Vendor_TenantId_Company` |
| `idx_claimsetname` / `idx_ClaimSetName` | `UX_ClaimSet_TenantId_ClaimSetName` |
| `uq_authorizationstrategyname` / `uq_AuthorizationStrategyName` | `UX_AuthorizationStrategy_TenantId_AuthorizationStrategyName` |
| `uq_claimname` / `uq_ClaimName` | `UX_ResourceClaim_TenantId_ClaimName` |
| `idx_vendor_applicationname` | `UX_Application_VendorId_ApplicationName` |
| `idx_datastore_context_unique` | `UX_DataStoreContext_DataStoreId_ContextKey` |
| `uq_tenant_name` | `UX_Tenant_Name` |
| `uq_profile_name` | `UX_Profile_ProfileName` |
| `fk_vendor` | `FK_Application_Vendor` |
| `fk_datastore` in application/API-client paths | `FK_ApiClientDataStore_DataStore` or `FK_DataStoreContext_DataStore`, based on statement |
| `fk_application` in API-client paths | `FK_ApiClient_Application` or `FK_ApiClientDataStore_ApiClient`, based on statement |
| `fk_applicationprofile_profile` | `FK_ApplicationProfile_Profile` |
| `fk_datastorederivative_datastore` | `FK_DataStoreDerivative_DataStore` |

`ON CONFLICT (ClaimSetName)` and `ON CONFLICT (ClaimName)` sites will not match tenant-scoped composite UX constraints;
they should use `ON CONFLICT ON CONSTRAINT` with the new UX names or equivalent tenant-aware logic.

## Redundant unique/index cleanup

- Remove `idx_Company`; it duplicates the current vendor UX and should not survive after
  `UX_Vendor_TenantId_Company`.
- Replace `idx_ClaimSetName`, `idx_vendor_applicationname`, and `idx_datastore_context_unique` with UX constraints.
- Remove `ix_profile_name`; it is a redundant non-unique lookup index over a globally unique profile name.
- Keep `idx_OpenIddictToken_*`, `idx_datastorederivative_datastoreid`, and tenant-id indexes as non-unique `IX_*`
  lookup indexes.
- Do not convert `OpenIddictToken.ReferenceId` or `OpenIddictKey.KeyId` to UX in this story unless a later task
  intentionally changes behavior; this inventory found lookup/use only, not existing uniqueness.
