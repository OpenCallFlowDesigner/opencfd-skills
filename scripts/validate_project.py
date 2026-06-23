#!/usr/bin/env python3
"""Validator for 3CX CFD project source files (.cfdproj + Main.flow).

Checks for common mistakes that cause CFD Studio errors or runtime failures.
Run against a project directory containing a .cfdproj and Main.flow.

Usage:
    python validate_project.py <project_dir>
    python validate_project.py projects/ClaimsLookupIVR
"""

import os
import sys
import glob
import re
import html
import xml.etree.ElementTree as ET

NS = "clr-namespace:TCX.CFD.Classes.Components;Assembly=3CX Call Flow Designer, Version=20.0.0.0, Culture=neutral, PublicKeyToken=7cb95a1a133e706e"
NS_XAML = "http://schemas.microsoft.com/winfx/2006/xaml"

COMPONENT_TYPES = {
    f"{{{NS}}}PromptPlaybackComponent",
    f"{{{NS}}}MenuComponent",
    f"{{{NS}}}TransferComponent",
    f"{{{NS}}}DisconnectCallComponent",
    f"{{{NS}}}ConditionalComponent",
    f"{{{NS}}}VariableAssignmentComponent",
    f"{{{NS}}}LoopComponent",
    f"{{{NS}}}UserInputComponent",
    f"{{{NS}}}VoiceInputComponent",
    f"{{{NS}}}WebServiceRestComponent",
    f"{{{NS}}}JsonXmlParserComponent",
    f"{{{NS}}}ExecuteCSharpCodeComponent",
    f"{{{NS}}}DatabaseAccessComponent",
    f"{{{NS}}}AuthenticationComponent",
    f"{{{NS}}}SurveyComponent",
    f"{{{NS}}}CRMLookupComponent",
    f"{{{NS}}}TcxGetGlobalPropertyComponent",
    f"{{{NS}}}RecordComponent",
    f"{{{NS}}}EMailSenderComponent",
    f"{{{NS}}}CreditCardComponent",
}

BRANCH_TYPES = {
    f"{{{NS}}}MenuComponentBranch",
    f"{{{NS}}}ConditionalComponentBranch",
    f"{{{NS}}}ComponentBranch",
}


class Issue:
    def __init__(self, level, message, location=None, node=None):
        self.level = level  # "error" or "warning"
        self.message = message
        self.location = location
        # x:Name of the offending component, when the issue maps to a single
        # node. None for project-level issues (variables, missing files).
        self.node = node

    def __str__(self):
        prefix = "ERROR" if self.level == "error" else "WARN "
        loc = f" [{self.location}]" if self.location else ""
        return f"  {prefix}: {self.message}{loc}"

    def to_dict(self):
        return {
            "level": self.level,
            "message": self.message,
            "location": self.location,
            "node": self.node,
        }


def find_cfdproj(project_dir):
    matches = glob.glob(os.path.join(project_dir, "*.cfdproj"))
    return matches[0] if matches else None


def parse_variables(xml_root, xpath):
    """Extract variable names from an ArrayOfVariable section."""
    variables = {}
    for var_elem in xml_root.findall(xpath):
        name_elem = var_elem.find("Name")
        init_elem = var_elem.find("InitialValue")
        if name_elem is not None and name_elem.text:
            variables[name_elem.text] = init_elem.text if init_elem is not None else ""
    return variables


def get_xname(elem):
    """Get the x:Name attribute from an element."""
    return elem.get(f"{{{NS_XAML}}}Name", "")


def collect_components(elem, components, depth=0):
    """Recursively collect all components and their x:Name values."""
    tag = elem.tag
    name = get_xname(elem)

    if tag in COMPONENT_TYPES and name:
        components.append({"tag": tag, "name": name, "elem": elem, "depth": depth})

    # Recurse into children (branches and nested components)
    for child in elem:
        child_tag = child.tag
        if child_tag in BRANCH_TYPES:
            branch_name = get_xname(child)
            if branch_name:
                components.append({"tag": child_tag, "name": branch_name, "elem": child, "depth": depth + 1})
            for grandchild in child:
                collect_components(grandchild, components, depth + 2)
        elif child_tag in COMPONENT_TYPES:
            collect_components(child, components, depth + 1)


