"""
Golden-diff harness — the correctness gate for our builder.

Builds each project with cfd_build and asserts the generated Main.cs matches the
real 3CX-Studio build stored in golden/{Project}/Sources/Main.cs, modulo a small
whitelist of genuinely volatile fields (currently: the ECC inner-class hash).

Run:  python3 -m pytest tools/cfd/tests -q
   or: python3 tools/cfd/tests/test_golden.py CreditCard   (standalone diff)
"""
from __future__ import annotations
import re
import sys
import difflib
from pathlib import Path

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
REPO = TOOLS.parent.parent
sys.path.insert(0, str(TOOLS))
import cfd_build  # noqa: E402

# Projects with a captured golden build. Add rows as goldens are collected.
GOLDEN_PROJECTS = ["CreditCard"]


def normalize(cs: str) -> str:
    """Neutralize fields that are volatile but semantically irrelevant.

    ECC inner-class hash: `readCallSid1018470446ECCComponent` -> `readCallSid<HASH>ECCComponent`.
    (Open question: is this hash deterministic? If we later learn to reproduce it,
    delete this rule so the diff becomes exact.)
    """
    cs = cs.replace("\r\n", "\n")  # goldens are CRLF (Studio/Windows); builder emits LF
    return re.sub(r"([A-Za-z_]\w*?)\d+ECCComponent", r"\1<HASH>ECCComponent", cs)


def build_main_cs(project_name: str) -> str:
    proj_dir = REPO / "references" / "3cx-official-demos" / project_name
    project = cfd_build.Project(proj_dir)
    # Manifest params come from the golden so the comparison isolates Main.cs codegen.
    return cfd_build.transpile(project, namespace=project_name)


def golden_main_cs(project_name: str) -> str:
    return (REPO / "golden" / project_name / "Sources" / "Main.cs").read_text()


def diff(project_name: str) -> list[str]:
    got = normalize(build_main_cs(project_name))
    want = normalize(golden_main_cs(project_name))
    return list(difflib.unified_diff(
        want.splitlines(keepends=True), got.splitlines(keepends=True),
        fromfile=f"golden/{project_name}/Main.cs", tofile=f"generated/{project_name}/Main.cs",
    ))


def test_golden_main_cs():
    for name in GOLDEN_PROJECTS:
        d = diff(name)
        assert not d, f"{name}: generated Main.cs diverges from golden:\n" + "".join(d[:80])


if __name__ == "__main__":
    target = sys.argv[1] if len(sys.argv) > 1 else GOLDEN_PROJECTS[0]
    d = diff(target)
    if not d:
        print(f"{target}: MATCH — generated Main.cs == golden (normalized)")
    else:
        sys.stdout.writelines(d)
        print(f"\n{target}: {len(d)} diff lines")
