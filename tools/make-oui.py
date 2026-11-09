"""Builds Resources/oui.bin: MAC vendor names for the Network page's device list.

Source: the IEEE MA-L registry (public listing): https://standards-oui.ieee.org/oui/oui.csv
Usage:  python tools/make-oui.py oui.csv Resources/oui.bin
Output: gzip of "NUOUI1\n" followed by "AABBCC<TAB>Short name\n" lines.
"""
import csv
import gzip
import re
import sys

SUFFIXES = {
    "co", "ltd", "inc", "corp", "corporation", "corporate", "llc", "gmbh", "limited", "sa", "ag", "bv", "b.v", "plc", "kg",
    "srl", "spa", "oy", "ab", "as", "company", "incorporated", "pte", "pty", "sas", "s.a", "s.p.a", "kk", "nv", "ltda",
    "technologies", "technology", "tech", "electronics", "electronic", "communications", "communication", "telecommunications",
    "telecommunication", "international", "industrial", "holdings", "group", "systems", "products", "the",
}


def short(name: str) -> str:
    name = re.sub(r"\s+", " ", name.replace(" ", " ")).strip()
    words = re.split(r"[\s,]+", name)
    while len(words) > 1 and words[-1].strip(".,()").lower() in SUFFIXES | {""}:
        words.pop()
    out = " ".join(words).strip(" .,-")
    return (out or name)[:32]


def main(src: str, dst: str) -> None:
    rows = []
    with open(src, newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            oui = (r.get("Assignment") or "").strip().upper()
            org = (r.get("Organization Name") or "").strip()
            if re.fullmatch(r"[0-9A-F]{6}", oui) and org and "private" not in org.lower():
                rows.append(f"{oui}\t{short(org)}\n")
    rows.sort()
    with gzip.open(dst, "wt", encoding="utf-8", compresslevel=9) as g:
        g.write("NUOUI1\n")
        g.writelines(rows)
    print(f"{len(rows)} vendors -> {dst}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
