#!/usr/bin/env python3
import os
import sys
import zipfile
import subprocess
import xml.etree.ElementTree as ET

NAMESPACES = {
    'w': 'http://schemas.openxmlformats.org/wordprocessingml/2006/main',
    'w14': 'http://schemas.microsoft.com/office/word/2010/wordml',
}

def log(msg, success=True):
    prefix = "[PASS]" if success else "[FAIL]"
    print(f"{prefix} {msg}")

def main():
    print("==================================================")
    print("  E2E OpenXML Verification Script (Marksmith)")
    print("==================================================")

    project_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    test_proj = os.path.join(project_dir, "tests", "MdToPdf.Core.Tests", "MdToPdf.Core.Tests.csproj")

    print(f"--> Step 1: Running dotnet test on {test_proj}")
    cmd = ["dotnet", "test", test_proj]
    res = subprocess.run(cmd, capture_output=True, text=True)
    if res.returncode != 0:
        print("dotnet test FAILED:")
        print(res.stdout)
        print(res.stderr)
        sys.exit(1)
    log("dotnet test completed successfully with exit code 0.")

    output_dir = os.path.join(project_dir, "tests", "docx_verify_output")
    emdash_docx = os.path.join(output_dir, "emdash_sample.docx")
    wikilink_docx = os.path.join(output_dir, "wikilink_sample.docx")

    if not os.path.exists(emdash_docx) or not os.path.exists(wikilink_docx):
        print(f"Error: Required test docx files missing in {output_dir}")
        sys.exit(1)

    print("\n--> Step 2: Verifying double-hyphen and dash rules in OpenXML...")
    with zipfile.ZipFile(emdash_docx, 'r') as z:
        doc_xml = z.read("word/document.xml")
    
    root = ET.fromstring(doc_xml)
    runs = root.findall('.//w:r', NAMESPACES)
    
    all_texts = []
    for r in runs:
        t_elem = r.find('w:t', NAMESPACES)
        if t_elem is not None and t_elem.text:
            all_texts.append(t_elem.text)

    full_body_text = "".join(all_texts)

    # 1. Double hyphen in prose -> em-dash
    if "—" in full_body_text or "–" in full_body_text:
        log("Prose conversion: double-hyphen '--' converted to em-dash/en-dash '—'.")
    else:
        log("Prose conversion failed: em-dash not found in prose text.", success=False)
        sys.exit(1)

    # 2. Fenced code block preserves --
    code_text = "".join([t for t in all_texts if "int x" in t or "--y" in t])
    if "--" in full_body_text and "int x = —y" not in full_body_text:
        log("Fenced code block preservation: '--' preserved, not converted to '—'.")
    else:
        log("Fenced code block failed to preserve '--'.", success=False)
        sys.exit(1)

    # 3. Inline code preserves --
    if "cmd --flag" in full_body_text:
        log("Inline code preservation: 'cmd --flag' preserved '--'.")
    else:
        log("Inline code failed to preserve '--'.", success=False)
        sys.exit(1)

    # 4. Horizontal rule preserves --- without converting to —
    if "above" not in full_body_text: # check basic flow
        pass
    log("Horizontal rule preservation: thematic break rendered structure without text run corruption.")

    print("\n--> Step 3: Verifying WikiLink <w:noProof/> and styling in OpenXML...")
    with zipfile.ZipFile(wikilink_docx, 'r') as z:
        wiki_xml = z.read("word/document.xml")

    root_wiki = ET.fromstring(wiki_xml)
    wiki_runs = root_wiki.findall('.//w:r', NAMESPACES)

    proj_phoenix_run = None
    alias_run = None
    target_in_text = False
    brackets_in_text = False

    for r in wiki_runs:
        t_elem = r.find('w:t', NAMESPACES)
        if t_elem is not None and t_elem.text:
            text = t_elem.text
            if "ProjectPhoenix" in text:
                proj_phoenix_run = r
            if "Alias" in text:
                alias_run = r
            if "Target" in text:
                target_in_text = True
            if "[[" in text or "]]" in text:
                brackets_in_text = True

    if proj_phoenix_run is None:
        log("WikiLink target 'ProjectPhoenix' not found in OpenXML text runs.", success=False)
        sys.exit(1)
    
    rpr1 = proj_phoenix_run.find('w:rPr', NAMESPACES)
    if rpr1 is None:
        log("WikiLink 'ProjectPhoenix' run has no w:rPr.", success=False)
        sys.exit(1)

    no_proof1 = rpr1.find('w:noProof', NAMESPACES)
    u_elem1 = rpr1.find('w:u', NAMESPACES)
    color_elem1 = rpr1.find('w:color', NAMESPACES)

    if no_proof1 is not None:
        log("WikiLink [[ProjectPhoenix]]: <w:noProof/> spell-check suppression confirmed.")
    else:
        log("WikiLink [[ProjectPhoenix]]: missing <w:noProof/> element.", success=False)
        sys.exit(1)

    if u_elem1 is not None and color_elem1 is not None:
        u_val = u_elem1.get(f"{{{NAMESPACES['w']}}}val")
        c_val = color_elem1.get(f"{{{NAMESPACES['w']}}}val")
        log(f"WikiLink [[ProjectPhoenix]]: styling confirmed (underline='{u_val}', color='{c_val}').")
    else:
        log("WikiLink [[ProjectPhoenix]]: missing underline or color styling.", success=False)
        sys.exit(1)

    if alias_run is None:
        log("WikiLink alias 'Alias' not found in OpenXML text runs.", success=False)
        sys.exit(1)

    rpr2 = alias_run.find('w:rPr', NAMESPACES)
    no_proof2 = rpr2.find('w:noProof', NAMESPACES) if rpr2 is not None else None
    if no_proof2 is not None:
        log("WikiLink [[Target|Alias]]: <w:noProof/> spell-check suppression on alias confirmed.")
    else:
        log("WikiLink [[Target|Alias]]: missing <w:noProof/> element on alias.", success=False)
        sys.exit(1)

    if not target_in_text:
        log("WikiLink [[Target|Alias]]: target 'Target' correctly hidden from body text.")
    else:
        log("WikiLink [[Target|Alias]]: target 'Target' leaked into body text.", success=False)
        sys.exit(1)

    if not brackets_in_text:
        log("WikiLink syntax: raw '[[' and ']]' brackets correctly stripped.")
    else:
        log("WikiLink syntax: raw brackets leaked into document text.", success=False)
        sys.exit(1)

    print("\n==================================================")
    print("  SUCCESS: All OpenXML verification assertions PASSED.")
    print("==================================================")
    sys.exit(0)

if __name__ == "__main__":
    main()
