@reset-data-before-scenario
Feature: Partition sizing and multi-partition consumption
    Genuine multi-partition behavior through the deployed stack, which the ordinary suite cannot reach.
    The mandatory minimum partition size is five maximum-sized pages, so at the shipped page size of 500
    a collection needs more than 2,500 rows before it can be cut into a second partition, and no
    scenario can seed that over HTTP. This feature therefore runs only in its own lane, against
    .env.cursorpartitions.e2e, whose maximum page size is 2 and whose minimum partition size is
    therefore 10.

    Twenty-one schools is the smallest seed that makes both halves load-bearing at that size. With the
    count omitted the configured default of ten partitions computes a size of ceiling(21/10) = 3, which
    the minimum raises to 10, so the boundaries fall at candidate rows 1, 11 and 21 and three real
    ranges come back. With number=2 the computed size is ceiling(21/2) = 11, above the minimum, so
    exactly two come back.

    The "at least two tokens" assertions are what prove the isolated environment actually reached DMS:
    against the ordinary stack every request here would return a single unbounded range and the feature
    would fail rather than pass vacuously.

        Background:
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                        | educationOrganizationCategories                                                                                       |
                  | 101      | Sizing 101        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 102      | Sizing 102        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 103      | Sizing 103        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 104      | Sizing 104        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 105      | Sizing 105        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 106      | Sizing 106        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 107      | Sizing 107        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 108      | Sizing 108        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 109      | Sizing 109        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 110      | Sizing 110        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 111      | Sizing 111        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 112      | Sizing 112        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 113      | Sizing 113        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 114      | Sizing 114        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 115      | Sizing 115        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 116      | Sizing 116        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 117      | Sizing 117        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 118      | Sizing 118        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 119      | Sizing 119        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 120      | Sizing 120        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 121      | Sizing 121        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |

        @CursorPartitionSizing
        Scenario: 01 Several real partitions are consumed both sequentially and concurrently
              # The default count: three ranges, walked one after another.
             When the partitions of "/ed-fi/schools" are requested
             Then it should respond with 200
              And at least 2 partition tokens were returned
              And at most 10 partition tokens were returned
             When every returned partition is walked with page size 2
             Then the walk returned 21 documents with no duplicates
              And the walk returned exactly these "schoolId" values
                  | schoolId |
                  | 101      |
                  | 102      |
                  | 103      |
                  | 104      |
                  | 105      |
                  | 106      |
                  | 107      |
                  | 108      |
                  | 109      |
                  | 110      |
                  | 111      |
                  | 112      |
                  | 113      |
                  | 114      |
                  | 115      |
                  | 116      |
                  | 117      |
                  | 118      |
                  | 119      |
                  | 120      |
                  | 121      |
              # A requested count of two: exactly two ranges, consumed at the same time by independent
              # workers, each reaching its own terminal empty page.
             When the partitions of "/ed-fi/schools" are requested with "number=2"
             Then it should respond with 200
              And at least 2 partition tokens were returned
              And at most 2 partition tokens were returned
             When every returned partition is walked concurrently with page size 2
             Then the walk returned 21 documents with no duplicates
              And the walk returned exactly these "schoolId" values
                  | schoolId |
                  | 101      |
                  | 102      |
                  | 103      |
                  | 104      |
                  | 105      |
                  | 106      |
                  | 107      |
                  | 108      |
                  | 109      |
                  | 110      |
                  | 111      |
                  | 112      |
                  | 113      |
                  | 114      |
                  | 115      |
                  | 116      |
                  | 117      |
                  | 118      |
                  | 119      |
                  | 120      |
                  | 121      |
