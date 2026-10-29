"""校验 Localization/ 下各语言 hjson 的键集合是否与基准语言一致。

用法：
    python check_localization.py <Localization 目录>

约定：
- 以文件名排序后的第一个文件为基准（当前是 en-US_*.hjson）。
- 嵌套对象展开成 a.b.c 形式的点号键再比较。
- 任一文件解析失败、或键集合不一致，退出码 1。

tML 真正加载模组时也会校验 hjson，但那要等到游戏里；
这个脚本让「少一个键」这类问题在 CI 阶段就暴露。
"""

import sys
from pathlib import Path

import hjson


def flatten(node, prefix=""):
    """把嵌套 dict 摊平成 {点号键: 值}，非 dict 视为叶子。"""
    flat = {}
    if isinstance(node, dict):
        for key, value in node.items():
            path = "%s.%s" % (prefix, key) if prefix else str(key)
            if isinstance(value, dict):
                flat.update(flatten(value, path))
            else:
                flat[path] = value
    return flat


def main():
    if len(sys.argv) != 2:
        print("usage: check_localization.py <Localization dir>", file=sys.stderr)
        return 2

    directory = Path(sys.argv[1])
    files = sorted(directory.glob("*.hjson"))
    if not files:
        print("::error::no *.hjson found in %s" % directory)
        return 1

    parsed = {}
    for path in files:
        try:
            with open(path, encoding="utf-8-sig") as handle:
                parsed[path.name] = flatten(hjson.load(handle))
        except Exception as exc:  # hjson 的异常类型不止一种，统一按解析失败处理
            print("::error file=%s::%s" % (path, exc))
            return 1

    base_name = files[0].name
    base_keys = parsed[base_name]
    print("base: %s (%d keys)" % (base_name, len(base_keys)))

    failed = False
    for path in files[1:]:
        keys = parsed[path.name]
        missing = sorted(set(base_keys) - set(keys))
        extra = sorted(set(keys) - set(base_keys))
        print("check: %s (%d keys)" % (path.name, len(keys)))

        for key in missing:
            print("::error file=%s::missing key '%s' (present in %s)" % (path, key, base_name))
            failed = True
        for key in extra:
            print("::error file=%s::unexpected key '%s' (absent from %s)" % (path, key, base_name))
            failed = True

        if not missing and not extra:
            print("  ok, %d keys aligned" % len(keys))

    if failed:
        return 1

    print("all %d file(s) aligned on %d keys" % (len(files), len(base_keys)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
