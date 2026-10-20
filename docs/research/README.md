# Research and development notes

These are dated lab notes kept from Scout's development: model screens,
evaluations, benchmarks and design investigations. They record what was measured
at the time, with the limits that applied then. They are not user documentation
and may describe superseded behavior. For current behavior, start with the
[main README](../../README.md).

The notes mention a few external projects:

- **[Intern](https://github.com/dabit3/intern)**: the macOS launcher whose
  public behavior Scout was modeled on. See [parity](../PARITY.md).
- **Jev**: the hosted ranking model ([TypeSafe](https://docs.typesafe.ai)) that
  Intern calls on every keystroke. Scout uses local models instead and has not
  been compared against Jev live.
- **[Laya](https://huggingface.co/convaiinnovations/laya)**: the local ranking
  model Scout uses for optional AI ranking.
- **Kev, Von, Ettin, Qwen, EmbeddingGemma**: candidate or optional local models
  evaluated as Laya alternatives or add-ons.

| Note | Topic |
|---|---|
| [VALIDATION](VALIDATION.md) | Chronological validation results and limits |
| [SEARCH_EVALUATION](SEARCH_EVALUATION.md) | Search upgrade evaluation on 504 frozen queries |
| [SEMANTIC_SEARCH](SEMANTIC_SEARCH.md) | Descriptive-search audit |
| [FSEARCH_REDESIGN](FSEARCH_REDESIGN.md) | Compact search-index redesign and benchmarks |
| [TASK_BENCHMARK](TASK_BENCHMARK.md) | Prospective task benchmark tooling and results |
| [LIGHTWEIGHT_BASELINE](LIGHTWEIGHT_BASELINE.md) | CPU baseline for Laya |
| [OFFLINE_EVALUATION](OFFLINE_EVALUATION.md) | Smaller offline alternatives |
| [LAYA_REPLACEMENT_RESEARCH](LAYA_REPLACEMENT_RESEARCH.md) | Reranker shortlist |
| [ETTIN_OFFLINE_SCREEN](ETTIN_OFFLINE_SCREEN.md) | Ettin model screen |
| [KEV_VON_RESEARCH](KEV_VON_RESEARCH.md) | Kev and Von source review |
| [LOCAL_SEMANTIC_HELPER](LOCAL_SEMANTIC_HELPER.md) | Research-only Qwen helper |
| [MULTIMODAL_ADDON](MULTIMODAL_ADDON.md) | Multimodal add-on design, protocol and log |
| [MULTIMODAL_RUNTIME_SCREEN](MULTIMODAL_RUNTIME_SCREEN.md) | Multimodal runtime spike |
| [MULTIMODAL_QUALITY_EVALUATION](MULTIMODAL_QUALITY_EVALUATION.md) | Multimodal evaluation protocol and datasets |
| [MULTIMODAL_HARDWARE_VALIDATION](MULTIMODAL_HARDWARE_VALIDATION.md) | Physical-PC validation procedure |
