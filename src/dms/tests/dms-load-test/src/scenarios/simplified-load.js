/**
 * Simplified Load Test Scenario
 * 
 * This scenario uses simplified API schemas to minimize dependencies
 * and focuses on core performance metrics without complex relationships.
 */

import { sleep, group, check } from 'k6';
import { SharedArray } from 'k6/data';
import { Counter, Trend, Rate } from 'k6/metrics';
import http from 'k6/http';
import { SharedAuthManager } from '../config/sharedAuth.js';

// Test configuration
export const options = {
    scenarios: {
        simplified_load: {
            executor: 'ramping-vus',
            startVUs: 1,
            stages: [
                { duration: '1m', target: 5 },    // Warm-up
                { duration: '2m', target: 20 },   // Ramp to 20 users
                { duration: '5m', target: 20 },   // Sustain 20 users
                { duration: '2m', target: 50 },   // Ramp to 50 users
                { duration: '10m', target: 50 },  // Sustain 50 users
                { duration: '2m', target: 100 },  // Ramp to 100 users
                { duration: '10m', target: 100 }, // Sustain 100 users
                { duration: '2m', target: 0 },    // Ramp down
            ],
            gracefulRampDown: '30s',
        },
    },
    thresholds: {
        http_req_duration: ['p(95)<3000', 'p(99)<5000'], // Response time thresholds
        http_req_failed: ['rate<0.05'],                  // Error rate < 5%
        'crud_operations': ['count>1000'],               // Minimum operations
        'entity_creation_time': ['p(95)<2000'],         // Entity creation p95
    },
};

// Custom metrics
const crudOperations = new Counter('crud_operations');
const entityCreationTime = new Trend('entity_creation_time');
const operationErrors = new Rate('operation_errors');

// Initialize components
const apiBaseUrl = __ENV.API_BASE_URL || 'http://localhost:5198';
const sharedAuthManager = new SharedAuthManager({
    tokenUrl: __ENV.OAUTH_TOKEN_URL || 'http://localhost:8080/realms/master/protocol/openid-connect/token',
    clientId: __ENV.CLIENT_ID || 'dms-client',
    clientSecret: __ENV.CLIENT_SECRET || 'client-secret'
});

// Simplified data generators
class SimplifiedDataGenerator {
    generateSchool(id) {
        return {
            schoolId: 200000 + id,
            nameOfInstitution: `Simplified Test School ${id}`,
            schoolCategories: [
                {
                    schoolCategoryDescriptor: "uri://ed-fi.org/SchoolCategoryDescriptor#Elementary School"
                }
            ],
            gradeLevels: [
                {
                    gradeLevelDescriptor: "uri://ed-fi.org/GradeLevelDescriptor#First Grade"
                }
            ]
        };
    }

    generateStudent(schoolId, studentIndex) {
        const uniqueId = `SIMP${Date.now()}${__VU}${studentIndex}`;
        return {
            studentUniqueId: uniqueId,
            firstName: `First${studentIndex}`,
            lastSurname: `Last${studentIndex}`,
            birthDate: "2015-01-01",
            enrollmentReference: {
                schoolId: schoolId
            }
        };
    }

    generateStaff(schoolId, staffIndex) {
        const uniqueId = `STAFF${Date.now()}${__VU}${staffIndex}`;
        return {
            staffUniqueId: uniqueId,
            firstName: `Staff${staffIndex}`,
            lastSurname: `Member${staffIndex}`,
            assignmentReference: {
                schoolId: schoolId
            }
        };
    }

    generateDescriptor(type, value) {
        return {
            codeValue: value,
            shortDescription: `${type} - ${value}`,
            description: `Description for ${type} ${value}`,
            namespace: "uri://ed-fi.org",
            effectiveBeginDate: "2024-01-01"
        };
    }
}

const dataGenerator = new SimplifiedDataGenerator();

// Shared data storage for created entities
const entityStore = {
    schools: [],
    students: [],
    staff: [],
    descriptors: []
};

export function setup() {
    console.log('🚀 Setting up Simplified Load Test');
    console.log(`📊 API Base URL: ${apiBaseUrl}`);
    console.log(`🔑 OAuth URL: ${__ENV.OAUTH_TOKEN_URL}`);
    
    // Pre-create some descriptors for reference
    const descriptorTypes = [
        { type: 'GradeLevel', values: ['Kindergarten', 'First Grade', 'Second Grade'] },
        { type: 'SchoolCategory', values: ['Elementary School', 'Middle School', 'High School'] },
        { type: 'AttendanceEventCategory', values: ['Present', 'Absent', 'Tardy'] }
    ];
    
    console.log('📝 Creating reference descriptors...');
    
    try {
        const token = sharedAuthManager.getToken();
        const headers = {
            'Content-Type': 'application/json',
            'Authorization': `Bearer ${token}`
        };
        
        for (const descriptorType of descriptorTypes) {
            for (const value of descriptorType.values) {
                const descriptor = dataGenerator.generateDescriptor(descriptorType.type, value);
                const endpoint = `${apiBaseUrl}/ed-fi/${descriptorType.type.toLowerCase()}Descriptors`;
                
                const response = http.post(endpoint, JSON.stringify(descriptor), { headers });
                
                if (response.status === 201 || response.status === 409) {
                    console.log(`✅ Descriptor created/exists: ${descriptorType.type} - ${value}`);
                } else {
                    console.warn(`⚠️ Failed to create descriptor: ${response.status}`);
                }
            }
        }
    } catch (error) {
        console.error('❌ Setup failed:', error.message);
    }
    
    return {
        startTime: Date.now(),
        apiBaseUrl: apiBaseUrl
    };
}

