/*
 * Выбор языка сайта MoniTune.
 *
 * Русская версия лежит в корне сайта, английская — в /en/.
 * При первом заходе посетителя с нерусскоязычным браузером страница один раз
 * переводится на английскую версию. Явный выбор в переключателе запоминается
 * и всегда важнее автоматики. Поисковых роботов не трогаем: обе версии должны
 * индексироваться, они связаны тегами hreflang.
 */
(function () {
  'use strict';

  var STORE_KEY = 'monitune-lang';
  var RU_LOCALES = ['ru', 'uk', 'be', 'kk', 'ky', 'tt', 'ba', 'ce', 'cv', 'sah', 'os', 'ab', 'tg', 'uz'];

  var html = document.documentElement;
  var pageLang = (html.getAttribute('lang') || 'ru').slice(0, 2).toLowerCase();

  /* ── Сохранённый выбор ────────────────────────── */

  function read() {
    try { return window.localStorage.getItem(STORE_KEY); } catch (e) { return null; }
  }
  function write(v) {
    try { window.localStorage.setItem(STORE_KEY, v); } catch (e) { /* приватный режим */ }
  }

  /* ── Адрес той же страницы на другом языке ────── */

  function counterpartPath() {
    var alt = document.querySelector('link[rel="alternate"][hreflang="' + (pageLang === 'ru' ? 'en' : 'ru') + '"]');
    if (alt && alt.href) return alt.href;
    return null;
  }

  /* ── Робот или человек ────────────────────────── */

  function isCrawler() {
    var ua = navigator.userAgent || '';
    return /bot|crawl|spider|slurp|bing|yandex|duckduck|baidu|lighthouse|headless|preview|facebookexternalhit|embedly|whatsapp|telegram/i.test(ua);
  }

  /* ── Переключатель в шапке ────────────────────── */

  function mountSwitch() {
    var nav = document.querySelector('.masthead .nav, .masthead .masthead-meta');
    if (!nav) return;

    var target = counterpartPath();
    if (!target) return;

    var a = document.createElement('a');
    a.className = 'lang-switch';
    a.href = target;
    a.setAttribute('lang', pageLang === 'ru' ? 'en' : 'ru');
    a.setAttribute('hreflang', pageLang === 'ru' ? 'en' : 'ru');
    a.textContent = pageLang === 'ru' ? 'EN' : 'RU';
    a.title = pageLang === 'ru' ? 'Switch to English' : 'Открыть русскую версию';
    a.addEventListener('click', function () { write(pageLang === 'ru' ? 'en' : 'ru'); });
    nav.appendChild(a);

    if (!document.getElementById('lang-switch-style')) {
      var s = document.createElement('style');
      s.id = 'lang-switch-style';
      s.textContent =
        '.lang-switch{border:1px solid var(--line-strong,rgba(237,229,210,.18));' +
        'border-radius:6px;padding:2px 9px;font-size:12px;letter-spacing:.04em;' +
        'color:var(--paper-dim,#B5AF9F);text-decoration:none;line-height:1.7;}' +
        '.lang-switch:hover{color:var(--amber,#FFB800);border-color:var(--amber,#FFB800);}';
      document.head.appendChild(s);
    }
  }

  /* ── Один автоматический переход ──────────────── */

  function autoRedirect() {
    if (read()) return;                   // выбор уже сделан
    if (isCrawler()) return;              // роботам обе версии как есть

    var langs = navigator.languages && navigator.languages.length
      ? navigator.languages
      : [navigator.language || ''];

    var prefersRu = langs.some(function (l) {
      return RU_LOCALES.indexOf(String(l).slice(0, 2).toLowerCase()) > -1;
    });

    var wanted = prefersRu ? 'ru' : 'en';
    if (wanted === pageLang) { write(wanted); return; }

    var target = counterpartPath();
    if (!target) return;

    write(wanted);
    window.location.replace(target + window.location.hash);
  }

  function start() {
    mountSwitch();
    autoRedirect();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start);
  } else {
    start();
  }
})();
