"""Verify actual PDF grid cells, not just the bridge's in-memory objects."""
import logging
import re
from pathlib import Path

import pdfplumber

logging.getLogger("pdfminer").setLevel(logging.ERROR)
root = Path(__file__).resolve().parents[2]
directory = root / "tmp/pdfs/icbc-source-fields/after"
checked_rows = 0


def text(value):
    return re.sub(r"\s+", "", value or "")


def extract(path):
    rows = []
    with pdfplumber.open(path) as document:
        for page in document.pages:
            # Keep source grid text, excluding larger rotated watermark/seal text.
            grid = page.filter(lambda item: item["object_type"] != "char" or (
                item.get("size", 0) < 8 and all(channel < 0.01 for channel in item.get("non_stroking_color", (0,)))))
            for table in grid.extract_tables():
                if not table:
                    continue
                headers = [text(value) for value in table[0]]
                if "交易日期" not in headers or "收入/支出金额" not in headers:
                    continue
                for values in table[1:]:
                    row = dict(zip(headers, map(text, values)))
                    if row.get("交易日期", "").startswith("2026-03-20"):
                        rows.append(row)
    return rows


for path in sorted(directory.glob("工行个人*.pdf")):
    rows = extract(path)
    assert len(rows) == 28, (path.name, "record-count", len(rows))
    masked = path.stem in {"工行个人电子版5", "工行个人电子版6", "工行个人电子版7"}
    for index, row in enumerate(rows, start=1):
        expected = {
            "账号": "ROW-2" if index == 2 else "ROW-ACCOUNT",
            "序号": "7" if index == 2 else "",
            "币种": "USD" if index == 2 else "",
            "钞汇": "钞" if index == 2 else "",
            "摘要": "利息" if index == 1 else "" if index == 3 else "消费",
            "地区": "0605",
            "收入/支出金额": "+53.56" if index == 1 else "-12.34",
            "余额": "53.56" if index == 1 else "41.22",
            "对方户名": "测试商户" if index == 2 else "",
            "对方账号": "" if index == 1 else "ABC" if index == 3 else "1000****7101" if masked else "1000107101",
            "交易场所": "测试场所" if index == 2 else "",
            "渠道": "手机银行" if index == 2 else "",
        }
        for field, value in expected.items():
            if field in row:
                assert row[field] == value, (path.name, index, field, row[field], value)
        checked_rows += 1
    print(f"PASS {path.stem}: 28 rows, all displayed cells match source")

assert checked_rows == 13 * 28, checked_rows
descending = extract(directory / "descending.pdf")
assert len(descending) == 2, len(descending)
assert [row["摘要"] for row in descending] == ["消费", "利息"]
assert [row["对方账号"] for row in descending] == ["1000107101", ""]
area_empty = extract(directory / "area-empty.pdf")
assert len(area_empty) == 1
assert area_empty[0]["地区"] == "", area_empty
print(f"PASS {checked_rows} source rows across 13 templates; descending order preserved")
