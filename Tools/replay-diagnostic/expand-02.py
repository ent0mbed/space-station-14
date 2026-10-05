#!/usr/bin/env python3
"""Explicit bounded diagnostic 0.2 -> 0.1 reader adapter; streams frames to stdout."""
import argparse
import json
import sys

MAX_RECORD_BYTES = 64 * 1024 * 1024
MAX_DEFINITIONS = 100_000
MAX_DEFINITION_BYTES = 512 * 1024 * 1024


def records(stream):
    while line := stream.readline(MAX_RECORD_BYTES + 1):
        if len(line) > MAX_RECORD_BYTES:
            raise ValueError("Diagnostic record budget exceeded")
        yield json.loads(line)


def expand(stream):
    definitions = {}
    definition_bytes = 0
    version = None
    for record in records(stream):
        kind = record.get("kind")
        if version is None:
            if kind != "diagnostic-header":
                raise ValueError("Missing diagnostic header")
            version = record.get("schema")
            if version not in ("ss14-diagnostic/0.1", "ss14-diagnostic/0.2"):
                raise ValueError(f"Unsupported diagnostic schema {version}")
            # Legacy strict readers know only the original 0.1 header fields.
            record = {key: value for key, value in record.items() if key in (
                "kind", "schema", "gameBuild", "engineVersion", "frameCount", "sourceStartTick",
                "timeUnit", "finalizedTransport")}
            record["schema"] = "ss14-diagnostic/0.1"
        elif version == "ss14-diagnostic/0.2":
            if kind == "resource-definition":
                continue  # Legacy logical references remain in sprite/audio/resource values.
            if kind == "sprite-definition":
                key = record["spriteId"]
                if key != len(definitions) + 1 or len(definitions) >= MAX_DEFINITIONS:
                    raise ValueError("Invalid or excessive sprite definitions")
                encoded = json.dumps(record["value"], separators=(",", ":")).encode()
                definition_bytes += len(encoded)
                if definition_bytes > MAX_DEFINITION_BYTES:
                    raise ValueError("Sprite definition byte budget exceeded")
                definitions[key] = encoded
                continue
            if kind in ("snapshot", "delta"):
                for entity in record["upserts"]:
                    key = entity.pop("spriteId")
                    if key is None:
                        entity["sprite"] = None
                    elif key not in definitions:
                        raise ValueError("Sprite referenced before its definition")
                    else:
                        entity["sprite"] = json.loads(definitions[key])
                record.pop("sourceServerTime100ns", None)
                for event in record["audioEvents"]:
                    if "value" in event:
                        event["value"].pop("startReplayTime100ns", None)
                        event["value"].pop("resourceId", None)
        yield record


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input")
    args = parser.parse_args()
    with open(args.input, "rb") as source:
        for record in expand(source):
            sys.stdout.write(json.dumps(record, separators=(",", ":")) + "\n")


if __name__ == "__main__":
    main()
