#!/usr/bin/env python3

# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

"""
Generate deterministic CSV files for the DMS performance harness.

This replicates the logic used in perf-claude/scripts/generate-test-data.sh but streams the data
to CSV so it can be loaded quickly with COPY.  The output matches the production schema:

* document.csv  -> dms.Document (excluding the identity column)
* alias.csv     -> dms.Alias
* reference.csv -> dms.Reference

Example:
    python generate_deterministic_data.py --output ./out --documents 100000 --references 20000000
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
import os
from dataclasses import dataclass
from pathlib import Path
from typing import List, Sequence
from uuid import UUID
from uuid_extensions import uuid7


def deterministic_uuid(prefix: str, value: int) -> str:
    """Return a stable UUID computed from the md5 hash of prefix:value."""
    digest = hashlib.md5(f"{prefix}:{value}".encode("utf-8")).hexdigest()
    return f"{digest[:8]}-{digest[8:12]}-{digest[12:16]}-{digest[16:20]}-{digest[20:]}"


def partition_key(uuid_str: str, num_partitions: int) -> int:
    """Partition key based on the last byte of the UUID, mirroring PartitionUtility."""
    return UUID(uuid_str).bytes[-1] % num_partitions


def resource_name_for(index: int) -> str:
    resources = (
        "students",
        "studentSchoolAssociations",
        "schools",
        "courses",
        "sections",
        "studentSectionAssociations",
        "staff",
        "staffSchoolAssociations",
        "grades",
        "assessments",
    )
    return resources[index % len(resources)]


@dataclass
class DocumentInfo:
    document_id: int
    partition_key: int
    alias_row_number: int
    weight: int
    seed: int


@dataclass
class AliasInfo:
    document_id: int
    document_partition: int
    referential_id: str
    referential_partition: int


def generate_documents(
    writer: csv.writer,
    num_documents: int,
    num_partitions: int,
    avg_refs_per_doc: int,
) -> tuple[List[DocumentInfo], List[AliasInfo]]:
    """Generate document and alias metadata while streaming document rows."""
    documents: List[DocumentInfo] = []
    aliases: List[AliasInfo] = []

    for index in range(1, num_documents + 1):
        doc_uuid = str(uuid7.uuid7())
        doc_partition = partition_key(doc_uuid, num_partitions)
        alias_uuid = deterministic_uuid("alias", doc_index)
        alias_partition = partition_key(alias_uuid, num_partitions)

        weight = (
            avg_refs_per_doc * 10
            if (index - 1) % 20 == 0
            else avg_refs_per_doc * 3
            if (index - 1) % 5 == 0
            else avg_refs_per_doc
        )

        documents.append(
            DocumentInfo(
                document_id=index,
                partition_key=doc_partition,
                alias_row_number=index - 1,
                weight=weight,
                seed=(index - 1) * 8191,
            )
        )
        aliases.append(
            AliasInfo(
                document_id=index,
                document_partition=doc_partition,
                referential_id=alias_uuid,
                referential_partition=alias_partition,
            )
        )

        edfi_doc = json.dumps(
            {
                "id": doc_uuid,
                "studentUniqueId": f"STU{index}",
                "firstName": f"FirstName{index}",
                "lastSurname": f"LastName{index}",
                "birthDate": "2010-01-01",
                "_etag": hashlib.md5(f"etag:{index}".encode("utf-8")).hexdigest(),
            },
            separators=(",", ":"),
        )

        security_elements = json.dumps(
            {
                "Namespace": ["uri://ed-fi.org"],
                "EducationOrganization": {"Id": str(index % 100)},
            },
            separators=(",", ":"),
        )

        writer.writerow(
            [
                doc_partition,
                doc_uuid,
                resource_name_for(index - 1),
                "5.0.0",
                "false",
                "ed-fi",
                edfi_doc,
                security_elements,
                f"perf-test-{index}",
            ]
        )

    return documents, aliases


def generate_aliases(writer: csv.writer, alias_infos: Sequence[AliasInfo]) -> None:
    for info in alias_infos:
        writer.writerow(
            [
                info.referential_partition,
                info.referential_id,
                info.document_id,
                info.document_partition,
            ]
        )


def generate_references(
    writer: csv.writer,
    documents: Sequence[DocumentInfo],
    aliases: Sequence[AliasInfo],
    num_references: int,
) -> int:
    """Generate deterministic reference rows limited to num_references."""
    total_aliases = len(aliases)
    if total_aliases == 0 or len(documents) == 0 or num_references <= 0:
        return 0

    total_weight = sum(doc.weight for doc in documents)
    if total_weight == 0:
        return 0

    references_written = 0

    for doc in documents:
        target_refs = math.ceil(num_references * doc.weight / total_weight)
        if target_refs <= 0:
            continue

        for seq in range(target_refs):
            if references_written >= num_references:
                return references_written

            candidate = (doc.seed + seq) % total_aliases
            if candidate == doc.alias_row_number:
                candidate = (candidate + 1) % total_aliases

            alias = aliases[candidate]

            writer.writerow(
                [
                    doc.document_id,
                    doc.partition_key,
                    alias.referential_id,
                    alias.referential_partition,
                ]
            )
            references_written += 1

    return references_written


def ensure_directory(path: Path) -> None:
    path.mkdir(parents=True, exist_ok=True)


def resolve(path: Path) -> Path:
    return path if path.is_absolute() else path.resolve()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--documents",
        type=int,
        default=int(os.getenv("NUM_DOCUMENTS", "100000")),
        help="Number of documents to generate (default: 100000)",
    )
    parser.add_argument(
        "--references",
        type=int,
        default=int(os.getenv("NUM_REFERENCES", "20000000")),
        help="Total number of references to generate (default: 20000000)",
    )
    parser.add_argument(
        "--avg-refs-per-doc",
        dest="avg_refs_per_doc",
        type=int,
        default=int(os.getenv("AVG_REFS_PER_DOC", "200")),
        help="Average references per document used for weighting (default: 200)",
    )
    parser.add_argument(
        "--partitions",
        type=int,
        default=int(os.getenv("NUM_PARTITIONS", "16")),
        help="Number of hash partitions (default: 16)",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path(os.getenv("OUTPUT_DIR", "./out")),
        help="Output directory for CSV files (default: ./out)",
    )
    return parser.parse_args()


if __name__ == "__main__":
    args = parse_args()
    output_dir = resolve(args.output)
    ensure_directory(output_dir)

    document_path = output_dir / "document.csv"
    alias_path = output_dir / "alias.csv"
    reference_path = output_dir / "reference.csv"

    print(f"Generating {args.documents:,} documents to {document_path}")
    with document_path.open("w", encoding="utf-8", newline="") as doc_file:
        document_writer = csv.writer(doc_file)
        document_writer.writerow(
            [
                "DocumentPartitionKey",
                "DocumentUuid",
                "ResourceName",
                "ResourceVersion",
                "IsDescriptor",
                "ProjectName",
                "EdfiDoc",
                "SecurityElements",
                "LastModifiedTraceId",
            ]
        )
        docs, alias_infos = generate_documents(
            document_writer,
            args.documents,
            args.partitions,
            args.avg_refs_per_doc,
        )

    print(f"Generating {len(alias_infos):,} aliases to {alias_path}")
    with alias_path.open("w", encoding="utf-8", newline="") as alias_file:
        alias_writer = csv.writer(alias_file)
        alias_writer.writerow(
            [
                "ReferentialPartitionKey",
                "ReferentialId",
                "DocumentId",
                "DocumentPartitionKey",
            ]
        )
        generate_aliases(alias_writer, alias_infos)

    print(f"Generating up to {args.references:,} references to {reference_path}")
    with reference_path.open("w", encoding="utf-8", newline="") as ref_file:
        reference_writer = csv.writer(ref_file)
        reference_writer.writerow(
            [
                "ParentDocumentId",
                "ParentDocumentPartitionKey",
                "ReferentialId",
                "ReferentialPartitionKey",
            ]
        )
        written = generate_references(reference_writer, docs, alias_infos, args.references)

    print(f"Reference rows written: {written:,}")
