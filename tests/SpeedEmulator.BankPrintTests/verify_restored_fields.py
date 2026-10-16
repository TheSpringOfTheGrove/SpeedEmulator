"""Verify actual PDF field cells, including blanks, after --restored-render."""
import json
import logging
from pathlib import Path
import re
import sys

import pdfplumber

logging.getLogger("pdfminer").setLevel(logging.ERROR)


def main():
    root = Path(__file__).resolve().parents[2]
    directory = Path(sys.argv[1]) if len(sys.argv) > 1 else root / "tmp/pdfs/restored-bank-fields-20261015"
    reports = json.loads((directory / "audit.json").read_text(encoding="utf-8"))
    cells = blanks = pages = 0
    for report in reports:
        assert "Error" not in report, report
        with pdfplumber.open(report["Path"]) as pdf:
            pages += len(pdf.pages)
            for field in report["Fields"]:
                header = field["PrintedColumn"]
                header_boxes = [page.search(re.escape(header)) for page in pdf.pages]
                assert len(header_boxes[0]) == 1 and all(len(matches) <= 1 for matches in header_boxes), (report["Template"], header, "headers")
                # Some CCB paper layouts do not repeat the first-page table header.
                header_boxes = [matches or [dict(header_boxes[0][0], top=0, bottom=0)] for matches in header_boxes]
                markers = []
                body_fonts = set()
                for row in report["Rows"]:
                    value = row["Values"][header]
                    if not value:
                        continue
                    matches = [(index, match) for index, page in enumerate(pdf.pages)
                               for match in page.search(re.escape(value))
                               if match["top"] > header_boxes[index][0]["bottom"]]
                    assert len(matches) == 1, (report["Template"], value, "missing/duplicate marker")
                    page_index, marker = matches[0]
                    heading = header_boxes[page_index][0]
                    # The native graph/XML check independently validates column identity.
                    # This catches a PDF placing its actual glyphs far from that column.
                    assert abs((marker["x0"] + marker["x1"] - heading["x0"] - heading["x1"]) / 2) < 30, (report["Template"], header, "wrong PDF column")
                    markers.append(marker)
                    body_fonts.update((char["fontname"], round(char["size"], 2)) for char in marker["chars"])
                left = min(box["x0"] for box in markers) - .5
                right = max(box["x1"] for box in markers) + .5
                body_pages = [page.filter(lambda obj: obj.get("object_type") != "char" or (obj["fontname"], round(obj["size"], 2)) in body_fonts) for page in pdf.pages]
                date_anchors = []
                if report["Template"] in {"工行个人纸质版（工资）", "兴业个人电子版9"} or report["Template"].startswith("工行对公纸质版"):
                    # These layouts print date-only rows and omit/mask balances.
                    date_anchors = [(page_index, match) for page_index, page in enumerate(body_pages)
                                      for match in page.search(r"(?:2026[-/]?)?03[-/]21")
                                      if match["top"] > header_boxes[page_index][0]["bottom"] and match["x0"] < page.width * .2]
                    assert len(date_anchors) == len(report["Rows"]), (report["Template"], len(date_anchors))
                for index, row in enumerate(report["Rows"], 1):
                    balance = 1000 + index
                    pattern = rf"(?<![A-Za-z0-9])1[,\s]*{balance % 1000:03d}(?:\s*\.\s*00)?(?![A-Za-z0-9])"
                    anchors = date_anchors[index - 1:index] if date_anchors else [(page_index, match) for page_index, page in enumerate(pdf.pages)
                               for match in page.search(pattern)
                               if match["top"] > header_boxes[page_index][0]["bottom"]]
                    if report["Template"].startswith("兴业") and not date_anchors:
                        # Electronic version 10 intentionally masks all balances.
                        anchors = [(page_index, match) for page_index, page in enumerate(body_pages)
                                   for match in page.search(re.escape(row["Time"]))
                                   if match["top"] > header_boxes[page_index][0]["bottom"]]
                    assert len(anchors) == 1, (report["Template"], balance, "missing/duplicate row anchor", len(anchors))
                    page_index, anchor = anchors[0]
                    # Use the balance line as a row anchor, allowing small font/baseline
                    # differences in MRT. The marker strip excludes neighbouring columns.
                    # Exclude decorative seal/watermark text, which uses a different
                    # font from the actual table data and can overlap the final rows.
                    body = body_pages[page_index]
                    text = body.crop((left, anchor["top"] - 2, right, anchor["bottom"] + 2)).extract_text() or ""
                    actual = "".join(text.split())
                    expected = row["Values"][header]
                    assert actual == expected, (report["Template"], header, index, actual, expected)
                    cells += 1
                    blanks += not expected
        print(f"PASS PDF cells {report['Template']}")
    print(f"PASS {len(reports)} templates / {pages} pages / {cells} exact cells ({blanks} blanks)")


if __name__ == "__main__":
    main()