def extract_audio_files_from_prompt(prompt_attr):
    """Extract .wav filenames from an escaped prompt XML attribute."""
    if not prompt_attr:
        return []
    try:
        decoded = html.unescape(prompt_attr)
        prompt_root = ET.fromstring(decoded)
        files = []
        for prompt in prompt_root.findall(".//{http://www.w3.org/2001/XMLSchema-instance}"):
            pass
        # AudioFileName elements
        for af in prompt_root.iter():
            if af.tag == "AudioFileName" and af.text:
                files.append(af.text)
        return files
    except ET.ParseError:
        return []


def extract_all_audio_refs(elem):
    """Recursively extract all audio file references from prompt attributes."""
    audio_files = set()
    prompt_attrs = [
        "PromptList", "InitialPromptList", "SubsequentPromptList",
        "InvalidDigitPromptList", "TimeoutPromptList",
    ]
    for attr in prompt_attrs:
        val = elem.get(attr, "")
        if val:
            audio_files.update(extract_audio_files_from_prompt(val))

    for child in elem:
        audio_files.update(extract_all_audio_refs(child))

    return audio_files


def extract_variable_refs(elem):
    """Extract callflow$.X references from component attributes."""
    refs = set()
    pattern = re.compile(r'callflow\$\.(\w+)')

    # Check all attributes
    for attr, val in elem.attrib.items():
        for match in pattern.finditer(val):
            refs.add(match.group(1))

        # Also check inside escaped XML (e.g., ResponseMappingsList)
        if '&lt;' in val or '&amp;' in val:
            decoded = html.unescape(val)
            for match in pattern.finditer(decoded):
                refs.add(match.group(1))

    for child in elem:
        refs.update(extract_variable_refs(child))

    return refs


def check_variable_sync(cfdproj_vars, flow_vars, strict=False):
    """Check that variables match between .cfdproj and .flow.

    Official 3CX demos frequently declare variables in only one of the two files
    and CFD Studio accepts them, so mismatches are warnings by default. Pass
    strict=True to promote them to errors.
    """
    issues = []
    level = "error" if strict else "warning"
    only_in_proj = set(cfdproj_vars.keys()) - set(flow_vars.keys())
    only_in_flow = set(flow_vars.keys()) - set(cfdproj_vars.keys())

    for v in only_in_proj:
        issues.append(Issue(level,
            f'Variable "{v}" is in .cfdproj but missing from Main.flow'))

    for v in only_in_flow:
        issues.append(Issue(level,
            f'Variable "{v}" is in Main.flow but missing from .cfdproj'))

    for v in set(cfdproj_vars.keys()) & set(flow_vars.keys()):
        if cfdproj_vars[v] != flow_vars[v]:
            issues.append(Issue("warning",
                f'Variable "{v}" initial value differs: .cfdproj="{cfdproj_vars[v]}" vs .flow="{flow_vars[v]}"'))

    return issues


def check_duplicate_names(components):
    """Check for duplicate component/branch names."""
    issues = []
    seen = {}
    for comp in components:
        name = comp["name"]
        if name in seen:
            issues.append(Issue("error",
                f'Duplicate component name "{name}" — '
                f'first used by {seen[name]}, also used by {comp["tag"].split("}")[-1]}',
                node=name))
        else:
            seen[name] = comp["tag"].split("}")[-1]
    return issues


def check_web_service_get(components):
    """WebServiceRestComponent: GET requests must have empty Content and ContentType."""
    issues = []
    ws_tag = f"{{{NS}}}WebServiceRestComponent"
    for comp in components:
        if comp["tag"] != ws_tag:
            continue
        elem = comp["elem"]
        method = elem.get("HttpRequestType", "GET")
        if method.upper() == "GET":
            content = elem.get("Content", "")
            content_type = elem.get("ContentType", "")
            if content and not content_type:
                issues.append(Issue("error",
                    f'WebServiceRestComponent "{comp["name"]}": GET request has Content but no ContentType — '
                    f'"Content and ContentType are both required, or must be both empty"',
                    node=comp["name"]))
            elif content_type and not content:
                issues.append(Issue("error",
                    f'WebServiceRestComponent "{comp["name"]}": GET request has ContentType but no Content — '
                    f'"Content and ContentType are both required, or must be both empty"',
                    node=comp["name"]))
            elif content and content_type:
                issues.append(Issue("error",
                    f'WebServiceRestComponent "{comp["name"]}": GET request should have empty Content and ContentType',
                    node=comp["name"]))
    return issues


