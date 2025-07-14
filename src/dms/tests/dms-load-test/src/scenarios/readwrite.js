import { sleep, group } from 'k6';
import { SharedArray } from 'k6/data';
import { SharedAuthManager } from '../config/sharedAuth.js';
import { ApiClient, getResourceEndpoint } from '../utils/api.js';
import { dataStore } from '../utils/dataStore.js';
import { DataGenerator } from '../generators/index.js';
import { Counter, Trend } from 'k6/metrics';

// Custom metrics
const operationCounter = new Counter('crud_operations');
const operationDuration = new Trend('crud_operation_duration');

// Test configuration
export const options = {
    scenarios: {
        readwrite_phase: {
            executor: 'constant-vus',
            vus: parseInt(__ENV.VUS_READWRITE_PHASE) || 50,
            duration: __ENV.DURATION_READWRITE_PHASE || '20m',
        },
    },
    thresholds: {
        http_req_duration: ['p(95)<3000'], // 95% of requests must complete below 3s
        http_req_failed: ['rate<0.05'], // http errors should be less than 5%
        errors: ['rate<0.05'], // custom error rate should be less than 5%
        crud_operations: ['count>10000'], // Ensure we perform enough operations
    },
};

// Initialize components
const apiBaseUrl = __ENV.API_BASE_URL || 'https://api.ed-fi.org/v7.3/api';
const sharedAuthManager = new SharedAuthManager({
    tokenUrl: __ENV.OAUTH_TOKEN_URL,
    clientId: __ENV.CLIENT_ID,
    clientSecret: __ENV.CLIENT_SECRET
});
const apiClient = new ApiClient(apiBaseUrl, sharedAuthManager);
const dataGenerator = new DataGenerator();

// CRUD operation weights (must sum to 100)
const operationWeights = {
    create: 20,  // 20% POST
    read: 50,    // 50% GET
    update: 20,  // 20% PUT
    delete: 10   // 10% DELETE
};

// Resource types to test with CRUD operations
const crudResourceTypes = new SharedArray('crudResourceTypes', function () {
    return [
        'students',
        'studentSchoolAssociations',
        'studentSectionAssociations',
        'grades',
        'studentAssessments',
        'staffs',
        'staffSchoolAssociations',
        'courses',
        'sections'
    ];
});

export function setup() {
    console.log('📖 Setting up read-write phase...');
    console.log(`🔄 CRUD operation mix: Create ${operationWeights.create}%, Read ${operationWeights.read}%, Update ${operationWeights.update}%, Delete ${operationWeights.delete}%`);
    
    // Ensure we have some data to work with
    const resourceCount = dataStore.getTotalResourceCount();
    if (resourceCount === 0) {
        console.warn('⚠️  No resources found in data store. Creating initial data...');
        createInitialData();
    }
    
    return {
        authManager,
        apiClient,
        dataGenerator,
        startTime: Date.now()
    };
}

export default function (data) {
    const { apiClient, dataGenerator } = data;
    
    // Select random resource type
    const resourceType = crudResourceTypes[Math.floor(Math.random() * crudResourceTypes.length)];
    
    // Select operation based on weights
    const operation = selectOperation();
    
    group(`${operation.toUpperCase()} ${resourceType}`, function () {
        const startTime = Date.now();
        let success = false;
        
        try {
            switch (operation) {
                case 'create':
                    success = performCreate(apiClient, dataGenerator, resourceType);
                    break;
                case 'read':
                    success = performRead(apiClient, resourceType);
                    break;
                case 'update':
                    success = performUpdate(apiClient, dataGenerator, resourceType);
                    break;
                case 'delete':
                    success = performDelete(apiClient, resourceType);
                    break;
            }
        } catch (error) {
            console.error(`Error in ${operation} ${resourceType}: ${error.message}`);
        }
        
        const duration = Date.now() - startTime;
        operationCounter.add(1, { operation: operation, resourceType: resourceType, success: success });
        operationDuration.add(duration, { operation: operation, resourceType: resourceType });
    });
    
    // Random delay between operations (0.5 to 2 seconds)
    sleep(0.5 + Math.random() * 1.5);
}

function selectOperation() {
    const rand = Math.random() * 100;
    let cumulative = 0;
    
    for (const [operation, weight] of Object.entries(operationWeights)) {
        cumulative += weight;
        if (rand < cumulative) {
            return operation;
        }
    }
    
    return 'read'; // Default fallback
}

function performCreate(apiClient, dataGenerator, resourceType) {
    const dependencies = getDependenciesForCreate(resourceType);
    const data = dataGenerator.generateForResourceType(resourceType, 1, dependencies);
    const endpoint = getResourceEndpoint(resourceType);
    
    const result = apiClient.post(endpoint, data, resourceType, { phase: 'readwrite' });
    
    if (result.success) {
        console.log(`✅ Created new ${resourceType}`);
    }
    
    return result.success;
}

function performRead(apiClient, resourceType) {
    const resources = dataStore.getAllResources(resourceType);
    
    if (resources.length === 0) {
        // Try to list from API
        const endpoint = getResourceEndpoint(resourceType);
        const result = apiClient.get(`${endpoint}?limit=10`, { phase: 'readwrite' });
        return result.success;
    }
    
    // Read specific resource
    const resource = resources[Math.floor(Math.random() * resources.length)];
    if (resource && resource.id) {
        const endpoint = getResourceEndpoint(resourceType);
        const result = apiClient.get(`${endpoint}/${resource.id}`, { phase: 'readwrite' });
        return result.success;
    }
    
    return false;
}

