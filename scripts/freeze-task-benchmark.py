"""Freeze manually labelled Scout search tasks against an indexed candidate snapshot.

The task file and output may contain private filenames. Keep them under the
ignored artifacts/diagnostics directory. Labels are supplied by a person, never
derived from Scout's search results. The output uses --search-eval's dataset
schema and refuses to overwrite an existing benchmark.
"""

import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path


ACTION = {"open": 0, "preview": 1, "reveal": 2, "copy": 3}


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def field(item, name):
    return item.get(name, item.get(name[0].upper() + name[1:]))


def freeze(source, tasks):
    candidates = field(source, "candidates")
    if not isinstance(candidates, list) or not candidates:
        raise ValueError("Source must contain indexed candidates")
    now = field(source, "now")
    roots = field(source, "roots") or []
    if not isinstance(now, str) or not now or not isinstance(roots, list):
        raise ValueError("Source must contain its snapshot time and root list")
    indexes = defaultdict(list)
    for candidate in candidates:
        for value in (field(candidate, "id"), field(candidate, "target"), field(candidate, "title")):
            if isinstance(value, str) and value:
                indexes[value.replace("/", "\\").casefold()].append(candidate)

    def resolve(label):
        if not isinstance(label, str) or not label.strip():
            raise ValueError("Each expected item must name an indexed ID, full path, or unique title")
        matches = {field(c, "id") for c in indexes[label.replace("/", "\\").casefold()]}
        if len(matches) != 1:
            raise ValueError(f"Expected item {label!r} resolves to {len(matches)} candidates; use its full path or ID")
        return matches.pop()

    supplied = tasks.get("cases")
    if not isinstance(supplied, list) or not supplied:
        raise ValueError("Task file must have a nonempty cases array")
    seen_ids, seen_queries, cases = set(), set(), []
    for position, task in enumerate(supplied):
        if not isinstance(task, dict):
            raise ValueError(f"Task {position + 1} must be an object")
        query = task.get("query")
        if not isinstance(query, str) or not query.strip() or len(query) > 512:
            raise ValueError(f"Task {position + 1} needs a query of 1–512 characters")
        if query.casefold() in seen_queries:
            raise ValueError(f"Repeated query in task {position + 1}; each task should be distinct")
        seen_queries.add(query.casefold())
        identifier = task.get("id", f"task-{position + 1:03d}")
        if not isinstance(identifier, str) or not identifier or identifier in seen_ids:
            raise ValueError(f"Task {position + 1} needs a unique string ID")
        seen_ids.add(identifier)
        if "expected" not in task:
            raise ValueError(f"Task {identifier} must explicitly set expected; use [] for no match")
        expected_labels = task["expected"]
        if isinstance(expected_labels, str):
            expected_labels = [expected_labels]
        if not isinstance(expected_labels, list):
            raise ValueError(f"Task {identifier} expected must be a string or list")
        expected = [resolve(label) for label in expected_labels]
        if len(expected) != len(set(expected)):
            raise ValueError(f"Task {identifier} repeats an expected item")
        mode = task.get("mode", "one" if expected else "empty")
        if mode not in {"one", "set", "empty"} or (mode == "empty") != (not expected):
            raise ValueError(f"Task {identifier} mode disagrees with expected items")
        action = task.get("action", "open")
        if action not in ACTION:
            raise ValueError(f"Task {identifier} action must be one of {', '.join(ACTION)}")
        count = task.get("count")
        if count is not None and (not isinstance(count, int) or isinstance(count, bool) or count < 1 or count > 25):
            raise ValueError(f"Task {identifier} count must be an integer from 1 to 25")
        folder = task.get("folder")
        if folder is not None and (not isinstance(folder, str) or not folder):
            raise ValueError(f"Task {identifier} folder must be a nonempty path")
        extensions = task.get("extensions", [])
        if not isinstance(extensions, list) or any(not isinstance(ext, str) for ext in extensions):
            raise ValueError(f"Task {identifier} extensions must be a string array")
        order_labels = task.get("order")
        if order_labels is None:
            order = [expected] if expected else []
        else:
            if not isinstance(order_labels, list) or any(not isinstance(tier, list) for tier in order_labels):
                raise ValueError(f"Task {identifier} order must be an array of tiers")
            order = [[resolve(label) for label in tier] for tier in order_labels]
            if set(expected) != {item for tier in order for item in tier}:
                raise ValueError(f"Task {identifier} order must contain exactly its expected items")
        cases.append(dict(id=identifier, query=query.strip(), family=task.get("family", "real-task"),
                          split=task.get("split", "holdout"), expected=expected, order=order,
                          mode=mode, count=count, folder=folder, direct=bool(task.get("direct", False)),
                          extensions=extensions, basis=task.get("basis"), action=ACTION[action],
                          literal=bool(task.get("literal", False)),
                          constraint=folder is not None or bool(extensions) or task.get("basis") is not None))
    return dict(now=now, roots=roots, candidates=candidates,
                cases=cases, summary=dict(cases=len(cases), splits=dict(Counter(c["split"] for c in cases)),
                                         families=dict(Counter(c["family"] for c in cases))))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True, help="Frozen Scout dataset with candidates and roots")
    parser.add_argument("--tasks", type=Path, required=True, help="Manually labelled task JSON")
    parser.add_argument("--output", type=Path, required=True, help="New frozen --search-eval dataset")
    args = parser.parse_args()
    if args.output.exists():
        parser.error("Refusing to overwrite a frozen benchmark; choose a new output path")
    source = json.loads(args.source.read_text(encoding="utf-8"))
    tasks = json.loads(args.tasks.read_text(encoding="utf-8"))
    report = freeze(source, tasks)
    report["provenance"] = dict(sourceHash=sha256(args.source), taskLabelsHash=sha256(args.tasks),
                                labels="Manually supplied; not inferred from Scout predictions")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps(report["summary"], indent=2))
    print("SHA256:", sha256(args.output))


if __name__ == "__main__":
    main()