def check_json_parser_input(components):
    """JsonXmlParserComponent must use Input= not Text=."""
    issues = []
    parser_tag = f"{{{NS}}}JsonXmlParserComponent"
    for comp in components:
        if comp["tag"] != parser_tag:
            continue
        elem = comp["elem"]
        has_input = elem.get("Input") is not None
        has_text = elem.get("Text") is not None

        if has_text and not has_input:
            issues.append(Issue("error",
                f'JsonXmlParserComponent "{comp["name"]}": uses Text= attribute instead of Input= — '
                f'causes "Input is required" error',
                node=comp["name"]))
        elif not has_input and not has_text:
            issues.append(Issue("error",
                f'JsonXmlParserComponent "{comp["name"]}": missing Input= attribute',
                node=comp["name"]))
    return issues


def check_menu_options(components):
    """Menu IsValidOption flags should match defined branches."""
    issues = []
    menu_tag = f"{{{NS}}}MenuComponent"
    branch_tag = f"{{{NS}}}MenuComponentBranch"

    for comp in components:
        if comp["tag"] != menu_tag:
            continue
        elem = comp["elem"]
        name = comp["name"]

        # Collect which options are flagged as valid
        valid_flags = set()
        for i in range(10):
            if elem.get(f"IsValidOption_{i}", "False") == "True":
                valid_flags.add(str(i))
        if elem.get("IsValidOption_Star", "False") == "True":
            valid_flags.add("Star")
        if elem.get("IsValidOption_Pound", "False") == "True":
            valid_flags.add("Pound")

        # Collect which option branches exist
        branch_options = set()
        for child in elem:
            if child.tag == branch_tag:
                opt = child.get("Option", "")
                if opt.startswith("Option"):
                    # "Option1" -> "1", "Option2" -> "2"
                    digit = opt.replace("Option", "")
                    branch_options.add(digit)

        # Check: flag set but no branch
        for opt in valid_flags - branch_options:
            issues.append(Issue("warning",
                f'MenuComponent "{name}": IsValidOption_{opt}="True" but no matching branch',
                node=name))

        # Check: branch exists but flag not set
        for opt in branch_options - valid_flags:
            issues.append(Issue("error",
                f'MenuComponent "{name}": has Option{opt} branch but IsValidOption_{opt}="False" — '
                f'option will never be accepted',
                node=name))

    return issues


def check_audio_files(flow_root, project_dir):
    """Check that referenced audio files exist in Audio/ directory."""
    issues = []
    audio_dir = os.path.join(project_dir, "Audio")
    existing_files = set()
    if os.path.isdir(audio_dir):
        existing_files = {f for f in os.listdir(audio_dir) if f.endswith(".wav")}

    referenced = extract_all_audio_refs(flow_root)

    for wav in sorted(referenced):
        if wav not in existing_files:
            issues.append(Issue("warning",
                f'Audio file "{wav}" referenced in prompts but not found in Audio/'))

    return issues


def check_variable_references(flow_root, declared_vars):
    """Check that callflow$.X references point to declared variables."""
    issues = []
    referenced = extract_variable_refs(flow_root)

    for var_name in sorted(referenced):
        if var_name not in declared_vars:
            issues.append(Issue("error",
                f'Variable reference "callflow$.{var_name}" used in flow but "{var_name}" is not declared'))

    return issues


def check_response_mappings(components, declared_vars):
    """Check ResponseMappingsList variables in JsonXmlParserComponent."""
    issues = []
    parser_tag = f"{{{NS}}}JsonXmlParserComponent"
    pattern = re.compile(r'callflow\$\.(\w+)')

    for comp in components:
        if comp["tag"] != parser_tag:
            continue
        elem = comp["elem"]
        mappings_raw = elem.get("ResponseMappingsList", "")
        if not mappings_raw:
            continue

        decoded = html.unescape(mappings_raw)
        for match in pattern.finditer(decoded):
            var_name = match.group(1)
            if var_name not in declared_vars:
                issues.append(Issue("error",
                    f'JsonXmlParserComponent "{comp["name"]}": response mapping targets '
                    f'"callflow$.{var_name}" but "{var_name}" is not declared — '
                    f'CFD Studio will report "Response mapping variable name is not set to a valid variable"',
                    node=comp["name"]))

    return issues


