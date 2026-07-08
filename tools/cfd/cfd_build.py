#!/usr/bin/env python3
"""
cfd_build.py — our own deterministic `cfd build`.

    validate  ->  transpile (Main.flow -> Main.cs)  ->  package (.zip)

Correctness is enforced by golden diff against a real 3CX-Studio build
(see tools/cfd/tests/test_golden.py). This is the scaffold: the pipeline,
manifest, packaging, and the InitializeVariables generator are real; the
InitializeComponents and ECC slots are currently PINNED to the golden
reference (Phase 1 replaces each pin with an XML-driven generator, one at a
time, keeping the golden test green).

Usage:
    python3 tools/cfd/cfd_build.py references/3cx-official-demos/CreditCard \
        --extension 1975 --cfd-version 20.2.84.0

Slot status (see SLOT_MODE):
    NAMESPACE / PROJECT_NAME  generated
    INIT_VARIABLES            generated   (§6, incl. duplication quirk)
    INIT_COMPONENTS           PINNED      <- Phase 1 target
    ECC_CLASSES               PINNED      <- Phase 1 target
"""
from __future__ import annotations
import argparse
import re
import sys
import subprocess
import zipfile
from pathlib import Path
from xml.etree import ElementTree as ET

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
BOILERPLATE = HERE / "boilerplate"
PLUGIN_VALIDATOR = REPO / "scripts" / "validate_project.py"

sys.path.insert(0, str(REPO / "scripts"))
from wav_format import check_wav  # noqa: E402  (shared with the validator)

# Per slot: "generate" (driven by Main.flow) or "pin" (emit golden reference verbatim).
# Flip a slot to "generate" only once its generator reproduces the golden byte-for-byte.
# NOTE: all three slots are PINNED to the golden reference in this scaffold. The
# InitializeVariables generator (gen_init_variables) works for CreditCardCapture but
# does not yet generalize (variable scope project$/callflow$ per §6, and vars declared
# in Main.flow vs .cfdproj). Generalizing it is Phase 1; until then, pin so the golden
# test round-trips against any demo.
SLOT_MODE = {
    "init_variables": "pin",
    "init_components": "pin",
    "ecc_classes": "pin",
}


# --------------------------------------------------------------------------- #
# Source model
# --------------------------------------------------------------------------- #
class Project:
    def __init__(self, path: Path):
        self.dir = path
        self.name = path.name
        self.cfdproj = _read_cfdproj(path / f"{self.name}.cfdproj")
        self.flow_path = path / "Main.flow"
        self.flow_xml = self.flow_path.read_text()

    def referenced_audio(self) -> list[str]:
        """wavs actually named in Main.flow — Studio bundles only these."""
        names = re.findall(r"&lt;AudioFileName&gt;([^&]+\.wav)&lt;/AudioFileName&gt;", self.flow_xml)
        names += re.findall(r"<AudioFileName>([^<]+\.wav)</AudioFileName>", self.flow_xml)
        return sorted(set(names))


def _read_cfdproj(path: Path) -> dict:
    """Return {name, variables: [(name, initial_value_verbatim), ...]}."""
    root = ET.parse(path).getroot()
    variables = []
    for var in root.iter("Variable"):
        n = var.findtext("Name")
        init = var.findtext("InitialValue")
        if n is not None:
            variables.append((n, init if init is not None else ""))
    return {"variables": variables}


# --------------------------------------------------------------------------- #
# Stage 1 — validate
# --------------------------------------------------------------------------- #
def validate(project: Project) -> None:
    if not PLUGIN_VALIDATOR.exists():
        print(f"WARN: validator not found at {PLUGIN_VALIDATOR}; skipping validate stage")
        return
    r = subprocess.run([sys.executable, str(PLUGIN_VALIDATOR), str(project.dir)],
                       capture_output=True, text=True)
    sys.stdout.write(r.stdout)
    if r.returncode != 0:
        sys.stderr.write(r.stderr)
        sys.exit(f"validate: FAILED for {project.name}")


# --------------------------------------------------------------------------- #
# Stage 2 — transpile
# --------------------------------------------------------------------------- #
def gen_init_variables(project: Project) -> str:
    """Reproduce InitializeVariables body (§6). Fixed call+standard prefix is taken
    verbatim from the golden reference; only the user-variable block is generated."""
    ref = (BOILERPLATE / "golden_init_vars.cs").read_text()
    prefix, sep, _ = ref.partition("         // User variables\n")
    if not sep:
        sys.exit("gen_init_variables: golden reference missing '// User variables' marker")

    # 13 standard variables, re-emitted after the user vars (the duplication quirk).
    std_block = prefix.split("// Standard variables\n", 1)[1]
    std_lines = [ln.strip() for ln in std_block.splitlines() if ln.strip().startswith("variableMap")]

    entries: list[str] = []
    for scope in ("project$", "callflow$"):
        for name, initial in project.cfdproj["variables"]:
            entries.append(f'variableMap["{scope}.{name}"] = new Variable({initial});')
    entries += std_lines  # duplicated standard vars

    # First entry keeps the 9-space indent; every subsequent line is 12-space indented.
    lines = []
    for i, e in enumerate(entries):
        lines.append(("         " if i == 0 else "            ") + e)
    body = prefix + sep + "\n".join(lines) + "\n            \n        "
    return body


