import { faker } from '../utils/faker-k6.js';
import { getRandomDescriptorUri } from './descriptors.js';

export function generateStudent() {
    const firstName = faker.person.firstName();
    const lastName = faker.person.lastName();
    const middleName = faker.helpers.maybe(() => faker.person.middleName(), { probability: 0.7 });
    const birthDate = faker.date.birthdate({ min: 5, max: 18, mode: 'age' });
    
    return {
        studentUniqueId: faker.string.alphanumeric({ length: 10, casing: 'upper' }),
        birthDate: birthDate.toISOString().split('T')[0],
        birthCity: faker.location.city(),
        birthStateAbbreviationDescriptor: 'uri://ed-fi.org/StateAbbreviationDescriptor#TX',
        birthCountryDescriptor: 'uri://ed-fi.org/CountryDescriptor#US',
        dateEnteredUS: faker.helpers.maybe(() => 
            faker.date.past({ years: 10 }).toISOString().split('T')[0], 
            { probability: 0.1 }
        ),
        firstName: firstName,
        generationCodeSuffix: faker.helpers.maybe(() => 
            faker.helpers.arrayElement(['Jr', 'Sr', 'III', 'IV']), 
            { probability: 0.05 }
        ),
        lastSurname: lastName,
        middleName: middleName,
        personalTitlePrefix: faker.helpers.maybe(() => 
            faker.helpers.arrayElement(['Mr', 'Ms', 'Miss']), 
            { probability: 0.1 }
        ),
        preferredFirstName: faker.helpers.maybe(() => firstName, { probability: 0.2 }),
        birthSexDescriptor: getRandomDescriptorUri('sex'),
        citizenshipStatusDescriptor: 'uri://ed-fi.org/CitizenshipStatusDescriptor#USCitizen'
    };
}

export function generateStudentEducationOrganizationAssociation(studentUniqueId, educationOrganizationId) {
    return {
        educationOrganizationReference: {
            educationOrganizationId: educationOrganizationId
        },
        studentReference: {
            studentUniqueId: studentUniqueId
        },
        sexDescriptor: getRandomDescriptorUri('sex'),
        races: generateRaces(),
        hispanicLatinoEthnicity: faker.datatype.boolean({ probability: 0.25 }),
        studentIdentificationCodes: [
            {
                assigningOrganizationIdentificationCode: educationOrganizationId.toString(),
                identificationCode: faker.string.numeric({ length: 6 }),
                studentIdentificationSystemDescriptor: 'uri://ed-fi.org/StudentIdentificationSystemDescriptor#District'
            }
        ],
        addresses: [
            {
                addressTypeDescriptor: getRandomDescriptorUri('addressType'),
                city: faker.location.city(),
                postalCode: faker.location.zipCode(),
                stateAbbreviationDescriptor: 'uri://ed-fi.org/StateAbbreviationDescriptor#TX',
                streetNumberName: faker.location.streetAddress(),
                apartmentRoomSuiteNumber: faker.helpers.maybe(() => 
                    faker.location.secondaryAddress(), 
                    { probability: 0.3 }
                )
            }
        ],
        telephones: [
            {
                telephoneNumber: faker.phone.number('###-###-####'),
                telephoneNumberTypeDescriptor: getRandomDescriptorUri('telephoneNumber')
            }
        ],
        electronicMails: [
            {
                electronicMailAddress: faker.internet.email({ firstName: studentUniqueId }),
                electronicMailTypeDescriptor: getRandomDescriptorUri('electronicMail')
            }
        ]
    };
}

export function generateStudentSchoolAssociation(studentUniqueId, schoolId, entryDate, gradeLevel) {
    return {
        studentReference: {
            studentUniqueId: studentUniqueId
        },
        schoolReference: {
            schoolId: schoolId
        },
        entryDate: entryDate,
        entryGradeLevelDescriptor: `uri://ed-fi.org/GradeLevelDescriptor#${gradeLevel.replace(/\s+/g, '')}`,
        entryTypeDescriptor: 'uri://ed-fi.org/EntryTypeDescriptor#Transfer',
        exitWithdrawDate: null,
        exitWithdrawTypeDescriptor: null,
        primarySchool: true,
        repeatGradeIndicator: false,
        schoolChoiceTransfer: false,
        graduationPlanTypeDescriptor: faker.helpers.maybe(() => 
            'uri://ed-fi.org/GraduationPlanTypeDescriptor#Recommended', 
            { probability: 0.3 }
        ),
        educationPlans: []
    };
}

export function generateParent() {
    const firstName = faker.person.firstName();
    const lastName = faker.person.lastName();
    
    return {
        parentUniqueId: faker.string.alphanumeric({ length: 10, casing: 'upper' }),
        firstName: firstName,
        lastSurname: lastName,
        sexDescriptor: getRandomDescriptorUri('sex'),
        addresses: [
            {
                addressTypeDescriptor: getRandomDescriptorUri('addressType'),
                city: faker.location.city(),
                postalCode: faker.location.zipCode(),
                stateAbbreviationDescriptor: 'uri://ed-fi.org/StateAbbreviationDescriptor#TX',
                streetNumberName: faker.location.streetAddress(),
                apartmentRoomSuiteNumber: faker.helpers.maybe(() => 
                    faker.location.secondaryAddress(), 
                    { probability: 0.3 }
                )
            }
        ],
        telephones: [
            {
                telephoneNumber: faker.phone.number('###-###-####'),
                telephoneNumberTypeDescriptor: getRandomDescriptorUri('telephoneNumber')
            }
        ],
        electronicMails: [
            {
                electronicMailAddress: faker.internet.email({ firstName, lastName }),
                electronicMailTypeDescriptor: getRandomDescriptorUri('electronicMail'),
                primaryEmailAddressIndicator: true
            }
        ]
    };
}

export function generateStudentParentAssociation(studentUniqueId, parentUniqueId) {
    return {
        parentReference: {
            parentUniqueId: parentUniqueId
        },
        studentReference: {
            studentUniqueId: studentUniqueId
        },
        relationDescriptor: getRandomDescriptorUri('relationDescriptor'),
        primaryContactStatus: faker.datatype.boolean({ probability: 0.5 }),
        livesWith: faker.datatype.boolean({ probability: 0.8 }),
        emergencyContactStatus: faker.datatype.boolean({ probability: 0.7 })
    };
}

function generateRaces() {
    const races = [];
    const numberOfRaces = faker.helpers.arrayElement([1, 1, 1, 2]); // Most people have 1 race
    
    for (let i = 0; i < numberOfRaces; i++) {
        races.push({
            raceDescriptor: getRandomDescriptorUri('race')
        });
    }
    
    return races;
}