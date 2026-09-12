// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

import com.fasterxml.jackson.databind.DeserializationFeature;
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import java.io.File;
import java.util.*;
import org.apache.kafka.common.Cluster;
import org.apache.kafka.common.Node;
import org.apache.kafka.common.PartitionInfo;
import org.apache.kafka.common.config.ConfigException;
import org.apache.kafka.common.record.AbstractRecords;
import org.apache.kafka.common.record.CompressionType;
import org.apache.kafka.common.record.RecordBatch;
import org.apache.kafka.connect.data.*;
import org.apache.kafka.connect.errors.DataException;
import org.apache.kafka.connect.header.ConnectHeaders;
import org.apache.kafka.connect.header.Header;
import org.apache.kafka.connect.source.SourceRecord;
import org.apache.kafka.connect.storage.StringConverter;
import org.edfi.kafka.connect.transforms.DocumentState;
import org.edfi.kafka.connect.converters.DocumentStateJsonConverter;
import org.edfi.kafka.connect.partitioner.KafkaMurmur2V1Partitioner;

/** Test-only adapter: builds Connect objects, invokes published code, and records observations. */
class MessageContractRunner {
    private static final ObjectMapper JSON = new ObjectMapper()
        .enable(DeserializationFeature.USE_BIG_DECIMAL_FOR_FLOATS);

    public static void main(String[] args) throws Exception {
        JsonNode request = JSON.readTree(new File(args[0]));
        ObjectNode result = JSON.createObjectNode();
        result.put("formatVersion", 1);
        ArrayNode observations = result.putArray("observations");
        for (JsonNode scenario : request.get("scenarios")) observations.add(run(scenario));
        JSON.writeValue(new File(args[1]), result);
    }

