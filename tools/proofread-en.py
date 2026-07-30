#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Проверка английских текстов MoniTune через LanguageTool с жёстко заданным en-US.

Зачем: сайт и приложение объявляют локаль en_US, а британские формы
(licence, behaviour, synchronised, per cent) проникают в текст незаметно —
глазом такое смешение почти не ловится, а выглядит непрофессионально.

Где берётся текст: из HTML вырезаются разметка и код, из .resw — значения
строк, из C# — содержимое строковых литералов. То есть проверяется ровно то,
что читает пользователь.

Запуск локально (публичный API, есть ограничение по частоте):
    python tools/proofread-en.py

Запуск в CI (свой сервер LanguageTool, ограничений нет):
    python tools/proofread-en.py --api http://localhost:8010/v2/check

Код возврата 1 — найдено замечание из списка критичных правил (см. HARD_RULES).
Остальные выводятся как предупреждения и сборку не роняют: словарь неизбежно
спотыкается на технических терминах, и падать из-за них смысла нет.
"""
import argparse
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

PUBLIC_API = 'https://api.languagetool.org/v2/check'
CHUNK = 15000

# Файлы с английским текстом. Русские сюда не попадают намеренно.
TARGETS = [
    ('docs/en/index.html', 'html'),
    ('docs/en/faq.html', 'html'),
    ('docs/en/privacy.html', 'html'),
    ('docs/en/eula.html', 'html'),
    ('docs/en/stats/index.html', 'html'),
    ('docs/404.html', 'html'),
    ('winui/Strings/en-US/Resources.resw', 'resw'),
    ('winui/AboutContentEn.cs', 'cs'),
]

# Из-за этих правил сборка падает: орфография и диалект — то, ради чего всё затевалось.
HARD_RULES = {
    'MORFOLOGIK_RULE_EN_US',   # слово не из американского словаря
    'EN_GB_SIMPLE_REPLACE',    # прямое указание на британскую форму
    'BRITISH_SIMPLE_REPLACE',
}

# Технические термины и имена собственные, на которые словарь ругается зря.
IGNORE_WORDS = {
    'MoniTune', 'MonitorTune', 'DDC', 'CI', 'VCP', 'WMI', 'eDP', 'MSIX', 'OSD',
    'DisplayLink', 'DisplayPort', 'FreeSync', 'Miracast', 'WinUI', 'AppNotifications',
    'StartupTask', 'LocalCache', 'certlm', 'msc', 'github', 'json', 'exe', 'px',
    'Ed', 'SHA', 'HDR', 'DVI', 'HDMI', 'ARM', 'Nvidia', 'AMD', 'BenQ', 'ASUS',
    'WmiMonitorBrightness', 'MCU', 'ms', 'setup', 'EDID', 'ga', '_ga', 'Ext',
    'linux', 'hardware', 'org', 'CC', 'BY', 'MIT', 'PNP', 'UEFI', 'Miracast',
}

# Правила, которые на нашем тексте дают только шум.
IGNORE_RULES = {
    'WHITESPACE_RULE', 'EN_QUOTES', 'DASH_RULE', 'ARROWS',
    'UPPERCASE_SENTENCE_START', 'PUNCTUATION_PARAGRAPH_END',
    'COMMA_PARENTHESIS_WHITESPACE', 'ENGLISH_WORD_REPEAT_BEGINNING_RULE',
    'ENGLISH_WORD_REPEAT_RULE', 'PHRASE_REPETITION',
    'EN_UPPER_CASE_NGRAM',      # подписи интерфейса в sentence case
    'CONFUSION_RULE_LIVE_LIFE', # «GitHub API · live» — так и задумано
    'FROM_FORM',                # «From … To …» — названия полей
    'POSSESSIVE_APOSTROPHE',    # «crashes folder» — имя папки
}

# Осознанно принятые формулировки: правило + фрагмент.
# «up to date» в предикативе пишется без дефисов — так же, как в Windows Update.
# «subject matter» и «the Author» с заглавной — термины текста соглашения.
ACCEPTED = {
    ('UP_TO_DATE_HYPHEN', 'up to date'),
    ('SUBJECT_MATTER', 'subject matter'),
    ('EN_UPPER_CASE_NGRAM', 'Author'),
}


def strip_html(s):
    s = re.sub(r'(?is)<script.*?</script>', ' ', s)
    s = re.sub(r'(?is)<style.*?</style>', ' ', s)
    s = re.sub(r'(?s)<!--.*?-->', ' ', s)
    metas = re.findall(r'<meta[^>]+content="([^"]{20,})"', s)
    title = re.findall(r'<title>(.*?)</title>', s, re.S)
    body = re.sub(r'(?s)<[^>]+>', ' ', s)
    body = re.sub(r'&nbsp;', ' ', body)
    body = re.sub(r'&[a-z]+;', ' ', body)
    return '\n'.join(title + metas + [body])


def strip_resw(s):
    vals = re.findall(r'<value>(.*?)</value>', s, re.S)
    return '\n'.join(v.strip() for v in vals[4:])   # первые четыре — служебные resheader


def strip_cs(s):
    out = []
    for lit in re.findall(r'"((?:[^"\\]|\\.)*)"', s):
        if len(lit) < 12:
            continue
        out.append(lit.replace('\\n', '\n').replace('\\\\', '\\'))
    return '\n'.join(out)


EXTRACT = {'html': strip_html, 'resw': strip_resw, 'cs': strip_cs}


def clean(s):
    s = re.sub(r'[ \t]+', ' ', s)
    s = re.sub(r'\n\s*\n+', '\n\n', s)
    return s.strip()


def chunks(s, n):
    out, cur = [], ''
    for line in s.split('\n'):
        if len(cur) + len(line) + 1 > n:
            if cur:
                out.append(cur)
            cur = line
        else:
            cur = cur + '\n' + line if cur else line
    if cur:
        out.append(cur)
    return out


def check(api, text, attempts=3):
    data = urllib.parse.urlencode({'text': text, 'language': 'en-US'}).encode()
    req = urllib.request.Request(api, data=data, headers={'User-Agent': 'monitune-proofread'})
    for i in range(attempts):
        try:
            with urllib.request.urlopen(req, timeout=90) as r:
                return json.loads(r.read().decode())
        except urllib.error.HTTPError as e:
            if e.code == 429 and i < attempts - 1:   # публичный API ограничивает частоту
                time.sleep(20)
                continue
            raise
    return {'matches': []}


def fragment(m):
    ctx = m['context']
    return ctx['text'][ctx['offset']:ctx['offset'] + ctx['length']].strip()


def noise(m):
    if m['rule']['id'] in IGNORE_RULES:
        return True
    frag = fragment(m)
    if not frag:
        return True
    if frag in IGNORE_WORDS or frag.strip('.,;:()_') in IGNORE_WORDS:
        return True
    if (m['rule']['id'], frag) in ACCEPTED:
        return True
    return False


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--api', default=PUBLIC_API)
    p.add_argument('--root', default=os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    p.add_argument('--pause', type=float, default=None,
                   help='пауза между запросами; для публичного API нужна, для своего сервера нет')
    args = p.parse_args()

    local = 'localhost' in args.api or '127.0.0.1' in args.api
    pause = args.pause if args.pause is not None else (0 if local else 4)

    hard, soft = [], []

    for rel, kind in TARGETS:
        path = os.path.join(args.root, rel.replace('/', os.sep))
        if not os.path.exists(path):
            print('пропуск (нет файла): %s' % rel)
            continue
        with open(path, encoding='utf-8') as f:
            text = clean(EXTRACT[kind](f.read()))
        if not text:
            continue

        found = []
        for part in chunks(text, CHUNK):
            for m in check(args.api, part).get('matches', []):
                if not noise(m):
                    found.append(m)
            if pause:
                time.sleep(pause)

        print('=== %s — символов %d, замечаний %d' % (rel, len(text), len(found)))
        for m in found:
            rule = m['rule']['id']
            frag = fragment(m)
            repl = ', '.join(r['value'] for r in m['replacements'][:3]) or '—'
            line = '  [%s] %s → %s' % (rule, frag, repl)
            ctx = m['context']['text'].replace('\n', ' ').strip()
            print(line)
            print('      %s' % ctx)
            (hard if rule in HARD_RULES else soft).append((rel, rule, frag))

    print()
    print('Предупреждений: %d' % len(soft))
    print('Ошибок (орфография и диалект): %d' % len(hard))

    if hard:
        print()
        print('Тексты объявлены как en-US, но содержат слова вне американской нормы:')
        for rel, rule, frag in hard:
            print('  %s — «%s» (%s)' % (rel, frag, rule))
        print()
        print('Исправьте написание либо, если слово корректно, добавьте его')
        print('в IGNORE_WORDS в tools/proofread-en.py.')
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
