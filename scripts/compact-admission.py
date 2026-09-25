"""Select a fixed no-match threshold on development probes, then score holdout.

Probe inputs contain local candidate IDs and belong under ignored diagnostics.
This is an offline screening tool, not a Scout runtime policy.
"""

import argparse
import hashlib
import json
from pathlib import Path


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def summary(rows, threshold):
    positives = [r for r in rows.values() if r["positive"]]
    negatives = [r for r in rows.values() if not r["positive"]]
    accepted = lambda row: row["topLogit"] > threshold
    return {
        "probedCases": len(rows),
        "probedPositives": len(positives),
        "recalledPositives": sum(r["recalled"] for r in positives),
        "positiveCorrect": sum(r["topCorrect"] and accepted(r) for r in positives),
        "probedNegatives": len(negatives),
        "falseMatches": sum(accepted(r) for r in negatives),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--development", type=Path)
    parser.add_argument("--policy", type=Path)
    parser.add_argument("--holdout", type=Path)
    parser.add_argument("--max-false-matches", type=int, default=2)
    parser.add_argument("--policy-out", type=Path)
    args = parser.parse_args()
    if args.max_false_matches < 0:
        parser.error("The false-match budget must be nonnegative")
    if bool(args.development) == bool(args.policy):
        parser.error("Specify exactly one of --development or --policy")
    if args.policy:
        if not args.holdout or args.policy_out:
            parser.error("--policy requires --holdout and cannot create another policy")
        policy = json.loads(args.policy.read_text(encoding="utf-8"))
        holdout = json.loads(args.holdout.read_text(encoding="utf-8"))
        if holdout["model"] != policy["model"]:
            parser.error("Holdout probe uses a different model")
        print(json.dumps({"policyHash": digest(args.policy), "model": policy["model"],
                          "threshold": policy["threshold"], "holdoutProbeHash": digest(args.holdout),
                          "holdout": summary(holdout["cases"], policy["threshold"])}, indent=2))
        return
    if args.holdout:
        parser.error("Freeze a policy before scoring holdout; use --policy")

    development = json.loads(args.development.read_text(encoding="utf-8"))
    rows = development["cases"]
    if not rows:
        parser.error("Development probe has no cases")
    thresholds = sorted({r["topLogit"] for r in rows.values()})
    choices = []
    for threshold in thresholds:
        result = summary(rows, threshold)
        if result["falseMatches"] <= args.max_false_matches:
            # Among equal positive counts, choose fewer false matches and the
            # stricter threshold. This is an in-sample policy choice.
            choices.append((result["positiveCorrect"], -result["falseMatches"], threshold))
    _, _, threshold = max(choices)
    policy = {
        "model": development["model"], "threshold": threshold,
        "developmentProbeHash": digest(args.development),
        "maxDevelopmentFalseMatches": args.max_false_matches,
        "development": summary(rows, threshold),
    }
    if args.policy_out:
        if args.policy_out.exists():
            parser.error("Refusing to overwrite a frozen policy")
        args.policy_out.parent.mkdir(parents=True, exist_ok=True)
        args.policy_out.write_text(json.dumps(policy, indent=2, allow_nan=False), encoding="utf-8")
        print(json.dumps(policy, indent=2))
        print("Policy SHA256:", digest(args.policy_out))
        return
    print(json.dumps(policy, indent=2))


if __name__ == "__main__":
    main()