export default function(data) {
    const vuId = __VU;
    const iteration = __ITER;
    
    // Get authentication token
    const token = sharedAuthManager.getToken();
    const headers = {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${token}`
    };
    
    // Determine operation based on iteration
    const operationType = selectOperation(iteration);
    
    group(`Operation: ${operationType}`, function() {
        let response;
        let startTime = Date.now();
        
        switch(operationType) {
            case 'CREATE_SCHOOL':
                const school = dataGenerator.generateSchool(vuId * 1000 + iteration);
                response = http.post(
                    `${apiBaseUrl}/ed-fi/schools`,
                    JSON.stringify(school),
                    { headers, tags: { operation: 'create', entity: 'school' } }
                );
                
                if (response.status === 201) {
                    entityStore.schools.push({
                        id: school.schoolId,
                        location: response.headers['Location']
                    });
                }
                break;
                
            case 'CREATE_STUDENT':
                // Use existing school or create with default
                const schoolId = entityStore.schools.length > 0 
                    ? entityStore.schools[Math.floor(Math.random() * entityStore.schools.length)].id
                    : 200000;
                    
                const student = dataGenerator.generateStudent(schoolId, iteration);
                response = http.post(
                    `${apiBaseUrl}/ed-fi/students`,
                    JSON.stringify(student),
                    { headers, tags: { operation: 'create', entity: 'student' } }
                );
                
                if (response.status === 201) {
                    entityStore.students.push({
                        id: student.studentUniqueId,
                        location: response.headers['Location']
                    });
                }
                break;
                
            case 'READ':
                // Read random existing entity
                let readEndpoint = `${apiBaseUrl}/ed-fi/schools?limit=10`;
                if (entityStore.students.length > 0 && Math.random() > 0.5) {
                    readEndpoint = `${apiBaseUrl}/ed-fi/students?limit=10`;
                }
                
                response = http.get(readEndpoint, { 
                    headers, 
                    tags: { operation: 'read' } 
                });
                break;
                
            case 'UPDATE':
                // Update existing entity if available
                if (entityStore.schools.length > 0) {
                    const schoolToUpdate = entityStore.schools[Math.floor(Math.random() * entityStore.schools.length)];
                    const updatedSchool = dataGenerator.generateSchool(schoolToUpdate.id);
                    updatedSchool.nameOfInstitution = `Updated ${updatedSchool.nameOfInstitution}`;
                    
                    response = http.put(
                        schoolToUpdate.location,
                        JSON.stringify(updatedSchool),
                        { headers, tags: { operation: 'update', entity: 'school' } }
                    );
                } else {
                    // Fallback to read if no entities to update
                    response = http.get(`${apiBaseUrl}/ed-fi/schools?limit=1`, { headers });
                }
                break;
                
            case 'QUERY':
                // Perform various query operations
                const queries = [
                    '/ed-fi/schools?limit=25',
                    '/ed-fi/students?limit=25',
                    '/ed-fi/schools?nameOfInstitution=Test&limit=10',
                    '/ed-fi/students?firstName=First&limit=10',
                    '/ed-fi/schools?offset=0&limit=50&totalCount=true'
                ];
                
                const query = queries[Math.floor(Math.random() * queries.length)];
                response = http.get(`${apiBaseUrl}${query}`, { 
                    headers, 
                    tags: { operation: 'query', query: query } 
                });
                break;
                
            default:
                // Default to read operation
                response = http.get(`${apiBaseUrl}/ed-fi/schools?limit=5`, { headers });
        }
        
        // Track metrics
        const duration = Date.now() - startTime;
        entityCreationTime.add(duration, { operation: operationType });
        crudOperations.add(1);
        
        // Check response
        const success = check(response, {
            'status is 2xx': (r) => r.status >= 200 && r.status < 300,
            'response time < 3s': (r) => r.timings.duration < 3000,
        });
        
        if (!success) {
            operationErrors.add(1);
            console.error(`Operation failed: ${operationType}, Status: ${response.status}`);
        }
    });
    
    // Small delay between operations
    sleep(Math.random() * 2 + 0.5);
}

function selectOperation(iteration) {
    // Progressive operation distribution
    // Early iterations focus on creation, later ones on mixed operations
    
    if (iteration < 100) {
        // First 100 iterations: Create base data
        if (iteration % 10 === 0) return 'CREATE_SCHOOL';
        if (iteration % 3 === 0) return 'CREATE_STUDENT';
        return 'READ';
    } else {
        // After base data: Mixed operations
        const random = Math.random();
        
        if (random < 0.15) return 'CREATE_SCHOOL';
        if (random < 0.30) return 'CREATE_STUDENT';
        if (random < 0.50) return 'UPDATE';
        if (random < 0.75) return 'READ';
        return 'QUERY';
    }
}

export function teardown(data) {
    const duration = (Date.now() - data.startTime) / 1000 / 60;
    
    console.log('\n📊 Simplified Load Test Summary:');
    console.log(`⏱️  Duration: ${duration.toFixed(2)} minutes`);
    console.log(`🏫 Schools created: ${entityStore.schools.length}`);
    console.log(`👨‍🎓 Students created: ${entityStore.students.length}`);
    console.log(`👨‍🏫 Staff created: ${entityStore.staff.length}`);
    console.log('\n✅ Test completed successfully');
}