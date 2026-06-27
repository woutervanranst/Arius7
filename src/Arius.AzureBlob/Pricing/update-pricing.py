#!/usr/bin/env python3
"""Regenerate pricing.json from the Azure Retail Prices API.

Writes the sibling ``pricing.json`` used by Arius.AzureBlob's cost estimator: EUR rates for
Standard general-purpose v2 block blobs (product "General Block Blob v2"), LRS redundancy, base
volume tier (0-50 TB), plus internet egress (product "Bandwidth", default Microsoft-network
routing). One entry per standard public commercial Azure region; Government / AT&T MEC / Delos
sovereign regions are excluded (not deployable from a commercial subscription), and so are
disaster-recovery / restricted regions that publish no internet-egress meter (e.g. Brazil
Southeast) — those aren't usable general-purpose backup targets and would otherwise undercount
restore egress. A tier is omitted where a region does not offer it (e.g. Belgium Central has no
Archive tier).

Usage:
    python3 src/Arius.AzureBlob/Pricing/update-pricing.py

No auth required (public API). Needs network access. Review the git diff before committing.
"""
import json
import os
import urllib.parse
import urllib.request

API = "https://prices.azure.com/api/retail/prices"
CURRENCY = "EUR"
BLOB_PRODUCT = "General Block Blob v2"
EXCLUDE_REGION_PREFIXES = ("usgov", "att", "deloscloud")  # Government / MEC / sovereign clouds


def fetch(odata_filter):
    params = {"api-version": "2023-01-01-preview", "currencyCode": f"'{CURRENCY}'", "$filter": odata_filter}
    url = API + "?" + urllib.parse.urlencode(params)
    items = []
    while url:
        with urllib.request.urlopen(url) as resp:
            doc = json.load(resp)
        items += doc["Items"]
        url = doc.get("NextPageLink")
    return [i for i in items if i["type"] == "Consumption"]


def pick(items, region, sku, name_substr, exclude=None, gb_month=None):
    cands = [
        i for i in items
        if i["armRegionName"] == region and i["skuName"] == sku
        and name_substr.lower() in i["meterName"].lower()
        and (exclude is None or exclude.lower() not in i["meterName"].lower())
    ]
    if gb_month is not None:
        cands = [i for i in cands if (i["unitOfMeasure"] == "1 GB/Month") == gb_month]
    if not cands:
        return None
    cands.sort(key=lambda i: i["tierMinimumUnits"])  # base volume tier (minimum units 0)
    return round(cands[0]["retailPrice"], 8)


def build_tier(items, region, sku, archive=False):
    storage = pick(items, region, sku, "Data Stored", gb_month=True)
    if storage is None:
        return None  # region doesn't offer this tier
    tier = {
        "storagePerGBPerMonth": storage,
        "writeOpsPer10000": pick(items, region, sku, "Write Operations"),
        "readOpsPer10000": pick(items, region, sku, "Read Operations", exclude="Priority"),
    }
    retrieval = pick(items, region, sku, "Data Retrieval", exclude="Priority")
    if retrieval is not None:
        tier["dataRetrievalPerGB"] = retrieval
    if archive:
        high_read = pick(items, region, sku, "Priority Read Operations")
        if high_read is not None:
            tier["readOpsHighPer10000"] = high_read
        high_retr = pick(items, region, sku, "Priority Data Retrieval")
        if high_retr is not None:
            tier["dataRetrievalHighPerGB"] = high_retr
    return {k: v for k, v in tier.items() if v is not None}


def egress(bandwidth, region):
    # Default Microsoft-network routing (MGN); first paid band (smallest tierMin with price > 0).
    for product in ("Rtn Preference: MGN", "Bandwidth - Routing Preference: Internet"):
        cands = [i for i in bandwidth if i["armRegionName"] == region and i["productName"] == product and i["retailPrice"] > 0]
        if cands:
            cands.sort(key=lambda i: i["tierMinimumUnits"])
            return round(cands[0]["retailPrice"], 8)
    return None


def main():
    print("Fetching blob (General Block Blob v2, LRS) and bandwidth meters …")
    blob = fetch(f"productName eq '{BLOB_PRODUCT}' and contains(skuName, 'LRS')")
    bandwidth = fetch("serviceName eq 'Bandwidth' and meterName eq 'Standard Data Transfer Out'")

    regions = sorted({
        i["armRegionName"] for i in blob
        if i["skuName"] == "Hot LRS" and "Data Stored" in i["meterName"] and i["armRegionName"]
        and not i["armRegionName"].startswith(EXCLUDE_REGION_PREFIXES)
    })

    out = {}
    skipped = []
    for region in regions:
        eg = egress(bandwidth, region)
        if eg is None:
            # Disaster-recovery / restricted regions (e.g. brazilsoutheast) publish no internet-egress
            # meter and aren't usable general-purpose backup targets — omit them entirely rather than
            # emit a region whose restore estimate silently drops egress.
            skipped.append(region)
            continue
        entry = {"currency": CURRENCY, "egressPerGB": eg}
        for name, sku, is_archive in (("hot", "Hot LRS", False), ("cool", "Cool LRS", False),
                                      ("cold", "Cold LRS", False), ("archive", "Archive LRS", True)):
            tier = build_tier(blob, region, sku, is_archive)
            if tier:
                entry[name] = tier
        out[region] = entry

    doc = {
        "_comment": (
            "Azure Blob Storage retail pricing — EUR, Standard general-purpose v2 block blobs, LRS "
            "redundancy, base volume tier (0-50 TB). Keyed by programmatic region name. Generated by "
            "update-pricing.py from the Azure Retail Prices API (prices.azure.com), products "
            "'General Block Blob v2' + 'Bandwidth'. Covers all standard public commercial regions "
            "(Government/MEC/sovereign clouds and disaster-recovery regions without an egress meter "
            "excluded). Azure bills per-GB values as binary GiB (2^30 "
            "bytes). Hot has no data-retrieval charge; cool/cold/archive do. A tier is omitted where the "
            "region does not offer it. egressPerGB is internet data-transfer-out (default Microsoft-network "
            "routing, 100 GB-10 TB band); the first 100 GiB/month is free account-wide."),
        "regions": out,
    }

    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "pricing.json")
    with open(path, "w") as f:
        json.dump(doc, f, indent=2)
        f.write("\n")
    print(f"Wrote {len(out)} regions to {path}")
    if skipped:
        print(f"Skipped {len(skipped)} region(s) with no internet-egress meter (DR/restricted): {', '.join(skipped)}")


if __name__ == "__main__":
    main()