def check_conditional_branches(components):
    """Check that non-last conditional branches have conditions (empty = default/else)."""
    issues = []
    cond_tag = f"{{{NS}}}ConditionalComponent"
    branch_tag = f"{{{NS}}}ConditionalComponentBranch"

    for comp in components:
        if comp["tag"] != cond_tag:
            continue
        elem = comp["elem"]
        name = comp["name"]

        branches = [child for child in elem if child.tag == branch_tag]
        if len(branches) < 2:
            if len(branches) == 1 and not branches[0].get("Condition", ""):
                issues.append(Issue("warning",
                    f'ConditionalComponent "{name}": single branch with no condition — always executes',
                    node=name))
            continue

        for i, branch in enumerate(branches[:-1]):
            condition = branch.get("Condition", "")
            branch_name = get_xname(branch)
            if not condition:
                issues.append(Issue("warning",
                    f'ConditionalComponent "{name}": branch "{branch_name}" has empty condition '
                    f'but is not the last branch — it will catch all cases, making subsequent branches unreachable',
                    node=name))

    return issues


def check_transfer_destination(components):
    """Check TransferComponent has a destination."""
    issues = []
    transfer_tag = f"{{{NS}}}TransferComponent"
    for comp in components:
        if comp["tag"] != transfer_tag:
            continue
        elem = comp["elem"]
        dest = elem.get("Destination", "")
        if not dest:
            issues.append(Issue("error",
                f'TransferComponent "{comp["name"]}": missing Destination attribute',
                node=comp["name"]))
    return issues


def check_execute_csharp(components):
    """Check ExecuteCSharpCodeComponent has required attributes."""
    issues = []
    ecc_tag = f"{{{NS}}}ExecuteCSharpCodeComponent"
    for comp in components:
        if comp["tag"] != ecc_tag:
            continue
        elem = comp["elem"]
        name = comp["name"]

        if not elem.get("Code", "").strip():
            issues.append(Issue("error",
                f'ExecuteCSharpCodeComponent "{name}": Code attribute is empty',
                node=name))

        if not elem.get("MethodName", "").strip():
            issues.append(Issue("error",
                f'ExecuteCSharpCodeComponent "{name}": MethodName attribute is empty',
                node=name))

    return issues


def check_variable_assignment(components, declared_vars):
    """Check VariableAssignmentComponent targets declared variables."""
    issues = []
    va_tag = f"{{{NS}}}VariableAssignmentComponent"
    pattern = re.compile(r'callflow\$\.(\w+)')

    for comp in components:
        if comp["tag"] != va_tag:
            continue
        elem = comp["elem"]
        var_path = elem.get("VariableName", "")
        match = pattern.search(var_path)
        if match:
            var_name = match.group(1)
            if var_name not in declared_vars:
                issues.append(Issue("error",
                    f'VariableAssignmentComponent "{comp["name"]}": assigns to '
                    f'"callflow$.{var_name}" but "{var_name}" is not declared',
                    node=comp["name"]))

        expr = elem.get("Expression", "")
        if not expr:
            issues.append(Issue("warning",
                f'VariableAssignmentComponent "{comp["name"]}": Expression is empty',
                node=comp["name"]))

    return issues


