import { sleep, group } from 'k6';
import { SharedAuthManager } from '../config/sharedAuth.js';
import { DependencyResolver } from '../utils/dependencies.js';
import { ApiDataClient, getResourceEndpoint } from '../utils/apiDataClient.js';
import { dataStore } from '../utils/dataStore.js';
import { DataGenerator } from '../generators/index.js';

// Test configuration
export const options = {
    scenarios: {
        load_phase: {
            executor: 'ramping-vus',
            startVUs: 1,
            stages: [
                { duration: '2m', target: parseInt(__ENV.VUS_LOAD_PHASE) || 10 }, // Ramp up
                { duration: __ENV.DURATION_LOAD_PHASE || '30m', target: parseInt(__ENV.VUS_LOAD_PHASE) || 10 }, // Stay at target
                { duration: '2m', target: 0 }, // Ramp down
            ],
            gracefulRampDown: '30s',
        },
    },
    thresholds: {
        http_req_duration: ['p(95)<5000'], // 95% of requests must complete below 5s
        http_req_failed: ['rate<0.05'], // http errors should be less than 5%
        errors: ['rate<0.05'] // custom error rate should be less than 5%
    },
};

// Initialize components
const apiBaseUrl = __ENV.API_BASE_URL || 'https://api.ed-fi.org/v7.3/api';
const sharedAuthManager = new SharedAuthManager({
    tokenUrl: __ENV.OAUTH_TOKEN_URL,
    clientId: __ENV.CLIENT_ID,
    clientSecret: __ENV.CLIENT_SECRET
});


// Focus on 5 key domains
const targetDomains = ['enrollment', 'studentAcademicRecord', 'teachingAndLearning', 'assessment', 'studentIdentification'];

