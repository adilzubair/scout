# Notes for coding agents

See [CONTRIBUTING.md](CONTRIBUTING.md) for build, test and pull request
guidance. In short:

- Build and run the regression suite with
  `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1`.
  Warnings are errors.
- Run diagnostic app modes only with `SCOUT_DATA_DIR` pointing at a throwaway
  directory. Never touch the real `%LOCALAPPDATA%\Scout` profile.
- Respect the byte-pinned files listed in `.gitattributes` and the CI hash pins
  for `scripts/test-package.ps1` and `scripts/verify-package.ps1`.

## Working preferences

- Create local Git commits when a meaningful unit of work is complete and
  verified; there's no need to ask first. Use clear commit messages and check
  the staged changes before committing.
- Preserve unrelated work in progress. Include it only when explicitly asked to
  commit the current workspace state.
- Respect `.gitignore`: private indexes, diagnostic data, model weights, local
  runtimes and build artifacts stay out of commits.
