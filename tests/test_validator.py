"""Tests for the CFD project validator.

Run from the repo root:
    python -m pytest tests/ -v
"""

import os
import sys
import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "scripts"))
import validate_project as vp

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
DEMOS_DIR = os.path.join(REPO_ROOT, "references", "3cx-official-demos")
FIXTURES_DIR = os.path.join(os.path.dirname(__file__), "fixtures")


# ---------------------------------------------------------------------------
# Golden tests: every bundled 3CX demo must pass the validator in default mode.
# ---------------------------------------------------------------------------

def _demo_dirs():
    if not os.path.isdir(DEMOS_DIR):
        return []
    return sorted(
        os.path.join(DEMOS_DIR, d)
        for d in os.listdir(DEMOS_DIR)
        if os.path.isdir(os.path.join(DEMOS_DIR, d))
    )


@pytest.mark.parametrize("demo_dir", _demo_dirs(), ids=os.path.basename)
def test_official_demo_has_no_errors(demo_dir):
    """Every official 3CX demo must pass the validator with zero errors."""
    issues = vp.validate_project(demo_dir)
    errors = [i for i in issues if i.level == "error"]
    assert not errors, "\n".join(str(e) for e in errors)


# ---------------------------------------------------------------------------
# Regression tests for specific checks, using synthesized fixtures.
# ---------------------------------------------------------------------------

def _write_project(tmp_path, cfdproj_body, flow_body):
    (tmp_path / "Test.cfdproj").write_text(cfdproj_body, encoding="utf-8")
    (tmp_path / "Main.flow").write_text(flow_body, encoding="utf-8")
    return str(tmp_path)


CFDPROJ_TEMPLATE = """<?xml version="1.0" encoding="utf-8"?>
<Graphical_Application_Designer_Project>
  <Version>2.1</Version>
  <Files><File path="Main.flow" type="callflow" /></Files>
  <Variables>
    <ArrayOfVariable xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
{vars}
    </ArrayOfVariable>
  </Variables>
</Graphical_Application_Designer_Project>
"""

FLOW_TEMPLATE = """<?xml version="1.0" encoding="utf-8"?>
<File>
  <Version>2.1</Version>
  <Variables>
    <ArrayOfVariable xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
{vars}
    </ArrayOfVariable>
  </Variables>
  <Flows>
    <MainFlow>
      <ns0:MainFlow Description="." DebugModeActive="False" x:Name="Main"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ns0="clr-namespace:TCX.CFD.Classes.Components;Assembly=3CX Call Flow Designer, Version=20.0.0.0, Culture=neutral, PublicKeyToken=7cb95a1a133e706e">
{components}
      </ns0:MainFlow>
    </MainFlow>
    <ErrorHandlerFlow />
    <DisconnectHandlerFlow />
  </Flows>
</File>
"""


def _var(name, initial='""'):
    return (
        f"      <Variable><Name>{name}</Name><InitialValue>{initial}</InitialValue>"
        f"<ShowScopeProperty>false</ShowScopeProperty>"
        f"<DebuggerVisible>true</DebuggerVisible><HelpText /></Variable>"
    )


def test_variable_mismatch_is_warning_by_default(tmp_path):
    proj_dir = _write_project(
        tmp_path,
        CFDPROJ_TEMPLATE.format(vars=_var("Foo")),
        FLOW_TEMPLATE.format(vars="", components=""),
    )
    issues = vp.validate_project(proj_dir)
    assert [i.level for i in issues] == ["warning"]


def test_variable_mismatch_is_error_in_strict_mode(tmp_path):
    proj_dir = _write_project(
        tmp_path,
        CFDPROJ_TEMPLATE.format(vars=_var("Foo")),
        FLOW_TEMPLATE.format(vars="", components=""),
    )
    issues = vp.validate_project(proj_dir, strict=True)
    assert any(i.level == "error" for i in issues)


def test_variable_declared_only_in_flow_is_accepted(tmp_path):
    """Reference to a variable declared only in .flow should not error."""
    component = (
        '<ns0:VariableAssignmentComponent x:Name="Assign1" '
        'VariableName="callflow$.Foo" Expression="&quot;hi&quot;" '
        'DebugModeActive="False" />'
    )
    proj_dir = _write_project(
        tmp_path,
        CFDPROJ_TEMPLATE.format(vars=""),
        FLOW_TEMPLATE.format(vars=_var("Foo"), components=component),
    )
    issues = vp.validate_project(proj_dir)
    errors = [i for i in issues if i.level == "error"]
    assert not errors, [str(e) for e in errors]


