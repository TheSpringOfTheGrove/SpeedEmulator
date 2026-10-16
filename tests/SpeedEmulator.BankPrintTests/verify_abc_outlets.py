"""Run --abc-render first; inspect actual PDF outlet cells, including empty cells."""
import json
import logging
from pathlib import Path
import re
import sys

import pdfplumber

logging.getLogger("pdfminer").setLevel(logging.ERROR)


def compact(value):
    return "".join((value or "").split())


def main():
    root = Path(__file__).resolve().parents[2]
    directory = Path(sys.argv[1]) if len(sys.argv) > 1 else root / "tmp/pdfs/abc-outlet-fix-20261015"
    reports = json.loads((directory / "audit.json").read_text(encoding="utf-8"))
    row_count = blank_count = page_count = 0
    for report in reports:
        with pdfplumber.open(report["Path"]) as document:
            pages = document.pages
            page_count += len(pages)
            headers = [page.search(r"交易(?:网点|地点)") for page in pages]
            if any(len(matches) != 1 for matches in headers):
                raise AssertionError(f"{report['Template']}: missing or ambiguous outlet headers")
            code_boxes = []
            for row in report["Rows"]:
                if not row["Place"]:
                    continue
                matches = [(index, match) for index, page in enumerate(pages)
                           for match in page.search(r"(?<![A-Za-z0-9])" + re.escape(row["Place"]) + r"(?![A-Za-z0-9])")
                           if match["top"] > headers[index][0]["bottom"]]
                if len(matches) != 1:
                    raise AssertionError(f"{report['Template']}: missing/duplicated outlet {row['Place']}")
                code_boxes.append(matches[0][1])
            left = min(box["x0"] for box in code_boxes) - 1
            right = max(box["x1"] for box in code_boxes) + 1
            for row in report["Rows"]:
                date = row["Date"]
                date_pattern = re.escape(date[:4]) + r"[-/\s]?" + re.escape(date[4:6]) + r"[-/\s]?" + re.escape(date[6:])
                matches = [(index, match) for index, page in enumerate(pages)
                           for match in page.search(date_pattern)
                           if match["top"] > headers[index][0]["bottom"]]
                if len(matches) != 1:
                    raise AssertionError(f"{report['Template']}: missing/duplicated date row {date}")
                index, anchor = matches[0]
                text = pages[index].crop((left, anchor["top"] - 1, right, anchor["bottom"] + 1)).extract_text()
                actual = compact(text)
                if actual != row["Place"]:
                    raise AssertionError(f"{report['Template']}/{date}: outlet {actual!r}, expected {row['Place']!r}")
                row_count += 1
                blank_count += not actual
            print(f"PASS {report['Template']}: {len(report['Rows'])} exact outlet cells")
    print(f"PASS {len(reports)} templates / {page_count} pages / {row_count} outlet cells ({blank_count} blanks)")


if __name__ == "__main__":
    main()
