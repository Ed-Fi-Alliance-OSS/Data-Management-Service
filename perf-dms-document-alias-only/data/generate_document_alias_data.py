#!/usr/bin/env python3

from __future__ import annotations

import argparse
import base64
import csv
import hashlib
import json
import math
import random
import uuid
from dataclasses import dataclass
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, List, Mapping, MutableMapping, Optional, Tuple
from uuid_extensions import uuid7


"""Generate paired Document/Alias CSV files populated with large Ed-Fi payloads."""


DEFAULT_RESOURCE_VERSION = "7.3.0"
DEFAULT_OPENAPI_PATH = (Path(__file__).resolve().parent.parent / "swagger.json").resolve()
SCHEMA_BY_RESOURCE: Dict[str, str] = {
    "sections": "edFi_section",
    "assessments": "edFi_assessment",
    "studentAcademicRecords": "edFi_studentAcademicRecord",
}


@dataclass(frozen=True)
class ResourceConfig:
    resource_name: str
    schema_name: str
    resource_version: str


@dataclass
class _BuilderContext:
    rng: random.Random
    doc_index: int


class OpenApiExampleBuilder:
    """Materialize complete sample payloads from OpenAPI component schemas."""

    def __init__(self, spec: Mapping[str, Any], *, base_seed: int = 12345) -> None:
        components = spec.get("components")
        if not isinstance(components, Mapping):
            raise ValueError("OpenAPI spec is missing components block")
        schemas = components.get("schemas")
        if not isinstance(schemas, MutableMapping) or not schemas:
            raise ValueError("OpenAPI spec does not provide component schemas")

        self._schemas: MutableMapping[str, Any] = schemas
        self._base_seed = base_seed
        self._max_depth = 7
        self._max_ref_visits = 2
        self._uuid_namespace = uuid.UUID("c783fc5e-8511-4bb8-8b7f-736cafe9fae0")

    def build(self, schema_name: str, *, doc_index: int) -> Any:
        if schema_name not in self._schemas:
            raise KeyError(f"Schema '{schema_name}' not found in OpenAPI components")

        rng_seed = self._base_seed + doc_index * 7919
        context = _BuilderContext(rng=random.Random(rng_seed), doc_index=doc_index)
        visit_counts: Dict[str, int] = {}

        return self._generate(
            {"$ref": f"#/components/schemas/{schema_name}"},
            path=(schema_name,),
            depth=0,
            context=context,
            visit_counts=visit_counts,
        )

    def _resolve_ref(self, ref: str) -> Mapping[str, Any]:
        if not ref.startswith("#/components/schemas/"):
            raise KeyError(f"Unsupported $ref target: {ref}")
        key = ref.split("/")[-1]
        try:
            return self._schemas[key]
        except KeyError as exc:  # pragma: no cover - defensive
            raise KeyError(f"Schema '{key}' not found for ref '{ref}'") from exc

    def _generate(
        self,
        schema: Mapping[str, Any],
        *,
        path: Tuple[str, ...],
        depth: int,
        context: _BuilderContext,
        visit_counts: Dict[str, int],
    ) -> Any:
        if depth > self._max_depth:
            return self._depth_placeholder(path, context)

        if schema is None:
            return None

        if "$ref" in schema:
            ref = schema["$ref"]
            ref_name = ref.split("/")[-1]
            visits = visit_counts.get(ref_name, 0)
            if visits >= self._max_ref_visits:
                return self._ref_placeholder(ref_name, path, context)

            visit_counts[ref_name] = visits + 1
            resolved = self._resolve_ref(ref)
            result = self._generate(
                resolved,
                path=path + (ref_name,),
                depth=depth + 1,
                context=context,
                visit_counts=visit_counts,
            )
            if visit_counts[ref_name] <= 1:
                visit_counts.pop(ref_name, None)
            else:
                visit_counts[ref_name] -= 1
            return result

        if "allOf" in schema:
            aggregate: Dict[str, Any] = {}
            fallback: Any = None
            for item in schema["allOf"]:
                value = self._generate(
                    item,
                    path=path,
                    depth=depth + 1,
                    context=context,
                    visit_counts=visit_counts,
                )
                if isinstance(value, dict):
                    aggregate.update(value)
                else:
                    fallback = value
            return aggregate if aggregate else fallback

        if "oneOf" in schema:
            return self._generate(
                schema["oneOf"][0],
                path=path,
                depth=depth + 1,
                context=context,
                visit_counts=visit_counts,
            )

        if "anyOf" in schema:
            return self._generate(
                schema["anyOf"][0],
                path=path,
                depth=depth + 1,
                context=context,
                visit_counts=visit_counts,
            )

        schema_type = schema.get("type")
        if schema_type is None:
            if "properties" in schema or "additionalProperties" in schema:
                schema_type = "object"
            elif "items" in schema:
                schema_type = "array"

        if schema_type == "object":
            result: Dict[str, Any] = {}
            properties = schema.get("properties") or {}
            for key, subschema in properties.items():
                result[key] = self._generate(
                    subschema,
                    path=path + (key,),
                    depth=depth + 1,
                    context=context,
                    visit_counts=visit_counts,
                )

            additional = schema.get("additionalProperties")
            if isinstance(additional, Mapping):
                result["additionalPropertyExample"] = self._generate(
                    additional,
                    path=path + ("additionalPropertyExample",),
                    depth=depth + 1,
                    context=context,
                    visit_counts=visit_counts,
                )
            elif additional is True:
                result["additionalPropertyExample"] = self._hash_string(
                    path,
                    context=context,
                    suffix="extra",
                )

            return result

        if schema_type == "array":
            items_schema = schema.get("items") or {}
            count = max(1, min(2, int(schema.get("minItems", 1) or 1)))
            values: List[Any] = []
            for index in range(count):
                element_path = path + (f"item{index}",)
                values.append(
                    self._generate(
                        items_schema,
                        path=element_path,
                        depth=depth + 1,
                        context=context,
                        visit_counts=visit_counts,
                    )
                )
            return values

        if schema_type == "string":
            return self._sample_string(schema, path=path, context=context)

        if schema_type == "integer":
            return self._sample_integer(schema, path=path, context=context)

        if schema_type == "number":
            return self._sample_number(schema, path=path, context=context)

        if schema_type == "boolean":
            return context.rng.choice([True, False])

        return self._hash_string(path, context=context, suffix="value")

    def _depth_placeholder(self, path: Tuple[str, ...], context: _BuilderContext) -> str:
        return self._hash_string(path, context=context, suffix="depth_limit")

    def _ref_placeholder(
        self, ref_name: str, path: Tuple[str, ...], context: _BuilderContext
    ) -> str:
        combined = path + (ref_name, "ref")
        return self._hash_string(combined, context=context, suffix="ref")

    def _hash_string(
        self, path: Tuple[str, ...], *, context: _BuilderContext, suffix: str = ""
    ) -> str:
        label = ".".join(path)
        digest = hashlib.sha1(
            f"{label}:{context.doc_index}:{self._base_seed}:{suffix}".encode("utf-8")
        ).hexdigest()
        key = path[-1] if path else "value"
        return f"{key}_{digest[:20]}"

    def _apply_length_constraints(
        self,
        value: str,
        *,
        min_length: Optional[int],
        max_length: Optional[int],
    ) -> str:
        if max_length is not None and len(value) > max_length:
            value = value[:max_length]
        if min_length:
            if len(value) < min_length:
                padding = (min_length - len(value)) * "x"
                value = (value + padding)[:max_length] if max_length else value + padding
        return value

    def _sample_string(
        self, schema: Mapping[str, Any], *, path: Tuple[str, ...], context: _BuilderContext
    ) -> str:
        enum_values = schema.get("enum")
        if enum_values:
            return enum_values[0]

        fmt = schema.get("format")
        min_length = schema.get("minLength")
        max_length = schema.get("maxLength")

        if fmt == "uuid":
            label = f"{context.doc_index}:{'.'.join(path)}"
            value = str(uuid.uuid5(self._uuid_namespace, label))
            return self._apply_length_constraints(value, min_length=min_length, max_length=max_length)

        if fmt == "date":
            base_days = context.doc_index * 7 + len(path) * 3
            computed_date = date(2000, 1, 1) + timedelta(days=base_days % 3650)
            return computed_date.isoformat()

        if fmt == "date-time":
            base_seconds = context.doc_index * 863 + len(path) * 97
            computed_datetime = datetime(2000, 1, 1, tzinfo=timezone.utc) + timedelta(
                seconds=base_seconds % (3650 * 24 * 3600)
            )
            return computed_datetime.isoformat().replace("+00:00", "Z")

        if fmt in {"uri", "url"}:
            digest = hashlib.sha1(
                f"{context.doc_index}:{'.'.join(path)}".encode("utf-8")
            ).hexdigest()
            value = f"https://example.org/{path[-1]}/{digest[:12]}"
            return self._apply_length_constraints(value, min_length=min_length, max_length=max_length)

        if fmt == "byte":
            digest = hashlib.sha1(
                f"{context.doc_index}:{'.'.join(path)}".encode("utf-8")
            ).digest()
            value = base64.b64encode(digest).decode("ascii")
            return self._apply_length_constraints(value, min_length=min_length, max_length=max_length)

        value = self._hash_string(path, context=context)
        return self._apply_length_constraints(value, min_length=min_length, max_length=max_length)

    def _sample_integer(
        self, schema: Mapping[str, Any], *, path: Tuple[str, ...], context: _BuilderContext
    ) -> int:
        minimum = schema.get("minimum")
        maximum = schema.get("maximum")
        exclusive_min = schema.get("exclusiveMinimum")
        exclusive_max = schema.get("exclusiveMaximum")

        min_value = int(minimum) if minimum is not None else 0
        max_value = int(maximum) if maximum is not None else min_value + 1000

        if exclusive_min is True:
            min_value += 1
        elif isinstance(exclusive_min, (int, float)):
            min_value = int(math.floor(exclusive_min)) + 1

        if exclusive_max is True:
            max_value -= 1
        elif isinstance(exclusive_max, (int, float)):
            max_value = int(math.floor(exclusive_max)) - 1

        if max_value < min_value:
            max_value = min_value

        span = max_value - min_value
        if span <= 0:
            value = min_value
        else:
            offset = (context.doc_index * 37 + len(path) * 13) % (span + 1)
            value = min_value + offset

        multiple = schema.get("multipleOf")
        if multiple:
            factor = int(multiple)
            if factor > 0:
                value = max(min_value, (value // factor) * factor)

        return int(value)

    def _sample_number(
        self, schema: Mapping[str, Any], *, path: Tuple[str, ...], context: _BuilderContext
    ) -> float:
        minimum = schema.get("minimum")
        maximum = schema.get("maximum")
        exclusive_min = schema.get("exclusiveMinimum")
        exclusive_max = schema.get("exclusiveMaximum")

        min_value = float(minimum) if minimum is not None else 0.0
        max_value = float(maximum) if maximum is not None else min_value + 1000.0

        if exclusive_min is True:
            min_value = math.nextafter(min_value, math.inf)
        elif isinstance(exclusive_min, (int, float)):
            min_value = float(exclusive_min) + 0.1

        if exclusive_max is True:
            max_value = math.nextafter(max_value, -math.inf)
        elif isinstance(exclusive_max, (int, float)):
            max_value = float(exclusive_max) - 0.1

        if max_value < min_value:
            max_value = min_value

        span = max_value - min_value
        base_fraction = ((context.doc_index * 17 + len(path) * 5) % 1000) / 1000.0
        value = min_value + span * base_fraction

        decimal_places = 5 if schema.get("format") == "double" else 3
        value = round(value, decimal_places)

        multiple = schema.get("multipleOf")
        if multiple:
            step = float(multiple)
            if step > 0:
                value = round(math.floor(value / step) * step, decimal_places)

        return value


def deterministic_uuid(prefix: str, value: int) -> str:
    digest = hashlib.md5(f"{prefix}:{value}".encode("utf-8")).hexdigest()
    return f"{digest[:8]}-{digest[8:12]}-{digest[12:16]}-{digest[16:20]}-{digest[20:]}"


def partition_key(uuid_str: str, modulus: int) -> int:
    return uuid.UUID(uuid_str).bytes[-1] % modulus


def parse_resource_cycle(raw: str, *, resource_version: str) -> List[ResourceConfig]:
    entries = [entry.strip() for entry in raw.split(",") if entry.strip()]
    if not entries:
        raise ValueError("resource cycle must include at least one entry")

    configs: List[ResourceConfig] = []
    for entry in entries:
        if ":" in entry:
            resource_name, schema_name = entry.split(":", 1)
        else:
            resource_name = entry
            schema_name = SCHEMA_BY_RESOURCE.get(resource_name)
            if not schema_name:
                raise ValueError(
                    f"Unknown resource '{resource_name}'. Use the form resource:schema."
                )
        configs.append(ResourceConfig(resource_name, schema_name, resource_version))

    return configs


def ensure_positive(name: str, value: int) -> None:
    if value <= 0:
        raise ValueError(f"{name} must be greater than zero (received {value})")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--documents",
        type=int,
        default=1000,
        help="Number of document rows to generate (default: 1000)",
    )
    parser.add_argument(
        "--aliases",
        type=int,
        default=None,
        help="Number of alias rows (must match documents for 1:1 mapping)",
    )
    parser.add_argument(
        "--partitions",
        type=int,
        default=16,
        help="Number of hash partitions to emulate (default: 16)",
    )
    parser.add_argument(
        "--openapi",
        type=Path,
        default=DEFAULT_OPENAPI_PATH,
        help="Path to the Ed-Fi OpenAPI specification (default: ./swagger.json)",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=None,
        help="Directory for generated CSV files (default: ./out relative to script)",
    )
    parser.add_argument(
        "--seed",
        type=int,
        default=12345,
        help="Base seed for deterministic payload generation",
    )
    parser.add_argument(
        "--resource-cycle",
        type=str,
        default="sections,assessments,studentAcademicRecords",
        help="Comma-separated resource list (optionally resource:schema) cycled per row",
    )
    parser.add_argument(
        "--resource-version",
        type=str,
        default=DEFAULT_RESOURCE_VERSION,
        help="Value written to Document.ResourceVersion (default: 7.3.0)",
    )
    parser.add_argument(
        "--project-name",
        type=str,
        default="perf-dms-document-alias-only",
        help="Value written to Document.ProjectName",
    )
    return parser.parse_args()


def load_openapi(path: Path) -> Mapping[str, Any]:
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def ensure_output_dir(path: Path) -> Path:
    path.mkdir(parents=True, exist_ok=True)
    return path


def main() -> None:
    args = parse_args()

    ensure_positive("documents", args.documents)
    ensure_positive("partitions", args.partitions)

    aliases = args.aliases if args.aliases is not None else args.documents
    if aliases != args.documents:
        raise ValueError("Document and alias counts must match for 1:1 generation")

    openapi_path = args.openapi.expanduser()
    if not openapi_path.exists():
        raise FileNotFoundError(f"OpenAPI spec not found: {openapi_path}")

    spec = load_openapi(openapi_path)
    builder = OpenApiExampleBuilder(spec, base_seed=args.seed)
    resource_cycle = parse_resource_cycle(
        args.resource_cycle, resource_version=args.resource_version
    )

    if args.output is None:
        default_output = Path(__file__).resolve().parent / "out"
        output_dir = ensure_output_dir(default_output)
    else:
        output_dir = ensure_output_dir(args.output.expanduser().resolve())

    document_path = output_dir / "document.csv"
    alias_path = output_dir / "alias.csv"

    print(f"Generating {args.documents:,} documents -> {document_path}")
    print(f"Generating {aliases:,} aliases   -> {alias_path}")

    base_timestamp = datetime(2020, 1, 1, tzinfo=timezone.utc)

    with document_path.open("w", encoding="utf-8", newline="") as doc_file, alias_path.open(
        "w", encoding="utf-8", newline=""
    ) as alias_file:
        document_writer = csv.writer(doc_file)
        alias_writer = csv.writer(alias_file)

        document_writer.writerow(
            [
                "DocumentPartitionKey",
                "DocumentUuid",
                "ResourceName",
                "ResourceVersion",
                "IsDescriptor",
                "ProjectName",
                "EdfiDoc",
                "LastModifiedTraceId",
            ]
        )

        alias_writer.writerow(
            [
                "ReferentialPartitionKey",
                "ReferentialId",
                "DocumentId",
                "DocumentPartitionKey",
            ]
        )

        for doc_index in range(1, args.documents + 1):
            resource = resource_cycle[(doc_index - 1) % len(resource_cycle)]

            document_uuid = str(uuid7())
            document_partition = partition_key(document_uuid, args.partitions)

            alias_uuid = deterministic_uuid("alias", doc_index)
            alias_partition = partition_key(alias_uuid, args.partitions)

            payload = builder.build(resource.schema_name, doc_index=doc_index)
            if not isinstance(payload, dict):
                payload = {"value": payload}

            payload["id"] = document_uuid
            payload["_etag"] = hashlib.md5(
                f"etag:{document_uuid}".encode("utf-8")
            ).hexdigest()
            payload["_lastModifiedDate"] = (
                base_timestamp + timedelta(minutes=doc_index)
            ).isoformat().replace("+00:00", "Z")

            last_trace_id = f"trace-{doc_index:08d}"

            edfi_doc = json.dumps(payload, separators=(",", ":"), ensure_ascii=False)

            document_writer.writerow(
                [
                    document_partition,
                    document_uuid,
                    resource.resource_name,
                    resource.resource_version,
                    "false",
                    args.project_name,
                    edfi_doc,
                    last_trace_id,
                ]
            )

            alias_writer.writerow(
                [
                    alias_partition,
                    alias_uuid,
                    doc_index,
                    document_partition,
                ]
            )

    print("Generation complete")


if __name__ == "__main__":
    main()
