"""Builds Resources/geoip.bin, the offline IP-to-country table NetUse uses for host flags.

Source data: geo-whois-asn-country from https://github.com/sapics/ip-location-db (CC0 / public domain).
    curl -LO https://cdn.jsdelivr.net/npm/@ip-location-db/geo-whois-asn-country/geo-whois-asn-country-ipv4-num.csv
    curl -LO https://cdn.jsdelivr.net/npm/@ip-location-db/geo-whois-asn-country/geo-whois-asn-country-ipv6-num.csv
    python -I tools/make-geoip.py geo-whois-asn-country-ipv4-num.csv geo-whois-asn-country-ipv6-num.csv Resources/geoip.bin

Format (gzip-compressed, little-endian):
    "NUGEO1"
    uint32 n4, then n4 x (uint32 first, uint32 last, 2 ASCII bytes country)    -- IPv4, sorted
    uint32 n6, then n6 x (uint64 first, uint64 last, 2 ASCII bytes country)    -- top 64 bits of IPv6, sorted
Adjacent ranges of the same country are merged.
"""
import csv
import gzip
import struct
import sys


def load(path, shift):
    rows = []
    with open(path, newline="", encoding="ascii") as f:
        for first, last, cc in csv.reader(f):
            cc = cc.strip().upper()
            if len(cc) != 2 or not cc.isalpha():
                continue
            rows.append((int(first) >> shift, int(last) >> shift, cc))
    rows.sort()
    merged = []
    for a, b, cc in rows:
        if merged and merged[-1][2] == cc and a <= merged[-1][1] + 1:
            if b > merged[-1][1]:
                merged[-1][1] = b
        elif merged and a <= merged[-1][1]:
            continue  # overlap with a different country after truncation: keep the first
        else:
            merged.append([a, b, cc])
    return merged


def main(v4, v6, out):
    r4 = load(v4, 0)
    r6 = load(v6, 64)
    buf = bytearray(b"NUGEO1")
    buf += struct.pack("<I", len(r4))
    for a, b, cc in r4:
        buf += struct.pack("<II", a, b) + cc.encode("ascii")
    buf += struct.pack("<I", len(r6))
    for a, b, cc in r6:
        buf += struct.pack("<QQ", a, b) + cc.encode("ascii")
    with gzip.open(out, "wb", compresslevel=9) as f:
        f.write(buf)
    print(f"IPv4 ranges: {len(r4)}, IPv6 ranges: {len(r6)}, raw {len(buf)} bytes -> {out}")


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    main(*sys.argv[1:])