export function setup() {
    console.log('🏫 Setting up Austin ISD scale load test...');
    console.log(`📊 Configuration: ${__ENV.SCHOOL_COUNT || 130} schools, ${__ENV.STUDENT_COUNT || 75000} students, ${__ENV.STAFF_COUNT || 12000} staff`);
    console.log(`🔧 Debug mode: ${__ENV.DEBUG === 'true' ? 'ENABLED' : 'disabled'}`);
    console.log(`🌐 API Base URL: ${apiBaseUrl}`);
    console.log(`🔑 OAuth Token URL: ${__ENV.OAUTH_TOKEN_URL}`);

    try {
        // Create instances in setup
        console.log('📦 Creating API client...');
        const apiClient = new ApiDataClient(apiBaseUrl, sharedAuthManager);
        console.log('✅ API client created');

        console.log('📦 Creating dependency resolver...');
        const dependencyResolver = new DependencyResolver(apiBaseUrl, sharedAuthManager);
        console.log('✅ Dependency resolver created');

        console.log('📦 Creating data generator...');
        const dataGenerator = new DataGenerator();
        console.log('✅ Data generator created');
        console.log(`📊 DataGenerator type: ${typeof dataGenerator}`);
        console.log(`📊 DataGenerator methods: ${Object.getOwnPropertyNames(Object.getPrototypeOf(dataGenerator)).join(', ')}`);

        // Fetch dependencies during setup (where HTTP requests are allowed)
        console.log('🔍 Fetching resource dependencies...');

        // First fetch all dependencies
        const allDependencies = dependencyResolver.fetchDependencies();
        console.log(`✅ Found ${Object.keys(allDependencies).length} total resources`);

        // Then filter by domains
        const filtered = dependencyResolver.filterByDomains(targetDomains);
        console.log(`📋 Filtered to ${filtered.length} resources for target domains`);

        if (filtered.length === 0) {
            console.error('❌ No resources found after filtering. Check domain keywords.');
            console.log('Available resources:', Object.keys(allDependencies).slice(0, 20).join(', '));

            // Fallback: use a hardcoded list of essential resources
            console.log('⚠️  Using fallback resource list...');
            const fallbackResources = [
                'gradeLevelDescriptors',
                'academicSubjectDescriptors',
                'calendarTypeDescriptors',
                'courseLevelCharacteristicDescriptors',
                'localEducationAgencies',
                'schools',
                'calendars',
                'students',
                'parents',
                'staffs',
                'courses',
                'courseOfferings',
                'sections',
                'studentSchoolAssociations',
                'studentSectionAssociations',
                'grades'
            ];

            const fallbackData = {
                apiBaseUrl: apiBaseUrl,
                tokenUrl: __ENV.OAUTH_TOKEN_URL,
                clientId: __ENV.CLIENT_ID,
                clientSecret: __ENV.CLIENT_SECRET,
                startTime: Date.now(),
                resourceOrder: fallbackResources,
                dependencies: {},
                config: {
                    schoolCount: parseInt(__ENV.SCHOOL_COUNT) || 130,
                    studentCount: parseInt(__ENV.STUDENT_COUNT) || 75000,
                    staffCount: parseInt(__ENV.STAFF_COUNT) || 12000,
                    coursesPerSchool: parseInt(__ENV.COURSES_PER_SCHOOL) || 50,
                    sectionsPerCourse: parseInt(__ENV.SECTIONS_PER_COURSE) || 4,
                    schoolYear: 2024
                }
            };

            console.log('📋 Fallback setup data prepared');
            return fallbackData;
        }

        // k6 can't pass class instances between setup and default, only plain data
        // So we'll pass the configuration and recreate instances in the VU
        const setupData = {
            apiBaseUrl: apiBaseUrl,
            tokenUrl: __ENV.OAUTH_TOKEN_URL,
            clientId: __ENV.CLIENT_ID,
            clientSecret: __ENV.CLIENT_SECRET,
            startTime: Date.now(),
            resourceOrder: filtered,  // Pass the resource order to the default function
            dependencies: allDependencies,  // Pass raw dependencies for debugging
            config: {
                schoolCount: parseInt(__ENV.SCHOOL_COUNT) || 130,
                studentCount: parseInt(__ENV.STUDENT_COUNT) || 75000,
                staffCount: parseInt(__ENV.STAFF_COUNT) || 12000,
                coursesPerSchool: parseInt(__ENV.COURSES_PER_SCHOOL) || 50,
                sectionsPerCourse: parseInt(__ENV.SECTIONS_PER_COURSE) || 4,
                schoolYear: 2024
            }
        };

        console.log('📋 Setup data prepared:');
        console.log(`  - resourceOrder length: ${setupData.resourceOrder.length}`);
        console.log(`  - dependencies count: ${Object.keys(setupData.dependencies).length}`);
        console.log(`  - config: ${JSON.stringify(setupData.config)}`);

        if (__ENV.DEBUG === 'true') {
            console.log('🔍 Resource order:', setupData.resourceOrder.slice(0, 10).join(', '), '...');
        }

        return setupData;
    } catch (error) {
        console.error('❌ Setup failed:', error.message);
        throw error;
    }
}

