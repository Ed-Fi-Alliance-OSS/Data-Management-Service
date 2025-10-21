import { faker } from '../utils/faker-k6.js';
import { getRandomDescriptorUri } from './descriptors.js';

export function generateLocalEducationAgency() {
    const leaId = faker.number.int({ min: 100000, max: 999999 });
    
    return {
        localEducationAgencyId: leaId,
        nameOfInstitution: `${faker.location.city()} Independent School District`,
        shortNameOfInstitution: `${faker.location.city()} ISD`,
        webSite: faker.internet.url(),
        operationalStatusDescriptor: 'uri://ed-fi.org/OperationalStatusDescriptor#Active',
        addresses: [
            {
                addressTypeDescriptor: getRandomDescriptorUri('addressType'),
                city: faker.location.city(),
                postalCode: faker.location.zipCode(),
                stateAbbreviationDescriptor: 'uri://ed-fi.org/StateAbbreviationDescriptor#TX',
                streetNumberName: faker.location.streetAddress(),
                apartmentRoomSuiteNumber: faker.helpers.maybe(() => faker.location.secondaryAddress(), { probability: 0.2 })
            }
        ],
        categories: [
            {
                localEducationAgencyCategoryDescriptor: 'uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent'
            }
        ],
        localEducationAgencyReference: {
            localEducationAgencyId: leaId
        }
    };
}

export function generateSchool(localEducationAgencyId, schoolId) {
    const schoolTypes = [
        'Elementary School',
        'Middle School',
        'High School',
        'Junior High School',
        'K-8 School',
        'K-12 School'
    ];
    
    const schoolType = faker.helpers.arrayElement(schoolTypes);
    const schoolName = `${faker.person.lastName()} ${schoolType}`;
    
    return {
        schoolId: schoolId,
        nameOfInstitution: schoolName,
        shortNameOfInstitution: schoolName.replace(' School', ''),
        webSite: faker.internet.url(),
        operationalStatusDescriptor: 'uri://ed-fi.org/OperationalStatusDescriptor#Active',
        schoolTypeDescriptor: getRandomDescriptorUri('schoolType'),
        charterStatusDescriptor: 'uri://ed-fi.org/CharterStatusDescriptor#NotACharterSchool',
        addresses: [
            {
                addressTypeDescriptor: getRandomDescriptorUri('addressType'),
                city: faker.location.city(),
                postalCode: faker.location.zipCode(),
                stateAbbreviationDescriptor: 'uri://ed-fi.org/StateAbbreviationDescriptor#TX',
                streetNumberName: faker.location.streetAddress()
            }
        ],
        educationOrganizationCategories: [
            {
                educationOrganizationCategoryDescriptor: 'uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School'
            }
        ],
        gradeLevels: generateGradeLevelsForSchoolType(schoolType),
        localEducationAgencyReference: {
            localEducationAgencyId: localEducationAgencyId
        },
        institutionTelephones: [
            {
                institutionTelephoneNumberTypeDescriptor: 'uri://ed-fi.org/InstitutionTelephoneNumberTypeDescriptor#Main',
                telephoneNumber: faker.phone.number('###-###-####')
            },
            {
                institutionTelephoneNumberTypeDescriptor: 'uri://ed-fi.org/InstitutionTelephoneNumberTypeDescriptor#Fax',
                telephoneNumber: faker.phone.number('###-###-####')
            }
        ]
    };
}

function generateGradeLevelsForSchoolType(schoolType) {
    const gradeLevelMap = {
        'Elementary School': ['Kindergarten', 'First grade', 'Second grade', 'Third grade', 'Fourth grade', 'Fifth grade'],
        'Middle School': ['Sixth grade', 'Seventh grade', 'Eighth grade'],
        'High School': ['Ninth grade', 'Tenth grade', 'Eleventh grade', 'Twelfth grade'],
        'Junior High School': ['Seventh grade', 'Eighth grade', 'Ninth grade'],
        'K-8 School': ['Kindergarten', 'First grade', 'Second grade', 'Third grade', 'Fourth grade', 'Fifth grade', 'Sixth grade', 'Seventh grade', 'Eighth grade'],
        'K-12 School': ['Kindergarten', 'First grade', 'Second grade', 'Third grade', 'Fourth grade', 'Fifth grade', 'Sixth grade', 'Seventh grade', 'Eighth grade', 'Ninth grade', 'Tenth grade', 'Eleventh grade', 'Twelfth grade']
    };
    
    const gradeLevels = gradeLevelMap[schoolType] || ['Ninth grade', 'Tenth grade', 'Eleventh grade', 'Twelfth grade'];
    
    return gradeLevels.map(grade => ({
        gradeLevelDescriptor: `uri://ed-fi.org/GradeLevelDescriptor#${grade.replace(/\s+/g, '')}`
    }));
}

export function generateCalendar(schoolId, schoolYear) {
    return {
        calendarCode: `${schoolId}-${schoolYear}`,
        schoolReference: {
            schoolId: schoolId
        },
        schoolYearTypeReference: {
            schoolYear: schoolYear
        },
        calendarTypeDescriptor: getRandomDescriptorUri('calendarType')
    };
}

export function generateCalendarDate(calendarCode, schoolId, schoolYear, date) {
    const events = [
        'Instructional day',
        'Holiday',
        'Teacher only day',
        'Weather day',
        'Student late arrival',
        'Emergency day'
    ];
    
    return {
        calendarReference: {
            calendarCode: calendarCode,
            schoolId: schoolId,
            schoolYear: schoolYear
        },
        date: date,
        calendarEvents: [
            {
                calendarEventDescriptor: `uri://ed-fi.org/CalendarEventDescriptor#${faker.helpers.arrayElement(events).replace(/\s+/g, '')}`
            }
        ]
    };
}

export function generateClassPeriod(schoolId, classPeriodName) {
    return {
        classPeriodName: classPeriodName,
        schoolReference: {
            schoolId: schoolId
        },
        meetingTimes: [
            {
                startTime: faker.helpers.arrayElement(['08:00:00', '09:00:00', '10:00:00', '11:00:00', '13:00:00', '14:00:00']),
                endTime: faker.helpers.arrayElement(['08:50:00', '09:50:00', '10:50:00', '11:50:00', '13:50:00', '14:50:00'])
            }
        ]
    };
}