    private static ObjectNode run(JsonNode scenario) {
        ObjectNode result = JSON.createObjectNode();
        result.put("scenarioId", scenario.get("scenarioId").asText());
        String stage = "input";
        try (DocumentState<SourceRecord> transform = new DocumentState<>();
             StringConverter keyConverter = new StringConverter();
             DocumentStateJsonConverter valueConverter = new DocumentStateJsonConverter();
             KafkaMurmur2V1Partitioner partitioner = new KafkaMurmur2V1Partitioner()) {
            JsonNode input = scenario.get("sourceRecord");
            Schema keySchema = schema(input.get("keySchema"));
            Schema valueSchema = schema(input.get("valueSchema"));
            ConnectHeaders headers = new ConnectHeaders();
            for (JsonNode header : input.get("headers")) {
                Schema headerSchema = schema(header.get("schema"));
                headers.add(header.get("key").asText(), value(headerSchema, header.get("value")), headerSchema);
            }
            SourceRecord record = new SourceRecord(map(input.get("sourcePartition")), map(input.get("sourceOffset")),
                input.get("sourceTopic").asText(), null, keySchema, value(keySchema, input.get("key")),
                valueSchema, value(valueSchema, input.get("value")),
                input.get("timestamp").isNull() ? null : input.get("timestamp").longValue(), headers);
            stage = "configuration";
            transform.configure(map(scenario.get("transformConfig")));
            keyConverter.configure(Map.of(), true);
            valueConverter.configure(Map.of("schemas.enable", false, "decimal.format", "NUMERIC"), false);
            partitioner.configure(Map.of());
            stage = "transform";
            SourceRecord output = scenario.path("converterOnly").asBoolean() ? record : transform.apply(record);
            if (output == null) {
                result.put("status", "dropped");
                return result;
            }
            stage = "conversion";
            byte[] keyBytes = keyConverter.fromConnectData(output.topic(), output.keySchema(), output.key());
            byte[] valueBytes = valueConverter.fromConnectData(output.topic(), output.valueSchema(), output.value());
            stage = "partition";
            int count = scenario.get("partitionCount").intValue();
            if (count < 1) throw new IllegalArgumentException();
            List<PartitionInfo> partitions = new ArrayList<>();
            for (int i = 0; i < count; i++)
                partitions.add(new PartitionInfo(output.topic(), i, null, new Node[0], new Node[0]));
            Cluster cluster = new Cluster("message-contract", List.of(), partitions, Set.of(), Set.of());
            int partition = partitioner.partition(output.topic(), output.key(), keyBytes,
                output.value(), valueBytes, cluster);
            result.put("status", "retained");
            ObjectNode observed = result.putObject("record");
            observed.put("topic", output.topic());
            observed.set("keySchema", describe(output.keySchema()));
            observed.set("valueSchema", describe(output.valueSchema()));
            observed.set("key", JSON.valueToTree(plain(output.key())));
            observed.set("value", JSON.valueToTree(plain(output.value())));
            observed.set("keyBytes", bytes(keyBytes));
            observed.set("valueBytes", bytes(valueBytes));
            observed.set("timestamp", JSON.valueToTree(output.timestamp()));
            observed.set("sourcePartition", JSON.valueToTree(output.sourcePartition()));
            observed.set("sourceOffset", JSON.valueToTree(output.sourceOffset()));
            observed.put("partition", partition);
            observed.put("partitionCount", count);
            // The pinned KafkaProducer uses this Kafka client API for its local max.request.size check.
            // Keep the algorithm in the image; this estimate includes framing, not just JSON length.
            if (scenario.path("measureProducerSize").asBoolean()) {
                if (output.headers().iterator().hasNext()) throw new IllegalArgumentException();
                observed.put("producerSizeUpperBound", AbstractRecords.estimateSizeInBytesUpperBound(
                    RecordBatch.CURRENT_MAGIC_VALUE, CompressionType.NONE, keyBytes, valueBytes,
                    new org.apache.kafka.common.header.Header[0]));
                observed.put("kafkaClientVersion", org.apache.kafka.common.utils.AppInfoParser.getVersion());
            }
            // Observe both byte equality and object identity; do not reproduce the converter handshake.
            if (output.value() instanceof byte[] raw) {
                observed.put("converterBytesEqual", Arrays.equals(raw, valueBytes));
                observed.put("converterDefensiveCopy", raw != valueBytes);
            }
            ArrayNode outputHeaders = observed.putArray("headers");
            for (Header header : output.headers()) {
                ObjectNode item = outputHeaders.addObject();
                item.put("key", header.key());
                item.set("schema", describe(header.schema()));
                item.set("value", JSON.valueToTree(plain(header.value())));
            }
        } catch (Exception failure) {
            // Never serialize exception prose, causes, stack traces, or any partially built record.
            result.remove("record");
            result.put("status", "failed");
            ObjectNode diagnostic = result.putObject("failure");
            diagnostic.put("stage", stage);
            diagnostic.put("category", failure instanceof ConfigException ? "ConfigException"
                : failure instanceof DataException ? "DataException" : "RunnerInputOrRuntimeException");
            ObjectNode metadata = diagnostic.putObject("metadata");
            if (failure instanceof DocumentState.TransformationFailureException artifactFailure) {
                diagnostic.put("reason", artifactFailure.reason().name());
                artifactFailure.metadata().entrySet().stream().limit(16).forEach(entry -> {
                    // Published accessors own the reason and metadata; this boundary further removes identities.
                    String key = entry.getKey();
                    if (!Set.of("provider", "operation", "sourceTopic", "sourceSchema", "sourceTable").contains(key)) return;
                    String text = entry.getValue();
                    boolean safe = key.equals("provider") && Set.of("postgresql", "sqlserver").contains(text)
                        || key.equals("operation") && Set.of("c", "u", "r", "d", "t").contains(text);
                    metadata.put(key, safe ? text : "[redacted]");
                });
            } else {
                diagnostic.put("reason", "UNCLASSIFIED_" + stage.toUpperCase(Locale.ROOT));
            }
            if (scenario.get("sourceRecord").has("diagnosticSentinels")) {
                // Inspect exception text in-container. Never return it, even when this audit fails.
                String message = String.valueOf(failure.getMessage());
                boolean sentinelFree = true;
                boolean metadataSentinelFree = true;
                int metadataCount = 0;
                int metadataMaxLength = 0;
                boolean metadataControlFree = true;
                Map<String, String> artifactMetadata = failure instanceof DocumentState.TransformationFailureException f
                    ? f.metadata() : Map.of();
                for (JsonNode sentinel : scenario.get("sourceRecord").get("diagnosticSentinels")) {
                    sentinelFree &= !message.contains(sentinel.asText());
                    for (String item : artifactMetadata.values())
                        metadataSentinelFree &= !item.contains(sentinel.asText());
                }
                for (String item : artifactMetadata.values()) {
                    metadataCount++;
                    metadataMaxLength = Math.max(metadataMaxLength, item.length());
                    metadataControlFree &= item.chars().noneMatch(Character::isISOControl);
                }
                ObjectNode audit = diagnostic.putObject("audit");
                audit.put("messageSentinelFree", sentinelFree);
                audit.put("metadataSentinelFree", metadataSentinelFree);
                audit.put("messageLength", message.length());
                audit.put("metadataCount", metadataCount);
                audit.put("metadataMaxLength", metadataMaxLength);
                audit.put("metadataControlFree", metadataControlFree);
                audit.put("hasCause", failure.getCause() != null);
            }
        }
        return result;
    }

    private static ObjectNode bytes(byte[] value) {
        ObjectNode result = JSON.createObjectNode();
        result.put("kind", value == null ? "kafka-null" : "bytes");
        if (value != null) {
            result.put("base64", Base64.getEncoder().encodeToString(value));
            result.put("length", value.length);
        }
        return result;
    }