export default function (data) {
    const vuId = __VU;
    const debug = __ENV.DEBUG === 'true';

    try {
        // Debug: Check what we're getting
        console.log(`VU ${vuId}: Starting execution...`);
        console.log(`VU ${vuId}: Received data keys:`, Object.keys(data));

        if (!data) {
            throw new Error('No data received from setup function');
        }

        const { apiBaseUrl, tokenUrl, clientId, clientSecret, resourceOrder, config } = data;

        // Create instances in the VU context
        console.log(`VU ${vuId}: Creating auth manager...`);
        const authManager = new SharedAuthManager({
            tokenUrl: tokenUrl,
            clientId: clientId,
            clientSecret: clientSecret
        });

        console.log(`VU ${vuId}: Creating API client...`);
        const apiClient = new ApiDataClient(apiBaseUrl, authManager);

        console.log(`VU ${vuId}: Creating data generator...`);
        const dataGenerator = new DataGenerator(config);

        if (!resourceOrder || !Array.isArray(resourceOrder)) {
            throw new Error(`resourceOrder is invalid: ${typeof resourceOrder}`);
        }

        console.log(`VU ${vuId}: dataGenerator type:`, typeof dataGenerator);
        console.log(`VU ${vuId}: resourceOrder length:`, resourceOrder.length);

        if (debug) {
            console.log(`VU ${vuId}: dataGenerator methods:`, Object.getOwnPropertyNames(Object.getPrototypeOf(dataGenerator)).join(', '));
            console.log(`VU ${vuId}: Testing generateForResourceType method:`, typeof dataGenerator.generateForResourceType);
        }

        // Each VU processes a subset of resources
        const totalVUs = parseInt(__ENV.VUS_LOAD_PHASE) || 10;

    // Calculate which resources this VU should create
    const resourcesPerVU = Math.ceil(resourceOrder.length / totalVUs);
    const startIndex = (vuId - 1) * resourcesPerVU;
    const endIndex = Math.min(startIndex + resourcesPerVU, resourceOrder.length);

    console.log(`VU ${vuId}: Processing resources ${startIndex} to ${endIndex}`);

    // Process assigned resources in dependency order
    for (let i = startIndex; i < endIndex; i++) {
        const resourceType = resourceOrder[i];

        group(`Create ${resourceType}`, function () {
            try {
                // Determine how many instances to create based on resource type
                const count = getResourceCount(resourceType);

                if (count > 0) {
                    console.log(`Creating ${count} ${resourceType}...`);

                    for (let j = 0; j < count; j++) {
                        // Get dependencies from data store
                        const dependencies = getDependencies(resourceType);

                        // Generate data
                        if (debug) {
                            console.log(`VU ${vuId}: Generating data for ${resourceType} with dependencies:`, JSON.stringify(dependencies));
                        }

                        if (!dataGenerator.generateForResourceType) {
                            throw new Error(`dataGenerator.generateForResourceType is not a function. Available methods: ${Object.getOwnPropertyNames(Object.getPrototypeOf(dataGenerator)).join(', ')}`);
                        }

                        const generatedData = dataGenerator.generateForResourceType(resourceType, 1, dependencies);

                        if (debug) {
                            console.log(`VU ${vuId}: Generated data:`, JSON.stringify(generatedData, null, 2));
                        }

                        // Create via API
                        const endpoint = getResourceEndpoint(resourceType);
                        if (debug) {
                            console.log(`VU ${vuId}: POSTing to ${endpoint}`);
                        }

                        const result = apiClient.post(endpoint, generatedData, resourceType, { domain: getDomain(resourceType) });

                        if (!result.success) {
                            console.error(`Failed to create ${resourceType}: ${result.error}`);
                        }

                        // Small delay to avoid overwhelming the API
                        sleep(0.1);
                    }
                }
            } catch (error) {
                console.error(`Error processing ${resourceType}: ${error.message}`);
            }
        });

        // Pause between resource types
        sleep(1);
    }
    } catch (error) {
        console.error(`VU ${vuId}: FATAL ERROR:`, error.message);
        console.error(`VU ${vuId}: Stack trace:`, error.stack);
        throw error;
    }
}

