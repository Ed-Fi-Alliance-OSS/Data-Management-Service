# Tasks: Dynamic Claims Loading for CMS

## 1. Create Claims.json Structure
- [X] Extract existing claim sets from `0010_Insert_Claimset.sql` 
- [X] Extract existing claims hierarchy from `0011_Insert_ClaimsHierarchy.sql`
- [X] Create JSON structure that combines both ClaimSet and ClaimsHierarchy data
- [X] Create file at `src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Deploy/Claims.json`
- [X] Define structure with:
  - [X] ClaimSets array (with ClaimSetName and IsSystemReserved fields)
  - [X] ClaimsHierarchy array (the existing JSON structure from SQL script)
- [X] Validate JSON structure is valid and parseable

## 2. Create Core Domain Models and Interfaces
- [X] Create `IClaimsProvider` interface (similar to IApiSchemaProvider)
  - [X] Define GetClaimsDocumentNodes() method
  - [X] Define ReloadClaimsAsync() method
  - [X] Define LoadClaimsFromAsync() method
  - [X] Add IsClaimsValid property
  - [X] Add ClaimsFailures property
  - [X] Add ReloadId property
- [X] Create `ClaimsProvider` implementation
  - [X] Implement load from file system functionality
  - [X] Implement load from assembly functionality
  - [X] Add validation support
  - [X] Implement reload functionality
  - [X] Add thread-safe operations with reload ID tracking
  - [X] Add caching mechanism
- [X] Create `IClaimsUploadService` interface
  - [X] Define UploadClaimsAsync() method
- [X] Create `ClaimsUploadService` implementation
  - [X] Implement claims parsing
  - [X] Add validation
  - [X] Integration with ClaimsProvider
- [X] Create request/response models for claims operations
  - [X] UploadClaimsRequest model
  - [X] UploadClaimsResponse model
  - [X] ReloadClaimsResponse model
  - [X] ClaimsLoadResult model
  - [X] ClaimsLoadStatus model
  - [X] ClaimsFailure model
- [X] Create `ClaimsDocumentNodes` class to hold loaded claims data

## 3. Modify Existing SQL Scripts
- [X] Update `0010_Insert_Claimset.sql`
  - [X] Add logic to read Claims.json file
  - [X] Parse ClaimSets array from JSON
  - [X] Maintain existing insert logic but use JSON data
  - [X] Add error handling for missing file
- [X] Update `0011_Insert_ClaimsHierarchy.sql`
  - [X] Add logic to read Claims.json file
  - [X] Parse ClaimsHierarchy from JSON
  - [X] Maintain existing insert logic but use JSON data
  - [X] Add error handling for missing file
- [X] Test SQL scripts work with new JSON approach

## 4. Add Management Endpoints
- [X] Create `ClaimsManagementModule` in frontend
  - [X] Implement POST `/management/reload-claims` endpoint
    - [X] Check if DangerouslyEnableDynamicClaimsLoading is enabled
    - [X] Return 404 if disabled
    - [X] Call ClaimsProvider.ReloadClaimsAsync()
    - [X] Return appropriate response
  - [X] Implement POST `/management/upload-claims` endpoint
    - [X] Check if DangerouslyEnableDynamicClaimsLoading is enabled
    - [X] Return 404 if disabled
    - [X] Validate request body
    - [X] Call ClaimsUploadService.UploadClaimsAsync()
    - [X] Return appropriate response
  - [X] Add logging for all operations
- [X] Register module in endpoint configuration

## 5. Configuration Updates
- [X] Add `DangerouslyEnableDynamicClaimsLoading` to AppSettings
  - [X] Set default value to false
  - [X] Add XML documentation explaining the setting
- [X] Add claims path configuration options
  - [X] UseClaimsPath (bool)
  - [X] ClaimsPath (string)
  - [X] Similar pattern to API schema configuration
- [X] Update configuration validation
- [X] Update appsettings.json templates

## 6. Service Registration and Integration
- [X] Register IClaimsProvider in DI container
  - [X] Add as singleton
  - [X] Configure based on settings
- [X] Register IClaimsUploadService in DI container
- [X] Update existing ClaimSetRepository to use ClaimsProvider when appropriate
- [X] Update existing ClaimsHierarchyRepository to use ClaimsProvider when appropriate
- [X] Ensure transactional consistency when updating both tables
  - [X] Implement transaction support in upload/reload operations
  - [X] Add rollback capability
- [X] Add integration tests for service registration

## 7. E2E Test Support
- [ ] Create test-specific Claims.json files
  - [ ] Minimal test claims set
  - [ ] Full test claims set
  - [ ] Invalid claims for error testing
- [ ] Add helper methods for tests
  - [ ] UploadTestClaims() method
  - [ ] ResetToDefaultClaims() method
  - [ ] ValidateClaimsLoaded() method
- [ ] Update existing E2E tests
  - [ ] Identify tests that need dynamic claims
  - [ ] Update test setup to use dynamic loading
  - [ ] Add cleanup to reset claims after tests
- [ ] Create new E2E tests specifically for claims management endpoints
  - [ ] Test reload functionality
  - [ ] Test upload functionality
  - [ ] Test disabled endpoint returns 404
  - [ ] Test invalid claims handling

## 8. Documentation and Final Steps
- [ ] Add XML documentation to all new interfaces and classes
- [ ] Create integration tests for new functionality
- [ ] Update existing unit tests affected by changes
- [ ] Add logging throughout the implementation
- [ ] Performance test the claims loading process
- [ ] Security review of new endpoints
- [ ] Update developer documentation
- [ ] Create migration guide for existing deployments

## Files to Create (Checklist):
- [ ] `src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Deploy/Claims.json`
- [ ] `src/config/core/EdFi.DmsConfigurationService.Core/Claims/IClaimsProvider.cs`
- [ ] `src/config/core/EdFi.DmsConfigurationService.Core/Claims/ClaimsProvider.cs`
- [ ] `src/config/core/EdFi.DmsConfigurationService.Core/Claims/IClaimsUploadService.cs`
- [ ] `src/config/core/EdFi.DmsConfigurationService.Core/Claims/ClaimsUploadService.cs`
- [ ] `src/config/core/EdFi.DmsConfigurationService.Core/Claims/ClaimsDocumentNodes.cs`
- [ ] `src/config/core/EdFi.DmsConfigurationService.Core/Claims/Models/UploadClaimsRequest.cs`
- [ ] `src/config/core/EdFi.DmsConfigurationService.Core/Claims/Models/UploadClaimsResponse.cs`
- [ ] `src/config/core/EdFi.DmsConfigurationService.Core/Claims/Models/ReloadClaimsResponse.cs`
- [ ] `src/config/core/EdFi.DmsConfigurationService.Core/Claims/Models/ClaimsLoadResult.cs`
- [ ] `src/config/core/EdFi.DmsConfigurationService.Core/Claims/Models/ClaimsLoadStatus.cs`
- [ ] `src/config/core/EdFi.DmsConfigurationService.Core/Claims/Models/ClaimsFailure.cs`
- [ ] `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/ClaimsManagementModule.cs`

## Files to Modify (Checklist):
- [ ] `src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Deploy/Scripts/0010_Insert_Claimset.sql`
- [ ] `src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Deploy/Scripts/0011_Insert_ClaimsHierarchy.sql`
- [ ] Configuration/AppSettings classes to add new settings
- [ ] Service registration in Program.cs or equivalent
- [ ] ClaimSetRepository to integrate with ClaimsProvider
- [ ] ClaimsHierarchyRepository to integrate with ClaimsProvider
