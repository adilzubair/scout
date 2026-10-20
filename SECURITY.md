# Security policy

## Reporting a vulnerability

Please don't open a public issue for security problems. Report them privately
through GitHub's
[private vulnerability reporting](https://github.com/adilzubair/scout/security/advisories/new).

Include the affected version, steps to reproduce and the impact you expect. You
should receive an acknowledgement within a week.

## Scope

Areas of particular interest:

- Anything that could make Scout launch or execute something without explicit
  user action.
- The local model services (loopback HTTP with bearer tokens) and the
  multimodal worker's sandboxing.
- Leaks of indexed file contents, browser history or settings outside
  `%LOCALAPPDATA%\Scout`.
- Integrity of the setup scripts' pinned downloads.

Scout is an experimental beta. Only the latest release receives fixes.