// Helper functions
function getResourceCount(resourceType) {
    // Austin ISD scale calculations
    const schoolCount = parseInt(__ENV.SCHOOL_COUNT) || 130;
    const studentCount = parseInt(__ENV.STUDENT_COUNT) || 75000;
    const staffCount = parseInt(__ENV.STAFF_COUNT) || 12000;
    const coursesPerSchool = parseInt(__ENV.COURSES_PER_SCHOOL) || 50;
    const sectionsPerCourse = parseInt(__ENV.SECTIONS_PER_COURSE) || 4;

    const counts = {
        // Descriptors - create a basic set
        'gradeLevelDescriptors': 14,
        'academicSubjectDescriptors': 9,
        'calendarTypeDescriptors': 4,
        'courseLevelCharacteristicDescriptors': 7,

        // Organizations
        'localEducationAgencies': 1,
        'schools': schoolCount,

        // Calendar
        'calendars': schoolCount,
        'calendarDates': schoolCount * 180, // ~180 school days
        'classPeriods': schoolCount * 8, // 8 periods per school

        // People
        'students': Math.floor(studentCount / 10), // Create 10% in this phase
        'parents': Math.floor(studentCount / 10 * 1.5), // 1.5 parents per student
        'staffs': Math.floor(staffCount / 10), // Create 10% in this phase

        // Academic
        'courses': schoolCount * coursesPerSchool,
        'courseOfferings': schoolCount * coursesPerSchool,
        'sections': schoolCount * coursesPerSchool * sectionsPerCourse,

        // Associations (created after base entities)
        'studentEducationOrganizationAssociations': Math.floor(studentCount / 10),
        'studentSchoolAssociations': Math.floor(studentCount / 10),
        'studentParentAssociations': Math.floor(studentCount / 10 * 1.5),
        'staffEducationOrganizationAssignmentAssociations': Math.floor(staffCount / 10),
        'staffSchoolAssociations': Math.floor(staffCount / 10),
        'studentSectionAssociations': Math.floor(studentCount / 10 * 6), // 6 sections per student

        // Assessments
        'assessments': 50, // State and district assessments
        'studentAssessments': Math.floor(studentCount / 10 * 0.3), // 30% take assessments

        // Grades
        'grades': Math.floor(studentCount / 10 * 6 * 4), // 6 sections * 4 grading periods
    };

    // Default to 0 if not specified
    return counts[resourceType] || 0;
}

function getDependencies(resourceType) {
    const dependencies = {};

    // Map dependencies based on resource type
    switch (resourceType) {
        case 'schools':
            const lea = dataStore.getResource('localEducationAgencies');
            if (lea) dependencies.localEducationAgencyId = lea.localEducationAgencyId;
            break;

        case 'calendars':
            const school = dataStore.getRandomSchool();
            if (school) dependencies.schoolId = school.schoolId;
            break;

        case 'calendarDates':
            const calendar = dataStore.getResource('calendars');
            if (calendar) {
                dependencies.calendarCode = calendar.calendarCode;
                dependencies.schoolId = calendar.schoolReference.schoolId;
            }
            break;

        case 'classPeriods':
            const schoolForPeriod = dataStore.getRandomSchool();
            if (schoolForPeriod) dependencies.schoolId = schoolForPeriod.schoolId;
            break;

        case 'studentEducationOrganizationAssociations':
            const student = dataStore.getRandomStudent();
            const leaForStudent = dataStore.getResource('localEducationAgencies');
            if (student && leaForStudent) {
                dependencies.studentUniqueId = student.studentUniqueId;
                dependencies.educationOrganizationId = leaForStudent.localEducationAgencyId;
            }
            break;

        case 'studentSchoolAssociations':
            const studentForSchool = dataStore.getRandomStudent();
            const schoolForStudent = dataStore.getRandomSchool();
            if (studentForSchool && schoolForStudent) {
                dependencies.studentUniqueId = studentForSchool.studentUniqueId;
                dependencies.schoolId = schoolForStudent.schoolId;
            }
            break;

        // Add more dependency mappings as needed
    }

    return dependencies;
}

function getDomain(resourceType) {
    const domainMap = {
        'students': 'studentIdentification',
        'studentEducationOrganizationAssociations': 'enrollment',
        'studentSchoolAssociations': 'enrollment',
        'studentSectionAssociations': 'enrollment',
        'courses': 'teachingAndLearning',
        'courseOfferings': 'teachingAndLearning',
        'sections': 'teachingAndLearning',
        'grades': 'studentAcademicRecord',
        'assessments': 'assessment',
        'studentAssessments': 'assessment'
    };

    return domainMap[resourceType] || 'other';
}

export function teardown(data) {
    const duration = (Date.now() - data.startTime) / 1000 / 60;
    console.log(`\n📊 Load Phase Summary:`);
    console.log(`⏱️  Duration: ${duration.toFixed(2)} minutes`);
    console.log(`📈 Total resources created: ${dataStore.getTotalResourceCount()}`);
    console.log(`📋 Resource breakdown:`);

    const summary = dataStore.getSummary();
    for (const [type, count] of Object.entries(summary.breakdown)) {
        console.log(`   - ${type}: ${count}`);
    }
}