def validate_project(project_dir, strict=False):
    """Run all validation checks on a CFD project directory."""
    issues = []

    # --- File existence ---
    cfdproj_path = find_cfdproj(project_dir)
    flow_path = os.path.join(project_dir, "Main.flow")

    if not cfdproj_path:
        issues.append(Issue("error", "No .cfdproj file found in project directory"))
        return issues

    # Dialer projects use *.dialer instead of Main.flow and aren't validated yet.
    if not os.path.isfile(flow_path):
        if glob.glob(os.path.join(project_dir, "*.dialer")):
            return issues
        issues.append(Issue("error", "Main.flow not found in project directory"))
        return issues

    # --- Parse files ---
    try:
        proj_tree = ET.parse(cfdproj_path)
        proj_root = proj_tree.getroot()
    except ET.ParseError as e:
        issues.append(Issue("error", f".cfdproj XML parse error: {e}"))
        return issues

    try:
        flow_tree = ET.parse(flow_path)
        flow_root = flow_tree.getroot()
    except ET.ParseError as e:
        issues.append(Issue("error", f"Main.flow XML parse error: {e}"))
        return issues

    # --- Extract variables ---
    proj_vars = parse_variables(proj_root, ".//Variable")
    flow_vars = parse_variables(flow_root, ".//Variable")

    # --- Collect components ---
    components = []
    main_flow = flow_root.find(f".//{{*}}MainFlow")
    if main_flow is None:
        issues.append(Issue("error", "MainFlow element not found in Main.flow"))
        return issues

    for child in main_flow:
        collect_components(child, components)

    # Also check error and disconnect handler flows
    for flow_name in ["ErrorHandlerFlow", "DisconnectHandlerFlow"]:
        flow_elem = flow_root.find(f".//{{*}}{flow_name}")
        if flow_elem is not None:
            for child in flow_elem:
                for grandchild in child:
                    collect_components(grandchild, components)

    # Variables declared in either file count as declared — CFD Studio accepts either.
    declared_vars = {**flow_vars, **proj_vars}

    # --- Run checks ---
    issues.extend(check_variable_sync(proj_vars, flow_vars, strict=strict))
    issues.extend(check_duplicate_names(components))
    issues.extend(check_web_service_get(components))
    issues.extend(check_json_parser_input(components))
    issues.extend(check_menu_options(components))
    issues.extend(check_audio_files(flow_root, project_dir))
    issues.extend(check_variable_references(flow_root, declared_vars))
    issues.extend(check_response_mappings(components, declared_vars))
    issues.extend(check_conditional_branches(components))
    issues.extend(check_transfer_destination(components))
    issues.extend(check_execute_csharp(components))
    issues.extend(check_variable_assignment(components, declared_vars))

    return issues


def main():
    import argparse
    parser = argparse.ArgumentParser(description="Validate a 3CX CFD project.")
    parser.add_argument("project_dir", help="Path to the CFD project directory")
    parser.add_argument("--strict", action="store_true",
                        help="Promote variable-sync mismatches from warning to error")
    parser.add_argument("--json", action="store_true",
                        help="Emit issues as a JSON array on stdout (machine-readable). "
                             "Each entry is {level, message, location, node}.")
    args = parser.parse_args()

    project_dir = args.project_dir
    if not os.path.isdir(project_dir):
        if args.json:
            import json
            print(json.dumps([{
                "level": "error",
                "message": f"{project_dir} is not a directory",
                "location": None,
                "node": None,
            }]))
            sys.exit(1)
        print(f"Error: {project_dir} is not a directory")
        sys.exit(1)

    proj_name = os.path.basename(os.path.abspath(project_dir))
    issues = validate_project(project_dir, strict=args.strict)

    errors = [i for i in issues if i.level == "error"]
    warnings = [i for i in issues if i.level == "warning"]

    # Machine-readable mode: stdout carries nothing but the JSON array so the
    # api-node validator subprocess can JSON.parse it directly.
    if args.json:
        import json
        print(json.dumps([i.to_dict() for i in issues]))
        sys.exit(1 if errors else 0)

    print(f"\nValidating: {proj_name}")
    print("=" * 60)

    if not issues:
        print("  No issues found.")
    else:
        if errors:
            print(f"\n  {len(errors)} error(s):")
            for issue in errors:
                print(issue)
        if warnings:
            print(f"\n  {len(warnings)} warning(s):")
            for issue in warnings:
                print(issue)

    print()
    if errors:
        print(f"FAILED — {len(errors)} error(s), {len(warnings)} warning(s)")
        sys.exit(1)
    elif warnings:
        print(f"PASSED with {len(warnings)} warning(s)")
        sys.exit(0)
    else:
        print("PASSED")
        sys.exit(0)


if __name__ == "__main__":
    main()
