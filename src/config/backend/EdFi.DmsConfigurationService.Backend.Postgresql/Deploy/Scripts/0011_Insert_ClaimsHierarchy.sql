-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

INSERT INTO dmscs.claimshierarchy(
	 hierarchy)
	VALUES ('[
  {
    "name": "http://ed-fi.org/identity/claims/domains/edFiTypes",
    "defaultAuthorization": {
      "actions": [
        {
          "name": "Read",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        }
      ]
    },
    "claimSets": [
      {
        "name": "SISVendor",
        "actions": [
          {
            "name": "Read"
          }
        ]
      },
      {
        "name": "EdFiSandbox",
        "actions": [
          {
            "name": "Read"
          }
        ]
      },
      {
        "name": "AssessmentVendor",
        "actions": [
          {
            "name": "Read"
          }
        ]
      },
      {
        "name": "DistrictHostedSISVendor",
        "actions": [
          {
            "name": "Read"
          }
        ]
      },
      {
        "name": "EdFiODSAdminApp",
        "actions": [
          {
            "name": "Read",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherReader",
        "actions": [
          {
            "name": "Read"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherWriter",
        "actions": [
          {
            "name": "Create",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Read",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Update",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Delete",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          }
        ]
      }
    ],
    "claims": [
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/schoolYearType",
        "claimSets": [
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "E2E-NameSpaceBasedClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      }
    ]
  },
  {
    "name": "http://ed-fi.org/identity/claims/domains/systemDescriptors",
    "defaultAuthorization": {
      "actions": [
        {
          "name": "Create",
          "authorizationStrategies": [
            {
              "name": "NamespaceBased"
            }
          ]
        },
        {
          "name": "Read",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        },
        {
          "name": "Update",
          "authorizationStrategies": [
            {
              "name": "NamespaceBased"
            }
          ]
        },
        {
          "name": "Delete",
          "authorizationStrategies": [
            {
              "name": "NamespaceBased"
            }
          ]
        },
        {
          "name": "ReadChanges",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        }
      ]
    },
    "claimSets": [
      {
        "name": "SISVendor",
        "actions": [
          {
            "name": "Read"
          }
        ]
      },
      {
        "name": "EdFiSandbox",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "AssessmentVendor",
        "actions": [
          {
            "name": "Read"
          }
        ]
      },
      {
        "name": "BootstrapDescriptorsandEdOrgs",
        "actions": [
          {
            "name": "Create",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          }
        ]
      },
      {
        "name": "DistrictHostedSISVendor",
        "actions": [
          {
            "name": "Read"
          }
        ]
      },
      {
        "name": "EdFiODSAdminApp",
        "actions": [
          {
            "name": "Create",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Read",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Update",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Delete",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherReader",
        "actions": [
          {
            "name": "Read"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherWriter",
        "actions": [
          {
            "name": "Create",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Read",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Update",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Delete",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          }
        ]
      },
      {
        "name": "EducationPreparationProgram",
        "actions": [
          {
            "name": "Read"
          }
        ]
      }
    ],
    "claims": [
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/absenceEventCategoryDescriptor",
        "claimSets": [
          {
            "name": "E2E-NameSpaceBasedClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NamespaceBased"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NamespaceBased"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NamespaceBased"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NamespaceBased"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NamespaceBased"
                  }
                ]
              }
            ]
          },
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/academicHonorCategoryDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/academicSubjectDescriptor",
        "claimSets": [
          {
            "name": "AssessmentVendor",
            "actions": [
              {
                "name": "Create"
              },
              {
                "name": "Read"
              },
              {
                "name": "Update"
              },
              {
                "name": "Delete"
              }
            ]
          },
          {
            "name": "ABConnect",
            "actions": [
              {
                "name": "Create"
              },
              {
                "name": "Read"
              },
              {
                "name": "Update"
              },
              {
                "name": "Delete"
              }
            ]
          },
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/accountTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/achievementCategoryDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/additionalCreditTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/addressTypeDescriptor",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/administrationEnvironmentDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/administrativeFundingControlDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/ancestryEthnicOriginDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentCategoryDescriptor",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentIdentificationSystemDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentItemCategoryDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentItemResultDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/assignmentLateStatusDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/attemptStatusDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/attendanceEventCategoryDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/barrierToInternetAccessInResidenceDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/behaviorDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/busRouteDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/calendarEventDescriptor",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/calendarTypeDescriptor",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/careerPathwayDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/charterApprovalAgencyTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/charterStatusDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/citizenshipStatusDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/classroomPositionDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/cohortScopeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/cohortTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/cohortYearTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/competencyLevelDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/contactTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/contentClassDescriptor",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/continuationOfServicesReasonDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/costRateDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/countryDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/courseAttemptResultDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/courseDefinedByDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/courseGPAApplicabilityDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/courseIdentificationSystemDescriptor",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/courseLevelCharacteristicDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/courseRepeatCodeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/credentialFieldDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/credentialTypeDescriptor",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/creditCategoryDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/creditTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/crisisTypeDescriptor",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Create",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Update",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Delete",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "ReadChanges",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            }
          ]
        }
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/cteProgramServiceDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/curriculumUsedDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/deliveryMethodDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/diagnosisDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/diplomaLevelDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/diplomaTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/disabilityDescriptor",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/disabilityDesignationDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/disabilityDeterminationSourceTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/disciplineActionLengthDifferenceReasonDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/disciplineDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/disciplineIncidentParticipationCodeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/displacedStudentStatusDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/educationalEnvironmentDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/educationOrganizationAssociationTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/educationOrganizationCategoryDescriptor",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/educationOrganizationIdentificationSystemDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/educationPlanDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/electronicMailTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/eligibilityDelayReasonDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/eligibilityEvaluationTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/employmentStatusDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/enrollmentTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/entryGradeLevelReasonDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/entryTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/evaluationDelayReasonDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/eventCircumstanceDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/exitWithdrawTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/financialCollectionDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/gradebookEntryTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/gradeLevelDescriptor",
        "claimSets": [
          {
            "name": "ABConnect",
            "actions": [
              {
                "name": "Create"
              },
              {
                "name": "Read"
              },
              {
                "name": "Update"
              },
              {
                "name": "Delete"
              }
            ]
          },
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/gradePointAverageTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/gradeTypeDescriptor",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/gradingPeriodDescriptor",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/graduationPlanTypeDescriptor",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/gunFreeSchoolsActReportingStatusDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/homelessPrimaryNighttimeResidenceDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/homelessProgramServiceDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/ideaPartDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/identificationDocumentUseDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/immunizationTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/incidentLocationDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/indicatorDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/indicatorGroupDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/indicatorLevelDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/institutionTelephoneNumberTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/interactivityStyleDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/internetAccessDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/internetAccessTypeInResidenceDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/internetPerformanceInResidenceDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/interventionClassDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/interventionEffectivenessRatingDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/languageDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/languageInstructionProgramServiceDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/languageUseDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/learningStandardCategoryDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/learningStandardEquivalenceStrengthDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/learningStandardScopeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/levelOfEducationDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/licenseStatusDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/licenseTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/limitedEnglishProficiencyDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/localeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/localEducationAgencyCategoryDescriptor",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/magnetSpecialProgramEmphasisSchoolDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/mediumOfInstructionDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/methodCreditEarnedDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/migrantEducationProgramServiceDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/modelEntityDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/monitoredDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/neglectedOrDelinquentProgramDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/neglectedOrDelinquentProgramServiceDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/networkPurposeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/nonMedicalImmunizationExemptionDescriptor",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Create",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Update",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Delete",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "ReadChanges",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            }
          ]
        }
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/operationalStatusDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/otherNameTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/participationDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/participationStatusDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/performanceBaseConversionDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/personalInformationVerificationDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/platformTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/populationServedDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/postingResultDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/postSecondaryEventCategoryDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/postSecondaryInstitutionLevelDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/primaryLearningDeviceAccessDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/primaryLearningDeviceAwayFromSchoolDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/primaryLearningDeviceProviderDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/proficiencyDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/programAssignmentDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/programCharacteristicDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/programEvaluationPeriodDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/programEvaluationTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/programSponsorDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/programTypeDescriptor",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/progressDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/progressLevelDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/providerCategoryDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/providerProfitabilityDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/providerStatusDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/publicationStatusDescriptor",
        "claimSets": [
          {
            "name": "ABConnect",
            "actions": [
              {
                "name": "Create"
              },
              {
                "name": "Read"
              },
              {
                "name": "Update"
              },
              {
                "name": "Delete"
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/questionFormDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/raceDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/ratingLevelDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/reasonExitedDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/recognitionTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/relationDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/repeatIdentifierDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/reporterDescriptionDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/reportingTagDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/residencyStatusDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/responseIndicatorDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/responsibilityDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/restraintEventReasonDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/resultDatatypeTypeDescriptor",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/retestIndicatorDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/schoolCategoryDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/schoolChoiceBasisDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/schoolChoiceImplementStatusDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/schoolFoodServiceProgramServiceDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/schoolTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/sectionCharacteristicDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/sectionTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/separationDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/separationReasonDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/serviceDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/sexDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/sourceSystemDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/specialEducationExitReasonDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/specialEducationProgramServiceDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/specialEducationSettingDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/staffClassificationDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/staffIdentificationSystemDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/staffLeaveEventCategoryDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/stateAbbreviationDescriptor",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentCharacteristicDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentIdentificationSystemDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentParticipationCodeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/submissionStatusDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/supporterMilitaryConnectionDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/surveyCategoryDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/surveyLevelDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/teachingCredentialBasisDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/teachingCredentialDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/technicalSkillsAssessmentDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/telephoneNumberTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/termDescriptor",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/titleIPartAParticipantDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/titleIPartAProgramServiceDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/titleIPartASchoolDesignationDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/transportationPublicExpenseEligibilityTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/transportationTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/travelDayofWeekDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/travelDirectionDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/tribalAffiliationDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/visaDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/weaponDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/imunizationTypeDescriptor",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Create",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Update",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Delete",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "ReadChanges",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            }
          ]
        }
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/Section504DisabilityDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/DualCreditTypeDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/DualCreditInstitutionDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/domains/tpdm/descriptors",
        "claims": [
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/accreditationStatusDescriptor"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/aidTypeDescriptor"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/certificationRouteDescriptor"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/coteachingStyleObservedDescriptor"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/credentialStatusDescriptor"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/educatorRoleDescriptor"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/englishLanguageExamDescriptor"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/ePPProgramPathwayDescriptor"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/evaluationElementRatingLevelDescriptor"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/evaluationPeriodDescriptor"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/evaluationRatingLevelDescriptor"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/evaluationRatingStatusDescriptor"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/evaluationTypeDescriptor"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/genderDescriptor"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/objectiveRatingLevelDescriptor"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/performanceEvaluationRatingLevelDescriptor"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/performanceEvaluationTypeDescriptor"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/rubricRatingLevelDescriptor"
          }
        ]
      }
    ]
  },
  {
    "name": "http://ed-fi.org/identity/claims/domains/managedDescriptors",
    "defaultAuthorization": {
      "actions": [
        {
          "name": "Create",
          "authorizationStrategies": [
            {
              "name": "NamespaceBased"
            }
          ]
        },
        {
          "name": "Read",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        },
        {
          "name": "Update",
          "authorizationStrategies": [
            {
              "name": "NamespaceBased"
            }
          ]
        },
        {
          "name": "Delete",
          "authorizationStrategies": [
            {
              "name": "NamespaceBased"
            }
          ]
        },
        {
          "name": "ReadChanges",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        }
      ]
    },
    "claimSets": [
      {
        "name": "SISVendor",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      },
      {
        "name": "EdFiSandbox",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "AssessmentVendor",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      },
      {
        "name": "BootstrapDescriptorsandEdOrgs",
        "actions": [
          {
            "name": "Create",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          }
        ]
      },
      {
        "name": "DistrictHostedSISVendor",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      },
      {
        "name": "EdFiODSAdminApp",
        "actions": [
          {
            "name": "Create",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Read",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Update",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Delete",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherReader",
        "actions": [
          {
            "name": "Read"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherWriter",
        "actions": [
          {
            "name": "Create",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Read",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Update",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Delete",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          }
        ]
      }
    ],
    "claims": [
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/reasonNotTestedDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/accommodationDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentPeriodDescriptor"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentReportingMethodDescriptor",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/performanceLevelDescriptor",
        "claimSets": [
          {
            "name": "AssessmentVendor",
            "actions": [
              {
                "name": "Create"
              },
              {
                "name": "Read"
              }
            ]
          },
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      }
    ]
  },
  {
    "name": "http://ed-fi.org/identity/claims/domains/educationOrganizations",
    "defaultAuthorization": {
      "actions": [
        {
          "name": "Create",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        },
        {
          "name": "Read",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        },
        {
          "name": "Update",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        },
        {
          "name": "Delete",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        },
        {
          "name": "ReadChanges",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        }
      ]
    },
    "claimSets": [
      {
        "name": "SISVendor",
        "actions": [
          {
            "name": "Read"
          }
        ]
      },
      {
        "name": "EdFiSandbox",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "RosterVendor",
        "actions": [
          {
            "name": "Read"
          }
        ]
      },
      {
        "name": "BootstrapDescriptorsandEdOrgs",
        "actions": [
          {
            "name": "Create",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          }
        ]
      },
      {
        "name": "DistrictHostedSISVendor",
        "actions": [
          {
            "name": "Read"
          }
        ]
      },
      {
        "name": "EdFiODSAdminApp",
        "actions": [
          {
            "name": "Create",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Read",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Update",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Delete",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherReader",
        "actions": [
          {
            "name": "Read"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherWriter",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      },
      {
        "name": "EducationPreparationProgram",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      }
    ],
    "claims": [
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/communityOrganization",
        "claimSets": [
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/communityProvider",
        "claimSets": [
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/educationOrganizationNetwork",
        "claimSets": [
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/educationServiceCenter",
        "claimSets": [
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/localEducationAgency",
        "claimSets": [
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "DistrictHostedSISVendor",
            "actions": [
              {
                "name": "Read"
              },
              {
                "name": "Update"
              }
            ]
          },
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "RelationshipsWithEdOrgsOnly"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "RelationshipsWithEdOrgsOnly"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "RelationshipsWithEdOrgsOnly"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "RelationshipsWithEdOrgsOnly"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "RelationshipsWithEdOrgsOnly"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/organizationDepartment",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Create",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsAndPeople"
                }
              ]
            },
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsAndPeople"
                }
              ]
            },
            {
              "name": "Update",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsAndPeople"
                }
              ]
            },
            {
              "name": "Delete",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsAndPeople"
                }
              ]
            },
            {
              "name": "ReadChanges",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            }
          ]
        },
        "claimSets": [
          {
            "name": "DistrictHostedSISVendor",
            "actions": [
              {
                "name": "Create"
              },
              {
                "name": "Read"
              },
              {
                "name": "Update"
              },
              {
                "name": "Delete"
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/postSecondaryInstitution",
        "claimSets": [
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/school",
        "claimSets": [
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "DistrictHostedSISVendor",
            "actions": [
              {
                "name": "Create"
              },
              {
                "name": "Read"
              },
              {
                "name": "Update"
              },
              {
                "name": "Delete"
              }
            ]
          },
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/stateEducationAgency",
        "claimSets": [
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      }
    ]
  },
  {
    "name": "http://ed-fi.org/identity/claims/domains/people",
    "defaultAuthorization": {
      "actions": [
        {
          "name": "Create",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        },
        {
          "name": "Read",
          "authorizationStrategies": [
            {
              "name": "RelationshipsWithEdOrgsAndPeople"
            }
          ]
        },
        {
          "name": "Update",
          "authorizationStrategies": [
            {
              "name": "RelationshipsWithEdOrgsAndPeople"
            }
          ]
        },
        {
          "name": "Delete",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        },
        {
          "name": "ReadChanges",
          "authorizationStrategies": [
            {
              "name": "RelationshipsWithEdOrgsAndPeopleIncludingDeletes"
            }
          ]
        }
      ]
    },
    "claimSets": [
      {
        "name": "SISVendor",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      },
      {
        "name": "EdFiSandbox",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "DistrictHostedSISVendor",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherReader",
        "actions": [
          {
            "name": "Read"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherWriter",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      }
    ],
    "claims": [
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/contact",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/staff",
        "claimSets": [
          {
            "name": "RosterVendor",
            "actions": [
              {
                "name": "Read"
              }
            ]
          },
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/student",
        "claimSets": [
          {
            "name": "RosterVendor",
            "actions": [
              {
                "name": "Read"
              }
            ]
          },
          {
            "name": "AssessmentVendor",
            "actions": [
              {
                "name": "Read"
              }
            ]
          },
          {
            "name": "AssessmentRead",
            "actions": [
              {
                "name": "Read"
              }
            ]
          },
          {
            "name": "EducationPreparationProgram",
            "actions": [
              {
                "name": "Create"
              },
              {
                "name": "Read"
              },
              {
                "name": "Update"
              },
              {
                "name": "Delete"
              }
            ]
          },
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/tpdm/candidate",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Create",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "Update",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "Delete",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "ReadChanges",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            }
          ]
        },
        "claimSets": [
          {
            "name": "EducationPreparationProgram",
            "actions": [
              {
                "name": "Create"
              },
              {
                "name": "Read"
              },
              {
                "name": "Update"
              },
              {
                "name": "Delete"
              }
            ]
          }
        ]
      }
    ]
  },
  {
    "name": "http://ed-fi.org/identity/claims/domains/relationshipBasedData",
    "defaultAuthorization": {
      "actions": [
        {
          "name": "Create",
          "authorizationStrategies": [
            {
              "name": "RelationshipsWithEdOrgsAndPeople"
            }
          ]
        },
        {
          "name": "Read",
          "authorizationStrategies": [
            {
              "name": "RelationshipsWithEdOrgsAndPeople"
            }
          ]
        },
        {
          "name": "Update",
          "authorizationStrategies": [
            {
              "name": "RelationshipsWithEdOrgsAndPeople"
            }
          ]
        },
        {
          "name": "Delete",
          "authorizationStrategies": [
            {
              "name": "RelationshipsWithEdOrgsAndPeople"
            }
          ]
        },
        {
          "name": "ReadChanges",
          "authorizationStrategies": [
            {
              "name": "RelationshipsWithEdOrgsAndPeopleIncludingDeletes"
            }
          ]
        }
      ]
    },
    "claimSets": [
      {
        "name": "SISVendor",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      },
      {
        "name": "EdFiSandbox",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "AssessmentVendor",
        "actions": [
          {
            "name": "Read"
          }
        ]
      },
      {
        "name": "DistrictHostedSISVendor",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherReader",
        "actions": [
          {
            "name": "Read"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherWriter",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      }
    ],
    "claims": [
      {
        "name": "http://ed-fi.org/identity/claims/domains/surveyDomain",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Create",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Update",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Delete",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "ReadChanges",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            }
          ]
        },
        "claimSets": [
          {
            "name": "EdFiSandbox",
            "actions": [
              {
                "name": "Create"
              },
              {
                "name": "Read"
              },
              {
                "name": "Update"
              },
              {
                "name": "Delete"
              },
              {
                "name": "ReadChanges"
              }
            ]
          },
          {
            "name": "EdFiAPIPublisherWriter",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "EducationPreparationProgram",
            "actions": [
              {
                "name": "Create"
              },
              {
                "name": "Read"
              },
              {
                "name": "Update"
              },
              {
                "name": "Delete"
              }
            ]
          }
        ],
        "claims": [
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/survey",
            "claimSets": [
              {
                "name": "E2E-NameSpaceBasedClaimSet",
                "actions": [
                  {
                    "name": "Create",
                    "authorizationStrategyOverrides": [
                      {
                        "name": "NamespaceBased"
                      }
                    ]
                  },
                  {
                    "name": "Read",
                    "authorizationStrategyOverrides": [
                      {
                        "name": "NamespaceBased"
                      }
                    ]
                  },
                  {
                    "name": "Update",
                    "authorizationStrategyOverrides": [
                      {
                        "name": "NamespaceBased"
                      }
                    ]
                  },
                  {
                    "name": "Delete",
                    "authorizationStrategyOverrides": [
                      {
                        "name": "NamespaceBased"
                      }
                    ]
                  },
                  {
                    "name": "ReadChanges",
                    "authorizationStrategyOverrides": [
                      {
                        "name": "NamespaceBased"
                      }
                    ]
                  }
                ]
              },
              {
                "name": "E2E-NoFurtherAuthRequiredClaimSet",
                "actions": [
                  {
                    "name": "Create",
                    "authorizationStrategyOverrides": [
                      {
                        "name": "NoFurtherAuthorizationRequired"
                      }
                    ]
                  },
                  {
                    "name": "Read",
                    "authorizationStrategyOverrides": [
                      {
                        "name": "NoFurtherAuthorizationRequired"
                      }
                    ]
                  },
                  {
                    "name": "Update",
                    "authorizationStrategyOverrides": [
                      {
                        "name": "NoFurtherAuthorizationRequired"
                      }
                    ]
                  },
                  {
                    "name": "Delete",
                    "authorizationStrategyOverrides": [
                      {
                        "name": "NoFurtherAuthorizationRequired"
                      }
                    ]
                  },
                  {
                    "name": "ReadChanges",
                    "authorizationStrategyOverrides": [
                      {
                        "name": "NoFurtherAuthorizationRequired"
                      }
                    ]
                  }
                ]
              }
            ]
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/surveyQuestion"
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/surveyQuestionResponse"
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/surveyResponse"
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/surveySection"
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/surveySectionResponse"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/surveyResponsePersonTargetAssociation"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/surveySectionResponsePersonTargetAssociation"
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/academicWeek",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "RelationshipsWithEdOrgsOnly"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "RelationshipsWithEdOrgsOnly"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "RelationshipsWithEdOrgsOnly"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "RelationshipsWithEdOrgsOnly"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "RelationshipsWithEdOrgsOnly"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/accountabilityRating",
        "claimSets": [
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/bellSchedule",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "RelationshipsWithEdOrgsOnly"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "RelationshipsWithEdOrgsOnly"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "RelationshipsWithEdOrgsOnly"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "RelationshipsWithEdOrgsOnly"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "RelationshipsWithEdOrgsOnly"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/calendar",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/calendarDate",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/classPeriod",
        "claimSets": [
          {
            "name": "RosterVendor",
            "actions": [
              {
                "name": "Read"
              }
            ]
          },
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/cohort"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/communityProviderLicense",
        "claimSets": [
          {
            "name": "EdFiSandbox",
            "actions": [
              {
                "name": "Create"
              },
              {
                "name": "Read"
              },
              {
                "name": "Update"
              },
              {
                "name": "Delete"
              }
            ]
          },
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "EdFiAPIPublisherReader",
            "actions": [
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "EdFiAPIPublisherWriter",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/competencyObjective"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/course",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsAndPeople"
                }
              ]
            }
          ]
        },
        "claimSets": [
          {
            "name": "RosterVendor",
            "actions": [
              {
                "name": "Read"
              }
            ]
          },
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/courseOffering",
        "claimSets": [
          {
            "name": "RosterVendor",
            "actions": [
              {
                "name": "Read"
              }
            ]
          },
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/courseTranscript"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/descriptorMapping",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Create",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Update",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Delete",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "ReadChanges",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            }
          ]
        }
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/disciplineAction"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/disciplineIncident"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/educationOrganizationInterventionPrescriptionAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/educationOrganizationNetworkAssociation",
        "claimSets": [
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/educationOrganizationPeerAssociation",
        "claimSets": [
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/evaluationRubricDimension"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/feederSchoolAssociation",
        "claimSets": [
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/grade",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/gradebookEntry",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Create",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Update",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Delete",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "ReadChanges",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            }
          ]
        }
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/gradingPeriod",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/graduationPlan",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/intervention"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/interventionPrescription"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/interventionStudy"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/location",
        "claimSets": [
          {
            "name": "RosterVendor",
            "actions": [
              {
                "name": "Read"
              }
            ]
          },
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/openStaffPosition"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/person",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Create",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "Update",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "Delete",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "ReadChanges",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            }
          ]
        },
        "claimSets": [
          {
            "name": "EdFiAPIPublisherReader",
            "actions": [
              {
                "name": "Read"
              },
              {
                "name": "ReadChanges"
              }
            ]
          },
          {
            "name": "EducationPreparationProgram",
            "actions": [
              {
                "name": "Create"
              },
              {
                "name": "Read"
              },
              {
                "name": "Update"
              },
              {
                "name": "Delete"
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/postSecondaryEvent",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/program",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsAndPeople"
                }
              ]
            }
          ]
        },
        "claimSets": [
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          },
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/programEvaluation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/programEvaluationElement"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/programEvaluationObjective"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/reportCard",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/restraintEvent"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/section",
        "claimSets": [
          {
            "name": "RosterVendor",
            "actions": [
              {
                "name": "Read"
              }
            ]
          },
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/sectionAttendanceTakenEvent"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/session",
        "claimSets": [
          {
            "name": "RosterVendor",
            "actions": [
              {
                "name": "Read"
              }
            ]
          },
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/staffAbsenceEvent"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/staffCohortAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/staffDisciplineIncidentAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/staffEducationOrganizationContactAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/staffLeave"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/staffProgramAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/staffSchoolAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/staffSectionAssociation",
        "claimSets": [
          {
            "name": "RosterVendor",
            "actions": [
              {
                "name": "Read"
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentAcademicRecord"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentAssessmentEducationOrganizationAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentCohortAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentCompetencyObjective"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentCTEProgramAssociation",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentDisciplineIncidentBehaviorAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentDisciplineIncidentNonOffenderAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentEducationOrganizationAssociation",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentEducationOrganizationResponsibilityAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentGradebookEntry"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentHomelessProgramAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentInterventionAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentInterventionAttendanceEvent"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentLanguageInstructionProgramAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentMigrantEducationProgramAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentNeglectedOrDelinquentProgramAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentProgramAssociation",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentProgramAttendanceEvent"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentProgramEvaluation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentSchoolAttendanceEvent"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentSchoolFoodServiceProgramAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentSectionAssociation",
        "claimSets": [
          {
            "name": "RosterVendor",
            "actions": [
              {
                "name": "Read"
              }
            ]
          },
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentSectionAttendanceEvent"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentSpecialEducationProgramAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentSpecialEducationProgramEligibilityAssociation",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Create",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsAndPeople"
                },
                {
                  "name": "RelationshipsWithStudentsOnlyThroughResponsibility"
                }
              ]
            },
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsAndPeople"
                },
                {
                  "name": "RelationshipsWithStudentsOnlyThroughResponsibility"
                }
              ]
            },
            {
              "name": "Update",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsAndPeople"
                },
                {
                  "name": "RelationshipsWithStudentsOnlyThroughResponsibility"
                }
              ]
            },
            {
              "name": "Delete",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsAndPeople"
                },
                {
                  "name": "RelationshipsWithStudentsOnlyThroughResponsibility"
                }
              ]
            },
            {
              "name": "ReadChanges",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsAndPeopleIncludingDeletes"
                },
                {
                  "name": "RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes"
                }
              ]
            }
          ]
        }
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentTitleIPartAProgramAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentTransportation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/surveyCourseAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/surveyProgramAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/surveyResponseEducationOrganizationTargetAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/surveyResponseStaffTargetAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/surveySectionAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/surveySectionResponseEducationOrganizationTargetAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/surveySectionResponseStaffTargetAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentSection504ProgramAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentEducationOrganizationAssessmentAccommodation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentEducationOrganizationAssessmentAccommodationGeneralAccommodation"
      }
    ]
  },
  {
    "name": "http://ed-fi.org/identity/claims/domains/assessmentMetadata",
    "defaultAuthorization": {
      "actions": [
        {
          "name": "Create",
          "authorizationStrategies": [
            {
              "name": "NamespaceBased"
            }
          ]
        },
        {
          "name": "Read",
          "authorizationStrategies": [
            {
              "name": "NamespaceBased"
            }
          ]
        },
        {
          "name": "Update",
          "authorizationStrategies": [
            {
              "name": "NamespaceBased"
            }
          ]
        },
        {
          "name": "Delete",
          "authorizationStrategies": [
            {
              "name": "NamespaceBased"
            }
          ]
        },
        {
          "name": "ReadChanges",
          "authorizationStrategies": [
            {
              "name": "NamespaceBased"
            }
          ]
        }
      ]
    },
    "claimSets": [
      {
        "name": "SISVendor",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      },
      {
        "name": "EdFiSandbox",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "AssessmentVendor",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      },
      {
        "name": "AssessmentRead",
        "actions": [
          {
            "name": "Read"
          }
        ]
      },
      {
        "name": "DistrictHostedSISVendor",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherReader",
        "actions": [
          {
            "name": "Read"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherWriter",
        "actions": [
          {
            "name": "Create",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Read",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Update",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Delete",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          }
        ]
      }
    ],
    "claims": [
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/assessment",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentItem"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentScoreRangeLearningStandard"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/objectiveAssessment"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentAssessment",
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentAdministration"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentAdministrationAssessmentAdminstrationPeriod"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentAdministrationAssessmentBatteryPart"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentBatteryPart"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentBatteryPartObjectiveAssessment"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentAdministrationParticipation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentAdministrationParticipationAdministrationPointOfContact"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentAssessmentRegistration"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentAssessmentRegistrationAssessmentAccommodation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentAssessmentRegistrationAssessmentCustomization"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentAssessmentRegistrationBatteryPartAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentAssessmentRegistrationBatteryPartAssociationAccommodation"
      }
    ]
  },
  {
    "name": "http://ed-fi.org/identity/claims/services/identity",
    "defaultAuthorization": {
      "actions": [
        {
          "name": "Create",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        },
        {
          "name": "Read",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        },
        {
          "name": "Update",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        }
      ]
    },
    "claimSets": [
      {
        "name": "EdFiSandbox",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          }
        ]
      }
    ]
  },
  {
    "name": "http://ed-fi.org/identity/claims/domains/educationStandards",
    "defaultAuthorization": {
      "actions": [
        {
          "name": "Create",
          "authorizationStrategies": [
            {
              "name": "NamespaceBased"
            }
          ]
        },
        {
          "name": "Read",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        },
        {
          "name": "Update",
          "authorizationStrategies": [
            {
              "name": "NamespaceBased"
            }
          ]
        },
        {
          "name": "Delete",
          "authorizationStrategies": [
            {
              "name": "NamespaceBased"
            }
          ]
        },
        {
          "name": "ReadChanges",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        }
      ]
    },
    "claimSets": [
      {
        "name": "SISVendor",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      },
      {
        "name": "EdFiSandbox",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "DistrictHostedSISVendor",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      },
      {
        "name": "ABConnect",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherReader",
        "actions": [
          {
            "name": "Read"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherWriter",
        "actions": [
          {
            "name": "Create",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Read",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Update",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Delete",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          }
        ]
      }
    ],
    "claims": [
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/credential",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Create",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "Update",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "Delete",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "ReadChanges",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            }
          ]
        },
        "claimSets": [
          {
            "name": "EducationPreparationProgram",
            "actions": [
              {
                "name": "Create"
              },
              {
                "name": "Read"
              },
              {
                "name": "Update"
              },
              {
                "name": "Delete"
              }
            ]
          },
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/learningStandard",
        "claimSets": [
          {
            "name": "AssessmentVendor",
            "actions": [
              {
                "name": "Create"
              },
              {
                "name": "Read"
              },
              {
                "name": "Update"
              },
              {
                "name": "Delete"
              }
            ]
          },
          {
            "name": "AssessmentRead",
            "actions": [
              {
                "name": "Read"
              }
            ]
          },
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/learningStandardEquivalenceAssociation",
        "claimSets": [
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      }
    ]
  },
  {
    "name": "http://ed-fi.org/identity/claims/domains/primaryRelationships",
    "defaultAuthorization": {
      "actions": [
        {
          "name": "Create",
          "authorizationStrategies": [
            {
              "name": "RelationshipsWithEdOrgsOnly"
            }
          ]
        },
        {
          "name": "Read",
          "authorizationStrategies": [
            {
              "name": "RelationshipsWithEdOrgsAndPeople"
            }
          ]
        },
        {
          "name": "Update",
          "authorizationStrategies": [
            {
              "name": "RelationshipsWithEdOrgsAndPeople"
            }
          ]
        },
        {
          "name": "Delete",
          "authorizationStrategies": [
            {
              "name": "RelationshipsWithEdOrgsAndPeople"
            }
          ]
        },
        {
          "name": "ReadChanges",
          "authorizationStrategies": [
            {
              "name": "RelationshipsWithEdOrgsAndPeopleIncludingDeletes"
            }
          ]
        }
      ]
    },
    "claimSets": [
      {
        "name": "SISVendor",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      },
      {
        "name": "EdFiSandbox",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "DistrictHostedSISVendor",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherReader",
        "actions": [
          {
            "name": "Read"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherWriter",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      }
    ],
    "claims": [
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentContactAssociation",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Create",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithStudentsOnly"
                }
              ]
            },
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithStudentsOnly"
                }
              ]
            },
            {
              "name": "Update",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithStudentsOnly"
                }
              ]
            },
            {
              "name": "Delete",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithStudentsOnly"
                }
              ]
            },
            {
              "name": "ReadChanges",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithStudentsOnlyIncludingDeletes"
                }
              ]
            }
          ]
        },
        "claimSets": [
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/staffEducationOrganizationAssignmentAssociation",
        "claimSets": [
          {
            "name": "RosterVendor",
            "actions": [
              {
                "name": "Read"
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/staffEducationOrganizationEmploymentAssociation"
      },
      {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentSchoolAssociation",
        "claimSets": [
          {
            "name": "RosterVendor",
            "actions": [
              {
                "name": "Read"
              }
            ]
          },
          {
            "name": "EducationPreparationProgram",
            "actions": [
              {
                "name": "Create"
              },
              {
                "name": "Read"
              },
              {
                "name": "Update"
              },
              {
                "name": "Delete"
              }
            ]
          },
          {
            "name": "E2E-NoFurtherAuthRequiredClaimSet",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Read",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Update",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "Delete",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              },
              {
                "name": "ReadChanges",
                "authorizationStrategyOverrides": [
                  {
                    "name": "NoFurtherAuthorizationRequired"
                  }
                ]
              }
            ]
          }
        ]
      }
    ]
  },
  {
    "name": "http://ed-fi.org/identity/claims/ed-fi/educationContent",
    "defaultAuthorization": {
      "actions": [
        {
          "name": "Create",
          "authorizationStrategies": [
            {
              "name": "NamespaceBased"
            }
          ]
        },
        {
          "name": "Read",
          "authorizationStrategies": [
            {
              "name": "NamespaceBased"
            }
          ]
        },
        {
          "name": "Update",
          "authorizationStrategies": [
            {
              "name": "NamespaceBased"
            }
          ]
        },
        {
          "name": "Delete",
          "authorizationStrategies": [
            {
              "name": "NamespaceBased"
            }
          ]
        },
        {
          "name": "ReadChanges",
          "authorizationStrategies": [
            {
              "name": "NamespaceBased"
            }
          ]
        }
      ]
    },
    "claimSets": [
      {
        "name": "SISVendor",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      },
      {
        "name": "EdFiSandbox",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "DistrictHostedSISVendor",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherReader",
        "actions": [
          {
            "name": "Read"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherWriter",
        "actions": [
          {
            "name": "Create",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Read",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Update",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Delete",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          }
        ]
      },
      {
        "name": "E2E-NoFurtherAuthRequiredClaimSet",
        "actions": [
          {
            "name": "Create",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Read",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Update",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "Delete",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          },
          {
            "name": "ReadChanges",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          }
        ]
      }
    ]
  },
  {
    "name": "http://ed-fi.org/identity/claims/domains/finance",
    "claimSets": [
      {
        "name": "EdFiSandbox",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherReader",
        "actions": [
          {
            "name": "Read"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherWriter",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      }
    ],
    "claims": [
      {
        "name": "http://ed-fi.org/identity/claims/domains/finance/dimensions",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Create",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "Update",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "Delete",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "ReadChanges",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            }
          ]
        },
        "claimSets": [
          {
            "name": "FinanceVendor",
            "actions": [
              {
                "name": "Read"
              }
            ]
          }
        ],
        "claims": [
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/fundDimension"
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/programDimension"
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/functionDimension"
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/objectDimension"
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/projectDimension"
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/operationalUnitDimension"
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/sourceDimension"
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/balanceSheetDimension"
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/domains/finance/locals",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Create",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsAndPeople"
                }
              ]
            },
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsAndPeople"
                }
              ]
            },
            {
              "name": "Update",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsAndPeople"
                }
              ]
            },
            {
              "name": "Delete",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsAndPeople"
                }
              ]
            },
            {
              "name": "ReadChanges",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsAndPeople"
                }
              ]
            }
          ]
        },
        "claimSets": [
          {
            "name": "FinanceVendor",
            "actions": [
              {
                "name": "Create"
              },
              {
                "name": "Read"
              },
              {
                "name": "Update"
              },
              {
                "name": "Delete"
              }
            ]
          }
        ],
        "claims": [
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/chartOfAccount",
            "defaultAuthorization": {
              "actions": [
                {
                  "name": "Read",
                  "authorizationStrategies": [
                    {
                      "name": "RelationshipsWithEdOrgsAndPeople"
                    }
                  ]
                }
              ]
            }
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/localAccount"
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/localBudget"
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/localActual"
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/localEncumbrance"
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/localContractedStaff"
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/localPayroll"
          }
        ]
      }
    ]
  },
  {
    "name": "http://ed-fi.org/identity/claims/ed-fi/crisisEvent",
    "defaultAuthorization": {
      "actions": [
        {
          "name": "Create",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        },
        {
          "name": "Read",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        },
        {
          "name": "Update",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        },
        {
          "name": "Delete",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        },
        {
          "name": "ReadChanges",
          "authorizationStrategies": [
            {
              "name": "NoFurtherAuthorizationRequired"
            }
          ]
        }
      ]
    },
    "claimSets": [
      {
        "name": "SISVendor",
        "actions": [
          {
            "name": "Read"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "EdFiSandbox",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "BootstrapDescriptorsandEdOrgs",
        "actions": [
          {
            "name": "Create"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherReader",
        "actions": [
          {
            "name": "Read"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherWriter",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      }
    ]
  },
  {
    "name": "http://ed-fi.org/identity/claims/ed-fi/studentHealth",
    "defaultAuthorization": {
      "actions": [
        {
          "name": "Create",
          "authorizationStrategies": [
            {
              "name": "RelationshipsWithEdOrgsAndPeople"
            }
          ]
        },
        {
          "name": "Read",
          "authorizationStrategies": [
            {
              "name": "RelationshipsWithEdOrgsAndPeople"
            }
          ]
        },
        {
          "name": "Update",
          "authorizationStrategies": [
            {
              "name": "RelationshipsWithEdOrgsAndPeople"
            }
          ]
        },
        {
          "name": "Delete",
          "authorizationStrategies": [
            {
              "name": "RelationshipsWithEdOrgsAndPeople"
            }
          ]
        },
        {
          "name": "ReadChanges",
          "authorizationStrategies": [
            {
              "name": "RelationshipsWithEdOrgsAndPeople"
            }
          ]
        }
      ]
    },
    "claimSets": [
      {
        "name": "E2E-NoFurtherAuthRequiredClaimSet",
        "actions": [
            {
            "name": "Create",
            "authorizationStrategyOverrides": [
                {
                "name": "NoFurtherAuthorizationRequired"
                }
            ]
            },
            {
            "name": "Read",
            "authorizationStrategyOverrides": [
                {
                "name": "NoFurtherAuthorizationRequired"
                }
            ]
            },
            {
            "name": "Update",
            "authorizationStrategyOverrides": [
                {
                "name": "NoFurtherAuthorizationRequired"
                }
            ]
            },
            {
            "name": "Delete",
            "authorizationStrategyOverrides": [
                {
                "name": "NoFurtherAuthorizationRequired"
                }
            ]
            },
            {
            "name": "ReadChanges",
            "authorizationStrategyOverrides": [
                {
                "name": "NoFurtherAuthorizationRequired"
                }
            ]
            }
        ]
      },
      {
        "name": "EdFiSandbox",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherReader",
        "actions": [
          {
            "name": "Read"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherWriter",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      }
    ]
  },
  {
  "name": "http://ed-fi.org/identity/claims/domains/homograph",
  "defaultAuthorization": {
    "actions": [
      {
        "name": "Create",
        "authorizationStrategies": [
          {
            "name": "NoFurtherAuthorizationRequired"
          }
        ]
      },
      {
        "name": "Read",
        "authorizationStrategies": [
          {
            "name": "NoFurtherAuthorizationRequired"
          }
        ]
      },
      {
        "name": "Update",
        "authorizationStrategies": [
          {
            "name": "NoFurtherAuthorizationRequired"
          }
        ]
      },
      {
        "name": "Delete",
        "authorizationStrategies": [
          {
            "name": "NoFurtherAuthorizationRequired"
          }
        ]
      },
      {
        "name": "ReadChanges",
        "authorizationStrategies": [
          {
            "name": "NoFurtherAuthorizationRequired"
          }
        ]
      }
    ]
  },
  "claimSets": [
    {
      "name": "EdFiSandbox",
      "actions": [
        {
          "name": "Create"
        },
        {
          "name": "Read"
        },
        {
          "name": "Update"
        },
        {
          "name": "Delete"
        },
        {
          "name": "ReadChanges"
        }
      ]
    }
  ],
  "claims": [
    {
      "name": "http://ed-fi.org/identity/claims/homograph/name"
    },
    {
      "name": "http://ed-fi.org/identity/claims/homograph/school"
    },
    {
      "name": "http://ed-fi.org/identity/claims/homograph/contact"
    },
    {
      "name": "http://ed-fi.org/identity/claims/homograph/student"
    },
    {
      "name": "http://ed-fi.org/identity/claims/homograph/staff"
    },
    {
      "name": "http://ed-fi.org/identity/claims/homograph/schoolYearType"
    },
    {
      "name": "http://ed-fi.org/identity/claims/homograph/studentSchoolAssociation"
    }
  ]
},
{
  "name": "http://ed-fi.org/identity/claims/domains/sample",
  "defaultAuthorization": {
    "actions": [
      {
        "name": "Create",
        "authorizationStrategies": [
          {
            "name": "NoFurtherAuthorizationRequired"
          }
        ]
      },
      {
        "name": "Read",
        "authorizationStrategies": [
          {
            "name": "NoFurtherAuthorizationRequired"
          }
        ]
      },
      {
        "name": "Update",
        "authorizationStrategies": [
          {
            "name": "NoFurtherAuthorizationRequired"
          }
        ]
      },
      {
        "name": "Delete",
        "authorizationStrategies": [
          {
            "name": "NoFurtherAuthorizationRequired"
          }
        ]
      },
      {
        "name": "ReadChanges",
        "authorizationStrategies": [
          {
            "name": "NoFurtherAuthorizationRequired"
          }
        ]
      }
    ]
  },
  "claimSets": [
    {
      "name": "EdFiSandbox",
      "actions": [
        {
          "name": "Create"
        },
        {
          "name": "Read"
        },
        {
          "name": "Update"
        },
        {
          "name": "Delete"
        },
        {
          "name": "ReadChanges"
        }
      ]
    }
  ],
  "claims": [
    {
      "name": "http://ed-fi.org/identity/claims/sample/bus"
    },
    {
      "name": "http://ed-fi.org/identity/claims/sample/busRoute"
    }
  ]
},
  {
    "name": "http://ed-fi.org/identity/claims/domains/tpdm",
    "claimSets": [
      {
        "name": "EdFiSandbox",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherReader",
        "actions": [
          {
            "name": "Read"
          },
          {
            "name": "ReadChanges"
          }
        ]
      },
      {
        "name": "EdFiAPIPublisherWriter",
        "actions": [
          {
            "name": "Create"
          },
          {
            "name": "Read"
          },
          {
            "name": "Update"
          },
          {
            "name": "Delete"
          }
        ]
      }
    ],
    "claims": [
      {
        "name": "http://ed-fi.org/identity/claims/domains/tpdm/performanceEvaluation",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Create",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsOnly"
                }
              ]
            },
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsOnly"
                }
              ]
            },
            {
              "name": "Update",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsOnly"
                }
              ]
            },
            {
              "name": "Delete",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsOnly"
                }
              ]
            },
            {
              "name": "ReadChanges",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsOnly"
                }
              ]
            }
          ]
        },
        "claimSets": [
          {
            "name": "EducationPreparationProgram",
            "actions": [
              {
                "name": "Create"
              },
              {
                "name": "Read"
              },
              {
                "name": "Update"
              },
              {
                "name": "Delete"
              }
            ]
          }
        ],
        "claims": [
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/performanceEvaluation"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/evaluation"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/evaluationObjective"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/evaluationElement"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/rubricDimension"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/evaluationRating"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/evaluationObjectiveRating"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/evaluationElementRating"
          },
          {
            "name": "http://ed-fi.org/identity/claims/tpdm/performanceEvaluationRating"
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/domains/tpdm/noFurtherAuthorizationRequiredData",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Create",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "Update",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "Delete",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "ReadChanges",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            }
          ]
        },
        "claims": [
          {
            "name": "http://ed-fi.org/identity/claims/domains/tpdm/candidatePreparation",
            "claimSets": [
              {
                "name": "EducationPreparationProgram",
                "actions": [
                  {
                    "name": "Create"
                  },
                  {
                    "name": "Read"
                  },
                  {
                    "name": "Update"
                  },
                  {
                    "name": "Delete"
                  }
                ]
              }
            ],
            "claims": [
              {
                "name": "http://ed-fi.org/identity/claims/tpdm/candidateEducatorPreparationProgramAssociation"
              }
            ]
          },
          {
            "name": "http://ed-fi.org/identity/claims/domains/tpdm/students",
            "claims": [
              {
                "name": "http://ed-fi.org/identity/claims/tpdm/financialAid"
              }
            ]
          }
        ]
      },
      {
        "name": "http://ed-fi.org/identity/claims/tpdm/educatorPreparationProgram",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Create",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsOnly"
                }
              ]
            },
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsOnly"
                }
              ]
            },
            {
              "name": "Update",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsOnly"
                }
              ]
            },
            {
              "name": "Delete",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsOnly"
                }
              ]
            },
            {
              "name": "ReadChanges",
              "authorizationStrategies": [
                {
                  "name": "RelationshipsWithEdOrgsOnly"
                }
              ]
            }
          ]
        },
        "claimSets": [
          {
            "name": "BootstrapDescriptorsandEdOrgs",
            "actions": [
              {
                "name": "Create"
              }
            ]
          },
          {
            "name": "EducationPreparationProgram",
            "actions": [
              {
                "name": "Create"
              },
              {
                "name": "Read"
              },
              {
                "name": "Update"
              },
              {
                "name": "Delete"
              }
            ]
          }
        ]
      }
    ]
  },
  {
    "name": "http://ed-fi.org/identity/claims/publishing/snapshot",
    "claimSets": [
      {
        "name": "EdFiAPIPublisherReader",
        "actions": [
          {
            "name": "Read",
            "authorizationStrategyOverrides": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          }
        ]
      }
    ]
  },
    {
        "name": "http://ed-fi.org/identity/claims/domains/edFiTypes",
        "defaultAuthorization": {
            "actions": [
                {
                    "name": "Read",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                }
            ]
        },
        "claimSets": [
            {
                "name": "SISVendor",
                "actions": [
                    {
                        "name": "Read"
                    }
                ]
            },
            {
                "name": "EdFiSandbox",
                "actions": [
                    {
                        "name": "Read"
                    }
                ]
            },
            {
                "name": "AssessmentVendor",
                "actions": [
                    {
                        "name": "Read"
                    }
                ]
            },
            {
                "name": "DistrictHostedSISVendor",
                "actions": [
                    {
                        "name": "Read"
                    }
                ]
            },
            {
                "name": "EdFiODSAdminApp",
                "actions": [
                    {
                        "name": "Read",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherReader",
                "actions": [
                    {
                        "name": "Read"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherWriter",
                "actions": [
                    {
                        "name": "Create",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Read",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Update",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Delete",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    }
                ]
            }
        ],
        "claims": [
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/schoolYearType",
                "claimSets": [
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "E2E-NameSpaceBasedClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            }
        ]
    },
    {
        "name": "http://ed-fi.org/identity/claims/domains/systemDescriptors",
        "defaultAuthorization": {
            "actions": [
                {
                    "name": "Create",
                    "authorizationStrategies": [
                        {
                            "name": "NamespaceBased"
                        }
                    ]
                },
                {
                    "name": "Read",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "Update",
                    "authorizationStrategies": [
                        {
                            "name": "NamespaceBased"
                        }
                    ]
                },
                {
                    "name": "Delete",
                    "authorizationStrategies": [
                        {
                            "name": "NamespaceBased"
                        }
                    ]
                },
                {
                    "name": "ReadChanges",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                }
            ]
        },
        "claimSets": [
            {
                "name": "SISVendor",
                "actions": [
                    {
                        "name": "Read"
                    }
                ]
            },
            {
                "name": "EdFiSandbox",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "AssessmentVendor",
                "actions": [
                    {
                        "name": "Read"
                    }
                ]
            },
            {
                "name": "BootstrapDescriptorsandEdOrgs",
                "actions": [
                    {
                        "name": "Create",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    }
                ]
            },
            {
                "name": "DistrictHostedSISVendor",
                "actions": [
                    {
                        "name": "Read"
                    }
                ]
            },
            {
                "name": "EdFiODSAdminApp",
                "actions": [
                    {
                        "name": "Create",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Read",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Update",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Delete",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherReader",
                "actions": [
                    {
                        "name": "Read"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherWriter",
                "actions": [
                    {
                        "name": "Create",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Read",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Update",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Delete",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    }
                ]
            },
            {
                "name": "EducationPreparationProgram",
                "actions": [
                    {
                        "name": "Read"
                    }
                ]
            }
        ],
        "claims": [
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/absenceEventCategoryDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NameSpaceBasedClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NamespaceBased"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NamespaceBased"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NamespaceBased"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NamespaceBased"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NamespaceBased"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/academicHonorCategoryDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/academicSubjectDescriptor",
                "claimSets": [
                    {
                        "name": "AssessmentVendor",
                        "actions": [
                            {
                                "name": "Create"
                            },
                            {
                                "name": "Read"
                            },
                            {
                                "name": "Update"
                            },
                            {
                                "name": "Delete"
                            }
                        ]
                    },
                    {
                        "name": "ABConnect",
                        "actions": [
                            {
                                "name": "Create"
                            },
                            {
                                "name": "Read"
                            },
                            {
                                "name": "Update"
                            },
                            {
                                "name": "Delete"
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/accountTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/achievementCategoryDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/additionalCreditTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/addressTypeDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/administrationEnvironmentDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/administrativeFundingControlDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/ancestryEthnicOriginDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentCategoryDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentIdentificationSystemDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentItemCategoryDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentItemResultDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/assignmentLateStatusDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/attemptStatusDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/attendanceEventCategoryDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/barrierToInternetAccessInResidenceDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/behaviorDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/busRouteDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/calendarEventDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/calendarTypeDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/careerPathwayDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/charterApprovalAgencyTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/charterStatusDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/citizenshipStatusDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/classroomPositionDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/cohortScopeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/cohortTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/cohortYearTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/competencyLevelDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/contactTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/contentClassDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/continuationOfServicesReasonDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/costRateDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/countryDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/courseAttemptResultDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/courseDefinedByDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/courseGPAApplicabilityDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/courseIdentificationSystemDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/courseLevelCharacteristicDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/courseRepeatCodeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/credentialFieldDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/credentialTypeDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/creditCategoryDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/creditTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/crisisTypeDescriptor",
                "defaultAuthorization": {
                    "actions": [
                        {
                            "name": "Create",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "Read",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "Update",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "Delete",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "ReadChanges",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        }
                    ]
                }
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/cteProgramServiceDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/curriculumUsedDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/deliveryMethodDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/diagnosisDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/diplomaLevelDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/diplomaTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/disabilityDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/disabilityDesignationDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/disabilityDeterminationSourceTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/disciplineActionLengthDifferenceReasonDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/disciplineDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/disciplineIncidentParticipationCodeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/displacedStudentStatusDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/educationalEnvironmentDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/educationOrganizationAssociationTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/educationOrganizationCategoryDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/educationOrganizationIdentificationSystemDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/educationPlanDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/electronicMailTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/eligibilityDelayReasonDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/eligibilityEvaluationTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/employmentStatusDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/enrollmentTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/entryGradeLevelReasonDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/entryTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/evaluationDelayReasonDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/eventCircumstanceDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/exitWithdrawTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/financialCollectionDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/gradebookEntryTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/gradeLevelDescriptor",
                "claimSets": [
                    {
                        "name": "ABConnect",
                        "actions": [
                            {
                                "name": "Create"
                            },
                            {
                                "name": "Read"
                            },
                            {
                                "name": "Update"
                            },
                            {
                                "name": "Delete"
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/gradePointAverageTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/gradeTypeDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/gradingPeriodDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/graduationPlanTypeDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/gunFreeSchoolsActReportingStatusDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/homelessPrimaryNighttimeResidenceDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/homelessProgramServiceDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/ideaPartDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/identificationDocumentUseDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/immunizationTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/incidentLocationDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/indicatorDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/indicatorGroupDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/indicatorLevelDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/institutionTelephoneNumberTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/interactivityStyleDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/internetAccessDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/internetAccessTypeInResidenceDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/internetPerformanceInResidenceDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/interventionClassDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/interventionEffectivenessRatingDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/languageDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/languageInstructionProgramServiceDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/languageUseDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/learningStandardCategoryDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/learningStandardEquivalenceStrengthDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/learningStandardScopeDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/levelOfEducationDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/licenseStatusDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/licenseTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/limitedEnglishProficiencyDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/localeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/localEducationAgencyCategoryDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/magnetSpecialProgramEmphasisSchoolDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/mediumOfInstructionDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/methodCreditEarnedDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/migrantEducationProgramServiceDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/modelEntityDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/monitoredDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/neglectedOrDelinquentProgramDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/neglectedOrDelinquentProgramServiceDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/networkPurposeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/nonMedicalImmunizationExemptionDescriptor",
                "defaultAuthorization": {
                    "actions": [
                        {
                            "name": "Create",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "Read",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "Update",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "Delete",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "ReadChanges",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        }
                    ]
                }
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/operationalStatusDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/otherNameTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/participationDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/participationStatusDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/performanceBaseConversionDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/personalInformationVerificationDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/platformTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/populationServedDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/postingResultDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/postSecondaryEventCategoryDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/postSecondaryInstitutionLevelDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/primaryLearningDeviceAccessDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/primaryLearningDeviceAwayFromSchoolDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/primaryLearningDeviceProviderDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/proficiencyDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/programAssignmentDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/programCharacteristicDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/programEvaluationPeriodDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/programEvaluationTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/programSponsorDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/programTypeDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/progressDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/progressLevelDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/providerCategoryDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/providerProfitabilityDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/providerStatusDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/publicationStatusDescriptor",
                "claimSets": [
                    {
                        "name": "ABConnect",
                        "actions": [
                            {
                                "name": "Create"
                            },
                            {
                                "name": "Read"
                            },
                            {
                                "name": "Update"
                            },
                            {
                                "name": "Delete"
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/questionFormDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/raceDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/ratingLevelDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/reasonExitedDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/recognitionTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/relationDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/repeatIdentifierDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/reporterDescriptionDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/reportingTagDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/residencyStatusDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/responseIndicatorDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/responsibilityDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/restraintEventReasonDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/resultDatatypeTypeDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/retestIndicatorDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/schoolCategoryDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/schoolChoiceBasisDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/schoolChoiceImplementStatusDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/schoolFoodServiceProgramServiceDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/schoolTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/sectionCharacteristicDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/sectionTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/separationDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/separationReasonDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/serviceDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/sexDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/sourceSystemDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/specialEducationExitReasonDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/specialEducationProgramServiceDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/specialEducationSettingDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/staffClassificationDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/staffIdentificationSystemDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/staffLeaveEventCategoryDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/stateAbbreviationDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentCharacteristicDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentIdentificationSystemDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentParticipationCodeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/submissionStatusDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/supporterMilitaryConnectionDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/surveyCategoryDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/surveyLevelDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/teachingCredentialBasisDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/teachingCredentialDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/technicalSkillsAssessmentDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/telephoneNumberTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/termDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/titleIPartAParticipantDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/titleIPartAProgramServiceDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/titleIPartASchoolDesignationDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/transportationPublicExpenseEligibilityTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/transportationTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/travelDayofWeekDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/travelDirectionDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/tribalAffiliationDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/visaDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/weaponDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/imunizationTypeDescriptor",
                "defaultAuthorization": {
                    "actions": [
                        {
                            "name": "Create",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "Read",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "Update",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "Delete",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "ReadChanges",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        }
                    ]
                }
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/Section504DisabilityDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/DualCreditTypeDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/DualCreditInstitutionDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/domains/tpdm/descriptors",
                "claims": [
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/accreditationStatusDescriptor"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/aidTypeDescriptor"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/certificationRouteDescriptor"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/coteachingStyleObservedDescriptor"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/credentialStatusDescriptor"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/educatorRoleDescriptor"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/englishLanguageExamDescriptor"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/ePPProgramPathwayDescriptor"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/evaluationElementRatingLevelDescriptor"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/evaluationPeriodDescriptor"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/evaluationRatingLevelDescriptor"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/evaluationRatingStatusDescriptor"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/evaluationTypeDescriptor"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/genderDescriptor"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/objectiveRatingLevelDescriptor"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/performanceEvaluationRatingLevelDescriptor"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/performanceEvaluationTypeDescriptor"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/rubricRatingLevelDescriptor"
                    }
                ]
            }
        ]
    },
    {
        "name": "http://ed-fi.org/identity/claims/domains/managedDescriptors",
        "defaultAuthorization": {
            "actions": [
                {
                    "name": "Create",
                    "authorizationStrategies": [
                        {
                            "name": "NamespaceBased"
                        }
                    ]
                },
                {
                    "name": "Read",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "Update",
                    "authorizationStrategies": [
                        {
                            "name": "NamespaceBased"
                        }
                    ]
                },
                {
                    "name": "Delete",
                    "authorizationStrategies": [
                        {
                            "name": "NamespaceBased"
                        }
                    ]
                },
                {
                    "name": "ReadChanges",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                }
            ]
        },
        "claimSets": [
            {
                "name": "SISVendor",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            },
            {
                "name": "EdFiSandbox",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "AssessmentVendor",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            },
            {
                "name": "BootstrapDescriptorsandEdOrgs",
                "actions": [
                    {
                        "name": "Create",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    }
                ]
            },
            {
                "name": "DistrictHostedSISVendor",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            },
            {
                "name": "EdFiODSAdminApp",
                "actions": [
                    {
                        "name": "Create",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Read",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Update",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Delete",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherReader",
                "actions": [
                    {
                        "name": "Read"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherWriter",
                "actions": [
                    {
                        "name": "Create",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Read",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Update",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Delete",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    }
                ]
            }
        ],
        "claims": [
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/reasonNotTestedDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/accommodationDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentPeriodDescriptor"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentReportingMethodDescriptor",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/performanceLevelDescriptor",
                "claimSets": [
                    {
                        "name": "AssessmentVendor",
                        "actions": [
                            {
                                "name": "Create"
                            },
                            {
                                "name": "Read"
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            }
        ]
    },
    {
        "name": "http://ed-fi.org/identity/claims/domains/educationOrganizations",
        "defaultAuthorization": {
            "actions": [
                {
                    "name": "Create",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "Read",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "Update",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "Delete",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "ReadChanges",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                }
            ]
        },
        "claimSets": [
            {
                "name": "SISVendor",
                "actions": [
                    {
                        "name": "Read"
                    }
                ]
            },
            {
                "name": "EdFiSandbox",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "RosterVendor",
                "actions": [
                    {
                        "name": "Read"
                    }
                ]
            },
            {
                "name": "BootstrapDescriptorsandEdOrgs",
                "actions": [
                    {
                        "name": "Create",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    }
                ]
            },
            {
                "name": "DistrictHostedSISVendor",
                "actions": [
                    {
                        "name": "Read"
                    }
                ]
            },
            {
                "name": "EdFiODSAdminApp",
                "actions": [
                    {
                        "name": "Create",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Read",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Update",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Delete",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherReader",
                "actions": [
                    {
                        "name": "Read"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherWriter",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            },
            {
                "name": "EducationPreparationProgram",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            }
        ],
        "claims": [
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/communityOrganization",
                "claimSets": [
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/communityProvider",
                "claimSets": [
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/educationOrganizationNetwork",
                "claimSets": [
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/educationServiceCenter",
                "claimSets": [
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/localEducationAgency",
                "claimSets": [
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "DistrictHostedSISVendor",
                        "actions": [
                            {
                                "name": "Read"
                            },
                            {
                                "name": "Update"
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "RelationshipsWithEdOrgsOnly"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "RelationshipsWithEdOrgsOnly"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "RelationshipsWithEdOrgsOnly"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "RelationshipsWithEdOrgsOnly"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "RelationshipsWithEdOrgsOnly"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/organizationDepartment",
                "defaultAuthorization": {
                    "actions": [
                        {
                            "name": "Create",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsAndPeople"
                                }
                            ]
                        },
                        {
                            "name": "Read",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsAndPeople"
                                }
                            ]
                        },
                        {
                            "name": "Update",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsAndPeople"
                                }
                            ]
                        },
                        {
                            "name": "Delete",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsAndPeople"
                                }
                            ]
                        },
                        {
                            "name": "ReadChanges",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        }
                    ]
                },
                "claimSets": [
                    {
                        "name": "DistrictHostedSISVendor",
                        "actions": [
                            {
                                "name": "Create"
                            },
                            {
                                "name": "Read"
                            },
                            {
                                "name": "Update"
                            },
                            {
                                "name": "Delete"
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/postSecondaryInstitution",
                "claimSets": [
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/school",
                "claimSets": [
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "DistrictHostedSISVendor",
                        "actions": [
                            {
                                "name": "Create"
                            },
                            {
                                "name": "Read"
                            },
                            {
                                "name": "Update"
                            },
                            {
                                "name": "Delete"
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/stateEducationAgency",
                "claimSets": [
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            }
        ]
    },
    {
        "name": "http://ed-fi.org/identity/claims/domains/people",
        "defaultAuthorization": {
            "actions": [
                {
                    "name": "Create",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "Read",
                    "authorizationStrategies": [
                        {
                            "name": "RelationshipsWithEdOrgsAndPeople"
                        }
                    ]
                },
                {
                    "name": "Update",
                    "authorizationStrategies": [
                        {
                            "name": "RelationshipsWithEdOrgsAndPeople"
                        }
                    ]
                },
                {
                    "name": "Delete",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "ReadChanges",
                    "authorizationStrategies": [
                        {
                            "name": "RelationshipsWithEdOrgsAndPeopleIncludingDeletes"
                        }
                    ]
                }
            ]
        },
        "claimSets": [
            {
                "name": "SISVendor",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            },
            {
                "name": "EdFiSandbox",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "DistrictHostedSISVendor",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherReader",
                "actions": [
                    {
                        "name": "Read"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherWriter",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            }
        ],
        "claims": [
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/contact",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/staff",
                "claimSets": [
                    {
                        "name": "RosterVendor",
                        "actions": [
                            {
                                "name": "Read"
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/student",
                "claimSets": [
                    {
                        "name": "RosterVendor",
                        "actions": [
                            {
                                "name": "Read"
                            }
                        ]
                    },
                    {
                        "name": "AssessmentVendor",
                        "actions": [
                            {
                                "name": "Read"
                            }
                        ]
                    },
                    {
                        "name": "AssessmentRead",
                        "actions": [
                            {
                                "name": "Read"
                            }
                        ]
                    },
                    {
                        "name": "EducationPreparationProgram",
                        "actions": [
                            {
                                "name": "Create"
                            },
                            {
                                "name": "Read"
                            },
                            {
                                "name": "Update"
                            },
                            {
                                "name": "Delete"
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/tpdm/candidate",
                "defaultAuthorization": {
                    "actions": [
                        {
                            "name": "Create",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "Read",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "Update",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "Delete",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "ReadChanges",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        }
                    ]
                },
                "claimSets": [
                    {
                        "name": "EducationPreparationProgram",
                        "actions": [
                            {
                                "name": "Create"
                            },
                            {
                                "name": "Read"
                            },
                            {
                                "name": "Update"
                            },
                            {
                                "name": "Delete"
                            }
                        ]
                    }
                ]
            }
        ]
    },
    {
        "name": "http://ed-fi.org/identity/claims/domains/relationshipBasedData",
        "defaultAuthorization": {
            "actions": [
                {
                    "name": "Create",
                    "authorizationStrategies": [
                        {
                            "name": "RelationshipsWithEdOrgsAndPeople"
                        }
                    ]
                },
                {
                    "name": "Read",
                    "authorizationStrategies": [
                        {
                            "name": "RelationshipsWithEdOrgsAndPeople"
                        }
                    ]
                },
                {
                    "name": "Update",
                    "authorizationStrategies": [
                        {
                            "name": "RelationshipsWithEdOrgsAndPeople"
                        }
                    ]
                },
                {
                    "name": "Delete",
                    "authorizationStrategies": [
                        {
                            "name": "RelationshipsWithEdOrgsAndPeople"
                        }
                    ]
                },
                {
                    "name": "ReadChanges",
                    "authorizationStrategies": [
                        {
                            "name": "RelationshipsWithEdOrgsAndPeopleIncludingDeletes"
                        }
                    ]
                }
            ]
        },
        "claimSets": [
            {
                "name": "SISVendor",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            },
            {
                "name": "EdFiSandbox",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "AssessmentVendor",
                "actions": [
                    {
                        "name": "Read"
                    }
                ]
            },
            {
                "name": "DistrictHostedSISVendor",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherReader",
                "actions": [
                    {
                        "name": "Read"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherWriter",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            }
        ],
        "claims": [
            {
                "name": "http://ed-fi.org/identity/claims/domains/surveyDomain",
                "defaultAuthorization": {
                    "actions": [
                        {
                            "name": "Create",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "Read",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "Update",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "Delete",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "ReadChanges",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        }
                    ]
                },
                "claimSets": [
                    {
                        "name": "EdFiSandbox",
                        "actions": [
                            {
                                "name": "Create"
                            },
                            {
                                "name": "Read"
                            },
                            {
                                "name": "Update"
                            },
                            {
                                "name": "Delete"
                            },
                            {
                                "name": "ReadChanges"
                            }
                        ]
                    },
                    {
                        "name": "EdFiAPIPublisherWriter",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "EducationPreparationProgram",
                        "actions": [
                            {
                                "name": "Create"
                            },
                            {
                                "name": "Read"
                            },
                            {
                                "name": "Update"
                            },
                            {
                                "name": "Delete"
                            }
                        ]
                    }
                ],
                "claims": [
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/survey",
                        "claimSets": [
                            {
                                "name": "E2E-NameSpaceBasedClaimSet",
                                "actions": [
                                    {
                                        "name": "Create",
                                        "authorizationStrategyOverrides": [
                                            {
                                                "name": "NamespaceBased"
                                            }
                                        ]
                                    },
                                    {
                                        "name": "Read",
                                        "authorizationStrategyOverrides": [
                                            {
                                                "name": "NamespaceBased"
                                            }
                                        ]
                                    },
                                    {
                                        "name": "Update",
                                        "authorizationStrategyOverrides": [
                                            {
                                                "name": "NamespaceBased"
                                            }
                                        ]
                                    },
                                    {
                                        "name": "Delete",
                                        "authorizationStrategyOverrides": [
                                            {
                                                "name": "NamespaceBased"
                                            }
                                        ]
                                    },
                                    {
                                        "name": "ReadChanges",
                                        "authorizationStrategyOverrides": [
                                            {
                                                "name": "NamespaceBased"
                                            }
                                        ]
                                    }
                                ]
                            },
                            {
                                "name": "E2E-NoFurtherAuthRequiredClaimSet",
                                "actions": [
                                    {
                                        "name": "Create",
                                        "authorizationStrategyOverrides": [
                                            {
                                                "name": "NoFurtherAuthorizationRequired"
                                            }
                                        ]
                                    },
                                    {
                                        "name": "Read",
                                        "authorizationStrategyOverrides": [
                                            {
                                                "name": "NoFurtherAuthorizationRequired"
                                            }
                                        ]
                                    },
                                    {
                                        "name": "Update",
                                        "authorizationStrategyOverrides": [
                                            {
                                                "name": "NoFurtherAuthorizationRequired"
                                            }
                                        ]
                                    },
                                    {
                                        "name": "Delete",
                                        "authorizationStrategyOverrides": [
                                            {
                                                "name": "NoFurtherAuthorizationRequired"
                                            }
                                        ]
                                    },
                                    {
                                        "name": "ReadChanges",
                                        "authorizationStrategyOverrides": [
                                            {
                                                "name": "NoFurtherAuthorizationRequired"
                                            }
                                        ]
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/surveyQuestion"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/surveyQuestionResponse"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/surveyResponse"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/surveySection"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/surveySectionResponse"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/surveyResponsePersonTargetAssociation"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/surveySectionResponsePersonTargetAssociation"
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/academicWeek",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "RelationshipsWithEdOrgsOnly"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "RelationshipsWithEdOrgsOnly"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "RelationshipsWithEdOrgsOnly"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "RelationshipsWithEdOrgsOnly"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "RelationshipsWithEdOrgsOnly"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/accountabilityRating",
                "claimSets": [
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/bellSchedule",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "RelationshipsWithEdOrgsOnly"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "RelationshipsWithEdOrgsOnly"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "RelationshipsWithEdOrgsOnly"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "RelationshipsWithEdOrgsOnly"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "RelationshipsWithEdOrgsOnly"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/calendar",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/calendarDate",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/classPeriod",
                "claimSets": [
                    {
                        "name": "RosterVendor",
                        "actions": [
                            {
                                "name": "Read"
                            }
                        ]
                    },
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/cohort"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/communityProviderLicense",
                "claimSets": [
                    {
                        "name": "EdFiSandbox",
                        "actions": [
                            {
                                "name": "Create"
                            },
                            {
                                "name": "Read"
                            },
                            {
                                "name": "Update"
                            },
                            {
                                "name": "Delete"
                            }
                        ]
                    },
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "EdFiAPIPublisherReader",
                        "actions": [
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "EdFiAPIPublisherWriter",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/competencyObjective"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/course",
                "defaultAuthorization": {
                    "actions": [
                        {
                            "name": "Read",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsAndPeople"
                                },
                                {
                                    "name": "RelationshipsWithEdOrgsAndPeopleInverted"
                                }
                            ]
                        }
                    ]
                },
                "claimSets": [
                    {
                        "name": "RosterVendor",
                        "actions": [
                            {
                                "name": "Read"
                            }
                        ]
                    },
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/courseOffering",
                "claimSets": [
                    {
                        "name": "RosterVendor",
                        "actions": [
                            {
                                "name": "Read"
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/courseTranscript"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/descriptorMapping",
                "defaultAuthorization": {
                    "actions": [
                        {
                            "name": "Create",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "Read",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "Update",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "Delete",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "ReadChanges",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        }
                    ]
                }
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/disciplineAction"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/disciplineIncident"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/educationOrganizationInterventionPrescriptionAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/educationOrganizationNetworkAssociation",
                "claimSets": [
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/educationOrganizationPeerAssociation",
                "claimSets": [
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/evaluationRubricDimension"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/feederSchoolAssociation",
                "claimSets": [
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/grade",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/gradebookEntry",
                "defaultAuthorization": {
                    "actions": [
                        {
                            "name": "Create",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "Read",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "Update",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "Delete",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        },
                        {
                            "name": "ReadChanges",
                            "authorizationStrategies": [
                                {
                                    "name": "NamespaceBased"
                                }
                            ]
                        }
                    ]
                }
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/gradingPeriod",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/graduationPlan",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/intervention"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/interventionPrescription"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/interventionStudy"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/location",
                "claimSets": [
                    {
                        "name": "RosterVendor",
                        "actions": [
                            {
                                "name": "Read"
                            }
                        ]
                    },
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/openStaffPosition"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/person",
                "defaultAuthorization": {
                    "actions": [
                        {
                            "name": "Create",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "Read",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "Update",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "Delete",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "ReadChanges",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        }
                    ]
                },
                "claimSets": [
                    {
                        "name": "EdFiAPIPublisherReader",
                        "actions": [
                            {
                                "name": "Read"
                            },
                            {
                                "name": "ReadChanges"
                            }
                        ]
                    },
                    {
                        "name": "EducationPreparationProgram",
                        "actions": [
                            {
                                "name": "Create"
                            },
                            {
                                "name": "Read"
                            },
                            {
                                "name": "Update"
                            },
                            {
                                "name": "Delete"
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/postSecondaryEvent",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/program",
                "defaultAuthorization": {
                    "actions": [
                        {
                            "name": "Read",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsAndPeople"
                                },
                                {
                                    "name": "RelationshipsWithEdOrgsAndPeopleInverted"
                                }
                            ]
                        }
                    ]
                },
                "claimSets": [
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/programEvaluation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/programEvaluationElement"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/programEvaluationObjective"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/reportCard",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/restraintEvent"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/section",
                "claimSets": [
                    {
                        "name": "RosterVendor",
                        "actions": [
                            {
                                "name": "Read"
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/sectionAttendanceTakenEvent"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/session",
                "claimSets": [
                    {
                        "name": "RosterVendor",
                        "actions": [
                            {
                                "name": "Read"
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/staffAbsenceEvent"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/staffCohortAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/staffDisciplineIncidentAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/staffEducationOrganizationContactAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/staffLeave"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/staffProgramAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/staffSchoolAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/staffSectionAssociation",
                "claimSets": [
                    {
                        "name": "RosterVendor",
                        "actions": [
                            {
                                "name": "Read"
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentAcademicRecord"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentAssessmentEducationOrganizationAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentCohortAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentCompetencyObjective"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentCTEProgramAssociation",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentDisciplineIncidentBehaviorAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentDisciplineIncidentNonOffenderAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentEducationOrganizationAssociation",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentEducationOrganizationResponsibilityAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentGradebookEntry"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentHomelessProgramAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentInterventionAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentInterventionAttendanceEvent"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentLanguageInstructionProgramAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentMigrantEducationProgramAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentNeglectedOrDelinquentProgramAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentProgramAssociation",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentProgramAttendanceEvent"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentProgramEvaluation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentSchoolAttendanceEvent"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentSchoolFoodServiceProgramAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentSectionAssociation",
                "claimSets": [
                    {
                        "name": "RosterVendor",
                        "actions": [
                            {
                                "name": "Read"
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentSectionAttendanceEvent"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentSpecialEducationProgramAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentSpecialEducationProgramEligibilityAssociation",
                "defaultAuthorization": {
                    "actions": [
                        {
                            "name": "Create",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsAndPeople"
                                },
                                {
                                    "name": "RelationshipsWithStudentsOnlyThroughResponsibility"
                                }
                            ]
                        },
                        {
                            "name": "Read",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsAndPeople"
                                },
                                {
                                    "name": "RelationshipsWithStudentsOnlyThroughResponsibility"
                                }
                            ]
                        },
                        {
                            "name": "Update",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsAndPeople"
                                },
                                {
                                    "name": "RelationshipsWithStudentsOnlyThroughResponsibility"
                                }
                            ]
                        },
                        {
                            "name": "Delete",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsAndPeople"
                                },
                                {
                                    "name": "RelationshipsWithStudentsOnlyThroughResponsibility"
                                }
                            ]
                        },
                        {
                            "name": "ReadChanges",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsAndPeopleIncludingDeletes"
                                },
                                {
                                    "name": "RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes"
                                }
                            ]
                        }
                    ]
                }
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentTitleIPartAProgramAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentTransportation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/surveyCourseAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/surveyProgramAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/surveyResponseEducationOrganizationTargetAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/surveyResponseStaffTargetAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/surveySectionAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/surveySectionResponseEducationOrganizationTargetAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/surveySectionResponseStaffTargetAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentSection504ProgramAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentEducationOrganizationAssessmentAccommodation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentEducationOrganizationAssessmentAccommodationGeneralAccommodation"
            }
        ]
    },
    {
        "name": "http://ed-fi.org/identity/claims/domains/assessmentMetadata",
        "defaultAuthorization": {
            "actions": [
                {
                    "name": "Create",
                    "authorizationStrategies": [
                        {
                            "name": "NamespaceBased"
                        }
                    ]
                },
                {
                    "name": "Read",
                    "authorizationStrategies": [
                        {
                            "name": "NamespaceBased"
                        }
                    ]
                },
                {
                    "name": "Update",
                    "authorizationStrategies": [
                        {
                            "name": "NamespaceBased"
                        }
                    ]
                },
                {
                    "name": "Delete",
                    "authorizationStrategies": [
                        {
                            "name": "NamespaceBased"
                        }
                    ]
                },
                {
                    "name": "ReadChanges",
                    "authorizationStrategies": [
                        {
                            "name": "NamespaceBased"
                        }
                    ]
                }
            ]
        },
        "claimSets": [
            {
                "name": "SISVendor",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            },
            {
                "name": "EdFiSandbox",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "AssessmentVendor",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            },
            {
                "name": "AssessmentRead",
                "actions": [
                    {
                        "name": "Read"
                    }
                ]
            },
            {
                "name": "DistrictHostedSISVendor",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherReader",
                "actions": [
                    {
                        "name": "Read"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherWriter",
                "actions": [
                    {
                        "name": "Create",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Read",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Update",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Delete",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    }
                ]
            }
        ],
        "claims": [
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/assessment",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentItem",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentScoreRangeLearningStandard"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/objectiveAssessment"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentAssessment",
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentAdministration"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentAdministrationAssessmentAdminstrationPeriod"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentAdministrationAssessmentBatteryPart"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentBatteryPart"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentBatteryPartObjectiveAssessment"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentAdministrationParticipation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/assessmentAdministrationParticipationAdministrationPointOfContact"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentAssessmentRegistration"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentAssessmentRegistrationAssessmentAccommodation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentAssessmentRegistrationAssessmentCustomization"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentAssessmentRegistrationBatteryPartAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentAssessmentRegistrationBatteryPartAssociationAccommodation"
            }
        ]
    },
    {
        "name": "http://ed-fi.org/identity/claims/services/identity",
        "defaultAuthorization": {
            "actions": [
                {
                    "name": "Create",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "Read",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "Update",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                }
            ]
        },
        "claimSets": [
            {
                "name": "EdFiSandbox",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    }
                ]
            }
        ]
    },
    {
        "name": "http://ed-fi.org/identity/claims/domains/educationStandards",
        "defaultAuthorization": {
            "actions": [
                {
                    "name": "Create",
                    "authorizationStrategies": [
                        {
                            "name": "NamespaceBased"
                        }
                    ]
                },
                {
                    "name": "Read",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "Update",
                    "authorizationStrategies": [
                        {
                            "name": "NamespaceBased"
                        }
                    ]
                },
                {
                    "name": "Delete",
                    "authorizationStrategies": [
                        {
                            "name": "NamespaceBased"
                        }
                    ]
                },
                {
                    "name": "ReadChanges",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                }
            ]
        },
        "claimSets": [
            {
                "name": "SISVendor",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            },
            {
                "name": "EdFiSandbox",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "DistrictHostedSISVendor",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            },
            {
                "name": "ABConnect",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherReader",
                "actions": [
                    {
                        "name": "Read"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherWriter",
                "actions": [
                    {
                        "name": "Create",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Read",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Update",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Delete",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    }
                ]
            }
        ],
        "claims": [
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/credential",
                "defaultAuthorization": {
                    "actions": [
                        {
                            "name": "Create",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "Read",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "Update",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "Delete",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "ReadChanges",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        }
                    ]
                },
                "claimSets": [
                    {
                        "name": "EducationPreparationProgram",
                        "actions": [
                            {
                                "name": "Create"
                            },
                            {
                                "name": "Read"
                            },
                            {
                                "name": "Update"
                            },
                            {
                                "name": "Delete"
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/learningStandard",
                "claimSets": [
                    {
                        "name": "AssessmentVendor",
                        "actions": [
                            {
                                "name": "Create"
                            },
                            {
                                "name": "Read"
                            },
                            {
                                "name": "Update"
                            },
                            {
                                "name": "Delete"
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "name": "AssessmentRead",
                        "actions": [
                            {
                                "name": "Read"
                            }
                        ]
                    },
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/learningStandardEquivalenceAssociation",
                "claimSets": [
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            }
        ]
    },
    {
        "name": "http://ed-fi.org/identity/claims/domains/primaryRelationships",
        "defaultAuthorization": {
            "actions": [
                {
                    "name": "Create",
                    "authorizationStrategies": [
                        {
                            "name": "RelationshipsWithEdOrgsOnly"
                        }
                    ]
                },
                {
                    "name": "Read",
                    "authorizationStrategies": [
                        {
                            "name": "RelationshipsWithEdOrgsAndPeople"
                        }
                    ]
                },
                {
                    "name": "Update",
                    "authorizationStrategies": [
                        {
                            "name": "RelationshipsWithEdOrgsAndPeople"
                        }
                    ]
                },
                {
                    "name": "Delete",
                    "authorizationStrategies": [
                        {
                            "name": "RelationshipsWithEdOrgsAndPeople"
                        }
                    ]
                },
                {
                    "name": "ReadChanges",
                    "authorizationStrategies": [
                        {
                            "name": "RelationshipsWithEdOrgsAndPeopleIncludingDeletes"
                        }
                    ]
                }
            ]
        },
        "claimSets": [
            {
                "name": "SISVendor",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            },
            {
                "name": "EdFiSandbox",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "DistrictHostedSISVendor",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherReader",
                "actions": [
                    {
                        "name": "Read"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherWriter",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            }
        ],
        "claims": [
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentContactAssociation",
                "defaultAuthorization": {
                    "actions": [
                        {
                            "name": "Create",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithStudentsOnly"
                                }
                            ]
                        },
                        {
                            "name": "Read",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithStudentsOnly"
                                }
                            ]
                        },
                        {
                            "name": "Update",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithStudentsOnly"
                                }
                            ]
                        },
                        {
                            "name": "Delete",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithStudentsOnly"
                                }
                            ]
                        },
                        {
                            "name": "ReadChanges",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithStudentsOnlyIncludingDeletes"
                                }
                            ]
                        }
                    ]
                },
                "claimSets": [
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/staffEducationOrganizationAssignmentAssociation",
                "claimSets": [
                    {
                        "name": "RosterVendor",
                        "actions": [
                            {
                                "name": "Read"
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/staffEducationOrganizationEmploymentAssociation"
            },
            {
                "name": "http://ed-fi.org/identity/claims/ed-fi/studentSchoolAssociation",
                "claimSets": [
                    {
                        "name": "RosterVendor",
                        "actions": [
                            {
                                "name": "Read"
                            }
                        ]
                    },
                    {
                        "name": "EducationPreparationProgram",
                        "actions": [
                            {
                                "name": "Create"
                            },
                            {
                                "name": "Read"
                            },
                            {
                                "name": "Update"
                            },
                            {
                                "name": "Delete"
                            }
                        ]
                    },
                    {
                        "name": "E2E-NoFurtherAuthRequiredClaimSet",
                        "actions": [
                            {
                                "name": "Create",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Read",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Update",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "Delete",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            },
                            {
                                "name": "ReadChanges",
                                "authorizationStrategyOverrides": [
                                    {
                                        "name": "NoFurtherAuthorizationRequired"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            }
        ]
    },
    {
        "name": "http://ed-fi.org/identity/claims/ed-fi/educationContent",
        "defaultAuthorization": {
            "actions": [
                {
                    "name": "Create",
                    "authorizationStrategies": [
                        {
                            "name": "NamespaceBased"
                        }
                    ]
                },
                {
                    "name": "Read",
                    "authorizationStrategies": [
                        {
                            "name": "NamespaceBased"
                        }
                    ]
                },
                {
                    "name": "Update",
                    "authorizationStrategies": [
                        {
                            "name": "NamespaceBased"
                        }
                    ]
                },
                {
                    "name": "Delete",
                    "authorizationStrategies": [
                        {
                            "name": "NamespaceBased"
                        }
                    ]
                },
                {
                    "name": "ReadChanges",
                    "authorizationStrategies": [
                        {
                            "name": "NamespaceBased"
                        }
                    ]
                }
            ]
        },
        "claimSets": [
            {
                "name": "SISVendor",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            },
            {
                "name": "EdFiSandbox",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "DistrictHostedSISVendor",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherReader",
                "actions": [
                    {
                        "name": "Read"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherWriter",
                "actions": [
                    {
                        "name": "Create",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Read",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Update",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Delete",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    }
                ]
            },
            {
                "name": "E2E-NoFurtherAuthRequiredClaimSet",
                "actions": [
                    {
                        "name": "Create",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Read",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Update",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Delete",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "ReadChanges",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    }
                ]
            }
        ]
    },
    {
        "name": "http://ed-fi.org/identity/claims/domains/finance",
        "claimSets": [
            {
                "name": "EdFiSandbox",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherReader",
                "actions": [
                    {
                        "name": "Read"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherWriter",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            }
        ],
        "claims": [
            {
                "name": "http://ed-fi.org/identity/claims/domains/finance/dimensions",
                "defaultAuthorization": {
                    "actions": [
                        {
                            "name": "Create",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "Read",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "Update",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "Delete",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "ReadChanges",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        }
                    ]
                },
                "claimSets": [
                    {
                        "name": "FinanceVendor",
                        "actions": [
                            {
                                "name": "Read"
                            }
                        ]
                    }
                ],
                "claims": [
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/fundDimension"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/programDimension"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/functionDimension"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/objectDimension"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/projectDimension"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/operationalUnitDimension"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/sourceDimension"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/balanceSheetDimension"
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/domains/finance/locals",
                "defaultAuthorization": {
                    "actions": [
                        {
                            "name": "Create",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsAndPeople"
                                }
                            ]
                        },
                        {
                            "name": "Read",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsAndPeople"
                                }
                            ]
                        },
                        {
                            "name": "Update",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsAndPeople"
                                }
                            ]
                        },
                        {
                            "name": "Delete",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsAndPeople"
                                }
                            ]
                        },
                        {
                            "name": "ReadChanges",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsAndPeople"
                                }
                            ]
                        }
                    ]
                },
                "claimSets": [
                    {
                        "name": "FinanceVendor",
                        "actions": [
                            {
                                "name": "Create"
                            },
                            {
                                "name": "Read"
                            },
                            {
                                "name": "Update"
                            },
                            {
                                "name": "Delete"
                            }
                        ]
                    }
                ],
                "claims": [
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/chartOfAccount",
                        "defaultAuthorization": {
                            "actions": [
                                {
                                    "name": "Read",
                                    "authorizationStrategies": [
                                        {
                                            "name": "RelationshipsWithEdOrgsAndPeople"
                                        },
                                        {
                                            "name": "RelationshipsWithEdOrgsAndPeopleInverted"
                                        }
                                    ]
                                }
                            ]
                        }
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/localAccount"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/localBudget"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/localActual"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/localEncumbrance"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/localContractedStaff"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/localPayroll"
                    }
                ]
            }
        ]
    },
    {
        "name": "http://ed-fi.org/identity/claims/ed-fi/crisisEvent",
        "defaultAuthorization": {
            "actions": [
                {
                    "name": "Create",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "Read",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "Update",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "Delete",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "ReadChanges",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                }
            ]
        },
        "claimSets": [
            {
                "name": "SISVendor",
                "actions": [
                    {
                        "name": "Read"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "EdFiSandbox",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "BootstrapDescriptorsandEdOrgs",
                "actions": [
                    {
                        "name": "Create"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherReader",
                "actions": [
                    {
                        "name": "Read"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherWriter",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            }
        ]
    },
    {
        "name": "http://ed-fi.org/identity/claims/ed-fi/studentHealth",
        "defaultAuthorization": {
            "actions": [
                {
                    "name": "Create",
                    "authorizationStrategies": [
                        {
                            "name": "RelationshipsWithEdOrgsAndPeople"
                        }
                    ]
                },
                {
                    "name": "Read",
                    "authorizationStrategies": [
                        {
                            "name": "RelationshipsWithEdOrgsAndPeople"
                        }
                    ]
                },
                {
                    "name": "Update",
                    "authorizationStrategies": [
                        {
                            "name": "RelationshipsWithEdOrgsAndPeople"
                        }
                    ]
                },
                {
                    "name": "Delete",
                    "authorizationStrategies": [
                        {
                            "name": "RelationshipsWithEdOrgsAndPeople"
                        }
                    ]
                },
                {
                    "name": "ReadChanges",
                    "authorizationStrategies": [
                        {
                            "name": "RelationshipsWithEdOrgsAndPeople"
                        }
                    ]
                }
            ]
        },
        "claimSets": [
            {
                "name": "E2E-NoFurtherAuthRequiredClaimSet",
                "actions": [
                    {
                        "name": "Create",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Read",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Update",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "Delete",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    },
                    {
                        "name": "ReadChanges",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    }
                ]
            },
            {
                "name": "EdFiSandbox",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherReader",
                "actions": [
                    {
                        "name": "Read"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherWriter",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            }
        ]
    },
    {
        "name": "http://ed-fi.org/identity/claims/domains/homograph",
        "defaultAuthorization": {
            "actions": [
                {
                    "name": "Create",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "Read",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "Update",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "Delete",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "ReadChanges",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                }
            ]
        },
        "claimSets": [
            {
                "name": "EdFiSandbox",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            }
        ],
        "claims": [
            {
                "name": "http://ed-fi.org/identity/claims/homograph/name"
            },
            {
                "name": "http://ed-fi.org/identity/claims/homograph/school"
            },
            {
                "name": "http://ed-fi.org/identity/claims/homograph/contact"
            },
            {
                "name": "http://ed-fi.org/identity/claims/homograph/student"
            },
            {
                "name": "http://ed-fi.org/identity/claims/homograph/staff"
            },
            {
                "name": "http://ed-fi.org/identity/claims/homograph/schoolYearType"
            },
            {
                "name": "http://ed-fi.org/identity/claims/homograph/studentSchoolAssociation"
            }
        ]
    },
    {
        "name": "http://ed-fi.org/identity/claims/domains/sample",
        "defaultAuthorization": {
            "actions": [
                {
                    "name": "Create",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "Read",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "Update",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "Delete",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                },
                {
                    "name": "ReadChanges",
                    "authorizationStrategies": [
                        {
                            "name": "NoFurtherAuthorizationRequired"
                        }
                    ]
                }
            ]
        },
        "claimSets": [
            {
                "name": "EdFiSandbox",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            }
        ],
        "claims": [
            {
                "name": "http://ed-fi.org/identity/claims/sample/bus"
            },
            {
                "name": "http://ed-fi.org/identity/claims/sample/busRoute"
            }
        ]
    },
    {
        "name": "http://ed-fi.org/identity/claims/domains/tpdm",
        "claimSets": [
            {
                "name": "EdFiSandbox",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherReader",
                "actions": [
                    {
                        "name": "Read"
                    },
                    {
                        "name": "ReadChanges"
                    }
                ]
            },
            {
                "name": "EdFiAPIPublisherWriter",
                "actions": [
                    {
                        "name": "Create"
                    },
                    {
                        "name": "Read"
                    },
                    {
                        "name": "Update"
                    },
                    {
                        "name": "Delete"
                    }
                ]
            }
        ],
        "claims": [
            {
                "name": "http://ed-fi.org/identity/claims/domains/tpdm/performanceEvaluation",
                "defaultAuthorization": {
                    "actions": [
                        {
                            "name": "Create",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsOnly"
                                }
                            ]
                        },
                        {
                            "name": "Read",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsOnly"
                                }
                            ]
                        },
                        {
                            "name": "Update",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsOnly"
                                }
                            ]
                        },
                        {
                            "name": "Delete",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsOnly"
                                }
                            ]
                        },
                        {
                            "name": "ReadChanges",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsOnly"
                                }
                            ]
                        }
                    ]
                },
                "claimSets": [
                    {
                        "name": "EducationPreparationProgram",
                        "actions": [
                            {
                                "name": "Create"
                            },
                            {
                                "name": "Read"
                            },
                            {
                                "name": "Update"
                            },
                            {
                                "name": "Delete"
                            }
                        ]
                    }
                ],
                "claims": [
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/performanceEvaluation"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/evaluation"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/evaluationObjective"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/evaluationElement"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/rubricDimension"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/evaluationRating"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/evaluationObjectiveRating"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/evaluationElementRating"
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/tpdm/performanceEvaluationRating"
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/domains/tpdm/noFurtherAuthorizationRequiredData",
                "defaultAuthorization": {
                    "actions": [
                        {
                            "name": "Create",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "Read",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "Update",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "Delete",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        },
                        {
                            "name": "ReadChanges",
                            "authorizationStrategies": [
                                {
                                    "name": "NoFurtherAuthorizationRequired"
                                }
                            ]
                        }
                    ]
                },
                "claims": [
                    {
                        "name": "http://ed-fi.org/identity/claims/domains/tpdm/candidatePreparation",
                        "claimSets": [
                            {
                                "name": "EducationPreparationProgram",
                                "actions": [
                                    {
                                        "name": "Create"
                                    },
                                    {
                                        "name": "Read"
                                    },
                                    {
                                        "name": "Update"
                                    },
                                    {
                                        "name": "Delete"
                                    }
                                ]
                            }
                        ],
                        "claims": [
                            {
                                "name": "http://ed-fi.org/identity/claims/tpdm/candidateEducatorPreparationProgramAssociation"
                            }
                        ]
                    },
                    {
                        "name": "http://ed-fi.org/identity/claims/domains/tpdm/students",
                        "claims": [
                            {
                                "name": "http://ed-fi.org/identity/claims/tpdm/financialAid"
                            }
                        ]
                    }
                ]
            },
            {
                "name": "http://ed-fi.org/identity/claims/tpdm/educatorPreparationProgram",
                "defaultAuthorization": {
                    "actions": [
                        {
                            "name": "Create",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsOnly"
                                }
                            ]
                        },
                        {
                            "name": "Read",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsOnly"
                                }
                            ]
                        },
                        {
                            "name": "Update",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsOnly"
                                }
                            ]
                        },
                        {
                            "name": "Delete",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsOnly"
                                }
                            ]
                        },
                        {
                            "name": "ReadChanges",
                            "authorizationStrategies": [
                                {
                                    "name": "RelationshipsWithEdOrgsOnly"
                                }
                            ]
                        }
                    ]
                },
                "claimSets": [
                    {
                        "name": "BootstrapDescriptorsandEdOrgs",
                        "actions": [
                            {
                                "name": "Create"
                            }
                        ]
                    },
                    {
                        "name": "EducationPreparationProgram",
                        "actions": [
                            {
                                "name": "Create"
                            },
                            {
                                "name": "Read"
                            },
                            {
                                "name": "Update"
                            },
                            {
                                "name": "Delete"
                            }
                        ]
                    }
                ]
            }
        ]
    },
    {
        "name": "http://ed-fi.org/identity/claims/publishing/snapshot",
        "claimSets": [
            {
                "name": "EdFiAPIPublisherReader",
                "actions": [
                    {
                        "name": "Read",
                        "authorizationStrategyOverrides": [
                            {
                                "name": "NoFurtherAuthorizationRequired"
                            }
                        ]
                    }
                ]
            }
        ]
    }
]'::jsonb);
