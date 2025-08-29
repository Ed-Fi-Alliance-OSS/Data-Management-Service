@ResetClaimsetsAfterScenario
Feature: CMS to DMS ClaimSet Synchronization
    This feature tests the synchronization of claim sets from the Configuration Management Service (CMS)
    to the Data Management Service (DMS). Each upload to CMS completely replaces all existing claimsets,
              and DMS can reload these claimsets to synchronize its state with CMS.

        Scenario: 01 Initial State Verification - View claimsets in DMS and verify baseline claimsets are present
             When a GET request is made to DMS management endpoint "/management/view-claimsets"
             Then the DMS view claimsets should be successful
              And the DMS view claimsets response should contain "SISVendor"
              And the DMS view claimsets response should contain "EdFiSandbox"

        Scenario: 02 Upload Complete Claimset to CMS → Sync to DMS - Upload TestClaimSet1 with Student access, reload DMS, verify only new claimset present
             When a claim set is uploaded to CMS that grants "Student" access to "TestClaimSet1"
             Then the claim set upload to CMS should be successful
             When a POST request is made to DMS management endpoint "/management/reload-claimsets"
             Then the DMS claimsets reload should be successful
             When a GET request is made to DMS management endpoint "/management/view-claimsets"
             Then the DMS view claimsets should be successful
              And the DMS view claimsets response should contain "TestClaimSet1"
              And system claim sets should have empty resource claims

        Scenario: 03 Upload Different Complete Claimset to CMS → Sync to DMS - Upload TestClaimSet2 with School access, reload DMS, verify only TestClaimSet2 present
             When a claim set is uploaded to CMS that grants "School" access to "TestClaimSet2"
             Then the claim set upload to CMS should be successful
             When a POST request is made to DMS management endpoint "/management/reload-claimsets"
             Then the DMS claimsets reload should be successful
             When a GET request is made to DMS management endpoint "/management/view-claimsets"
             Then the DMS view claimsets should be successful
              And the DMS view claimsets response should contain "TestClaimSet2"

        Scenario: 04 Upload Multiple Claimsets - both TestClaimSetA and TestClaimSetB, reload DMS, verify TestClaimSetB
             When a claim set is uploaded to CMS that grants "Student" access to "TestClaimSetA"
             Then the claim set upload to CMS should be successful
             When a POST request is made to DMS management endpoint "/management/reload-claimsets"
             Then the DMS claimsets reload should be successful

             When a claim set is uploaded to CMS that grants "School" access to "TestClaimSetB"
             Then the claim set upload to CMS should be successful
             When a POST request is made to DMS management endpoint "/management/reload-claimsets"
             Then the DMS claimsets reload should be successful

             When a GET request is made to DMS management endpoint "/management/view-claimsets"
             Then the DMS view claimsets should be successful
              And the DMS view claimsets response should contain "TestClaimSetB"
              And the DMS view claimsets response should not contain "TestClaimSetA"

        Scenario: 05 Reset CMS to Original → Sync to DMS - Reset CMS with reload-claims, reload DMS, verify original state restored
             When a POST request is made to CMS "/management/reload-claims"
             Then the CMS reload should be successful
             When a POST request is made to DMS management endpoint "/management/reload-claimsets"
             Then the DMS claimsets reload should be successful
             When a GET request is made to DMS management endpoint "/management/view-claimsets"
             Then the DMS view claimsets should be successful
              And the DMS view claimsets response should contain "SISVendor"
              And the DMS view claimsets response should contain "EdFiSandbox"
