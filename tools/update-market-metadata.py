#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""按仓库中实际的 .icplugin 包，重算市场目录里的 size 与 SHA256，并同步 lastUpdated。

背景：插件重新打包后字节数必然变化（zip 内含文件时间戳），若目录里的 size / checksum
不跟着更新，ICU 插件工坊会判定文件被篡改而拒绝安装。本脚本把这一步自动化。

用法：
    python tools/update-market-metadata.py [--repo-root DIR] [--check]

默认会更新两个目录文件（二者内容一致，后者是历史遗留副本）：
    market/v1/market.json    主程序读取的在线目录
    plugins.json             仓库根目录的历史副本

写入采用"按旧值精确替换"的方式，不经过 json.dump，因此保留文件原有的排版与字段顺序。
"""

import argparse
import hashlib
import io
import json
import os
import re
import sys
from datetime import datetime, timezone

MARKET_REL = os.path.join("market", "v1", "market.json")
LEGACY_REL = "plugins.json"


def sha256_of(path):
    """计算文件 SHA256，返回大写十六进制。"""
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def file_name_from_url(url):
    """从 downloadUrl 取出包名（忽略查询串）。"""
    return url.rstrip("/").rsplit("/", 1)[-1].split("?")[0]


def collect_entries(root, catalog_path):
    """解析目录文件，返回 (entries, problems)。

    entries 每项为 (plugin_id, 包名, old_size, new_size, old_sha, new_sha)。
    """
    # utf-8-sig 对无 BOM 的 UTF-8 同样适用
    data = json.loads(io.open(catalog_path, encoding="utf-8-sig").read())
    entries, problems = [], []

    for p in data.get("plugins", []):
        pid = p.get("id") or "<unknown>"
        url = p.get("downloadUrl") or p.get("fallbackUrl")
        if not url:
            problems.append((pid, "缺少 downloadUrl / fallbackUrl"))
            continue

        name = file_name_from_url(url)
        pkg = os.path.join(root, name)
        if not os.path.isfile(pkg):
            problems.append((pid, "仓库根目录下找不到包文件 %s" % name))
            continue

        old_size = p.get("size")
        old_sha = ((p.get("checksum") or {}).get("value") or "").upper()
        entries.append((pid, name, old_size, os.path.getsize(pkg), old_sha, sha256_of(pkg)))

    return entries, problems


def replace_once(text, old, new, pid):
    """精确替换一处文本；old 不唯一时报错，避免误改其它插件的字段。"""
    count = text.count(old)
    if count != 1:
        raise RuntimeError("替换 [%s] 时在目录中找到 %d 处匹配（应为 1 处）：%s" % (pid, count, old))
    return text.replace(old, new)


def apply_updates(catalog_path, entries, new_last_updated):
    """按旧值替换 size / checksum / lastUpdated。

    以二进制读写，原样保留文件的行尾（CRLF / LF）与 BOM，避免整份文件被 git 判为改动。
    """
    with open(catalog_path, "rb") as f:
        raw = f.read()

    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig" if bom else "utf-8")

    for pid, _name, old_size, new_size, old_sha, new_sha in entries:
        if old_size != new_size:
            text = replace_once(text, '"size": %d,' % old_size, '"size": %d,' % new_size, pid)
        if old_sha and old_sha != new_sha:
            text = replace_once(text, '"value": "%s"' % old_sha, '"value": "%s"' % new_sha, pid)

    text = re.sub(r'"lastUpdated":\s*"[^"]*"', '"lastUpdated": "%s"' % new_last_updated, text, count=1)

    # 写回前再解析一次，确保没有把 JSON 改坏
    json.loads(text)
    out = text.encode("utf-8")
    if bom:
        out = b"\xef\xbb\xbf" + out
    with open(catalog_path, "wb") as f:
        f.write(out)


def report_unregistered(root, catalog_path):
    """提示仓库根目录里存在、但未登记进目录的 .icplugin。"""
    data = json.loads(io.open(catalog_path, encoding="utf-8-sig").read())
    registered = {file_name_from_url(p.get("downloadUrl") or p.get("fallbackUrl") or "")
                  for p in data.get("plugins", [])}
    unregistered = sorted(n for n in os.listdir(root)
                          if n.endswith(".icplugin") and n not in registered)
    return unregistered


def main():
    parser = argparse.ArgumentParser(description="重算插件市场目录的 size 与 SHA256")
    parser.add_argument("--repo-root", help="插件仓库根目录（默认：本脚本所在目录的上级目录）")
    parser.add_argument("--check", action="store_true",
                        help="只比对不写入；存在不一致时退出码为 1（适合提交前自检 / CI）")
    args = parser.parse_args()

    root = args.repo_root or os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    root = os.path.abspath(root)
    stamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    targets = [os.path.join(root, MARKET_REL), os.path.join(root, LEGACY_REL)]
    has_diff = False

    for catalog in targets:
        rel = os.path.relpath(catalog, root)
        if not os.path.isfile(catalog):
            print("[跳过] %s（文件不存在）" % rel)
            continue

        entries, problems = collect_entries(root, catalog)
        diffs = [e for e in entries if e[2] != e[3] or e[4] != e[5]]

        print("\n== %s ==" % rel)
        for pid, name, old_size, new_size, old_sha, new_sha in entries:
            if (old_size, old_sha) == (new_size, new_sha):
                print("  %-30s %-24s 一致" % (pid, name))
            else:
                has_diff = True
                print("  %-30s %-24s size %s -> %s | sha %s -> %s"
                      % (pid, name, old_size, new_size, old_sha[:8], new_sha[:8]))
        for pid, msg in problems:
            print("  [警告] %-30s %s" % (pid, msg))

        for name in report_unregistered(root, catalog):
            print("  [提示] 未登记进目录的包：%s" % name)

        if diffs and not args.check:
            apply_updates(catalog, entries, stamp)
            print("  已更新 %d 个插件的元数据，lastUpdated = %s" % (len(diffs), stamp))

    if args.check:
        print("\n检查结果：%s" % ("存在不一致，请运行本脚本更新" if has_diff else "全部一致"))
        return 1 if has_diff else 0

    print("\n完成：%s" % ("已同步元数据" if has_diff else "元数据已是最新，无需改动"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
