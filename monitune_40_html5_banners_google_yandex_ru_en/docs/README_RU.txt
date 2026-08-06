MoniTune — HTML5 animated banner pack
======================================

Состав:
- 40 HTML5-баннеров: Google Ads 10 RU + 10 EN, Yandex Direct 10 RU + 10 EN.
- 10 разных motion-концепций: Sync Slider, Before/After Light, UI Micro Motion,
  Mask Reveal, Kinetic Typography, Depth/Parallax, Number Counter,
  Night→Day, Interactive Drag, CTA Pulse.
- Каждый ZIP содержит index.html + hero.jpg.
- Люди в hero.jpg созданы AI и адаптированы из согласованной серии креативов.
- Логотип-солнце отрисован векторно прямо в HTML; текстовый логотип MoniTune увеличен.
- CTA: RU «Скачай сейчас!», EN «Download now!».

Стиль:
- Тёмный фон сайта MoniTune.
- Фирменный жёлтый #FFB900.
- Тонкие крупные заголовки, близкие к стилю лендинга.
- UI-панель и слайдеры показывают ключевую механику продукта.

Технически:
- Без видео, Canvas и тяжёлых JS-библиотек.
- Основная анимация: CSS transform/opacity/filter; JS только в Number Counter и Interactive Drag.
- Анимации не зациклены бесконечно; Google-версии укладываются в политику до 30 секунд.
- Все ресурсы локальные.
- Yandex: используется yandexHTML5BannerApi.getClickURLNum(1) с fallback на сайт.
- Google: используется один clickTag/final exit.
- Размеры прописаны через meta name="ad.size".

Важно:
- Google Ads требует eligibility/доступ для загрузки HTML5-креативов на некоторых аккаунтах.
- Перед запуском рекомендуется прогнать ZIP через валидатор платформы и модерацию.
- Для Яндекса лимит обычного HTML5 ZIP — 512 KB; созданные файлы значительно легче.
- Для Google HTML5 ZIP — до 600 KB; созданные файлы значительно легче.

Лендинги:
RU: https://nextgen-seo-ai.github.io/monitune/
EN: https://nextgen-seo-ai.github.io/monitune/en/

Файл manifest.csv содержит имя каждого ZIP, размер, систему, язык,
motion-концепт и элементы конкретного баннера.
