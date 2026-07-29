import re
from openpyxl import Workbook

SRC = r"D:\JoyMaker\JoyMakerGame\ROGHack_items.txt"
OUT = r"D:\JoyMaker\JoyMakerGame\ROGHack_items.xlsx"

# Fields worth keeping as columns; anything else (LevelLimit.*, _rp, functions,
# userdata) is noise from the Lua marshalling layer and gets dropped.
WANTED_ORDER = [
    "ItemID", "ItemName", "ItemIcon", "ItemAtlas", "ItemQuality",
    "TypeTab", "ParentID", "SortID", "TimeType", "IsLock", "Overlap", "Weight",
]

SKIP_KEY_RE = re.compile(r"LevelLimit|_rp$|_rp\.|^\?$")


def parse_line(line):
    parts = line.rstrip("\n").split("\t")
    if not parts:
        return None
    row_key = parts[0]
    fields = {}
    for token in parts[1:]:
        if "=" not in token:
            continue
        key, _, value = token.partition("=")
        # keys look like "_ri.ItemID" or "_ri.LevelLimit.sequence"
        short_key = key.split(".")[-1]
        if SKIP_KEY_RE.search(key):
            continue
        if value.startswith("userdata:") or value.startswith("function:") or value == "table":
            continue
        fields[short_key] = value
    return row_key, fields


def main():
    rows = []
    with open(SRC, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            parsed = parse_line(line)
            if not parsed:
                continue
            row_key, fields = parsed
            if not fields:
                continue
            rows.append((row_key, fields))

    wb = Workbook()
    ws = wb.active
    ws.title = "Items"
    header = ["RowKey"] + WANTED_ORDER
    ws.append(header)

    for row_key, fields in rows:
        ws.append([row_key] + [fields.get(col, "") for col in WANTED_ORDER])

    ws.freeze_panes = "A2"
    ws.auto_filter.ref = ws.dimensions
    widths = [10, 10, 14, 34, 20, 12, 10, 10, 12, 10, 8, 10, 8]
    for i, w in enumerate(widths, start=1):
        ws.column_dimensions[chr(64 + i) if i <= 26 else "A"].width = w

    wb.save(OUT)
    print(f"Wrote {len(rows)} rows to {OUT}")


if __name__ == "__main__":
    main()
