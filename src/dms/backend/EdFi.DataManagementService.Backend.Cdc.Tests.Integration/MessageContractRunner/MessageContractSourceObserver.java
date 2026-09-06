// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

package org.edfi.contract;

import com.fasterxml.jackson.databind.ObjectMapper;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import org.apache.kafka.common.config.ConfigDef;
import org.apache.kafka.connect.data.Field;
import org.apache.kafka.connect.data.Schema;
import org.apache.kafka.connect.data.Struct;
import org.apache.kafka.connect.errors.DataException;
import org.apache.kafka.connect.source.SourceRecord;
import org.apache.kafka.connect.transforms.Transformation;

/** Test-only observation before DocumentState. Returns the identical SourceRecord; no routing or conversion. */
public final class MessageContractSourceObserver implements Transformation<SourceRecord> {
    private static final Path OUTPUT = Path.of("/tmp/message-contract-source-observations.jsonl");
    private static final Set<String> TABLES = Set.of("Document", "DocumentCache", "CdcHeartbeat");
    private static final ObjectMapper JSON = new ObjectMapper();
    private String server;
    private String database;
    private int count;

    // The qualified image supports Java source launch and the compiler module, but has no javac/jar executables.
    public static void main(String[] args) throws Exception {
        Path classes = Path.of("/tmp/contract-observer");
        Files.createDirectories(classes.resolve("META-INF/services"));
        int result = javax.tools.ToolProvider.getSystemJavaCompiler().run(null,
                java.io.OutputStream.nullOutputStream(), java.io.OutputStream.nullOutputStream(),
                "-cp", System.getProperty("java.class.path"), "-d", classes.toString(),
                "/tmp/MessageContractSourceObserver.java");
        if (result != 0) throw new IllegalStateException("Observer compilation failed");
        Files.writeString(classes.resolve("META-INF/services/org.apache.kafka.connect.transforms.Transformation"),
                "org.edfi.contract.MessageContractSourceObserver\n");
        Path jar = Path.of("/kafka/connect/contract-observer/observer.jar");
        Files.createDirectories(jar.getParent());
        try (var output = new java.util.jar.JarOutputStream(Files.newOutputStream(jar));
                var paths = Files.walk(classes)) {
            for (Path path : paths.filter(Files::isRegularFile).toList()) {
                output.putNextEntry(new java.util.jar.JarEntry(classes.relativize(path).toString()));
                Files.copy(path, output);
                output.closeEntry();
            }
        }
    }

    public ConfigDef config() { return new ConfigDef().define("expected.server", ConfigDef.Type.STRING,
            ConfigDef.Importance.HIGH, "Isolated fixture server for equality checks only")
            .define("expected.database", ConfigDef.Type.STRING, "", ConfigDef.Importance.HIGH,
                    "Isolated fixture database for equality checks only"); }
    public void configure(Map<String, ?> props) {
        server = (String) props.get("expected.server");
        database = props.get("expected.database") instanceof String value ? value : "";
    }
    public void close() { }

