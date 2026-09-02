# Dioma Connectome Editor

This directory is reserved for the standalone Rust-based connectome editor.

The editor is intentionally not implemented in this change because the requested target is a separate Desktop project while the current workspace is `dioma-engine`. Creating a new application here would mix product boundaries and could overwrite unrelated work.

Planned capabilities:

- inspect Dioma Unity connectome JSON;
- visualize regions, neurons, edges, delays, signs, and weights;
- inspect HAAP profiles and organism metadata;
- validate round-trip compatibility with `DiomaBrain`, `SpeciesSpec`, and Unity;
- export JSON without changing unknown fields;
- compare two connectomes and highlight structural or numerical differences;
- optionally load compatible Rust Dioma and Prometheus components through explicit adapters.

The first implementation should be a Rust CLI with a local HTML report. A desktop GUI can be added after the data model and round-trip tests are stable.
