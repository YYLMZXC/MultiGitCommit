#!/usr/bin/env python3
"""从 I18n/strings.*.tsv 生成 C# 字典源码（Strings.*.g.cs）。

用法（仓库任意目录下均可）：
    python scripts/BuildI18n.py

约定：
  * 每行一条：key<TAB>文本；@N@ 表示换行；以 # 开头的行为注释。
  * 简体中文是默认语言，中文文案同时作为 C# 侧的「资源键」，所以
    strings.zh-CN.tsv 只需要收 XAML 资源键（L.xxx）；
    strings.en-US.tsv 既收 L.xxx，也收 C# 源码里出现的中文原句 / 格式串。
  * key 列一律按 C# 字符串字面量的内容书写（例如 \\n、\\\" 保持转义形式）；
    文本列按运行时文本书写，需要换行时写 @N@。
  * C# 侧新增文案用 Loc.T("中文") / Loc.F($"中文 {x}")，并把同样的中文键
    补进 strings.en-US.tsv；XAML 侧新增文案用 {DynamicResource L.xxx}，
    并把该键同时补进两个 tsv。
  * 生成文件为 .g.cs，会被 SDK 自动编译，请勿手工编辑。
"""

import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
I18N = os.path.join(HERE, '..', 'src', 'MultiGitCommit.Avalonia', 'I18n')


def read_tsv(path):
    entries = []
    with open(path, encoding='utf-8-sig') as f:
        for lineno, raw in enumerate(f, 1):
            line = raw.rstrip('\r\n')
            if not line.strip() or line.lstrip().startswith('#'):
                continue
            if '\t' not in line:
                raise ValueError('%s:%d 缺少 TAB 分隔符: %s' % (path, lineno, line[:80]))
            key, text = line.split('\t', 1)
            entries.append((key, text.replace('@N@', '\n')))
    return entries


def literal(text):
    """Python 字符串 -> C# 字符串字面量（含两侧引号）。"""
    return json.dumps(text, ensure_ascii=False)


def generate(class_name, entries, literal_keys, output):
    lines = [
        'namespace MultiGitCommit;',
        '',
        '/// <summary>本地化字符串（由 scripts/BuildI18n.py 生成，请勿手工编辑）。</summary>',
        'internal static class %s' % class_name,
        '{',
        '    public static readonly System.Collections.Generic.Dictionary<string, string> Values =',
        '        new(System.StringComparer.Ordinal)',
        '        {',
    ]
    for key, text in entries:
        # key 列是 C# 字符串字面量的「内容」：已含 \n、\" 等转义，补上两侧引号即可；
        # 中文包的 key 是普通文本，走 literal() 统一转义。
        key_literal = '"' + key + '"' if literal_keys else literal(key)
        lines.append('            { %s, %s },' % (key_literal, literal(text)))
    lines += ['        };', '}', '']
    with open(output, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines))


def main():
    zh_path = os.path.join(I18N, 'strings.zh-CN.tsv')
    en_path = os.path.join(I18N, 'strings.en-US.tsv')
    zh = read_tsv(zh_path)
    en = read_tsv(en_path)

    for name, entries in (('zh-CN', zh), ('en-US', en)):
        keys = [k for k, _ in entries]
        dup = {k for k in keys if keys.count(k) > 1}
        if dup:
            raise ValueError('strings.%s.tsv 存在重复 key: %s' % (name, sorted(dup)[:10]))

    en_keys = {k for k, _ in en}
    missing = sorted({k for k, _ in zh} - en_keys)
    if missing:
        raise ValueError('英文包缺少 %d 个 XAML 键: %s' % (len(missing), missing[:10]))

    generate('LocStringsZh', zh, literal_keys=False,
             output=os.path.join(I18N, 'Strings.zh-CN.g.cs'))
    generate('LocStringsEn', en, literal_keys=True,
             output=os.path.join(I18N, 'Strings.en-US.g.cs'))
    print('I18n: %d 个界面文案键，英文包 %d 条（含 %d 条 C# 原文键）'
          % (len(zh), len(en), len(en) - len(zh)))


if __name__ == '__main__':
    try:
        main()
    except Exception as ex:
        print('BuildI18n 失败: %s' % ex, file=sys.stderr)
        sys.exit(1)