    public synchronized SourceRecord apply(SourceRecord record) {
        try {
            if (++count > 4096) throw new DataException("Source observation limit exceeded");
            Struct envelope = record.value() instanceof Struct value ? value : null;
            Struct source = field(envelope, "source") instanceof Struct value ? value : null;
            String table = TABLES.contains(String.valueOf(field(source, "table")))
                    ? (String) field(source, "table") : "other";
            boolean nativeHeartbeat = record.topic().equals("__debezium-heartbeat." + server);
            Map<String, Object> observation = new LinkedHashMap<>();
            observation.put("kind", nativeHeartbeat ? "native" : table);
            Object op = field(envelope, "op");
            observation.put("operation", Set.of("c", "u", "r", "d", "t").contains(String.valueOf(op)) ? op : "other");
            observation.put("topicMatches", record.topic().equals(nativeHeartbeat
                    ? "__debezium-heartbeat." + server : server + (database.isEmpty() ? "" : "." + database) + ".dms." + table));
            observation.put("serverMatches", server.equals(record.sourcePartition().get("server")));
            observation.put("partitionFields", record.sourcePartition().size());
            observation.put("databaseMatches", database.isEmpty()
                    ? !record.sourcePartition().containsKey("database")
                    : database.equals(record.sourcePartition().get("database")));
            observation.put("sourceDatabaseMatches", source == null || database.isEmpty()
                    || database.equals(field(source, "db")));
            observation.put("schemaIsDms", "dms".equals(field(source, "schema")));
            observation.put("keyIsStruct", record.key() instanceof Struct);
            observation.put("keyIsNull", record.key() == null);
            Struct key = record.key() instanceof Struct value ? value : null;
            Object uuid = field(key, "DocumentUuid");
            observation.put("uuid", canonicalUuid(uuid));
            observation.put("keyFieldCount", key == null ? 0 : key.schema().fields().size());
            observation.put("keyUuidSchema", schema(key, "DocumentUuid"));
            Struct row = field(envelope, "after") instanceof Struct value ? value : null;
            observation.put("jsonSchema", schema(row, "DocumentJson"));
            observation.put("timeSchema", schema(row, "LastModifiedAt"));
            observation.put("versionSchema", schema(row, "ContentVersion"));
            Struct before = field(envelope, "before") instanceof Struct value ? value : null;
            observation.put("beforeUuidMatches", !canonicalUuid(uuid).isEmpty()
                    && canonicalUuid(uuid).equals(canonicalUuid(field(before, "DocumentUuid"))));
            observation.put("beforeUuidState", before == null ? "absent" :
                    "__debezium_unavailable_value".equals(field(before, "DocumentUuid")) ? "unavailable" :
                    field(before, "DocumentUuid") == null ? "null" :
                    canonicalUuid(field(before, "DocumentUuid")).isEmpty() ? "other" : "uuid");
            observation.put("beforeJsonState", before == null ? "absent" :
                    "__debezium_unavailable_value".equals(field(before, "DocumentJson")) ? "unavailable" :
                    field(before, "DocumentJson") == null ? "null" : "present");
            Object time = field(row, "LastModifiedAt");
            observation.put("timeHasSevenFractionalDigits", time instanceof String text
                    && text.matches(".*\\.\\d{7}Z"));
            observation.put("valueIsNull", record.value() == null);
            Object lsn = record.sourceOffset().get("lsn");
            observation.put("lsn", lsn instanceof Long ? lsn : 0L);
            // No body, timestamps, credentials, database/server/tenant names or arbitrary metadata are serialized.
            Files.writeString(OUTPUT, JSON.writeValueAsString(observation) + "\n",
                    StandardOpenOption.CREATE, StandardOpenOption.APPEND);
            return record;
        } catch (Exception failure) {
            throw new DataException("Source observation failed; details redacted");
        }
    }

    private static Object field(Struct value, String name) {
        return value == null || value.schema().field(name) == null ? null : value.get(name);
    }

    private static String canonicalUuid(Object value) {
        if (!(value instanceof String text) || text.length() != 36) return "";
        try { return UUID.fromString(text).toString(); }
        catch (IllegalArgumentException failure) { return ""; }
    }

    private static Map<String, Object> schema(Struct value, String fieldName) {
        if (value == null) return Map.of();
        Field field = value.schema().field(fieldName);
        if (field == null) return Map.of();
        Schema schema = field.schema();
        String name = schema.name();
        String safeName = name == null ? "" : Set.of("io.debezium.data.Uuid", "io.debezium.data.Json",
                "io.debezium.time.ZonedTimestamp", "io.debezium.time.IsoTimestamp").contains(name) ? name : "other";
        return Map.of("type", schema.type().name(), "name", safeName, "optional", schema.isOptional(),
                "version", schema.version() == null ? 0 : schema.version());
    }
}