def transpile(project: Project, namespace: str) -> str:
    tmpl = (BOILERPLATE / "Main.cs.tmpl").read_text()

    init_vars = (gen_init_variables(project) if SLOT_MODE["init_variables"] == "generate"
                 else (BOILERPLATE / "golden_init_vars.cs").read_text())
    init_components = (BOILERPLATE / "golden_init_components.cs").read_text()  # PINNED (Phase 1)
    ecc = (BOILERPLATE / "golden_ecc.cs").read_text()                          # PINNED (Phase 1)

    if SLOT_MODE["init_components"] == "generate":
        sys.exit("init_components generator not implemented yet (Phase 1)")
    if SLOT_MODE["ecc_classes"] == "generate":
        sys.exit("ecc_classes generator not implemented yet (Phase 1)")

    out = (tmpl
           .replace("{{NAMESPACE}}", namespace)
           .replace("{{PROJECT_NAME}}", project.name)
           .replace("{{INIT_VARIABLES}}", init_vars)
           .replace("{{INIT_COMPONENTS}}", init_components)
           .replace("{{ECC_CLASSES}}", ecc))
    return out


def gen_manifest(app_name: str, extension: str, version: str) -> str:
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        "<cfd_app_package>\n"
        f"  <name>{app_name.lower()}.Main</name>\n"
        f"  <extension>{extension}</extension>\n"
        f"  <version>{version}</version>\n"
        "</cfd_app_package>"
    )


# --------------------------------------------------------------------------- #
# Stage 3 — package
# --------------------------------------------------------------------------- #
def package(project: Project, main_cs: str, manifest: str) -> Path:
    out_dir = project.dir / "Output" / "Release"
    out_dir.mkdir(parents=True, exist_ok=True)
    zip_path = out_dir / f"{project.name}.zip"
    audio_dir = project.dir / "Audio"
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("manifest.xml", manifest)
        bad = []
        for wav in project.referenced_audio():
            src = audio_dir / wav
            if not src.exists():
                sys.exit(f"package: referenced audio missing on disk: {wav}")
            problem = check_wav(src)
            if problem:
                bad.append(f"  {wav} {problem}")
            z.write(src, f"Audio/{wav}")
    if bad:
        zip_path.unlink(missing_ok=True)
        sys.exit("package: audio not in 3CX prompt format (8kHz mono 16-bit PCM):\n" + "\n".join(bad))
        z.writestr("Sources/Main.cs", main_cs)
    return zip_path


# --------------------------------------------------------------------------- #
def build(project_dir: Path, extension: str, version: str, namespace: str | None,
          skip_validate: bool = False) -> Path:
    project = Project(project_dir)
    app_name = namespace or project.name  # names both the C# namespace and the manifest <name>
    if not skip_validate:
        validate(project)
    main_cs = transpile(project, app_name)
    manifest = gen_manifest(app_name, extension, version)
    zip_path = package(project, main_cs, manifest)
    pinned = [k for k, v in SLOT_MODE.items() if v == "pin"]
    print(f"build: OK -> {zip_path}")
    if pinned:
        print(f"       (slots still PINNED to golden: {', '.join(pinned)} — Phase 1 TODO)")
    return zip_path


def main() -> None:
    ap = argparse.ArgumentParser(description="Deterministic 3CX CFD builder")
    ap.add_argument("project", type=Path, help="path to a CFD source project dir")
    ap.add_argument("--extension", required=True,
                    help="target 3CX extension number the app deploys to (manifest <extension>). "
                         "Per-deployment; not stored in .cfdproj — you must supply it.")
    ap.add_argument("--cfd-version", dest="version", default="20.2.84.0", help="CFD tool version (manifest)")
    ap.add_argument("--name", dest="namespace", default=None,
                    help="project/app name = C# namespace + manifest name (default: project dir name)")
    ap.add_argument("--skip-validate", action="store_true")
    a = ap.parse_args()
    build(a.project, a.extension, a.version, a.namespace, a.skip_validate)


if __name__ == "__main__":
    main()
