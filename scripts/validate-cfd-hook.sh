#!/bin/bash
# PostToolUse hook: auto-validate CFD project after writing/editing .flow or .cfdproj files.
# Receives tool input as JSON on stdin. Emits validation errors to stderr (exit 2) so
# Claude sees them as context and can self-correct.

INPUT=$(cat)
FILE_PATH=$(printf '%s' "$INPUT" | python3 -c "import sys,json; print(json.load(sys.stdin).get('tool_input',{}).get('file_path',''))" 2>/dev/null)

if [ -z "$FILE_PATH" ]; then
    exit 0
fi

# Only validate .flow, .cfdproj, or files inside a CFD project (Audio/, Libraries/).
case "$FILE_PATH" in
    *.flow|*.cfdproj|*/Audio/*|*/Libraries/*)
        ;;
    *)
        exit 0
        ;;
esac

# Walk up from the file's directory to find the enclosing *.cfdproj.
PROJECT_DIR=$(dirname "$FILE_PATH")
while [ "$PROJECT_DIR" != "/" ] && [ -n "$PROJECT_DIR" ]; do
    if ls "$PROJECT_DIR"/*.cfdproj 1>/dev/null 2>&1; then
        break
    fi
    PROJECT_DIR=$(dirname "$PROJECT_DIR")
done

# Need both .cfdproj and Main.flow to validate.
if ! ls "$PROJECT_DIR"/*.cfdproj 1>/dev/null 2>&1 || [ ! -f "$PROJECT_DIR/Main.flow" ]; then
    exit 0
fi

VALIDATOR="${CLAUDE_PLUGIN_ROOT}/scripts/validate_project.py"
if [ ! -f "$VALIDATOR" ]; then
    exit 0
fi

OUTPUT=$(python3 "$VALIDATOR" "$PROJECT_DIR" 2>&1)
EXIT_CODE=$?

if [ $EXIT_CODE -ne 0 ]; then
    echo "$OUTPUT" >&2
    exit 2
fi

exit 0
