#!/usr/bin/env python3
import os
import sys
import zipfile
import xml.etree.ElementTree as ET

NAMESPACES = {
    'w': 'http://schemas.openxmlformats.org/wordprocessingml/2006/main',
    'w14': 'http://schemas.microsoft.com/office/word/2010/wordml',
}

PPR_ORDER = [
    'pStyle', 'keepNext', 'keepLines', 'pageBreakBefore', 'framePr', 'widowControl',
    'numPr', 'suppressLineNumbers', 'pBdr', 'shd', 'tabs', 'suppressAutoHyphens',
    'kinsoku', 'wordWrap', 'overflowPunct', 'topLinePunct', 'autoSpaceDE',
    'autoSpaceDN', 'bidi', 'adjustRightInd', 'snapToGrid', 'spacing', 'ind',
    'contextualSpacing', 'mirrorIndents', 'suppressOverlap', 'jc', 'textDirection',
    'textAlignment', 'textboxTightWrap', 'outlineLvl', 'divId', 'cnfStyle',
    'rPr', 'sectPr', 'pPrChange'
]

RPR_ORDER = [
    'rStyle', 'rFonts', 'b', 'bCs', 'i', 'iCs', 'caps', 'smallCaps', 'strike',
    'dstrike', 'outline', 'shadow', 'emboss', 'imprint', 'noProof', 'snapToGrid',
    'vanish', 'webHidden', 'color', 'spacing', 'w', 'kern', 'position', 'sz',
    'szCs', 'highlight', 'u', 'effect', 'bdr', 'shd', 'fitText', 'vertAlign',
    'rtl', 'cs', 'em', 'lang', 'eastAsianLayout', 'specVanish', 'oMath', 'rPrChange'
]

def tag_local(elem):
    if elem.tag.startswith('{http://schemas.openxmlformats.org/wordprocessingml/2006/main}'):
        return elem.tag.split('}', 1)[1]
    return None

def verify_element_order(parent, expected_order, parent_name):
    last_idx = -1
    for child in parent:
        local = tag_local(child)
        if local in expected_order:
            curr_idx = expected_order.index(local)
            if curr_idx < last_idx:
                print(f"[FAIL] Out-of-order element <w:{local}> in <w:{parent_name}>. Previous max index: {last_idx}, current index: {curr_idx}")
                return False
            last_idx = max(last_idx, curr_idx)
    return True

def check_docx(docx_path):
    print(f"\nVerifying DOCX file: {docx_path}")
    with zipfile.ZipFile(docx_path, 'r') as z:
        file_list = z.namelist()
        if "word/document.xml" not in file_list:
            print(f"[FAIL] Missing word/document.xml in {docx_path}")
            return False

        doc_xml = z.read("word/document.xml")
        root = ET.fromstring(doc_xml)

        # 1. Check trailing <w:p> in table cells (<w:tc>)
        tcs = root.findall('.//w:tc', NAMESPACES)
        for idx, tc in enumerate(tcs):
            children = [c for c in tc if c.tag != ET.Comment]
            if not children:
                print(f"[FAIL] Table cell #{idx} is completely empty (missing <w:p>)")
                return False
            last_child = children[-1]
            if tag_local(last_child) != 'p':
                print(f"[FAIL] Table cell #{idx} does not end with <w:p>. Trailing element tag: {last_child.tag}")
                return False
        print(f"  [PASS] All {len(tcs)} table cell(s) contain proper trailing <w:p> element(s).")

        # 2. Check element ordering in w:pPr and w:rPr
        pPrs = root.findall('.//w:pPr', NAMESPACES)
        for pPr in pPrs:
            if not verify_element_order(pPr, PPR_ORDER, "pPr"):
                return False
        print(f"  [PASS] All {len(pPrs)} <w:pPr> element orders compliant with OpenXML schema.")

        rPrs = root.findall('.//w:rPr', NAMESPACES)
        for rPr in rPrs:
            if not verify_element_order(rPr, RPR_ORDER, "rPr"):
                return False
        print(f"  [PASS] All {len(rPrs)} <w:rPr> element orders compliant with OpenXML schema.")

        # 3. Check numbering.xml if present
        if "word/numbering.xml" in file_list:
            num_xml = z.read("word/numbering.xml")
            num_root = ET.fromstring(num_xml)
            if tag_local(num_root) != "numbering":
                print(f"[FAIL] Invalid root element in word/numbering.xml: {num_root.tag}")
                return False
            abstract_nums = num_root.findall('w:abstractNum', NAMESPACES)
            nums = num_root.findall('w:num', NAMESPACES)
            print(f"  [PASS] word/numbering.xml valid: {len(abstract_nums)} abstractNum(s), {len(nums)} num(s).")

    return True

def main():
    project_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    output_dir = os.path.join(project_dir, "tests", "docx_verify_output")
    
    docx_files = [
        os.path.join(output_dir, "emdash_sample.docx"),
        os.path.join(output_dir, "wikilink_sample.docx")
    ]
    
    all_ok = True
    for docx in docx_files:
        if os.path.exists(docx):
            if not check_docx(docx):
                all_ok = False
        else:
            print(f"[FAIL] File not found: {docx}")
            all_ok = False

    if all_ok:
        print("\n==================================================")
        print("  SUCCESS: OpenXML schema and structure verified!")
        print("==================================================")
        sys.exit(0)
    else:
        print("\n==================================================")
        print("  FAILURE: OpenXML schema/structure errors detected.")
        print("==================================================")
        sys.exit(1)

if __name__ == "__main__":
    main()
