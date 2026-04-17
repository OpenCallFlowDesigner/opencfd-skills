---
description: Validate a CFD project for common mistakes
---

Run the CFD project validator and interpret the results.

If the user specified a project directory, validate that. Otherwise, detect which project to validate from `$ARGUMENTS` or ask.

Run:
```bash
python3 "${CLAUDE_PLUGIN_ROOT}/scripts/validate_project.py" {project_dir}
```

If there are errors, explain each one and offer to fix them. If all clear, confirm the project is valid.

$ARGUMENTS
