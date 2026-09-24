# Working preferences

- Standing user instruction: automatically create local Git commits when a meaningful unit of work is complete and appropriately verified. Do not wait for the user to request a commit or ask for confirmation each time.
- Use clear commit messages describing the completed change. Check the staged changes before committing.
- Preserve unrelated work in progress; include it only when the user explicitly requests committing the current workspace state.
- Respect `.gitignore`: private indexes, diagnostic data, model weights, local runtimes and build artifacts stay out of commits.
