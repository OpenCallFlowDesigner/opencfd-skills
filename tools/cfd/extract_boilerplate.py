#!/usr/bin/env python3
"""
extract_boilerplate.py — turn a golden Studio-built Main.cs into a slotted template.

One-time (re-runnable) tooling: reads golden/{Project}/Sources/Main.cs, replaces the
three project-specific regions with {{SLOT}} markers, and writes:

    tools/cfd/boilerplate/Main.cs.tmpl        # fixed skeleton + 4 slots
    tools/cfd/boilerplate/golden_init_vars.cs      # reference: InitializeVariables body
    tools/cfd/boilerplate/golden_init_components.cs # reference: InitializeComponents body
    tools/cfd/boilerplate/golden_ecc.cs            # reference: ECC inner classes

Slots in the template:
    {{NAMESPACE}}         namespace + class project name (e.g. CreditCardCapture)
    {{PROJECT_NAME}}      string literals used at runtime (PromptQueue / logHeader)
    {{INIT_VARIABLES}}    body of InitializeVariables(...)
    {{INIT_COMPONENTS}}   the component-construction block of InitializeComponents(...)
    {{ECC_CLASSES}}       ExecuteCSharpCode inner classes (may be empty)

Anchors are structural strings that are stable across all Studio builds. If a future
golden changes them, this script fails loudly rather than producing a silent mismatch.
"""
from __future__ import annotations
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent


def anchor(text: str, needle: str) -> int:
    idx = text.find(needle)
    if idx < 0:
        sys.exit(f"ERROR: anchor not found in golden Main.cs: {needle!r}")
    return idx


def extract(project: str) -> None:
    golden = REPO / "golden" / project / "Sources" / "Main.cs"
    src = golden.read_text()
    out = HERE / "boilerplate"
    out.mkdir(exist_ok=True)

    # --- Slot: INIT_VARIABLES (body between the signature brace and its close) ---
    iv_sig = "private void InitializeVariables(string callID)\n"
    iv_start = anchor(src, iv_sig) + len(iv_sig)
    iv_open = src.index("{", iv_start)
    ic_sig = "private void InitializeComponents(ICallflow callflow, ICall myCall, string logHeader)"
    iv_body_end = src.rindex("}", iv_open, anchor(src, ic_sig))
    init_vars = src[iv_open + 1:iv_body_end]

    # --- Slot: INIT_COMPONENTS (between `scope = ...CreateScope(...);` and the auto-added disconnect comment) ---
    scope_line = "scope = CfdModule.Instance.CreateScope(callflow, myCall, logHeader);\n"
    ic_start = anchor(src, scope_line) + len(scope_line)
    autodisc = "// Add a final DisconnectCall component to the main and error handler flows"
    ic_end = anchor(src, autodisc)
    # back up to the line indentation before the comment
    ic_end = src.rindex("\n", ic_start, ic_end)
    init_components = src[ic_start:ic_end]

    # --- Slot: ECC_CLASSES (inner classes after the last Process* method, before the final two braces) ---
    ecc_marker = "ECCComponent : ExternalCodeExecutionComponent"
    if ecc_marker in src:
        # first inner class starts at its `public class <name>ECCComponent`
        ecc_start = src.rindex("public class ", 0, anchor(src, ecc_marker))
        # walk back to the start of that (indented) line
        ecc_start = src.rindex("\n", 0, ecc_start) + 1
        tail_close = src.rindex("\n   }\n}")  # class Main close + namespace close
        ecc_classes = src[ecc_start:tail_close]
    else:
        ecc_classes = ""
        ecc_start = ecc_end = -1

    # --- Build the template by replacing the slot spans (right-to-left to keep indices valid) ---
    tmpl = src
    if ecc_classes:
        tmpl = tmpl[:ecc_start] + "{{ECC_CLASSES}}\n" + tmpl[tail_close + 1:]
    # recompute ic/iv spans against the (possibly shortened) tmpl by re-anchoring on stable text
    tmpl = tmpl.replace(init_components, "{{INIT_COMPONENTS}}", 1)
    tmpl = tmpl.replace(init_vars, "{{INIT_VARIABLES}}", 1)
    tmpl = tmpl.replace(f"namespace {project}", "namespace {{NAMESPACE}}", 1)
    tmpl = re.sub(rf'(["\(]){re.escape(project)}(["\)\s])', r"\1{{PROJECT_NAME}}\2", tmpl)

    (out / "Main.cs.tmpl").write_text(tmpl)
    (out / "golden_init_vars.cs").write_text(init_vars)
    (out / "golden_init_components.cs").write_text(init_components)
    (out / "golden_ecc.cs").write_text(ecc_classes)

    n_slots = sum(tmpl.count(s) for s in ("{{INIT_VARIABLES}}", "{{INIT_COMPONENTS}}", "{{ECC_CLASSES}}", "{{NAMESPACE}}"))
    print(f"OK  wrote template ({len(tmpl.splitlines())} lines) + 3 golden references")
    print(f"    slots present: INIT_VARIABLES={tmpl.count('{{INIT_VARIABLES}}')} "
          f"INIT_COMPONENTS={tmpl.count('{{INIT_COMPONENTS}}')} "
          f"ECC_CLASSES={tmpl.count('{{ECC_CLASSES}}')} NAMESPACE={tmpl.count('{{NAMESPACE}}')} "
          f"PROJECT_NAME={tmpl.count('{{PROJECT_NAME}}')}")
    if not all(tmpl.count(s) == 1 for s in ("{{INIT_VARIABLES}}", "{{INIT_COMPONENTS}}", "{{NAMESPACE}}")):
        sys.exit("ERROR: expected exactly one of each core slot — anchors drifted, inspect the golden.")


if __name__ == "__main__":
    extract(sys.argv[1] if len(sys.argv) > 1 else "CreditCardCapture")
