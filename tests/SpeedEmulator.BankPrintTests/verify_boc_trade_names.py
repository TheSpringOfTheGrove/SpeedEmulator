"""Check actual transaction-name cells, including blanks and page boundaries.

Run BankPrintTests --boc-render first. Only generated test PDFs are read.
"""
import json
import logging
from pathlib import Path
import sys

import pdfplumber

logging.getLogger("pdfminer").setLevel(logging.ERROR)


def compact(value):
    return "".join((value or "").split())


def main():
    root = Path(__file__).resolve().parents[2]
    directory = Path(sys.argv[1]) if len(sys.argv) > 1 else root / "tmp/pdfs/boc-name-fix-20261015"
    reports = json.loads((directory / "audit.json").read_text(encoding="utf-8"))
    total_rows = total_blanks = total_pages = 0
    for report in reports:
        expected = {row["Time"]: row["Name"] for row in report["Rows"]}
        found = {}
        with pdfplumber.open(report["Path"]) as document:
            total_pages += len(document.pages)
            for page in document.pages:
                tables = page.extract_tables()
                for table in tables:
                    header_index = next((index for index, row in enumerate(table)
                                         if any(compact(cell).startswith("交易名称") for cell in row)
                                         and any(compact(cell).startswith("记账时间") for cell in row)), None)
                    if header_index is None:
                        continue
                    header = [compact(cell) for cell in table[header_index]]
                    name_column = next(index for index, cell in enumerate(header) if cell.startswith("交易名称"))
                    time_column = next(index for index, cell in enumerate(header) if cell.startswith("记账时间"))
                    for row in table[header_index + 1:]:
                        time = compact(row[time_column])
                        if time not in expected:
                            continue
                        if time in found:
                            raise AssertionError(f"{report['Template']}: duplicate row {time}")
                        actual = compact(row[name_column])
                        if actual != expected[time]:
                            raise AssertionError(f"{report['Template']}/{time}: name {actual!r}, expected {expected[time]!r}")
                        found[time] = actual
        if set(found) != set(expected):
            raise AssertionError(f"{report['Template']}: missing rows {set(expected) - set(found)}")
        blanks = sum(not value for value in found.values())
        total_rows += len(found)
        total_blanks += blanks
        print(f"PASS {report['Template']}: {len(found)} exact name cells, {blanks} truly blank cells")
    print(f"PASS {len(reports)} templates / {total_pages} pages / {total_rows} exact name cells ({total_blanks} blanks)")


if __name__ == "__main__":
    main()