    private static Schema schema(JsonNode descriptor) {
        if (descriptor == null || descriptor.isNull()) return null;
        Schema.Type type = Schema.Type.valueOf(descriptor.get("type").asText());
        SchemaBuilder builder = switch (type) {
            case ARRAY -> SchemaBuilder.array(schema(descriptor.get("items")));
            case MAP -> SchemaBuilder.map(schema(descriptor.get("keys")), schema(descriptor.get("values")));
            default -> new SchemaBuilder(type);
        };
        if (descriptor.path("optional").asBoolean()) builder.optional();
        if (descriptor.has("name")) builder.name(descriptor.get("name").asText());
        if (descriptor.has("version")) builder.version(descriptor.get("version").intValue());
        if (descriptor.has("parameters")) descriptor.get("parameters").fields()
            .forEachRemaining(entry -> builder.parameter(entry.getKey(), entry.getValue().asText()));
        if (type == Schema.Type.STRUCT) descriptor.get("fields").fields()
            .forEachRemaining(entry -> builder.field(entry.getKey(), schema(entry.getValue())));
        return builder.build();
    }

    private static Object value(Schema schema, JsonNode node) {
        if (node == null || node.isNull()) return null;
        if (node.isObject() && node.has("$javaType")) return runtimeValue(node);
        if (schema == null) return JSON.convertValue(node, Object.class);
        return switch (schema.type()) {
            case STRING -> node.textValue();
            case BOOLEAN -> node.booleanValue();
            case INT8 -> (byte) node.intValue();
            case INT16 -> (short) node.intValue();
            case INT32 -> node.intValue();
            case INT64 -> node.longValue();
            case FLOAT32 -> node.floatValue();
            case FLOAT64 -> node.doubleValue();
            case BYTES -> {
                byte[] bytes = Base64.getDecoder().decode(node.textValue());
                // Descriptors encode physical bytes; Connect logical decimals use BigDecimal.
                yield Decimal.LOGICAL_NAME.equals(schema.name()) ? Decimal.toLogical(schema, bytes) : bytes;
            }
            case STRUCT -> {
                // Public Struct.put validates runtime types before the transform can see them.
                // A test-only Struct overrides reads for explicitly tagged malformed fields;
                // all ordinary fields still use Connect's normal construction and validation.
                Map<String, Object> malformed = new HashMap<>();
                Struct struct = new Struct(schema) {
                    @Override public Object getWithoutDefault(String name) {
                        return malformed.containsKey(name) ? malformed.get(name) : super.getWithoutDefault(name);
                    }
                    @Override public void validate() {
                        // Parent Struct.put recursively validates children. Only explicitly
                        // malformed fields bypass this earlier Connect construction boundary.
                        if (malformed.isEmpty()) super.validate();
                    }
                };
                node.fields().forEachRemaining(entry -> {
                    JsonNode item = entry.getValue();
                    if (item.isNull() && !schema.field(entry.getKey()).schema().isOptional())
                        malformed.put(entry.getKey(), null);
                    else if (item.isObject() && item.has("$javaType")) malformed.put(entry.getKey(), runtimeValue(item));
                    else struct.put(entry.getKey(), value(schema.field(entry.getKey()).schema(), item));
                });
                yield struct;
            }
            case ARRAY -> {
                List<Object> items = new ArrayList<>();
                node.forEach(item -> items.add(value(schema.valueSchema(), item)));
                yield items;
            }
            case MAP -> throw new IllegalArgumentException();
        };
    }

    private static Object runtimeValue(JsonNode descriptor) {
        JsonNode value = descriptor.get("value");
        return switch (descriptor.get("$javaType").asText()) {
            case "UUID" -> UUID.fromString(value.asText());
            case "STRING" -> value.textValue();
            case "INT32" -> value.intValue();
            case "INT64" -> value.longValue();
            case "BYTES" -> Base64.getDecoder().decode(value.textValue());
            case "BYTE_BUFFER" -> java.nio.ByteBuffer.wrap(Base64.getDecoder().decode(value.textValue()));
            case "MAP" -> map(value);
            default -> throw new IllegalArgumentException();
        };
    }

    private static JsonNode describe(Schema schema) {
        if (schema == null) return JSON.nullNode();
        ObjectNode result = JSON.createObjectNode();
        result.put("type", schema.type().name());
        result.put("optional", schema.isOptional());
        if (schema.name() != null) result.put("name", schema.name());
        if (schema.version() != null) result.put("version", schema.version());
        if (schema.parameters() != null) result.set("parameters", JSON.valueToTree(schema.parameters()));
        if (schema.type() == Schema.Type.STRUCT) {
            ObjectNode fields = result.putObject("fields");
            for (Field field : schema.fields()) fields.set(field.name(), describe(field.schema()));
        }
        if (schema.type() == Schema.Type.ARRAY) result.set("items", describe(schema.valueSchema()));
        if (schema.type() == Schema.Type.MAP) {
            result.set("keys", describe(schema.keySchema()));
            result.set("values", describe(schema.valueSchema()));
        }
        return result;
    }

    private static Object plain(Object value) {
        if (value instanceof Struct struct) {
            Map<String, Object> result = new LinkedHashMap<>();
            for (Field field : struct.schema().fields()) result.put(field.name(), plain(struct.get(field)));
            return result;
        }
        if (value instanceof List<?> items) return items.stream().map(MessageContractRunner::plain).toList();
        return value;
    }

    @SuppressWarnings("unchecked")
    private static Map<String, Object> map(JsonNode node) {
        return JSON.convertValue(node, LinkedHashMap.class);
    }
}
