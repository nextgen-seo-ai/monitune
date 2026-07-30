/*
 * Аналитика посещений сайта MoniTune.
 *
 * Google Analytics 4 загружается ТОЛЬКО после явного согласия посетителя.
 * Отказ или отсутствие ответа — ни одного запроса к Google, ни одной cookie.
 * Выбор хранится в localStorage этого домена и меняется в любой момент:
 * ссылка «Аналитика» в подвале либо window.mtAnalytics.reset().
 *
 * Приложения MoniTune это не касается — в нём телеметрии нет.
 */
(function () {
  'use strict';

  var GA_ID = 'G-XXXXXXXXXX';          // Measurement ID; пока заглушка — счётчика и баннера нет
  var GA_PLACEHOLDER = 'G-XXXXXXXXXX';
  var STORE_KEY = 'monitune-analytics-consent';

  // Корень сайта вычисляем от собственного пути, чтобы ссылка работала
  // и со вложенных страниц (/stats/), и при открытии файлов локально
  var SITE_ROOT = (function () {
    var self = document.currentScript;
    var src = self && self.src ? self.src : '';
    var cut = src.indexOf('assets/analytics.js');
    return cut > -1 ? src.slice(0, cut) : '';
  })();

  // Язык баннера берём у самой страницы: английские страницы лежат в /en/
  // и объявляют lang="en". Без этого англоязычный посетитель видел бы
  // русские кнопки, хотя политика велит нажать «Allow».
  var IS_RU = (document.documentElement.getAttribute('lang') || 'ru')
    .slice(0, 2).toLowerCase() === 'ru';

  var POLICY_URL = SITE_ROOT + (IS_RU ? 'privacy.html' : 'en/privacy.html');

  var TEXT = IS_RU ? {
    body: 'Разрешить счётчик посещений Google Analytics? Он помогает понять, какие разделы ' +
          'документации читают, и ставит cookie. Без вашего согласия счётчик не загружается. ',
    policy: 'Политика конфиденциальности',
    accept: 'Разрешить',
    decline: 'Отказаться',
    label: 'Согласие на аналитику'
  } : {
    body: 'Allow the Google Analytics visit counter? It helps us see which parts of the ' +
          'documentation people read, and it sets a cookie. Without your consent the counter ' +
          'is not loaded. ',
    policy: 'Privacy policy',
    accept: 'Allow',
    decline: 'Decline',
    label: 'Analytics consent'
  };

  // Заглушка тоже подходит под формат Measurement ID, поэтому исключаем её отдельно
  var configured = GA_ID !== GA_PLACEHOLDER && /^G-[A-Z0-9]{6,}$/.test(GA_ID);

  /* ── Хранилище выбора ─────────────────────────── */

  function read() {
    try { return window.localStorage.getItem(STORE_KEY); } catch (e) { return null; }
  }
  function write(value) {
    try { window.localStorage.setItem(STORE_KEY, value); } catch (e) { /* приватный режим */ }
  }
  function clear() {
    try { window.localStorage.removeItem(STORE_KEY); } catch (e) { /* приватный режим */ }
  }

  /* ── Загрузка GA4 ─────────────────────────────── */

  var loaded = false;

  function loadGa() {
    if (loaded || !configured) return;
    loaded = true;

    window.dataLayer = window.dataLayer || [];
    window.gtag = function () { window.dataLayer.push(arguments); };

    // Согласие уже получено — иначе сюда не попадаем
    window.gtag('consent', 'default', {
      ad_storage: 'denied',
      ad_user_data: 'denied',
      ad_personalization: 'denied',
      analytics_storage: 'granted'
    });

    window.gtag('js', new Date());
    window.gtag('config', GA_ID, { anonymize_ip: true });

    var s = document.createElement('script');
    s.async = true;
    s.src = 'https://www.googletagmanager.com/gtag/js?id=' + encodeURIComponent(GA_ID);
    document.head.appendChild(s);
  }

  /* ── Баннер согласия ──────────────────────────── */

  var STYLE = [
    '.mt-consent{',
    '  position:fixed;left:16px;right:16px;bottom:16px;z-index:9999;',
    '  max-width:660px;margin:0 auto;',
    '  background:var(--ground-lift,#12161A);',
    '  color:var(--paper,#EDE5D2);',
    '  border:1px solid var(--line-strong,rgba(237,229,210,.18));',
    '  border-radius:12px;padding:18px 20px;',
    '  box-shadow:0 18px 48px rgba(0,0,0,.55);',
    '  font-size:13.5px;line-height:1.55;',
    '  display:flex;gap:18px;align-items:flex-start;flex-wrap:wrap;',
    '  transform:translateY(12px);opacity:0;',
    '  transition:opacity .22s ease,transform .22s ease;',
    '}',
    '.mt-consent.mt-in{transform:none;opacity:1;}',
    '.mt-consent p{margin:0;flex:1 1 320px;color:var(--paper-dim,#B5AF9F);}',
    '.mt-consent a{color:var(--amber,#FFB800);text-decoration:none;}',
    '.mt-consent a:hover{text-decoration:underline;}',
    '.mt-consent-actions{display:flex;gap:10px;flex:0 0 auto;align-items:center;}',
    '.mt-consent button{',
    '  font:inherit;font-size:13px;cursor:pointer;border-radius:8px;',
    '  padding:9px 18px;border:1px solid var(--line-strong,rgba(237,229,210,.18));',
    '  background:transparent;color:var(--paper-dim,#B5AF9F);',
    '  transition:color .15s,border-color .15s,background .15s;',
    '}',
    '.mt-consent button:hover{color:var(--paper,#EDE5D2);border-color:var(--paper-dim,#B5AF9F);}',
    '.mt-consent button.mt-yes{',
    '  background:var(--amber,#FFB800);border-color:var(--amber,#FFB800);',
    '  color:var(--ink,#0B0E10);font-weight:600;',
    '}',
    '.mt-consent button.mt-yes:hover{filter:brightness(1.08);color:var(--ink,#0B0E10);}',
    '@media (max-width:560px){',
    '  .mt-consent{flex-direction:column;gap:14px;}',
    '  .mt-consent-actions{width:100%;}',
    '  .mt-consent button{flex:1;}',
    '}',
    '@media (prefers-reduced-motion:reduce){',
    '  .mt-consent{transition:none;transform:none;opacity:1;}',
    '}'
  ].join('\n');

  var banner = null;

  function styleOnce() {
    if (document.getElementById('mt-consent-style')) return;
    var el = document.createElement('style');
    el.id = 'mt-consent-style';
    el.textContent = STYLE;
    document.head.appendChild(el);
  }

  function hide() {
    if (!banner) return;
    banner.classList.remove('mt-in');
    var node = banner;
    banner = null;
    window.setTimeout(function () {
      if (node.parentNode) node.parentNode.removeChild(node);
    }, 240);
  }

  function decide(value) {
    write(value);
    hide();
    if (value === 'granted') loadGa();
  }

  function showBanner() {
    if (banner || !configured) return;
    styleOnce();

    banner = document.createElement('aside');
    banner.className = 'mt-consent';
    banner.setAttribute('role', 'dialog');
    banner.setAttribute('aria-live', 'polite');
    banner.setAttribute('aria-label', TEXT.label);

    var text = document.createElement('p');
    text.innerHTML = TEXT.body + '<a href="' + POLICY_URL + '">' + TEXT.policy + '</a>.';

    var actions = document.createElement('div');
    actions.className = 'mt-consent-actions';

    var no = document.createElement('button');
    no.type = 'button';
    no.textContent = TEXT.decline;
    no.addEventListener('click', function () { decide('denied'); });

    var yes = document.createElement('button');
    yes.type = 'button';
    yes.className = 'mt-yes';
    yes.textContent = TEXT.accept;
    yes.addEventListener('click', function () { decide('granted'); });

    actions.appendChild(no);
    actions.appendChild(yes);
    banner.appendChild(text);
    banner.appendChild(actions);
    document.body.appendChild(banner);

    window.requestAnimationFrame(function () {
      window.requestAnimationFrame(function () {
        if (banner) banner.classList.add('mt-in');
      });
    });

    yes.focus({ preventScroll: true });
  }

  /* ── Публичный доступ для страницы политики ───── */

  window.mtAnalytics = {
    state: function () { return configured ? (read() || 'unset') : 'disabled'; },
    grant: function () { decide('granted'); },
    deny: function () { decide('denied'); },
    reset: function () { clear(); showBanner(); }
  };

  /* ── Старт ────────────────────────────────────── */

  function start() {
    if (!configured) return;
    var choice = read();
    if (choice === 'granted') loadGa();
    else if (choice !== 'denied') showBanner();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start);
  } else {
    start();
  }
})();