def test_undeclared_variable_reference_is_error(tmp_path):
    component = (
        '<ns0:VariableAssignmentComponent x:Name="Assign1" '
        'VariableName="callflow$.Undeclared" Expression="&quot;hi&quot;" '
        'DebugModeActive="False" />'
    )
    proj_dir = _write_project(
        tmp_path,
        CFDPROJ_TEMPLATE.format(vars=""),
        FLOW_TEMPLATE.format(vars="", components=component),
    )
    issues = vp.validate_project(proj_dir)
    assert any(
        i.level == "error" and "Undeclared" in i.message for i in issues
    )


def test_duplicate_component_names_are_error(tmp_path):
    components = (
        '<ns0:DisconnectCallComponent x:Name="Dup" DebugModeActive="False" />'
        '<ns0:DisconnectCallComponent x:Name="Dup" DebugModeActive="False" />'
    )
    proj_dir = _write_project(
        tmp_path,
        CFDPROJ_TEMPLATE.format(vars=""),
        FLOW_TEMPLATE.format(vars="", components=components),
    )
    issues = vp.validate_project(proj_dir)
    assert any(i.level == "error" and "Duplicate" in i.message for i in issues)


def test_web_service_get_with_content_is_error(tmp_path):
    component = (
        '<ns0:WebServiceRestComponent x:Name="Api" HttpRequestType="GET" '
        'URI="&quot;https://x&quot;" Content="body" ContentType="application/json" '
        'DebugModeActive="False" />'
    )
    proj_dir = _write_project(
        tmp_path,
        CFDPROJ_TEMPLATE.format(vars=""),
        FLOW_TEMPLATE.format(vars="", components=component),
    )
    issues = vp.validate_project(proj_dir)
    assert any(i.level == "error" and "GET" in i.message for i in issues)


def _credit_card_component(name):
    return (
        f'<ns0:CreditCardComponent x:Name="{name}" DebugModeActive="False" '
        'MaxRetryCount="3" IsExpirationRequired="True" IsSecurityCodeRequired="False">'
        '<ns0:ComponentBranch DisplayedText="Valid Input" x:Name="' + name + 'Valid" '
        'DebugModeActive="False" />'
        '<ns0:ComponentBranch DisplayedText="Invalid Input" x:Name="' + name + 'Invalid" '
        'DebugModeActive="False" />'
        '</ns0:CreditCardComponent>'
    )


def test_credit_card_component_is_recognized(tmp_path):
    """A CreditCardComponent must be collected and pass validation."""
    proj_dir = _write_project(
        tmp_path,
        CFDPROJ_TEMPLATE.format(vars=""),
        FLOW_TEMPLATE.format(vars="", components=_credit_card_component("requestCard")),
    )
    issues = vp.validate_project(proj_dir)
    errors = [i for i in issues if i.level == "error"]
    assert not errors, [str(e) for e in errors]


def test_duplicate_credit_card_component_names_are_error(tmp_path):
    """Proves CreditCardComponent participates in duplicate-name detection."""
    components = _credit_card_component("dupCard") + _credit_card_component("dupCard")
    proj_dir = _write_project(
        tmp_path,
        CFDPROJ_TEMPLATE.format(vars=""),
        FLOW_TEMPLATE.format(vars="", components=components),
    )
    issues = vp.validate_project(proj_dir)
    assert any(i.level == "error" and "Duplicate" in i.message for i in issues)


def test_dialer_project_is_skipped(tmp_path):
    (tmp_path / "Test.cfdproj").write_text(CFDPROJ_TEMPLATE.format(vars=""))
    (tmp_path / "Main.dialer").write_text("<dialer/>")
    issues = vp.validate_project(str(tmp_path))
    assert not issues


def test_missing_flow_is_error(tmp_path):
    (tmp_path / "Test.cfdproj").write_text(CFDPROJ_TEMPLATE.format(vars=""))
    issues = vp.validate_project(str(tmp_path))
    assert any(
        i.level == "error" and "Main.flow not found" in i.message for i in issues
    )


def test_missing_cfdproj_is_error(tmp_path):
    (tmp_path / "Main.flow").write_text(FLOW_TEMPLATE.format(vars="", components=""))
    issues = vp.validate_project(str(tmp_path))
    assert any(
        i.level == "error" and ".cfdproj" in i.message for i in issues
    )