function performUpdate(apiClient, dataGenerator, resourceType) {
    const resources = dataStore.getAllResources(resourceType);
    
    if (resources.length === 0) {
        console.warn(`No ${resourceType} available for update`);
        return false;
    }
    
    const resource = resources[Math.floor(Math.random() * resources.length)];
    if (!resource || !resource.id) {
        return false;
    }
    
    // Make a minor update (varies by resource type)
    const updatedData = makeUpdate(resource, resourceType);
    
    const endpoint = getResourceEndpoint(resourceType);
    const result = apiClient.put(`${endpoint}/${resource.id}`, updatedData, { phase: 'readwrite' });
    
    if (result.success) {
        console.log(`✅ Updated ${resourceType} ${resource.id}`);
    }
    
    return result.success;
}

function performDelete(apiClient, resourceType) {
    // Only delete certain resource types to avoid breaking references
    const deletableTypes = ['grades', 'studentAssessments'];
    
    if (!deletableTypes.includes(resourceType)) {
        // Perform a read instead
        return performRead(apiClient, resourceType);
    }
    
    const resources = dataStore.getAllResources(resourceType);
    if (resources.length === 0) {
        return false;
    }
    
    // Delete oldest resource
    const resource = resources[0];
    if (!resource || !resource.id) {
        return false;
    }
    
    const endpoint = getResourceEndpoint(resourceType);
    const result = apiClient.delete(`${endpoint}/${resource.id}`, { phase: 'readwrite' });
    
    if (result.success) {
        console.log(`✅ Deleted ${resourceType} ${resource.id}`);
        // Remove from data store
        resources.shift();
    }
    
    return result.success;
}

function getDependenciesForCreate(resourceType) {
    // Similar to load phase dependencies
    const dependencies = {};
    
    try {
        switch (resourceType) {
            case 'students':
                // No dependencies for base student
                break;
                
            case 'studentSchoolAssociations':
                const student = dataStore.getRandomStudent();
                const school = dataStore.getRandomSchool();
                if (student && school) {
                    dependencies.studentUniqueId = student.studentUniqueId;
                    dependencies.schoolId = school.schoolId;
                }
                break;
                
            case 'studentSectionAssociations':
                const studentForSection = dataStore.getRandomStudent();
                const section = dataStore.getRandomSection();
                if (studentForSection && section) {
                    dependencies.studentUniqueId = studentForSection.studentUniqueId;
                    dependencies.sectionIdentifier = section.sectionIdentifier;
                    dependencies.localCourseCode = section.courseOfferingReference.localCourseCode;
                    dependencies.schoolId = section.courseOfferingReference.schoolId;
                    dependencies.schoolYear = section.courseOfferingReference.schoolYear;
                    dependencies.sessionName = section.courseOfferingReference.sessionName;
                }
                break;
                
            case 'grades':
                // Need existing student section association
                const studentSection = dataStore.getResource('studentSectionAssociations');
                if (studentSection) {
                    dependencies.studentUniqueId = studentSection.studentReference.studentUniqueId;
                    dependencies.sectionIdentifier = studentSection.sectionReference.sectionIdentifier;
                    dependencies.localCourseCode = studentSection.sectionReference.localCourseCode;
                    dependencies.schoolId = studentSection.sectionReference.schoolId;
                    dependencies.schoolYear = studentSection.sectionReference.schoolYear;
                    dependencies.sessionName = studentSection.sectionReference.sessionName;
                    dependencies.beginDate = studentSection.beginDate;
                }
                break;
                
            // Add more as needed
        }
    } catch (error) {
        console.error(`Error getting dependencies for ${resourceType}: ${error.message}`);
    }
    
    return dependencies;
}

function makeUpdate(resource, resourceType) {
    // Make type-specific updates
    const updated = { ...resource };
    
    switch (resourceType) {
        case 'students':
            updated.firstName = updated.firstName + '-Updated';
            break;
            
        case 'grades':
            // Change grade slightly
            const grades = ['A+', 'A', 'A-', 'B+', 'B', 'B-', 'C+', 'C'];
            updated.letterGradeEarned = grades[Math.floor(Math.random() * grades.length)];
            updated.numericGradeEarned = 70 + Math.floor(Math.random() * 30);
            break;
            
        case 'staffs':
            updated.yearsOfPriorProfessionalExperience = (updated.yearsOfPriorProfessionalExperience || 0) + 1;
            break;
            
        // Add more update patterns as needed
    }
    
    // Remove id field if present (not needed for PUT)
    delete updated.id;
    
    return updated;
}

function createInitialData() {
    // Create minimal data set for read-write testing
    console.log('Creating initial test data...');
    
    // This would typically be done by running the load phase first
    // For now, we'll just create a few resources
    // In production, ensure load phase runs before read-write phase
}

export function teardown(data) {
    const duration = (Date.now() - data.startTime) / 1000 / 60;
    console.log(`\n📊 Read-Write Phase Summary:`);
    console.log(`⏱️  Duration: ${duration.toFixed(2)} minutes`);
    console.log(`🔄 CRUD operations performed`);
    console.log(`📈 Total resources in store: ${dataStore.getTotalResourceCount()}`);
}