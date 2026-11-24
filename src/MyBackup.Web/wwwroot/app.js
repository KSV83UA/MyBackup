'use strict';
/* MyBackup — веб-интерфейс. Один файл без зависимостей: работает на сервере без доступа в интернет. */

const MASK = '••••••';
const S = {
  session: null,
  status: null,
  cfg: null,        // черновик настроек (редактируется на страницах)
  cfgSaved: '',     // сохранённая версия (JSON) — для «есть изменения»
  cfgMeta: null,    // путь, ошибки и т. п. из /api/config
  saveErrors: [],
  page: null,
  onStatus: null,
  pollTimer: 0,
};

/* =============== DOM =============== */

const $ = (sel, root = document) => root.querySelector(sel);

function h(tag, props, ...children) {
  const el = document.createElement(tag);
  if (props) {
    for (const [k, v] of Object.entries(props)) {
      if (v == null || v === false) continue;
      if (k === 'class') el.className = v;
      else if (k === 'text') el.textContent = v;
      else if (k === 'style') Object.assign(el.style, v);
      else if (k === 'dataset') Object.assign(el.dataset, v);
      else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
      else if (k === 'value') el.value = v;
      else if (['checked', 'disabled', 'indeterminate', 'selected', 'open', 'hidden'].includes(k)) el[k] = !!v;
      else if (v === true) el.setAttribute(k, '');
      else el.setAttribute(k, v);
    }
  }
  return append(el, children);
}

/** Заменить содержимое; null и false пропускаются (replaceChildren вывел бы их текстом). */
function fill(el, ...children) {
  el.replaceChildren();
  return append(el, children);
}

function append(el, children) {
  for (const c of children.flat(Infinity)) {
    if (c == null || c === false || c === true) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return el;
}

const ICONS = {
  dashboard: '<rect x="3" y="3" width="7" height="9" rx="1.5"/><rect x="14" y="3" width="7" height="5" rx="1.5"/><rect x="14" y="12" width="7" height="9" rx="1.5"/><rect x="3" y="16" width="7" height="5" rx="1.5"/>',
  jobs: '<rect x="3" y="4.5" width="18" height="16.5" rx="2"/><path d="M3 9.5h18M8 2.5v4M16 2.5v4M7.5 14h3M7.5 17.5h6"/>',
  history: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3.2 2"/>',
  snapshots: '<rect x="2.5" y="3.5" width="19" height="5" rx="1"/><path d="M4.5 8.5v11a1.5 1.5 0 0 0 1.5 1.5h12a1.5 1.5 0 0 0 1.5-1.5v-11M10 12.5h4"/>',
  settings: '<path d="M4 6.5h9M17 6.5h3M4 12h3M11 12h9M4 17.5h11M19 17.5h1"/><circle cx="15" cy="6.5" r="2"/><circle cx="9" cy="12" r="2"/><circle cx="17" cy="17.5" r="2"/>',
  logout: '<path d="M9 21H5a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1h4M16 17l5-5-5-5M21 12H9"/>',
  play: '<path d="M7 4.5v15l12.5-7.5z"/>',
  stop: '<rect x="6" y="6" width="12" height="12" rx="1.5"/>',
  pause: '<path d="M9 5v14M15 5v14"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  trash: '<path d="M4 7h16M9.5 7V4.5h5V7M6 7l1 13h10l1-13M10 11v5M14 11v5"/>',
  up: '<path d="M6 15l6-6 6 6"/>',
  down: '<path d="M6 9l6 6 6-6"/>',
  right: '<path d="M9 6l6 6-6 6"/>',
  folder: '<path d="M3 6.5A1.5 1.5 0 0 1 4.5 5H9l2 2h8.5A1.5 1.5 0 0 1 21 8.5v9a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 17.5z"/>',
  file: '<path d="M6 3h8l5 5v13H6z"/><path d="M14 3v5h5"/>',
  db: '<ellipse cx="12" cy="5.5" rx="7.5" ry="2.8"/><path d="M4.5 5.5v13c0 1.5 3.4 2.8 7.5 2.8s7.5-1.3 7.5-2.8v-13M4.5 12c0 1.5 3.4 2.8 7.5 2.8s7.5-1.3 7.5-2.8"/>',
  base1c: '<path d="M6 3h8l5 5v13H6z"/><path d="M14 3v5h5M9.5 17v-5l-1.5 1M15.5 12.6a2.4 2.4 0 1 0 0 3.8"/>',
  users: '<circle cx="9" cy="8" r="3.5"/><path d="M2.5 20c0-3.6 2.9-6 6.5-6s6.5 2.4 6.5 6"/><path d="M16 4.6a3.5 3.5 0 0 1 0 6.8M21.5 20c0-2.8-1.6-4.8-4-5.6"/>',
  shield: '<path d="M12 3l8 3v6c0 4.8-3.4 7.9-8 9-4.6-1.1-8-4.2-8-9V6z"/><path d="M9 12l2 2 4-4"/>',
  drive: '<rect x="2.5" y="13" width="19" height="7.5" rx="2"/><path d="M5 13l2.5-8.5h9L19 13M6.5 16.8h.01"/>',
  key: '<circle cx="7.5" cy="15.5" r="4"/><path d="M10.5 12.5L20 3M16.5 6.5l3 3M14 9l2 2"/>',
  check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
  x: '<path d="M6 6l12 12M18 6L6 18"/>',
  alert: '<path d="M12 3.5l9.5 16.5h-19z"/><path d="M12 10v4.5M12 17.3h.01"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v5.5M12 7.8h.01"/>',
  refresh: '<path d="M20 11.5a8 8 0 1 0-2.4 5.8"/><path d="M20 4.5v7h-7"/>',
  copy: '<rect x="8.5" y="8.5" width="12" height="12" rx="2"/><path d="M15.5 8.5V5a1.5 1.5 0 0 0-1.5-1.5H5A1.5 1.5 0 0 0 3.5 5v9A1.5 1.5 0 0 0 5 15.5h3.5"/>',
  search: '<circle cx="11" cy="11" r="6.5"/><path d="M20 20l-4.3-4.3"/>',
  send: '<path d="M21 3L10 14M21 3l-6.5 18-4.5-7-7-4.5z"/>',
  eye: '<path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12z"/><circle cx="12" cy="12" r="3"/>',
  server: '<rect x="3" y="3.5" width="18" height="7" rx="1.5"/><rect x="3" y="13.5" width="18" height="7" rx="1.5"/><path d="M7 7h.01M7 17h.01"/>',
  export: '<path d="M6 3h8l5 5v13H6z"/><path d="M14 3v5h5M12.5 11v6.5M9.8 14.8l2.7 2.7 2.7-2.7"/>',
  download: '<path d="M12 4v11M7.5 10.5 12 15l4.5-4.5M4.5 19.5h15"/>',
};

function icon(name, cls) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('class', 'icon' + (cls ? ' ' + cls : ''));
  svg.setAttribute('aria-hidden', 'true');
  svg.innerHTML = ICONS[name] || '';
  return svg;
}

function logo() {
  const img = h('img', { class: 'logo', src: '/favicon.svg', alt: '' });
  return img;
}

const spinner = () => h('span', { class: 'spinner', role: 'status', 'aria-label': 'Загрузка' });

function debounce(fn, ms) {
  let t = 0;
  return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); };
}

/* =============== форматирование =============== */

function fmtSize(b) {
  if (b == null) return '—';
  const u = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
  let v = b, i = 0;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  if (i === 0) return `${b} Б`;
  const s = v >= 100 ? String(Math.round(v)) : v.toFixed(1).replace(/\.0$/, '').replace('.', ',');
  return `${s} ${u[i]}`;
}

function fmtDur(sec) {
  if (sec == null || isNaN(sec)) return '—';
  sec = Math.max(0, Math.round(sec));
  if (sec < 60) return `${sec} с`;
  if (sec < 3600) return `${Math.floor(sec / 60)} мин`;
  return `${Math.floor(sec / 3600)} ч ${String(Math.floor(sec % 3600 / 60)).padStart(2, '0')} мин`;
}

const pad2 = n => String(n).padStart(2, '0');

function fmtDateTime(iso) {
  if (!iso) return '—';
  const d = new Date(iso), now = new Date();
  const time = `${pad2(d.getHours())}:${pad2(d.getMinutes())}`;
  const sameDay = (a, b) => a.toDateString() === b.toDateString();
  if (sameDay(d, now)) return `сегодня ${time}`;
  const y = new Date(now); y.setDate(now.getDate() - 1);
  if (sameDay(d, y)) return `вчера ${time}`;
  const t = new Date(now); t.setDate(now.getDate() + 1);
  if (sameDay(d, t)) return `завтра ${time}`;
  return `${pad2(d.getDate())}.${pad2(d.getMonth() + 1)}${d.getFullYear() !== now.getFullYear() ? '.' + d.getFullYear() : ''} ${time}`;
}

const fmtTime = iso => { const d = new Date(iso); return `${pad2(d.getHours())}:${pad2(d.getMinutes())}`; };

function fmtRel(iso) {
  const diff = (new Date(iso) - Date.now()) / 1000, a = Math.abs(diff);
  const w = a < 90 ? null : a < 3600 ? `${Math.round(a / 60)} мин` : a < 172800 ? `${Math.round(a / 3600)} ч` : `${Math.round(a / 86400)} дн.`;
  if (!w) return diff > 0 ? 'сейчас' : 'только что';
  return diff > 0 ? `через ${w}` : `${w} назад`;
}

/** TimeSpan .NET: "06:00:00", "1.06:00:00", "00:01:02.1234567". */
function tsToSec(s) {
  const m = /^(?:(\d+)\.)?(\d+):(\d+):(\d+)(?:\.(\d+))?$/.exec(s || '');
  if (!m) return 0;
  return (+m[1] || 0) * 86400 + (+m[2]) * 3600 + (+m[3]) * 60 + (+m[4]) + (m[5] ? +('0.' + m[5]) : 0);
}

function secToTs(sec) {
  sec = Math.max(0, Math.round(sec));
  const d = Math.floor(sec / 86400); sec -= d * 86400;
  return (d ? d + '.' : '') + `${pad2(Math.floor(sec / 3600))}:${pad2(Math.floor(sec % 3600 / 60))}:${pad2(sec % 60)}`;
}

const runSeconds = r => (new Date(r.finished) - new Date(r.started)) / 1000;
const isAbsPath = p => /^([a-zA-Z]:[\\/]|\\\\)/.test(p || '');
const baseName = p => (p || '').replace(/[\\/]+$/, '').split(/[\\/]/).pop();

const STATUS = {
  Ok: { cls: 'ok', text: 'успешно', icon: 'check' },
  Warning: { cls: 'warn', text: 'с замечаниями', icon: 'alert' },
  Failed: { cls: 'err', text: 'ошибка', icon: 'x' },
  Skipped: { cls: '', text: 'пропущено', icon: 'right' },
};

const statusBadge = s => h('span', { class: 'badge ' + (STATUS[s]?.cls || '') }, icon(STATUS[s]?.icon || 'info', 'sm'), STATUS[s]?.text || s);

/* =============== API =============== */

class ApiError extends Error {
  constructor(message, status, data) { super(message); this.status = status; this.data = data; }
}

async function api(method, url, body) {
  const headers = { 'X-MyBackup': '1' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  let res;
  try {
    res = await fetch(url, { method, headers, body: body !== undefined ? JSON.stringify(body) : undefined, credentials: 'same-origin' });
  } catch {
    throw new ApiError('Нет связи со службой MyBackup', 0);
  }
  let data = null;
  try { data = await res.json(); } catch { /* пустой ответ */ }
  if (res.status === 401 && !url.endsWith('/login')) {
    if (S.session) S.session.authenticated = false;
    renderLogin('Сеанс завершён — войдите снова.');
    throw new ApiError('Требуется вход', 401, data);
  }
  if (!res.ok) throw new ApiError(data?.error || (data?.errors || []).join('; ') || `Ошибка ${res.status}`, res.status, data);
  return data;
}

/* =============== уведомления и окна =============== */

function toast(text, kind = 'ok', ms = 4500) {
  const box = $('#toasts');
  if (!box) return;
  const el = h('div', { class: 'toast ' + kind, role: 'status' },
    icon(kind === 'ok' ? 'check' : kind === 'err' ? 'alert' : 'info'), h('div', null, text));
  box.append(el);
  setTimeout(() => el.remove(), ms);
}

function modal({ title, body, actions = [], wide = false, onClose }) {
  const back = h('div', { class: 'modal-back' });
  const close = () => { back.remove(); document.removeEventListener('keydown', onKey); onClose?.(); };
  const onKey = e => { if (e.key === 'Escape') close(); };
  const foot = actions.length
    ? h('div', { class: 'modal-foot' }, actions.map(a => a instanceof Node ? a
      : h('button', { class: 'btn ' + (a.kind || ''), type: 'button', disabled: a.disabled, onclick: () => a.onClick(close) }, a.icon && icon(a.icon), a.label)))
    : null;
  const dlg = h('div', { class: 'modal' + (wide ? ' wide' : ''), role: 'dialog', 'aria-modal': 'true', 'aria-label': title },
    h('div', { class: 'modal-head' }, h('h2', null, title),
      h('button', { class: 'btn ghost icon-only small', type: 'button', title: 'Закрыть', onclick: close }, icon('x'))),
    h('div', { class: 'modal-body' }, body), foot);
  back.append(dlg);
  back.addEventListener('mousedown', e => { if (e.target === back) close(); });
  document.addEventListener('keydown', onKey);
  document.body.append(back);
  return { close, dialog: dlg, foot };
}

function confirmDialog(text, okLabel = 'Да', danger = false) {
  return new Promise(resolve => {
    let done = false;
    const m = modal({
      title: 'Подтверждение',
      body: h('p', { style: { margin: '0' } }, text),
      onClose: () => { if (!done) resolve(false); },
      actions: [
        { label: 'Отмена', onClick: c => c() },
        { label: okLabel, kind: danger ? 'danger' : 'primary', onClick: c => { done = true; resolve(true); c(); } },
      ],
    });
    m.dialog.querySelector('.modal-foot .btn:last-child')?.focus();
  });
}

const errorBox = text => h('div', { class: 'notice err' }, icon('alert'), h('span', null, text));

function pageHead(title, sub, ...actions) {
  return h('div', { class: 'page-head' },
    h('div', null, h('h1', null, title), sub && h('div', { class: 'sub' }, sub)),
    actions.length ? h('div', { class: 'row wrap' }, actions) : null);
}

function panel(title, hint, ...body) {
  return h('section', { class: 'panel' },
    h('div', { class: 'panel-head' }, h('h2', null, title), hint && h('span', { class: 'hint' }, hint)),
    h('div', { class: 'panel-body' }, body));
}

/* =============== элементы форм =============== */

function field(label, control, hint) {
  return h('div', { class: 'field' }, h('span', { class: 'field-label' }, label), control, hint && h('span', { class: 'hint' }, hint));
}

function inText(obj, key, opts = {}) {
  const el = h('input', {
    type: 'text', class: 'input' + (opts.mono ? ' mono' : ''), value: obj[key] ?? '',
    placeholder: opts.placeholder || '', spellcheck: 'false', autocomplete: 'off',
  });
  el.addEventListener('input', () => {
    obj[key] = opts.nullable && el.value.trim() === '' ? null : el.value;
    markDirty();
    opts.onInput?.(el.value);
  });
  return el;
}

function inNumber(obj, key, opts = {}) {
  const scale = opts.scale || 1;
  const el = h('input', {
    type: 'number', class: 'input ' + (opts.cls || 'w-num'), min: opts.min, max: opts.max, step: opts.step || 1,
    value: obj[key] == null ? '' : +(obj[key] / scale).toFixed(2), placeholder: opts.placeholder || '',
  });
  el.addEventListener('input', () => {
    if (el.value === '') obj[key] = opts.nullable ? null : 0;
    else obj[key] = opts.round === false ? +el.value * scale : Math.round(+el.value * scale);
    markDirty();
    opts.onInput?.(obj[key]);
  });
  return el;
}

function inSwitch(obj, key, label, hint, onChange) {
  const box = h('input', { type: 'checkbox', checked: !!obj[key] });
  box.addEventListener('change', () => { obj[key] = box.checked; markDirty(); onChange?.(box.checked); });
  return h('label', { class: 'switch' }, box, h('span', { class: 'slider' }),
    label && h('span', { class: 'switch-text' }, h('span', { class: 'switch-label' }, label), hint && h('span', { class: 'hint' }, hint)));
}

function inLines(obj, key, opts = {}) {
  const el = h('textarea', {
    class: 'input' + (opts.mono !== false ? ' mono' : ''), rows: opts.rows || 3, placeholder: opts.placeholder || '', spellcheck: 'false',
  });
  el.value = (opts.get ? opts.get() : obj[key] || []).join('\n');
  el.addEventListener('input', () => {
    const lines = el.value.split('\n').map(s => s.trim()).filter(Boolean);
    if (opts.set) opts.set(lines); else obj[key] = lines;
    markDirty();
    opts.onInput?.(lines);
  });
  return el;
}

function inSelect(options, value, onChange, cls = 'w-auto') {
  const el = h('select', { class: 'input ' + cls }, options.map(([v, label]) => h('option', { value: v }, label)));
  el.value = value;
  el.addEventListener('change', () => onChange(el.value));
  return el;
}

function inSecret(obj, key, opts = {}) {
  const wrap = h('div', { class: 'secret' });
  const original = obj[key];
  const render = editing => {
    if (obj[key] === MASK && !editing) {
      fill(wrap, 
        h('span', { class: 'secret-set' }, icon('key', 'sm'), 'задан'),
        h('button', { class: 'btn small', type: 'button', onclick: () => render(true) }, 'Изменить'));
      return;
    }
    const inp = h('input', {
      type: 'password', class: 'input mono', value: obj[key] === MASK ? '' : (obj[key] ?? ''),
      placeholder: original === MASK ? 'новое значение' : (opts.placeholder || ''), autocomplete: 'new-password', spellcheck: 'false',
    });
    inp.addEventListener('input', () => { obj[key] = inp.value === '' && original === MASK ? MASK : inp.value; markDirty(); });
    const parts = [inp, h('button', {
      class: 'btn icon-only', type: 'button', title: 'Показать или скрыть',
      onclick: () => { inp.type = inp.type === 'password' ? 'text' : 'password'; },
    }, icon('eye'))];
    if (original === MASK) {
      parts.push(h('button', { class: 'btn ghost small', type: 'button', onclick: () => { obj[key] = MASK; markDirty(); render(false); } }, 'Не менять'));
    }
    fill(wrap, ...parts);
    if (editing) inp.focus();
  };
  render(false);
  return wrap;
}

/** Поле пути с кнопкой «Выбрать…» (дерево папок сервера). */
function inPath(obj, key, opts = {}) {
  const inp = inText(obj, key, { mono: true, placeholder: opts.placeholder, nullable: opts.nullable, onInput: opts.onInput });
  const btn = h('button', {
    class: 'btn', type: 'button', onclick: async () => {
      const picked = await openTreePicker({ mode: opts.mode || 'folder', title: opts.title || 'Выбор папки', initial: obj[key] });
      if (picked) { obj[key] = picked; inp.value = picked; markDirty(); opts.onInput?.(picked); }
    },
  }, icon(opts.mode === 'file' ? 'file' : 'folder'), 'Выбрать…');
  return h('div', { class: 'input-group' }, inp, btn);
}

function dayToggles(days, onChange) {
  const names = [[1, 'Пн'], [2, 'Вт'], [3, 'Ср'], [4, 'Чт'], [5, 'Пт'], [6, 'Сб'], [0, 'Вс']];
  return h('div', { class: 'days' }, names.map(([d, label]) => {
    const box = h('input', { type: 'checkbox', checked: days.includes(d) });
    box.addEventListener('change', () => {
      const next = box.checked ? [...days, d] : days.filter(x => x !== d);
      if (next.length === 0) { box.checked = true; return; }
      days.splice(0, days.length, ...next.sort());
      onChange(days);
    });
    return h('label', { class: 'chip-toggle' }, box, h('span', null, label));
  }));
}

function timeInput(hh, mm, onChange) {
  const el = h('input', { type: 'time', class: 'input w-time', value: `${pad2(hh)}:${pad2(mm)}` });
  el.addEventListener('change', () => {
    const m = /^(\d{1,2}):(\d{2})/.exec(el.value);
    if (m) onChange(+m[1], +m[2]);
  });
  return el;
}

function chip(text, onRemove, cls = '') {
  return h('span', { class: 'chip ' + cls, title: text }, h('span', null, text),
    onRemove && h('button', { class: 'x', type: 'button', title: 'Убрать', onclick: onRemove }, icon('x', 'sm')));
}

/* =============== настройки: загрузка и сохранение =============== */

async function ensureConfig(force = false) {
  if (S.cfg && !force) return;
  const r = await api('GET', '/api/config');
  S.cfg = r.config;
  S.cfgSaved = JSON.stringify(r.config);
  S.cfgMeta = r;
  S.saveErrors = [];
  updateSaveBar();
}

const isDirty = () => !!S.cfg && JSON.stringify(S.cfg) !== S.cfgSaved;

function markDirty() { updateSaveBar(); }

function updateSaveBar() {
  const bar = $('#savebar');
  if (!bar) return;
  const dirty = isDirty();
  bar.hidden = !dirty;
  if (!dirty) { fill(bar); return; }
  const saveBtn = h('button', { class: 'btn primary', type: 'button', onclick: () => saveConfig(saveBtn) }, icon('check'), 'Сохранить');
  fill(bar, h('div', { class: 'savebar-inner' },
    icon('info'), h('span', { class: 'grow' }, 'Есть несохранённые изменения'),
    h('button', { class: 'btn ghost', type: 'button', onclick: discardChanges }, 'Отменить'),
    saveBtn,
    S.saveErrors.length ? h('ul', { class: 'savebar-errors' }, S.saveErrors.map(e => h('li', null, e))) : null));
}

async function saveConfig(btn) {
  btn.disabled = true;
  try {
    const r = await api('PUT', '/api/config', S.cfg);
    await ensureConfig(true);
    S.cfgMeta.errors = r.errors;
    if (r.errors.length) toast(`Сохранено. В настройках ${r.errors.length} ошибок — задачи с ошибками не выполняются.`, 'warn', 7000);
    else toast('Настройки сохранены', 'ok');
    pollNow();
    rerender();
  } catch (e) {
    S.saveErrors = e.data?.errors || [e.message];
    updateSaveBar();
    if (e.status !== 401) toast('Не сохранено: ' + S.saveErrors[0], 'err', 7000);
  } finally {
    btn.disabled = false;
  }
}

async function discardChanges() {
  if (!await confirmDialog('Отменить все несохранённые изменения?', 'Отменить изменения', true)) return;
  S.cfg = JSON.parse(S.cfgSaved);
  S.saveErrors = [];
  updateSaveBar();
  rerender();
}

window.addEventListener('beforeunload', e => { if (isDirty()) e.preventDefault(); });

function configErrors() {
  const errs = S.cfgMeta?.errors || [];
  if (!errs.length) return null;
  return h('div', { class: 'notice err' },
    h('div', { class: 'notice-title' }, icon('alert'), 'Ошибки в сохранённых настройках — задачи с ошибками не выполняются'),
    h('ul', null, errs.map(e => h('li', null, e))));
}

/* =============== каркас, вход, маршруты =============== */

const NAV = [
  ['#/', 'dashboard', 'Сводка'],
  ['#/jobs', 'jobs', 'Задачи'],
  ['#/history', 'history', 'Журнал'],
  ['#/snapshots', 'snapshots', 'Снимки'],
  ['#/settings', 'settings', 'Настройки'],
];

const ROUTES = [
  [/^#?\/?$/, pageDashboard],
  [/^#\/jobs$/, pageJobs],
  [/^#\/jobs\/(\d+)$/, pageJob],
  [/^#\/history$/, pageHistory],
  [/^#\/snapshots$/, pageSnapshots],
  [/^#\/settings(?:\/(\w+))?$/, pageSettings],
];

function renderLogin(message) {
  clearTimeout(S.pollTimer);
  document.querySelectorAll('.modal-back').forEach(m => m.remove());
  const root = $('#root');
  root.className = 'login-wrap';
  const s = S.session || {};
  const err = h('div', { class: 'form-error', role: 'alert' });
  const pw = h('input', { type: 'password', class: 'input', autocomplete: 'current-password', id: 'login-password' });
  const btn = h('button', { class: 'btn primary wide', type: 'submit' }, 'Войти');
  const form = h('form', {
    class: 'login card', onsubmit: async e => {
      e.preventDefault();
      btn.disabled = true;
      err.textContent = '';
      try {
        await api('POST', '/api/login', { password: pw.value });
        S.session = await api('GET', '/api/session');
        startApp();
      } catch (ex) {
        err.textContent = ex.message;
        pw.select();
      } finally {
        btn.disabled = false;
      }
    },
  },
  h('div', { class: 'login-logo' }, logo(),
    h('div', null, h('div', { class: 'brand-name' }, 'MyBackup'), h('div', { class: 'muted small' }, s.serverName || ''))),
  s.passwordSet === false
    ? h('div', { class: 'notice warn' }, h('span', null,
      'Пароль веб-интерфейса ещё не задан. На сервере в PowerShell от имени администратора выполните:',
      h('code', { class: 'block' }, '& "C:\\Program Files\\MyBackup\\MyBackup.exe" web-password'),
      'и обновите эту страницу.'))
    : [h('label', { class: 'field', for: 'login-password' }, h('span', { class: 'field-label' }, 'Пароль'), pw), err, btn],
  message && s.passwordSet !== false ? h('div', { class: 'muted small' }, message) : null);
  fill(root, form);
  pw.focus();
}

function renderShell() {
  const root = $('#root');
  root.className = 'app';
  fill(root, 
    h('aside', { class: 'side' },
      h('a', { class: 'brand', href: '#/' }, logo(),
        h('div', null, h('div', { class: 'brand-name' }, 'MyBackup'), h('div', { class: 'brand-server', id: 'brand-server' }, S.session.serverName))),
      h('nav', { class: 'nav', 'aria-label': 'Разделы' }, NAV.map(([href, ic, label]) =>
        h('a', { href, class: 'nav-item', dataset: { href } }, icon(ic), h('span', null, label)))),
      h('div', { id: 'side-status' }),
      h('div', { class: 'side-foot' },
        h('span', { class: 'muted small' }, 'версия ' + S.session.version),
        h('button', { class: 'btn ghost small', type: 'button', title: 'Выйти', onclick: logout }, icon('logout'), h('span', { class: 'btn-text' }, 'Выйти')))),
    h('main', { class: 'main', id: 'main' }),
    h('div', { class: 'savebar', id: 'savebar', hidden: true }),
    h('div', { class: 'toasts', id: 'toasts', 'aria-live': 'polite' }));
  updateSaveBar();
}

async function logout() {
  if (isDirty() && !await confirmDialog('Есть несохранённые изменения. Выйти без сохранения?', 'Выйти', true)) return;
  try { await api('POST', '/api/logout'); } catch { /* всё равно выходим */ }
  S.cfg = null;
  S.status = null;
  S.session.authenticated = false;
  location.hash = '#/';
  renderLogin();
}

function startApp() {
  renderShell();
  route();
  pollNow();
}

async function route() {
  if (!S.session?.authenticated) return;
  const hash = location.hash || '#/';
  let page = null, args = [];
  for (const [re, fn] of ROUTES) {
    const m = re.exec(hash);
    if (m) { page = fn; args = m.slice(1); break; }
  }
  if (!page) { location.hash = '#/'; return; }
  document.querySelectorAll('.nav-item').forEach(a => {
    const href = a.dataset.href;
    a.classList.toggle('active', href === '#/' ? hash === '#/' || hash === '' || hash === '#' : hash.startsWith(href));
  });
  S.onStatus = null;
  S.page = { fn: page, args };
  const main = $('#main');
  fill(main, h('div', { class: 'loading' }, spinner(), 'Загрузка…'));
  window.scrollTo(0, 0);
  try {
    await page(main, ...args);
  } catch (e) {
    if (e.status !== 401) fill(main, h('div', { class: 'page' }, errorBox(e.message)));
  }
}

/** Перерисовать текущую страницу без смены адреса (после добавления/удаления элементов). */
async function rerender() {
  if (!S.page) return;
  const y = window.scrollY;
  try {
    await S.page.fn($('#main'), ...S.page.args);
  } catch (e) {
    if (e.status !== 401) toast(e.message, 'err');
  }
  window.scrollTo(0, y);
}

window.addEventListener('hashchange', route);

/* =============== опрос состояния службы =============== */

async function poll() {
  clearTimeout(S.pollTimer);
  if (!S.session?.authenticated) return;
  try {
    S.status = await api('GET', '/api/status');
    renderSideStatus();
    S.onStatus?.();
  } catch (e) {
    if (e.status === 401) return;
  }
  const fast = S.status?.current || S.page?.fn === pageDashboard;
  S.pollTimer = setTimeout(poll, fast ? 3000 : 15000);
}

const pollNow = () => { clearTimeout(S.pollTimer); S.pollTimer = setTimeout(poll, 50); };

function renderSideStatus() {
  const box = $('#side-status');
  if (!box) return;
  const c = S.status?.current;
  if (!c) { fill(box); box.className = ''; return; }
  const pct = c.percent != null ? Math.round(c.percent * 100) : null;
  box.className = 'side-status';
  fill(box, 
    h('div', { class: 'row' }, h('span', { class: 'dot run' }), h('b', { class: 'ellipsis' }, c.jobName), pct != null ? h('span', { class: 'right' }, pct + '%') : null),
    h('div', { class: 'muted ellipsis' }, c.unitTitle || 'подготовка…'),
    progressBar(pct));
}

function progressBar(pct) {
  return pct == null
    ? h('div', { class: 'bar indeterminate' }, h('span'))
    : h('div', { class: 'bar', role: 'progressbar', 'aria-valuenow': pct, 'aria-valuemin': 0, 'aria-valuemax': 100 },
      h('span', { style: { width: pct + '%' } }));
}

/* =============== расписание (cron) =============== */

function expandDays(s) {
  const out = new Set();
  for (const part of s.split(',')) {
    const r = /^(\d)(?:-(\d))?$/.exec(part);
    if (!r) return null;
    const a = +r[1], b = r[2] != null ? +r[2] : a;
    if (a > 7 || b > 7 || b < a) return null;
    for (let d = a; d <= b; d++) out.add(d % 7);
  }
  return [...out].sort();
}

function parseCron(expr) {
  const f = (expr || '').trim().split(/\s+/);
  if (f.length !== 5) return { mode: 'custom', expr: expr || '' };
  const [mi, hr, dom, mon, dow] = f;
  const num = s => /^\d{1,2}$/.test(s) ? +s : null;
  const m = num(mi);
  if (dom === '*' && mon === '*' && m != null && m < 60) {
    const hh = num(hr);
    if (hh != null && hh < 24) {
      if (dow === '*') return { mode: 'daily', h: hh, m };
      const days = expandDays(dow);
      if (days) return { mode: 'weekly', h: hh, m, days };
    }
    const step = /^\*\/(\d+)$/.exec(hr);
    if (dow === '*' && step) return { mode: 'hourly', every: +step[1], m };
    if (dow === '*' && hr === '*') return { mode: 'hourly', every: 1, m };
  }
  return { mode: 'custom', expr };
}

function buildCron(s) {
  switch (s.mode) {
    case 'daily': return `${s.m} ${s.h} * * *`;
    case 'weekly': {
      const days = [...s.days].sort();
      return `${s.m} ${s.h} * * ${days.join(',') === '1,2,3,4,5' ? '1-5' : days.join(',')}`;
    }
    case 'hourly': return s.every === 1 ? `${s.m} * * * *` : `${s.m} */${s.every} * * *`;
    default: return s.expr;
  }
}

function describeCron(expr) {
  const s = parseCron(expr);
  const at = s.h != null ? `в ${pad2(s.h)}:${pad2(s.m)}` : '';
  switch (s.mode) {
    case 'daily': return `ежедневно ${at}`;
    case 'weekly': {
      const key = s.days.join(',');
      if (key === '1,2,3,4,5') return `по будням ${at}`;
      if (key === '0,6') return `по выходным ${at}`;
      const names = ['Вс', 'Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб'];
      const order = [1, 2, 3, 4, 5, 6, 0].filter(d => s.days.includes(d));
      return `${order.map(d => names[d]).join(', ')} ${at}`;
    }
    case 'hourly': return s.every === 1 ? `каждый час в :${pad2(s.m)}` : `каждые ${s.every} ч${s.m ? ` (в :${pad2(s.m)})` : ''}`;
    default: return `cron: ${expr}`;
  }
}

function scheduleEditor(job) {
  let st = parseCron(job.schedule);
  const preview = h('div', { class: 'hint' });
  const box = h('div', { class: 'schedule' });
  const refresh = debounce(async () => {
    try {
      const r = await api('GET', '/api/cron?expr=' + encodeURIComponent(job.schedule));
      fill(preview, r.valid
        ? (r.next.length ? 'Ближайшие запуски: ' + r.next.slice(0, 3).map(fmtDateTime).join(' · ') : 'Запусков по этому расписанию не будет')
        : h('span', { class: 'text-err' }, 'Ошибка в расписании: ' + r.error));
    } catch { /* не критично */ }
  }, 300);
  const update = () => { job.schedule = buildCron(st); markDirty(); refresh(); };
  const render = () => {
    const mode = inSelect([['daily', 'Каждый день'], ['weekly', 'По дням недели'], ['hourly', 'Каждые несколько часов'], ['custom', 'Своё (cron)']], st.mode, v => {
      const hh = st.h ?? 1, mm = st.m ?? 0;
      st = v === 'daily' ? { mode: v, h: hh, m: mm }
        : v === 'weekly' ? { mode: v, h: hh, m: mm, days: [1, 2, 3, 4, 5] }
          : v === 'hourly' ? { mode: v, every: 4, m: 0 }
            : { mode: v, expr: job.schedule };
      render();
      update();
    });
    const parts = [mode];
    if (st.mode === 'daily' || st.mode === 'weekly') {
      parts.push(h('span', { class: 'muted' }, 'в'), timeInput(st.h, st.m, (hh, mm) => { st.h = hh; st.m = mm; update(); }));
    } else if (st.mode === 'hourly') {
      parts.push(inSelect([1, 2, 3, 4, 6, 8, 12].map(n => [String(n), n === 1 ? 'каждый час' : `каждые ${n} ч`]), String(st.every), v => { st.every = +v; update(); }));
      const minute = { v: st.m };
      parts.push(h('span', { class: 'muted' }, 'минута'), inNumber(minute, 'v', { min: 0, max: 59, onInput: v => { st.m = Math.min(59, Math.max(0, v)); update(); } }));
    } else {
      const holder = { v: st.expr };
      const inp = inText(holder, 'v', { mono: true, placeholder: '0 3 * * *', onInput: v => { st.expr = v.trim(); update(); } });
      inp.classList.add('w-auto');
      parts.push(inp, h('span', { class: 'hint' }, 'минута час день месяц день_недели'));
    }
    const rows = [h('div', { class: 'row wrap' }, parts)];
    if (st.mode === 'weekly') rows.push(dayToggles(st.days, () => update()));
    fill(box, ...rows, preview);
  };
  render();
  refresh();
  return box;
}

/* =============== сводка =============== */

async function pageDashboard(main) {
  if (!S.status) S.status = await api('GET', '/api/status');
  const render = () => fill(main, dashboardView(S.status));
  render();
  S.onStatus = render;
  pollNow();
}

const levelClass = l => l === 'error' ? 'err' : l === 'warn' ? 'warn' : 'info';

function dashboardView(st) {
  return h('div', { class: 'page' },
    pageHead('Сводка', `${st.serverName} · служба запущена ${fmtDateTime(st.startedAt)}`),
    st.configErrors.length
      ? h('div', { class: 'notice err' },
        h('div', { class: 'notice-title' }, icon('alert'), 'Ошибки в настройках'),
        h('ul', null, st.configErrors.map(e => h('li', null, e))),
        h('a', { href: '#/jobs', class: 'link' }, 'Исправить в настройках →'))
      : null,
    st.warnings.length
      ? h('div', { class: 'warnings' }, st.warnings.map(w => h('div', { class: 'notice ' + levelClass(w.level) },
        icon(w.level === 'info' ? 'info' : 'alert'), h('span', null, w.text), w.link && h('a', { href: w.link, class: 'link' }, 'Открыть'))))
      : null,
    currentCard(st),
    h('h2', { class: 'section-title' }, 'Задачи', h('a', { href: '#/jobs', class: 'small' }, 'изменить →')),
    st.jobs.length
      ? h('div', { class: 'grid jobs-grid' }, st.jobs.map((j, i) => jobCard(j, i, st)))
      : h('div', { class: 'card empty' }, h('p', null, 'Задач пока нет.'), h('a', { class: 'btn primary', href: '#/jobs' }, icon('plus'), 'Создать задачу')),
    h('h2', { class: 'section-title' }, 'Последние запуски', h('a', { href: '#/history', class: 'small' }, 'весь журнал →')),
    st.recent.length ? runsTable(st.recent) : h('div', { class: 'card empty small' }, 'Запусков ещё не было.'));
}

function currentCard(st) {
  const c = st.current;
  if (!c) {
    const next = st.jobs.filter(j => j.enabled && j.nextRun).sort((a, b) => new Date(a.nextRun) - new Date(b.nextRun))[0];
    return h('div', { class: 'card current idle' },
      h('div', { class: 'current-icon' }, icon('pause')),
      h('div', { class: 'grow' },
        h('div', { class: 'current-title' }, 'Сейчас ничего не выполняется'),
        h('div', { class: 'muted' }, next ? `Следующий запуск: ${next.name} — ${fmtDateTime(next.nextRun)} (${fmtRel(next.nextRun)})` : 'Запусков по расписанию нет'),
        st.waiting.length ? h('div', { class: 'muted small' }, 'В очереди: ' + st.waiting.join(', ')) : null));
  }
  const pct = c.percent != null ? Math.round(c.percent * 100) : null;
  const details = pct == null ? ['подготовка…'] : [
    `${pct}%`,
    c.totalBytes ? `${fmtSize(c.bytesDone)} из ${fmtSize(c.totalBytes)}` : null,
    c.totalFiles ? `файлов ${c.filesDone} из ${c.totalFiles}` : null,
    c.secondsRemaining > 0 ? `осталось ~${fmtDur(c.secondsRemaining)}` : null,
  ].filter(Boolean);
  return h('div', { class: 'card current' },
    h('div', { class: 'current-head' }, h('span', { class: 'pulse' }),
      h('div', { class: 'grow' },
        h('div', { class: 'current-title' }, `Выполняется: ${c.jobName}`),
        h('div', { class: 'muted small' }, `${c.trigger} · начато ${fmtTime(c.startedAt)} · идёт ${fmtDur((Date.now() - new Date(c.startedAt)) / 1000)}`)),
      c.cancelRequested
        ? h('span', { class: 'badge warn' }, 'останавливается…')
        : h('button', { class: 'btn danger-outline', type: 'button', onclick: cancelCurrent }, icon('stop'), 'Остановить')),
    c.unitTitle
      ? h('div', { class: 'unit' },
        h('div', { class: 'unit-line' }, h('b', null, c.unitTitle), h('span', { class: 'muted' }, `${c.unitIndex} из ${c.unitTotal}`),
          c.attempt > 1 ? h('span', { class: 'badge warn' }, `попытка ${c.attempt} — после обрыва`) : null),
        progressBar(pct),
        h('div', { class: 'muted small' }, details.join(' · ')))
      : h('div', { class: 'muted' }, 'Подготовка списка источников…'),
    st.waiting.length ? h('div', { class: 'muted small' }, 'В очереди: ' + st.waiting.join(', ')) : null);
}

function jobCard(j, index, st) {
  const running = st.current?.jobName === j.name;
  const queued = st.waiting.includes(j.name);
  const last = j.last;
  const stale = j.lastSuccess && (Date.now() - new Date(j.lastSuccess)) / 3.6e6 > 48;
  return h('div', { class: 'card job-card' },
    h('div', { class: 'job-card-head' },
      h('span', { class: 'dot ' + (running ? 'run' : !last ? '' : STATUS[last.status]?.cls || '') }),
      h('span', { class: 'job-name grow' }, j.name),
      j.enabled ? null : h('span', { class: 'badge' }, 'отключена')),
    h('div', { class: 'kv' },
      h('span', null, 'Расписание'), h('span', null, describeCron(j.schedule)),
      h('span', null, 'Следующий'), h('span', null, j.enabled && j.nextRun ? `${fmtDateTime(j.nextRun)} (${fmtRel(j.nextRun)})` : '—'),
      h('span', null, 'Последний'), h('span', null, last ? [statusBadge(last.status), ' ', fmtDateTime(last.started), ' · ', fmtDur(runSeconds(last))] : 'ещё не запускалась'),
      last ? [h('span', null, 'Передано'), h('span', null, fmtSize(last.uploaded) + (last.retries ? ` · повторов после обрывов: ${last.retries}` : ''))] : null,
      h('span', null, 'Успешная копия'), h('span', { class: stale ? 'text-err' : '' }, j.lastSuccess ? `${fmtDateTime(j.lastSuccess)} (${fmtRel(j.lastSuccess)})` : '—')),
    j.blockingErrors.length
      ? h('div', { class: 'notice err small' }, icon('alert', 'sm'),
        h('span', null, `Не будет выполнена: ${j.blockingErrors[0]}${j.blockingErrors.length > 1 ? ` (и ещё ${j.blockingErrors.length - 1})` : ''}`))
      : null,
    h('div', { class: 'card-actions' },
      h('button', {
        class: 'btn primary', type: 'button', disabled: running || queued || j.blockingErrors.length > 0, onclick: () => runJob(j.name),
      }, icon('play'), running ? 'Выполняется' : queued ? 'В очереди' : 'Запустить сейчас'),
      h('a', { class: 'btn', href: '#/jobs/' + index }, 'Изменить')));
}

async function runJob(name) {
  try {
    await api('POST', `/api/jobs/${encodeURIComponent(name)}/run`);
    toast(`Задача «${name}» поставлена в очередь`, 'ok');
    pollNow();
  } catch (e) {
    toast(e.message + (e.data?.errors ? ': ' + e.data.errors.join('; ') : ''), 'err', 8000);
  }
}

async function cancelCurrent() {
  const ok = await confirmDialog(
    'Остановить выполняемую задачу? Уже переданные данные не пропадут: следующий запуск продолжит с того же места.',
    'Остановить', true);
  if (!ok) return;
  try {
    await api('POST', '/api/current/cancel');
    toast('Задача останавливается', 'warn');
    pollNow();
  } catch (e) {
    toast(e.message, 'err');
  }
}

function runsTable(runs, opts = {}) {
  const rows = [];
  for (const r of runs) {
    const tr = h('tr', { class: 'clickable' },
      h('td', { class: 'nowrap' }, fmtDateTime(r.started)),
      h('td', null, r.jobName),
      h('td', null, statusBadge(r.status)),
      h('td', { class: 'num' }, fmtDur(runSeconds(r))),
      h('td', { class: 'num' }, fmtSize(r.uploaded ?? r.totalUploaded)),
      h('td', { class: 'num' }, String(r.retries ?? r.totalRetries ?? 0)),
      h('td', { class: 'muted' }, r.trigger));
    rows.push(tr);
    if (opts.details) {
      const det = h('tr', { class: 'details', hidden: true }, h('td', { colspan: 7 }, runDetails(r)));
      tr.addEventListener('click', () => { det.hidden = !det.hidden; });
      rows.push(det);
    } else {
      tr.addEventListener('click', () => { location.hash = '#/history'; });
    }
  }
  return h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
    h('thead', null, h('tr', null,
      h('th', null, 'Начало'), h('th', null, 'Задача'), h('th', null, 'Итог'), h('th', { class: 'num' }, 'Длительность'),
      h('th', { class: 'num' }, 'Передано'), h('th', { class: 'num' }, 'Повторы'), h('th', null, 'Запуск'))),
    h('tbody', null, rows)));
}

function runDetails(r) {
  return h('div', { class: 'sub-table' },
    r.notes?.length ? h('div', { class: 'notice warn small' }, icon('info', 'sm'), h('span', null, r.notes.join(' · '))) : null,
    h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
      h('thead', null, h('tr', null, h('th', null, 'Что'), h('th', null, 'Итог'), h('th', { class: 'num' }, 'Объём'),
        h('th', { class: 'num' }, 'Передано'), h('th', { class: 'num' }, 'Попыток'), h('th', { class: 'num' }, 'Время'), h('th', null, 'Подробности'))),
      h('tbody', null, (r.units || []).map(u => h('tr', null,
        h('td', null, u.title),
        h('td', null, statusBadge(u.status)),
        h('td', { class: 'num' }, u.bytesProcessed ? fmtSize(u.bytesProcessed) : '—'),
        h('td', { class: 'num' }, u.dataAddedPacked ? fmtSize(u.dataAddedPacked) : '—'),
        h('td', { class: 'num' }, String(u.attempts)),
        h('td', { class: 'num' }, fmtDur(tsToSec(u.duration))),
        h('td', null, u.message ? h('div', null, u.message) : null,
          (u.notes || []).map(n => h('div', { class: 'muted small' }, n)),
          u.snapshotId ? h('div', { class: 'faint small mono' }, 'снимок ' + u.snapshotId.slice(0, 8)) : null)))))));
}

/* =============== задачи =============== */

const SOURCE_TYPES = {
  fileBase1C: {
    label: 'Файловая база 1С', icon: 'base1c', desc: 'Папка с 1Cv8.1CD. Копия через снимок VSS — база не останавливается.',
    create: () => ({ type: 'fileBase1C', name: '', enabled: true, path: '', excludeFullTextIndex: true, excludeEventLog: false }),
  },
  msSql: {
    label: 'Базы MS SQL', icon: 'db', desc: 'BACKUP DATABASE по одной базе, проверка, отправка, удаление файла.',
    create: () => ({
      type: 'msSql', name: '', enabled: true, connectionString: 'Server=.;Integrated Security=true;TrustServerCertificate=true',
      databases: ['*'], excludeDatabases: [], verify: true, reserveFreeSpaceMiB: 1024,
    }),
  },
  postgreSql: {
    label: 'Базы PostgreSQL', icon: 'db', desc: 'pg_dump потоком прямо в хранилище, на диск сервера ничего не пишется.',
    create: () => ({
      type: 'postgreSql', name: '', enabled: true, pgDumpPath: '', host: 'localhost', port: 5432, user: 'postgres', password: '',
      databases: ['*'], excludeDatabases: ['postgres'],
    }),
  },
  userProfiles: {
    label: 'Профили пользователей', icon: 'users', desc: 'Рабочий стол, Документы и другие папки всех пользователей, кроме системных.',
    create: () => ({
      type: 'userProfiles', name: '', enabled: true, folders: ['Desktop', 'Documents', 'Downloads', 'Pictures', 'Favorites'],
      excludeUsers: [], exclude: ['~$*', '*.tmp', 'Thumbs.db', 'desktop.ini'],
    }),
  },
  paths: {
    label: 'Папки и файлы', icon: 'folder', desc: 'Выбор папок и файлов в дереве, исключения по маске.',
    create: () => ({ type: 'paths', name: '', enabled: true, paths: [], exclude: [], useVss: true }),
  },
  oneCService: {
    label: 'Служебные файлы 1С', icon: 'shield', desc: 'Лицензии, настройки кластера сервера 1С, списки баз пользователей.',
    create: () => ({ type: 'oneCService', name: '', enabled: true, srvInfoPaths: [], includeLicenses: true, includeUserBaseLists: true }),
  },
  oneCDump: {
    label: 'Выгрузка 1С (.dt)', icon: 'export', desc: 'Выгрузка информационной базы через Конфигуратор. Нужна установленная платформа 1С.',
    create: () => ({ type: 'oneCDump', name: '', enabled: true, filePath: '', user: '', password: '', timeout: '03:00:00' }),
  },
};

function effectiveName(s) {
  if (s.name) return s.name;
  switch (s.type) {
    case 'fileBase1C': return baseName(s.path) || 'База 1С';
    case 'userProfiles': return 'Профили пользователей';
    case 'paths': return 'Папки';
    case 'oneCService': return 'Служебные файлы 1С';
    case 'msSql': return 'MS SQL';
    case 'postgreSql': return 'PostgreSQL';
    case 'oneCDump':
      if (s.filePath) return baseName(s.filePath) + '.dt';
      if (s.serverBase) return s.serverBase.trim().split(/[\\/]/).pop() + '.dt';
      return 'Выгрузка 1С';
    default: return s.type;
  }
}

const FOLDERS = [['Desktop', 'Рабочий стол'], ['Documents', 'Документы'], ['Downloads', 'Загрузки'], ['Pictures', 'Изображения'],
  ['Music', 'Музыка'], ['Videos', 'Видео'], ['Favorites', 'Избранное']];
const KNOWN_FOLDERS = new Set(FOLDERS.map(f => f[0].toLowerCase()));

function dbSummary(list, exclude) {
  if (!list?.length) return 'базы не выбраны';
  if (list.includes('*')) return 'все базы' + (exclude?.length ? `, кроме: ${exclude.join(', ')}` : '');
  return list.join(', ');
}

function sourceSummary(s) {
  switch (s.type) {
    case 'fileBase1C': return s.path || 'папка не указана';
    case 'msSql': return dbSummary(s.databases, s.excludeDatabases);
    case 'postgreSql': return dbSummary(s.databases, s.excludeDatabases);
    case 'userProfiles': {
      const names = s.folders.map(f => FOLDERS.find(x => x[0].toLowerCase() === f.toLowerCase())?.[1] || f);
      return names.join(', ') + (s.excludeUsers.length ? ` · без: ${s.excludeUsers.join(', ')}` : '');
    }
    case 'paths': {
      const ex = s.exclude.filter(isAbsPath).length;
      return s.paths.length ? `${s.paths.slice(0, 2).join(', ')}${s.paths.length > 2 ? ` и ещё ${s.paths.length - 2}` : ''}${ex ? ` · исключено путей: ${ex}` : ''}` : 'пути не выбраны';
    }
    case 'oneCService': return [s.includeLicenses && 'лицензии', 'кластер 1С', s.includeUserBaseLists && 'списки баз'].filter(Boolean).join(', ');
    case 'oneCDump': return s.filePath || s.serverBase || 'база не указана';
    default: return '';
  }
}

function newJob() {
  const names = new Set(S.cfg.jobs.map(j => j.name.toLowerCase()));
  let name = 'nightly', n = 2;
  while (names.has(name.toLowerCase())) name = `job${n++}`;
  return {
    name, enabled: true, schedule: '0 1 * * *', maxDuration: '06:00:00', sources: [],
    retention: { applyFromClient: false, keepDaily: 7, keepWeekly: 4, keepMonthly: 12, prune: false },
  };
}

async function pageJobs(main) {
  await ensureConfig();
  const st = S.status;
  const jobs = S.cfg.jobs;
  const list = jobs.length
    ? h('div', { class: 'grid jobs-grid' }, jobs.map((j, i) => {
      const live = st?.jobs.find(x => x.name === j.name);
      return h('div', { class: 'card job-card' },
        h('div', { class: 'job-card-head' }, h('span', { class: 'job-name grow' }, j.name || 'Без имени'), inSwitch(j, 'enabled', null, null, () => rerender())),
        h('div', { class: 'kv' },
          h('span', null, 'Расписание'), h('span', null, describeCron(j.schedule)),
          h('span', null, 'Следующий'), h('span', null, live?.nextRun && j.enabled ? fmtDateTime(live.nextRun) : '—'),
          h('span', null, 'Источники'), h('span', null, j.sources.length ? j.sources.map(effectiveName).join(', ') : h('span', { class: 'text-err' }, 'не добавлены'))),
        h('div', { class: 'card-actions' },
          h('a', { class: 'btn primary', href: '#/jobs/' + i }, 'Открыть'),
          h('button', {
            class: 'btn ghost', type: 'button', onclick: async () => {
              if (!await confirmDialog(`Удалить задачу «${j.name}»? Уже сделанные копии в хранилище останутся.`, 'Удалить', true)) return;
              jobs.splice(i, 1);
              markDirty();
              rerender();
            },
          }, icon('trash'), 'Удалить')));
    }))
    : h('div', { class: 'card empty' }, 'Задач пока нет. Задача — это расписание и список того, что копировать.');

  fill(main, h('div', { class: 'page' },
    pageHead('Задачи', 'Что копировать и когда. Изменения применяются после сохранения, без перезапуска службы.',
      h('button', {
        class: 'btn primary', type: 'button', onclick: () => {
          jobs.push(newJob());
          markDirty();
          location.hash = '#/jobs/' + (jobs.length - 1);
        },
      }, icon('plus'), 'Новая задача')),
    configErrors(),
    list));
}

const expanded = new WeakSet();

async function pageJob(main, idxText) {
  await ensureConfig();
  const idx = +idxText;
  const job = S.cfg.jobs[idx];
  if (!job) {
    fill(main, h('div', { class: 'page' }, errorBox('Задача не найдена'), h('a', { href: '#/jobs' }, '← к списку задач')));
    return;
  }
  const savedJob = JSON.parse(S.cfgSaved).jobs?.[idx];
  const title = h('h1', null, job.name || 'Без имени');
  const dirtyNow = isDirty();

  const runBtn = h('button', {
    class: 'btn', type: 'button', disabled: !savedJob || dirtyNow, title: dirtyNow ? 'Сначала сохраните изменения' : '',
    onclick: () => runJob(savedJob.name),
  }, icon('play'), 'Запустить сейчас');

  fill(main, h('div', { class: 'page' },
    h('a', { href: '#/jobs', class: 'back' }, '← Задачи'),
    h('div', { class: 'page-head' }, h('div', null, title, h('div', { class: 'sub' }, describeCron(job.schedule))), h('div', { class: 'row' }, runBtn)),
    configErrors(),
    panel('Основное', null,
      h('div', { class: 'form-grid' },
        field('Название задачи', inText(job, 'name', { onInput: v => { title.textContent = v || 'Без имени'; } }), 'Используется в отчётах и в команде /run в Telegram'),
        field('Сколько времени можно докачивать', hoursInput(job, 'maxDuration'),
          'При обрывах связи задача повторяет попытки до истечения этого времени с момента старта')),
      inSwitch(job, 'enabled', 'Запускать по расписанию', 'Выключенную задачу можно запустить вручную'),
      field('Расписание', scheduleEditor(job))),
    h('section', { class: 'panel' },
      h('div', { class: 'panel-head' }, h('h2', { class: 'grow' }, 'Что копировать'),
        h('button', { class: 'btn primary small', type: 'button', onclick: () => addSource(job) }, icon('plus'), 'Добавить')),
      h('div', { class: 'panel-body' },
        job.sources.length
          ? h('div', { class: 'sources' }, job.sources.map((s, i) => sourceCard(job, s, i)))
          : h('div', { class: 'empty small' }, 'Источники ещё не добавлены — нажмите «Добавить».'))),
    panel('Хранение копий', null, retentionForm(job.retention))));
}

function hoursInput(obj, key) {
  const holder = { v: tsToSec(obj[key]) / 3600 };
  const el = inNumber(holder, 'v', {
    min: 0.5, max: 168, step: 0.5, round: false, onInput: v => { obj[key] = secToTs(v * 3600); markDirty(); },
  });
  return h('div', { class: 'row' }, el, h('span', { class: 'muted' }, 'ч'));
}

function addSource(job) {
  const m = modal({
    title: 'Что добавить в копию?', wide: true,
    body: h('div', { class: 'type-grid' }, Object.entries(SOURCE_TYPES).map(([type, t]) =>
      h('button', {
        class: 'type-option', type: 'button', onclick: () => {
          const s = t.create();
          job.sources.push(s);
          expanded.add(s);
          markDirty();
          m.close();
          rerender();
        },
      }, h('div', { class: 'source-icon t-' + type }, icon(t.icon)), h('div', null, h('b', null, t.label), h('span', { class: 'muted small' }, t.desc))))),
  });
}

function sourceCard(job, s, i) {
  const t = SOURCE_TYPES[s.type] || { label: s.type, icon: 'file' };
  const open = expanded.has(s);
  const titleEl = h('span', null, effectiveName(s));
  const sumEl = h('div', { class: 'source-sum muted small ellipsis' }, sourceSummary(s));
  const refreshHead = () => { titleEl.textContent = effectiveName(s); sumEl.textContent = sourceSummary(s); };
  const move = d => { job.sources.splice(i + d, 0, job.sources.splice(i, 1)[0]); markDirty(); rerender(); };
  const head = h('div', {
    class: 'source-head', onclick: e => {
      if (e.target.closest('button, input, label, a')) return;
      if (open) expanded.delete(s); else expanded.add(s);
      rerender();
    },
  },
  h('div', { class: 'source-icon t-' + s.type }, icon(t.icon)),
  h('div', { class: 'grow min0' }, h('div', { class: 'source-title' }, titleEl,
    effectiveName(s) !== t.label ? h('span', { class: 'source-type' }, t.label) : null), sumEl),
  inSwitch(s, 'enabled', null, null, () => rerender()),
  h('button', { class: 'btn ghost small icon-only', type: 'button', title: 'Выше', disabled: i === 0, onclick: () => move(-1) }, icon('up')),
  h('button', { class: 'btn ghost small icon-only', type: 'button', title: 'Ниже', disabled: i === job.sources.length - 1, onclick: () => move(1) }, icon('down')),
  h('button', {
    class: 'btn ghost small icon-only', type: 'button', title: 'Удалить', onclick: async () => {
      if (!await confirmDialog(`Удалить «${effectiveName(s)}» из задачи? Уже сделанные копии в хранилище останутся.`, 'Удалить', true)) return;
      job.sources.splice(i, 1);
      markDirty();
      rerender();
    },
  }, icon('trash')),
  h('span', { class: 'chev' + (open ? ' open' : '') }, icon('down')));
  return h('div', { class: 'source card' + (s.enabled ? '' : ' disabled') },
    head, open ? h('div', { class: 'source-body' }, sourceForm(job, s, refreshHead)) : null);
}

function sourceForm(job, s, refreshHead) {
  const name = (placeholder, hint) => field('Название', inText(s, 'name', { placeholder, onInput: refreshHead }), hint);
  switch (s.type) {
    case 'fileBase1C': return formFileBase(job, s, name, refreshHead);
    case 'msSql': return formMsSql(s, name, refreshHead);
    case 'postgreSql': return formPostgres(s, name, refreshHead);
    case 'userProfiles': return formProfiles(s, name, refreshHead);
    case 'paths': return formPaths(s, name, refreshHead);
    case 'oneCService': return formOneC(s, name, refreshHead);
    case 'oneCDump': return formOneCDump(s, name, refreshHead);
    default: return h('div', { class: 'muted' }, 'Неизвестный тип источника');
  }
}

function formFileBase(job, s, name, refreshHead) {
  return [
    h('div', { class: 'form-grid' },
      field('Папка базы', inPath(s, 'path', { mode: 'folder', title: 'Папка базы 1С', onInput: refreshHead }), 'Папка, в которой лежит файл 1Cv8.1CD'),
      name(baseName(s.path) || 'по имени папки', 'Имя в отчётах и в списке снимков')),
    h('div', { class: 'row wrap' },
      h('button', { class: 'btn small', type: 'button', onclick: () => discover1C(job) }, icon('search'), 'Найти базы 1С на сервере')),
    inSwitch(s, 'excludeFullTextIndex', 'Не копировать полнотекстовый индекс', '1С перестроит его сама; без индекса копия меньше'),
    inSwitch(s, 'excludeEventLog', 'Не копировать журнал регистрации', 'Журнал бывает в несколько гигабайт; включите, если при восстановлении он не нужен'),
  ];
}

/* ----- MS SQL ----- */

const sqlInfo = new WeakMap();

function formMsSql(s, name, refreshHead) {
  const result = h('div');
  const showInfo = () => {
    const info = sqlInfo.get(s);
    if (!info) { fill(result); return; }
    if (info.error) { fill(result, errorBox(info.error)); return; }
    const denied = info.databases.filter(d => !d.canBackup);
    fill(result, h('div', { class: 'stack tight' },
      h('div', { class: 'notice ok small' }, icon('check', 'sm'), h('span', null, `Подключено как ${info.login} · SQL Server ${info.version} · баз: ${info.databases.length}`)),
      denied.length
        ? h('div', { class: 'notice warn small' }, icon('alert', 'sm'), h('span', null,
          `У учётной записи ${info.login} нет права BACKUP на базы: ${denied.map(d => d.name).join(', ')}. Проще всего выполнить в SQL Server Management Studio:`,
          h('code', { class: 'block' }, `ALTER SERVER ROLE [sysadmin] ADD MEMBER [${info.login}];`)))
        : null));
  };
  const check = h('button', {
    class: 'btn small', type: 'button', onclick: async () => {
      check.disabled = true;
      fill(result, h('div', { class: 'row muted' }, spinner(), 'Подключение…'));
      try {
        sqlInfo.set(s, await api('POST', '/api/discover/mssql', { connectionString: s.connectionString }));
      } catch (e) {
        sqlInfo.set(s, { error: e.message });
      }
      check.disabled = false;
      showInfo();
      fill(dbBox, dbPicker());
    },
  }, icon('refresh'), 'Проверить подключение');

  const dbBox = h('div');
  const dbPicker = () => databasesPicker(s, sqlInfo.get(s)?.databases, refreshHead);
  dbBox.append(dbPicker());
  showInfo();

  return [
    field('Строка подключения', inText(s, 'connectionString', { mono: true }),
      'Служба работает от имени SYSTEM: при Integrated Security этой учётной записи нужны права на BACKUP — кнопка ниже проверит'),
    h('div', null, check), result,
    field('Какие базы', dbBox),
    h('div', { class: 'form-grid' }, name('', 'Добавляется к имени базы: «Название/База». Можно оставить пустым')),
    h('details', { class: 'more' }, h('summary', null, icon('right', 'sm'), 'Дополнительно'),
      h('div', { class: 'stack' },
        field('Папка для временной копии .bak', inPath(s, 'backupDirectory', { nullable: true, placeholder: 'по умолчанию — папка резервных копий SQL Server' }),
          'Нужно место под самую большую базу; файл удаляется сразу после отправки'),
        h('div', { class: 'form-grid' },
          field('Оставлять свободным, МиБ', inNumber(s, 'reserveFreeSpaceMiB', { min: 0, step: 256 }), 'Если места не хватит, база пропускается с ошибкой, а не забивает диск')),
        inSwitch(s, 'verify', 'Проверять копию (RESTORE VERIFYONLY)', 'Читает файл копии целиком; занимает время, но ловит повреждения до отправки'))),
  ];
}

/** Выбор баз: «все, кроме» или «только выбранные»; с галочками, если список получен с сервера. */
function databasesPicker(s, known, onChange) {
  const all = s.databases.includes('*');
  const box = h('div', { class: 'stack tight' });
  const setMode = v => {
    s.databases = v === 'all' ? ['*'] : (known ? known.map(d => d.name).filter(n => !s.excludeDatabases.includes(n)) : []);
    markDirty();
    onChange?.();
    box.replaceWith(databasesPicker(s, known, onChange));
  };
  const mode = inSelect([['all', 'Все пользовательские базы'], ['list', 'Только выбранные']], all ? 'all' : 'list', setMode);
  box.append(mode);
  if (known?.length) {
    box.append(h('div', { class: 'chips' }, known.map(d => {
      const on = all ? !s.excludeDatabases.includes(d.name) : s.databases.includes(d.name);
      const cb = h('input', { type: 'checkbox', checked: on });
      cb.addEventListener('change', () => {
        if (all) s.excludeDatabases = cb.checked ? s.excludeDatabases.filter(n => n !== d.name) : [...s.excludeDatabases, d.name];
        else s.databases = cb.checked ? [...s.databases, d.name] : s.databases.filter(n => n !== d.name);
        markDirty();
        onChange?.();
      });
      return h('label', { class: 'chip-toggle', title: `${d.recoveryModel} · ${d.state}` }, cb,
        h('span', null, d.canBackup ? null : icon('alert', 'sm'), d.name, h('span', { class: 'faint' }, d.sizeBytes != null ? ' · ' + fmtSize(d.sizeBytes) : '')));
    })));
    box.append(h('span', { class: 'hint' }, all ? 'Снимите галочки с баз, которые не нужно копировать. Новые базы попадут в копию автоматически.' : 'Отмеченные базы.'));
  } else if (all) {
    box.append(field('Кроме баз', inLines(s, 'excludeDatabases', { rows: 2, placeholder: 'по одной в строке', onInput: onChange })));
  } else {
    box.append(field('Базы', inLines(s, 'databases', { rows: 3, placeholder: 'имя базы — по одной в строке', onInput: onChange })));
  }
  return box;
}

/* ----- PostgreSQL ----- */

function formPostgres(s, name, refreshHead) {
  return [
    field('pg_dump.exe', inPath(s, 'pgDumpPath', { mode: 'file', title: 'Где лежит pg_dump.exe', placeholder: 'C:\\Program Files\\PostgreSQL\\16\\bin\\pg_dump.exe' }),
      'Для варианта «все базы» рядом должен лежать psql.exe'),
    h('div', { class: 'form-grid narrow' },
      field('Сервер', inText(s, 'host')),
      field('Порт', inNumber(s, 'port', { min: 1, max: 65535, cls: 'w-auto' })),
      field('Пользователь', inText(s, 'user')),
      field('Пароль', inSecret(s, 'password'))),
    field('Какие базы', databasesPicker(s, null, refreshHead)),
    h('div', { class: 'form-grid' }, name('', 'Добавляется к имени базы: «Название/База»')),
  ];
}

/* ----- профили ----- */

const profileInfo = new WeakMap();

function formProfiles(s, name, refreshHead) {
  const preview = h('div', { class: 'stack tight' });
  const users = h('div');
  const matches = (excl, userName) => {
    const short = userName.split('.')[0].toLowerCase();
    return excl.toLowerCase() === userName.toLowerCase() || excl.toLowerCase() === short;
  };

  const renderPreview = () => {
    const info = profileInfo.get(s);
    if (!info) { fill(preview, h('div', { class: 'row muted small' }, spinner(), 'Поиск профилей…')); return; }
    if (info.error) { fill(preview, errorBox(info.error)); return; }
    fill(preview, 
      h('div', { class: 'notice info small' }, icon('info', 'sm'),
        h('span', null, `Будет скопировано: пользователей ${info.users}, папок ${info.paths.length}`)),
      info.notes.length ? h('div', { class: 'notice warn small' }, icon('alert', 'sm'), h('span', null, info.notes.join(' · '))) : null,
      info.paths.length
        ? h('details', { class: 'more' }, h('summary', null, icon('right', 'sm'), 'Список папок'),
          h('div', { class: 'stack tight' }, info.paths.map(p => h('code', null, p))))
        : null);
  };

  const renderUsers = () => {
    const info = profileInfo.get(s);
    if (!info?.profiles) { fill(users); return; }
    const known = info.profiles.map(p => p.userName);
    const others = s.excludeUsers.filter(e => !known.some(u => matches(e, u)));
    fill(users, h('div', { class: 'stack tight' },
      h('div', { class: 'chips' }, info.profiles.map(p => {
        const on = !s.excludeUsers.some(e => matches(e, p.userName));
        const cb = h('input', { type: 'checkbox', checked: on });
        cb.addEventListener('change', () => {
          s.excludeUsers = cb.checked ? s.excludeUsers.filter(e => !matches(e, p.userName)) : [...s.excludeUsers, p.userName];
          markDirty();
          refreshHead();
          reload();
        });
        return h('label', { class: 'chip-toggle', title: p.profilePath + (p.hiveLoaded ? ' · сейчас в системе' : '') }, cb,
          h('span', null, icon('users', 'sm'), p.userName));
      })),
      h('span', { class: 'hint' }, 'Снимите галочку, чтобы не копировать профиль пользователя. Новые пользователи попадают в копию автоматически.'),
      field('Исключить также', inLines(s, 'excludeUsers', {
        rows: 2, placeholder: 'имена пользователей, которых сейчас нет на сервере',
        get: () => others,
        set: lines => { s.excludeUsers = [...s.excludeUsers.filter(e => known.some(u => matches(e, u))), ...lines]; },
        onInput: () => { refreshHead(); reloadSoon(); },
      }))));
  };

  const reload = async () => {
    try {
      profileInfo.set(s, await api('POST', '/api/discover/profiles', { folders: s.folders, excludeUsers: s.excludeUsers }));
    } catch (e) {
      profileInfo.set(s, { error: e.message });
    }
    renderPreview();
    renderUsers();
  };
  const reloadSoon = debounce(reload, 500);

  const folderChips = h('div', { class: 'chips' }, FOLDERS.map(([key, label]) => {
    const cb = h('input', { type: 'checkbox', checked: s.folders.some(f => f.toLowerCase() === key.toLowerCase()) });
    cb.addEventListener('change', () => {
      s.folders = cb.checked ? [...s.folders, key] : s.folders.filter(f => f.toLowerCase() !== key.toLowerCase());
      markDirty();
      refreshHead();
      reloadSoon();
    });
    return h('label', { class: 'chip-toggle' }, cb, h('span', null, label));
  }));

  if (profileInfo.has(s)) { renderPreview(); renderUsers(); } else { renderPreview(); reload(); }

  return [
    field('Папки профиля', folderChips, 'Перенаправленные папки (например, «Документы» на диске D:) учитываются'),
    field('Другие папки внутри профиля', inLines(s, 'folders', {
      rows: 2, placeholder: 'AppData\\Roaming\\Программа — по одной в строке',
      get: () => s.folders.filter(f => !KNOWN_FOLDERS.has(f.toLowerCase())),
      set: lines => { s.folders = [...s.folders.filter(f => KNOWN_FOLDERS.has(f.toLowerCase())), ...lines]; },
      onInput: () => { refreshHead(); reloadSoon(); },
    })),
    field('Пользователи', users),
    preview,
    h('div', { class: 'form-grid' },
      field('Не копировать файлы (маски)', inLines(s, 'exclude', { rows: 3, placeholder: '*.tmp' }), 'Без учёта регистра: *.tmp, ~$*, Thumbs.db'),
      name('Профили пользователей')),
  ];
}

/* ----- папки и файлы ----- */

function formPaths(s, name, refreshHead) {
  const list = h('div');
  const masks = () => s.exclude.filter(p => !isAbsPath(p));
  const absExcludes = () => s.exclude.filter(isAbsPath);
  const renderList = () => {
    refreshHead();
    fill(list, 
      s.paths.length
        ? h('div', { class: 'chip-list' }, s.paths.map(p => chip(p, () => { s.paths = s.paths.filter(x => x !== p); markDirty(); renderList(); })))
        : h('div', { class: 'muted small' }, 'Ничего не выбрано.'),
      absExcludes().length
        ? h('div', { class: 'stack tight' }, h('span', { class: 'hint' }, 'Исключено:'),
          h('div', { class: 'chip-list' }, absExcludes().map(p => chip(p, () => { s.exclude = s.exclude.filter(x => x !== p); markDirty(); renderList(); }, 'exclude'))))
        : null);
  };
  renderList();

  const manual = { v: '' };
  const manualInput = inText(manual, 'v', { mono: true, placeholder: 'D:\\Обмен или \\\\nas\\share\\папка' });
  manualInput.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); addManual(); } });
  // поле ручного ввода не должно считаться изменением настроек
  const addManual = () => {
    const p = manual.v.trim();
    if (!p) return;
    if (!s.paths.some(x => x.toLowerCase() === p.toLowerCase())) s.paths = [...s.paths, p];
    manual.v = '';
    manualInput.value = '';
    markDirty();
    renderList();
  };

  return [
    field('Что копировать', h('div', { class: 'stack tight' }, list,
      h('div', { class: 'row wrap' },
        h('button', {
          class: 'btn primary small', type: 'button', onclick: async () => {
            const r = await openTreePicker({ mode: 'multi', title: 'Папки и файлы для копирования', paths: s.paths, excludes: absExcludes() });
            if (!r) return;
            s.paths = r.paths;
            s.exclude = [...masks(), ...r.excludes];
            markDirty();
            renderList();
          },
        }, icon('folder'), 'Выбрать в дереве…')),
      h('div', { class: 'input-group' }, manualInput, h('button', { class: 'btn', type: 'button', onclick: addManual }, icon('plus'), 'Добавить путь'))),
    'В дереве снимите галочку внутри выбранной папки, чтобы исключить вложенную папку или файл'),
    h('div', { class: 'form-grid' },
      field('Не копировать файлы (маски)', inLines(s, 'exclude', {
        rows: 3, placeholder: '*.tmp\n*.log',
        get: masks,
        set: lines => { s.exclude = [...lines, ...absExcludes()]; },
      }), 'Без учёта регистра; можно указывать и имена папок'),
      name('Папки', 'Имя в отчётах и в списке снимков')),
    inSwitch(s, 'useVss', 'Копировать открытые файлы через VSS', 'Нужно для файлов, которые открыты программами во время копирования'),
  ];
}

/* ----- служебные файлы 1С ----- */

function formOneC(s, name) {
  return [
    inSwitch(s, 'includeLicenses', 'Программные лицензии 1С', 'Иначе после переустановки сервера лицензии придётся активировать заново'),
    inSwitch(s, 'includeUserBaseLists', 'Списки баз пользователей (ibases.v8i)', 'Чтобы после восстановления у пользователей были те же базы в окне запуска'),
    h('div', { class: 'form-grid' },
      field('Папки srvinfo сервера 1С', inLines(s, 'srvInfoPaths', { rows: 2, placeholder: 'пусто — найти автоматически в Program Files\\1cv8\\srvinfo' }),
        'Настройки кластера (*.lst); сами базы на SQL копируются отдельно'),
      name('Служебные файлы 1С')),
  ];
}

/* ----- выгрузка 1С (.dt) ----- */

let platformsCache = null;

function formOneCDump(s, name, refreshHead) {
  // Режим определяется тем, какое поле есть (пустая строка — тоже «есть»): у серверной базы нет filePath.
  const server = s.serverBase != null && s.filePath == null;
  const platformInfo = h('div', { class: 'hint' }, 'Ищу установленную платформу 1С…');
  (platformsCache ||= api('GET', '/api/discover/1c-platforms').catch(() => []))
    .then(list => fill(platformInfo, list.length
      ? `Пусто — будет использована ${list[0].version}: ${list[0].path}${list.length > 1 ? ` (всего найдено платформ: ${list.length})` : ''}`
      : h('span', { class: 'text-err' }, 'Платформа 1С на сервере не найдена — установите 1С или укажите путь к 1cv8.exe')));

  const modeSel = inSelect([['file', 'Файловая база'], ['server', 'Серверная база (1С:Предприятие сервер)']], server ? 'server' : 'file', v => {
    if (v === 'server') { delete s.filePath; s.serverBase = s.serverBase || ''; } else { delete s.serverBase; s.filePath = s.filePath || ''; }
    markDirty();
    refreshHead();
    rerender();
  }, '');

  return [
    h('div', { class: 'notice info small' }, icon('info', 'sm'), h('span', null,
      'Файл .dt сжат, поэтому при каждом запуске передаётся целиком, а не только изменения. Удобно вынести выгрузку в отдельную задачу раз в неделю, ',
      'а ежедневные копии делать источником «Файловая база 1С».')),
    field('Какая база', modeSel),
    server
      ? field('Сервер и база', inText(s, 'serverBase', { mono: true, placeholder: 'srv1c\\buh', onInput: refreshHead }), 'Как в окне запуска 1С: сервер\\имя_базы')
      : field('Папка базы', inPath(s, 'filePath', { mode: 'folder', title: 'Папка базы 1С', onInput: refreshHead }), 'Папка с файлом 1Cv8.1CD'),
    h('div', { class: 'form-grid narrow' },
      field('Пользователь 1С', inText(s, 'user', { placeholder: 'пусто — если пользователей нет' }), 'С правами администратора'),
      field('Пароль', inSecret(s, 'password'))),
    h('details', { class: 'more' }, h('summary', null, icon('right', 'sm'), 'Дополнительно'),
      h('div', { class: 'stack' },
        field('1cv8.exe', inPath(s, 'platformPath', { mode: 'file', nullable: true, title: 'Где лежит 1cv8.exe', placeholder: 'пусто — самая новая установленная платформа' }), platformInfo),
        h('div', { class: 'form-grid' },
          field('Ждать выгрузку не дольше', hoursInput(s, 'timeout'), 'Если Конфигуратор завис (например, ждёт ответа в окне), выгрузка прерывается с ошибкой'),
          name('', 'Имя в отчётах и в списке снимков; по умолчанию — имя базы с .dt')))),
  ];
}

/* ----- хранение ----- */

function retentionForm(r) {
  const box = h('div', { class: 'stack' });
  const render = () => {
    fill(box, 
      inSwitch(r, 'applyFromClient', 'Удалять старые копии с этого сервера',
        'Выключено — правильно для хранилища в режиме «только добавление» (rest-server --append-only): старые копии удаляет само хранилище, и шифровальщик на сервере 1С не может их стереть',
        render),
      r.applyFromClient
        ? h('div', { class: 'stack' },
          h('div', { class: 'form-grid narrow' },
            field('Ежедневных', inNumber(r, 'keepDaily', { min: 0 })),
            field('Еженедельных', inNumber(r, 'keepWeekly', { min: 0 })),
            field('Ежемесячных', inNumber(r, 'keepMonthly', { min: 0 }))),
          inSwitch(r, 'prune', 'Сразу освобождать место (prune)', 'Освобождение места скачивает часть данных и занимает время — при медленном канале лучше делать на стороне хранилища'))
        : h('div', { class: 'hint' }, 'Правила хранения на стороне хранилища описаны в README (restic forget --keep-daily 7 --keep-weekly 4 --keep-monthly 12 --prune).'));
  };
  render();
  return box;
}

/* =============== поиск баз 1С =============== */

function discover1C(job) {
  const body = h('div', { class: 'stack' }, h('div', { class: 'row muted' }, spinner(), 'Читаю списки баз пользователей…'));
  const selected = new Set();
  let found = [];
  const existing = () => new Set(job.sources.filter(x => x.type === 'fileBase1C').map(x => (x.path || '').replace(/[\\/]+$/, '').toLowerCase()));
  const addBtn = h('button', { class: 'btn primary', type: 'button', disabled: true }, icon('plus'), 'Добавить выбранные');

  const render = () => {
    const have = existing();
    const files = found.filter(b => b.kind === 'file');
    const servers = found.filter(b => b.kind === 'server');
    const folderBox = h('div', { class: 'input-group' });
    const scanPath = { v: '' };
    const scanInput = inText(scanPath, 'v', { mono: true, placeholder: 'D:\\ — где искать папки с 1Cv8.1CD' });
    const scanBtn = h('button', {
      class: 'btn', type: 'button', onclick: async () => {
        if (!scanPath.v.trim()) { const p = await openTreePicker({ mode: 'folder', title: 'Где искать базы 1С' }); if (!p) return; scanPath.v = p; }
        scanBtn.disabled = true;
        fill(scanBtn, spinner(), 'Поиск…');
        try {
          const more = await api('GET', '/api/discover/1c-scan?path=' + encodeURIComponent(scanPath.v.trim()));
          for (const b of more) if (!found.some(f => f.path?.toLowerCase() === b.path.toLowerCase())) found.push(b);
          if (!more.length) toast('В этой папке баз 1С не найдено', 'warn');
        } catch (e) { toast(e.message, 'err'); }
        render();
      },
    }, icon('search'), 'Искать в папке');
    folderBox.append(scanInput, scanBtn);

    fill(body, 
      files.length
        ? h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
          h('thead', null, h('tr', null, h('th'), h('th', null, 'База'), h('th', null, 'Папка'), h('th', { class: 'num' }, 'Размер'), h('th', null, 'У кого в списке'))),
          h('tbody', null, files.map(b => {
            const already = have.has((b.path || '').toLowerCase());
            const cb = h('input', { type: 'checkbox', disabled: already || !b.exists, checked: selected.has(b.path) });
            cb.addEventListener('change', () => { if (cb.checked) selected.add(b.path); else selected.delete(b.path); addBtn.disabled = selected.size === 0; });
            return h('tr', null, h('td', null, cb), h('td', null, b.name),
              h('td', { class: 'mono small' }, b.path, already ? h('div', { class: 'badge ok' }, 'уже в задаче') : !b.exists ? h('div', { class: 'badge err' }, 'нет 1Cv8.1CD') : null),
              h('td', { class: 'num' }, fmtSize(b.sizeBytes)), h('td', { class: 'small muted' }, (b.users || []).join(', ')));
          }))))
        : h('div', { class: 'muted' }, 'Файловых баз в списках пользователей не найдено.'),
      servers.length
        ? h('div', { class: 'notice info small' }, icon('info', 'sm'), h('span', null,
          'Клиент-серверные базы (копируются источником «Базы MS SQL» или PostgreSQL): ',
          servers.map(b => `${b.name} (${b.server}/${b.ref})`).join(', ')))
        : null,
      field('Поиск на диске', folderBox, 'Если база не подключена ни у одного пользователя. Ищет до 6 уровней вложенности.'));
  };

  addBtn.addEventListener('click', () => {
    const names = new Set(job.sources.map(x => effectiveName(x).toLowerCase()));
    for (const b of found.filter(x => selected.has(x.path))) {
      let nm = b.name, n = 2;
      while (names.has(nm.toLowerCase())) nm = `${b.name} (${n++})`;
      names.add(nm.toLowerCase());
      const s = { ...SOURCE_TYPES.fileBase1C.create(), name: nm, path: b.path };
      job.sources.push(s);
    }
    markDirty();
    m.close();
    rerender();
    toast(`Добавлено баз: ${selected.size}. Не забудьте сохранить.`, 'ok');
  });

  const m = modal({ title: 'Базы 1С на этом сервере', wide: true, body, actions: [{ label: 'Закрыть', onClick: c => c() }, addBtn] });
  api('GET', '/api/discover/1c').then(r => { found = r; render(); }).catch(e => fill(body, errorBox(e.message)));
}

/* =============== дерево папок сервера =============== */

const normPath = p => (p || '').replace(/[\\/]+$/, '').toLowerCase();
const sameOrUnder = (child, parent) => { const c = normPath(child), p = normPath(parent); return c === p || c.startsWith(p + '\\'); };
const strictlyUnder = (child, parent) => normPath(child) !== normPath(parent) && sameOrUnder(child, parent);

/**
 * mode: multi — галочки (папки и файлы; снятая галочка внутри выбранной папки — исключение),
 *       folder — выбрать одну папку, file — выбрать один файл.
 */
function openTreePicker({ mode, title, paths = [], excludes = [], initial }) {
  return new Promise(resolve => {
    let inc = [...paths];
    let exc = [...excludes];
    let picked = initial || null;
    const nodes = new Map(); // путь (норм.) → узел
    const root = { path: null, children: null, expanded: true, loading: false, error: null };
    const treeEl = h('div', { class: 'tree' });
    const summary = h('span', { class: 'muted small grow' });
    let done = false;

    const isOn = p => {
      let best = -1, val = false;
      for (const x of inc) if (sameOrUnder(p, x) && normPath(x).length > best) { best = normPath(x).length; val = true; }
      for (const x of exc) if (sameOrUnder(p, x) && normPath(x).length > best) { best = normPath(x).length; val = false; }
      return val;
    };
    const isPartial = p => isOn(p) ? exc.some(x => strictlyUnder(x, p)) : inc.some(x => strictlyUnder(x, p));
    const toggle = p => {
      const was = isOn(p);
      inc = inc.filter(x => !sameOrUnder(x, p));
      exc = exc.filter(x => !sameOrUnder(x, p));
      const inherited = isOn(p);
      if (was && inherited) exc.push(p);
      else if (!was && !inherited) inc.push(p);
      render();
    };

    const load = async node => {
      node.loading = true;
      render();
      try {
        const files = mode !== 'folder';
        const r = await api('GET', '/api/fs?files=' + files + (node.path ? '&path=' + encodeURIComponent(node.path) : ''));
        node.error = r.error;
        node.truncated = r.truncated;
        node.children = r.entries.map(e => {
          const n = { ...e, children: null, expanded: false, loading: false, error: null };
          nodes.set(normPath(e.path), n);
          return n;
        });
      } catch (e) {
        node.error = e.message;
        node.children = [];
      }
      node.loading = false;
      render();
    };

    const expandTo = async target => {
      if (!target || !isAbsPath(target)) return;
      if (!root.children) await load(root);
      const parts = target.replace(/[\\/]+$/, '').split(/[\\/]/);
      let cur = parts[0] + '\\';
      for (let i = 1; i <= parts.length; i++) {
        const node = nodes.get(normPath(cur));
        if (!node || !node.isDirectory || node.isLink) break;
        node.expanded = true;
        if (!node.children) await load(node);
        if (i < parts.length) cur = cur.replace(/\\$/, '') + '\\' + parts[i];
      }
    };

    const row = n => {
      const canExpand = n.isDirectory && !n.isLink;
      const toggleBtn = h('button', {
        class: 'tree-toggle' + (canExpand ? '' : ' none') + (n.expanded ? ' open' : ''), type: 'button', tabindex: -1,
        onclick: () => { n.expanded = !n.expanded; if (n.expanded && !n.children) load(n); else render(); },
      }, icon('right'));
      const ic = n.totalBytes != null ? icon('drive', 'drive') : n.isDirectory ? icon('folder', 'folder') : icon('file', 'file');
      const meta = n.totalBytes != null
        ? `${n.label ? n.label + ' · ' : ''}свободно ${fmtSize(n.freeBytes)} из ${fmtSize(n.totalBytes)}`
        : n.isLink ? 'ссылка' : !n.isDirectory && n.size != null ? fmtSize(n.size) : '';
      const nameEl = h('span', { class: 'tree-name', title: n.path }, n.name);
      const parts = [toggleBtn];
      let selectable = true;
      if (mode === 'multi') {
        const cb = h('input', { type: 'checkbox', checked: isOn(n.path), disabled: n.isLink });
        cb.indeterminate = !n.isLink && isPartial(n.path);
        cb.addEventListener('change', () => toggle(n.path));
        nameEl.addEventListener('click', () => { if (canExpand) { n.expanded = !n.expanded; if (n.expanded && !n.children) load(n); else render(); } });
        parts.push(cb);
      } else {
        selectable = mode === 'folder' ? canExpand : !n.isDirectory;
        nameEl.addEventListener('click', () => {
          if (selectable) { picked = n.path; render(); } else if (canExpand) { n.expanded = !n.expanded; if (n.expanded && !n.children) load(n); else render(); }
        });
        nameEl.addEventListener('dblclick', () => { if (canExpand) { n.expanded = !n.expanded; if (n.expanded && !n.children) load(n); else render(); } });
      }
      parts.push(ic, nameEl, meta ? h('span', { class: 'tree-meta' }, meta) : null);
      const cls = 'tree-row' + (n.hidden ? ' hidden-item' : '') + (mode !== 'multi' && picked && normPath(picked) === normPath(n.path) ? ' selected' : '');
      return h('div', { class: cls }, parts);
    };

    const list = node => {
      const items = [];
      if (node.loading) items.push(h('li', { class: 'tree-msg' }, 'загрузка…'));
      if (node.error) items.push(h('li', { class: 'tree-msg text-err' }, node.error));
      for (const c of node.children || []) {
        items.push(h('li', null, row(c), c.expanded && c.children ? list(c) : c.expanded && c.loading ? h('div', { class: 'tree-msg' }, 'загрузка…') : null));
      }
      if (node.children && !node.children.length && !node.error && !node.loading) items.push(h('li', { class: 'tree-msg' }, 'пусто'));
      if (node.truncated) items.push(h('li', { class: 'tree-msg' }, 'показаны первые 3000 элементов'));
      return h('ul', null, items);
    };

    const render = () => {
      const body = treeEl.closest('.modal-body');
      const y = body?.scrollTop;
      fill(treeEl, list(root));
      if (body) body.scrollTop = y;
      summary.textContent = mode === 'multi'
        ? `Выбрано: ${inc.length}${exc.length ? ` · исключено: ${exc.length}` : ''}`
        : picked || 'ничего не выбрано';
    };

    const m = modal({
      title, wide: true, body: treeEl,
      onClose: () => { if (!done) resolve(null); },
      actions: [summary,
        { label: 'Отмена', onClick: c => c() },
        {
          label: 'Готово', kind: 'primary', onClick: c => {
            done = true;
            resolve(mode === 'multi' ? { paths: inc, excludes: exc } : picked);
            c();
          },
        }],
    });
    void m;
    render();
    (async () => {
      await load(root);
      // раскрываем дерево до уже выбранных путей
      const parentOf = p => p.replace(/[\\/]+$/, '').replace(/[\\/][^\\/]*$/, '') || p;
      const targets = mode === 'multi' ? [...inc, ...exc].slice(0, 12).map(parentOf)
        : initial ? [mode === 'file' ? parentOf(initial) : initial] : [];
      for (const t of targets) await expandTo(t);
      render();
      const first = treeEl.querySelector('input[type=checkbox]:checked:not([disabled]), .tree-row.selected');
      const firstRow = first?.closest('.tree-row') || first;
      if (firstRow && (mode !== 'multi' || inc.length)) firstRow.scrollIntoView({ block: 'center' });
    })();
  });
}

/* =============== журнал =============== */

async function pageHistory(main) {
  const st = S.status || await api('GET', '/api/status');
  let job = '';
  let tab = 'runs';
  const content = h('div', { class: 'stack' });
  const tabs = h('div', { class: 'tabs' });

  const renderTabs = () => fill(tabs, 
    h('a', { href: '#/history', class: 'tab' + (tab === 'runs' ? ' active' : ''), onclick: e => { e.preventDefault(); tab = 'runs'; renderTabs(); load(); } }, 'Запуски'),
    h('a', { href: '#/history', class: 'tab' + (tab === 'log' ? ' active' : ''), onclick: e => { e.preventDefault(); tab = 'log'; renderTabs(); load(); } }, 'Журнал службы'));

  const load = async () => {
    fill(content, h('div', { class: 'loading' }, spinner(), 'Загрузка…'));
    try {
      if (tab === 'runs') {
        const runs = await api('GET', '/api/history?count=200' + (job ? '&job=' + encodeURIComponent(job) : ''));
        const filter = st.jobs.length > 1
          ? h('div', { class: 'row' }, h('span', { class: 'muted' }, 'Задача'),
            inSelect([['', 'все'], ...st.jobs.map(j => [j.name, j.name])], job, v => { job = v; load(); }))
          : null;
        fill(content, filter,
          runs.length
            ? runsTable(runs.map(r => ({ ...r, uploaded: r.totalUploaded, retries: r.totalRetries })), { details: true })
            : h('div', { class: 'card empty' }, 'Запусков ещё не было.'),
          runs.length ? h('div', { class: 'hint' }, 'Нажмите на строку, чтобы увидеть подробности по каждому источнику.') : null);
      } else {
        const r = await api('GET', '/api/logs?lines=600');
        const pre = h('pre', { class: 'log' }, r.lines.map(line => {
          const m = /^(\S+ \S+) \[(\w{3})\] (.*)$/.exec(line);
          if (!m) return h('div', null, line);
          return h('div', { class: 'l-' + m[2].toLowerCase() }, h('span', { class: 'l-ts' }, m[1] + ' '), m[3]);
        }));
        fill(content, 
          h('div', { class: 'row wrap' }, h('span', { class: 'muted small mono grow' }, r.file || 'журнал пуст'),
            h('button', { class: 'btn small', type: 'button', onclick: load }, icon('refresh'), 'Обновить')),
          pre);
        pre.scrollTop = pre.scrollHeight;
      }
    } catch (e) {
      if (e.status !== 401) fill(content, errorBox(e.message));
    }
  };

  renderTabs();
  fill(main, h('div', { class: 'page' }, pageHead('Журнал', 'Отчёты о запусках и журнал службы'), tabs, content));
  await load();
}

/* =============== снимки =============== */

async function pageSnapshots(main) {
  const body = h('div', { class: 'stack' }, h('div', { class: 'loading' }, spinner(), 'Читаю список снимков из хранилища…'));
  fill(main, h('div', { class: 'page' },
    pageHead('Снимки', 'Копии в хранилище. Восстановление — командой на сервере (кнопка «Команда»)',
      h('button', { class: 'btn', type: 'button', onclick: () => rerender() }, icon('refresh'), 'Обновить')),
    body));

  let snaps;
  try {
    snaps = await api('GET', '/api/snapshots');
  } catch (e) {
    if (e.status !== 401) fill(body, errorBox('Не удалось получить список: ' + e.message));
    return;
  }
  if (!snaps.length) { fill(body, h('div', { class: 'card empty' }, 'Снимков пока нет.')); return; }

  const groups = new Map();
  for (const s of snaps) {
    const name = s.tags.find(t => t.startsWith('name:'))?.slice(5) || '—';
    const type = s.tags.find(t => t.startsWith('src:'))?.slice(4) || '';
    if (!groups.has(name)) groups.set(name, { name, type, items: [] });
    groups.get(name).items.push(s);
  }

  fill(body, h('div', { class: 'muted small' }, `Всего снимков: ${snaps.length}, источников: ${groups.size}`),
    ...[...groups.values()].sort((a, b) => a.name.localeCompare(b.name, 'ru')).map(g => {
      const latest = g.items[0];
      const t = SOURCE_TYPES[g.type];
      const det = h('div', { hidden: true });
      const head = h('div', {
        class: 'source-head', onclick: () => {
          det.hidden = !det.hidden;
          head.querySelector('.chev').classList.toggle('open', !det.hidden);
          if (!det.hidden && !det.firstChild) det.append(snapshotTable(g));
        },
      },
      h('div', { class: 'source-icon t-' + g.type }, icon(t?.icon || 'snapshots')),
      h('div', { class: 'grow min0' },
        h('div', { class: 'source-title' }, g.name, t ? h('span', { class: 'source-type' }, t.label) : null),
        h('div', { class: 'muted small' }, `снимков: ${g.items.length} · последний ${fmtDateTime(latest.time)} (${fmtRel(latest.time)})`
          + (latest.summary ? ` · ${fmtSize(latest.summary.totalBytesProcessed)}` : ''))),
      h('span', { class: 'chev' }, icon('down')));
      return h('div', { class: 'source card' }, head, det);
    }));
}

const toResticPath = p => '/' + p.replace(':', '').replace(/\\/g, '/');

/** Откуда начать просмотр снимка: папка источника; у файловых снимков (.bak, .dt) — их папка; у дампов PostgreSQL — корень. */
function snapshotStart(g, s) {
  if (g.type === 'postgreSql' || s.paths.length !== 1 || !isAbsPath(s.paths[0])) return '/';
  const p = toResticPath(s.paths[0]);
  return g.type === 'msSql' || g.type === 'oneCDump' ? (p.replace(/\/[^/]+$/, '') || '/') : p;
}

function restoreCommand(g, s) {
  const exe = '& "C:\\Program Files\\MyBackup\\MyBackup.exe"';
  const safe = g.name.replace(/[\\/:*?"<>|]/g, '_');
  const path = s.paths.length === 1 ? s.paths[0] : null;
  // Дамп PostgreSQL принят restic из потока: внутри снимка он лежит в корне (/zup.pgdump), хотя в списке путь C:\zup.pgdump.
  if (path && g.type === 'postgreSql') return `${exe} restic dump ${s.shortId} "/${baseName(path)}" --target "D:\\Restore\\${baseName(path)}"`;
  if (path && (g.type === 'msSql' || g.type === 'oneCDump')) {
    return `${exe} restic dump ${s.shortId} "${toResticPath(path)}" --target "D:\\Restore\\${baseName(path)}"`;
  }
  if (path && isAbsPath(path)) return `${exe} restic restore "${s.shortId}:${toResticPath(path)}" --target "D:\\Restore\\${safe}"`;
  return `${exe} restic restore ${s.shortId} --target "D:\\Restore\\${safe}"`;
}

function showRestoreCommand(g, s) {
  const cmd = restoreCommand(g, s);
  modal({
    title: 'Восстановление снимка ' + s.shortId, wide: true,
    body: [
      h('p', { class: 'muted', style: { margin: '0' } }, 'Выполните в PowerShell на сервере от имени администратора. Папку назначения можно изменить; существующие файлы в ней будут перезаписаны.'),
      h('code', { class: 'block' }, cmd),
      h('p', { class: 'hint', style: { margin: '0' } }, `Пути в снимке: ${s.paths.join(', ')}`),
    ],
    actions: [{ label: 'Скопировать', icon: 'copy', kind: 'primary', onClick: async c => { try { await navigator.clipboard.writeText(cmd); toast('Команда скопирована'); c(); } catch { toast('Не удалось скопировать — выделите текст вручную', 'warn'); } } }],
  });
}

/** Просмотр снимка по папкам и скачивание: файл — как есть, папка — zip-архивом. */
function openSnapshotBrowser(g, s) {
  const crumbs = h('div');
  const list = h('div');
  const link = (path, type, size) =>
    `/api/snapshots/${s.id}/download?path=${encodeURIComponent(path)}&type=${type}${size != null ? '&size=' + size : ''}`;

  const load = async path => {
    renderCrumbs(path);
    fill(list, h('div', { class: 'row muted' }, spinner(), 'Читаю снимок…'));
    try {
      const r = await api('GET', `/api/snapshots/${s.id}/ls?path=${encodeURIComponent(path)}`);
      fill(list, r.entries.length
        ? h('div', { class: 'table-wrap' }, h('table', { class: 'table files' },
          h('thead', null, h('tr', null, h('th', null, 'Имя'), h('th', { class: 'num' }, 'Размер'), h('th', null, 'Изменён'), h('th'))),
          h('tbody', null, r.entries.map(e => {
            const isDir = e.type === 'dir';
            return h('tr', null,
              h('td', null, h('span', { class: 'row' }, icon(isDir ? 'folder' : 'file', isDir ? 'folder' : 'file'),
                isDir ? h('button', { class: 'link', type: 'button', onclick: () => load(e.path) }, e.name) : h('span', null, e.name))),
              h('td', { class: 'num' }, isDir ? '' : fmtSize(e.size)),
              h('td', { class: 'nowrap muted' }, e.mtime ? fmtDateTime(e.mtime) : ''),
              h('td', { class: 'num' }, isDir || e.type === 'file'
                ? h('a', { class: 'btn small', href: link(e.path, e.type, isDir ? null : e.size), download: true, title: isDir ? 'Скачать папку zip-архивом' : 'Скачать файл' },
                  icon('download'), isDir ? 'zip' : 'Скачать')
                : null));
          }))))
        : h('div', { class: 'muted' }, 'Папка пуста.'));
    } catch (e) {
      if (e.status !== 401) fill(list, errorBox(e.message));
    }
  };

  const renderCrumbs = path => {
    const parts = path === '/' ? [] : path.split('/').filter(Boolean);
    const label = (p, i) => (i === 0 && /^[A-Za-z]$/.test(p) ? p + ':' : p);
    const items = [h('button', { class: 'link', type: 'button', onclick: () => load('/') }, 'снимок')];
    parts.forEach((p, i) => {
      const target = '/' + parts.slice(0, i + 1).join('/');
      items.push(h('span', { class: 'faint' }, '›'),
        i === parts.length - 1 ? h('b', null, label(p, i)) : h('button', { class: 'link', type: 'button', onclick: () => load(target) }, label(p, i)));
    });
    fill(crumbs, h('div', { class: 'row wrap' }, items,
      path !== '/' ? h('a', { class: 'btn small right', href: link(path, 'dir'), download: true }, icon('download'), 'Вся папка (zip)') : null));
  };

  modal({
    title: `Файлы снимка ${s.shortId} · ${fmtDateTime(s.time)}`, wide: true,
    body: [
      h('p', { class: 'hint', style: { margin: '0' } },
        'Файл скачивается как есть, папка — zip-архивом, в папку загрузок браузера. Большие базы удобнее восстанавливать командой (кнопка «Команда»).'),
      crumbs, list,
    ],
  });
  load(snapshotStart(g, s));
}

function snapshotTable(g) {
  return h('div', { class: 'source-body' }, h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
    h('thead', null, h('tr', null, h('th', null, 'Время'), h('th', { class: 'num' }, 'Объём'), h('th', { class: 'num' }, 'Новых данных'),
      h('th', null, 'Снимок'), h('th'))),
    h('tbody', null, g.items.map(s => h('tr', null,
      h('td', { class: 'nowrap' }, fmtDateTime(s.time)),
      h('td', { class: 'num' }, s.summary ? fmtSize(s.summary.totalBytesProcessed) : '—'),
      h('td', { class: 'num' }, s.summary ? fmtSize(s.summary.dataAddedPacked || s.summary.dataAdded) : '—'),
      h('td', { class: 'mono small' }, s.shortId),
      h('td', null, h('div', { class: 'row' },
        h('button', { class: 'btn small primary', type: 'button', onclick: () => openSnapshotBrowser(g, s) }, icon('folder'), 'Файлы'),
        h('button', { class: 'btn small', type: 'button', onclick: () => showRestoreCommand(g, s) }, 'Команда')))))))));
}

/* =============== настройки =============== */

const SETTINGS_TABS = [['storage', 'Хранилище'], ['network', 'Скорость и повторы'], ['telegram', 'Telegram'], ['access', 'Доступ']];

async function pageSettings(main, tab) {
  await ensureConfig();
  tab = SETTINGS_TABS.some(t => t[0] === tab) ? tab : 'storage';
  const c = S.cfg;
  const content = {
    storage: () => settingsStorage(c),
    network: () => settingsNetwork(c),
    telegram: () => settingsTelegram(c),
    access: () => settingsAccess(c),
  }[tab]();
  fill(main, h('div', { class: 'page' },
    pageHead('Настройки', S.cfgMeta.path),
    h('div', { class: 'tabs' }, SETTINGS_TABS.map(([id, label]) => h('a', { href: '#/settings/' + id, class: 'tab' + (id === tab ? ' active' : '') }, label))),
    configErrors(),
    content));
}

const saveFirstNotice = () => h('div', { class: 'notice warn small' }, icon('info', 'sm'),
  h('span', null, 'Есть несохранённые изменения. Сначала нажмите «Сохранить» в нижней панели — проверка идёт по сохранённым настройкам.'));

function settingsStorage(c) {
  const r = c.restic;
  const result = h('div');
  const check = h('button', {
    class: 'btn', type: 'button', onclick: async () => {
      // Проверка и создание хранилища идут по сохранённым настройкам: иначе хранилище можно создать
      // с паролем, которого нет в файле настроек.
      if (isDirty()) { fill(result, saveFirstNotice()); return; }
      check.disabled = true;
      fill(result, h('div', { class: 'row muted' }, spinner(), 'Проверка… (через медленный канал может занять минуту)'));
      try {
        const x = await api('POST', '/api/repository/check');
        if (x.exists === true) fill(result, h('div', { class: 'notice ok small' }, icon('check', 'sm'), h('span', null, 'Хранилище доступно · ' + (x.version || ''))));
        else if (x.exists === false) {
          const init = h('button', {
            class: 'btn primary small', type: 'button', onclick: async () => {
              if (isDirty()) { fill(result, saveFirstNotice()); return; }
              if (!await confirmDialog('Создать новое хранилище по этому адресу с указанным паролем? Сохраните пароль отдельно от сервера — без него копии не восстановить.', 'Создать')) return;
              init.disabled = true;
              try { await api('POST', '/api/repository/init'); toast('Хранилище создано'); check.click(); } catch (e) { toast(e.message, 'err', 8000); init.disabled = false; }
            },
          }, 'Создать хранилище');
          fill(result, h('div', { class: 'notice warn small' }, icon('alert', 'sm'), h('span', null, 'Хранилище по этому адресу ещё не создано.'), init));
        } else fill(result, errorBox('Хранилище недоступно: ' + (x.error || 'неизвестная ошибка')));
      } catch (e) {
        fill(result, errorBox(e.message));
      }
      check.disabled = false;
    },
  }, icon('refresh'), 'Проверить хранилище');

  return h('div', { class: 'stack' },
    panel('Куда копировать', null,
      field('Адрес хранилища', inText(r, 'repository', { mono: true, placeholder: 'rest:https://user:пароль@backup.example.ru:8000/rdp-1c/' }),
        h('span', null, 'rest:https://… — rest-server (рекомендуется, режим «только добавление»); s3:https://…/bucket/папка; sftp:user@host:/путь; D:\\restic-repo или \\\\nas\\backup')),
      field('Пароль хранилища', inSecret(r, 'password'), 'Шифрует все копии. Храните его отдельно от сервера: без пароля копии не восстановить'),
      h('div', { class: 'row wrap' }, check, h('span', { class: 'hint' }, 'Проверяются сохранённые настройки')),
      result),
    panel('Дополнительно', null,
      inSwitch(r, 'useVss', 'Снимки VSS для открытых файлов', 'Базы 1С и открытые файлы копируются согласованно, без остановки. Нужны права администратора (у службы они есть)'),
      h('div', { class: 'form-grid narrow' },
        field('Размер пакета, МиБ', inNumber(r, 'packSizeMiB', { min: 4, max: 128, nullable: true, placeholder: '16', cls: 'w-auto' }), 'Меньше — меньше потерь при обрыве'),
        field('Сжатие', inSelect([['', 'по умолчанию (auto)'], ['auto', 'auto'], ['max', 'max — сильнее, медленнее'], ['off', 'off']], r.compression || '', v => { r.compression = v || null; markDirty(); }, ''))),
      field('Папка кеша restic', inPath(r, 'cacheDirectory', { nullable: true, placeholder: 'по умолчанию — в папке данных MyBackup' })),
      field('Переменные окружения', envEditor(r), 'Например, ключи S3: AWS_ACCESS_KEY_ID и AWS_SECRET_ACCESS_KEY. Значения хранятся зашифрованными')));
}

function envEditor(r) {
  const box = h('div', { class: 'stack tight' });
  const render = () => {
    const entries = Object.entries(r.environment || {});
    const rebuild = list => { r.environment = Object.fromEntries(list.filter(([k]) => k !== undefined)); markDirty(); };
    fill(box, 
      ...entries.map(([k], i) => {
        const keyHolder = { v: k };
        const keyInput = inText(keyHolder, 'v', {
          mono: true, placeholder: 'ИМЯ', onInput: v => {
            const list = Object.entries(r.environment);
            list[i][0] = v.trim();
            rebuild(list);
          },
        });
        const valueHolder = { v: r.environment[k] };
        const secret = inSecret(valueHolder, 'v');
        secret.addEventListener('input', () => {
          const list = Object.entries(r.environment);
          list[i][1] = valueHolder.v;
          rebuild(list);
        });
        secret.addEventListener('click', () => setTimeout(() => {
          const list = Object.entries(r.environment);
          if (list[i] && list[i][1] !== valueHolder.v) { list[i][1] = valueHolder.v; rebuild(list); }
        }));
        return h('div', { class: 'row' }, h('div', { style: { flex: '0 0 40%' } }, keyInput), h('div', { class: 'grow' }, secret),
          h('button', {
            class: 'btn ghost small icon-only', type: 'button', title: 'Удалить', onclick: () => {
              const list = Object.entries(r.environment);
              list.splice(i, 1);
              rebuild(list);
              render();
            },
          }, icon('trash')));
      }),
      h('div', null, h('button', {
        class: 'btn small', type: 'button', onclick: () => {
          let name = 'NEW_VARIABLE', n = 2;
          while (name in r.environment) name = `NEW_VARIABLE_${n++}`;
          r.environment = { ...r.environment, [name]: '' };
          markDirty();
          render();
        },
      }, icon('plus'), 'Добавить переменную')));
  };
  r.environment ||= {};
  render();
  return box;
}

const KIB_PER_MBIT = 1e6 / 8 / 1024;

function speedInput(obj, key) {
  const holder = { v: obj[key] ? +(obj[key] / KIB_PER_MBIT).toFixed(1) : 0 };
  const el = inNumber(holder, 'v', {
    min: 0, step: 0.5, round: false, onInput: v => { obj[key] = Math.round((v || 0) * KIB_PER_MBIT); markDirty(); },
  });
  return h('div', { class: 'row' }, el, h('span', { class: 'muted' }, 'Мбит/с'));
}

function settingsNetwork(c) {
  const b = c.bandwidth;
  const box = h('div', { class: 'stack' });
  const render = () => {
    const hasHours = !!b.workHours;
    const [from, to] = (b.workHours || '08:00-20:00').split('-');
    const parseT = t => { const m = /^(\d{1,2}):(\d{2})$/.exec((t || '').trim()); return m ? [+m[1], +m[2]] : [8, 0]; };
    const [fh, fm] = parseT(from), [th, tm] = parseT(to);
    const holder = { on: hasHours };
    fill(box, 
      panel('Ограничение скорости отправки', 'чтобы копирование не мешало пользователям RDP',
        inSwitch(holder, 'on', 'Разное ограничение днём и ночью', null, on => { b.workHours = on ? '08:00-20:00' : null; markDirty(); render(); }),
        hasHours
          ? h('div', { class: 'stack' },
            h('div', { class: 'row wrap' }, h('span', { class: 'field-label' }, 'Рабочее время'),
              timeInput(fh, fm, (hh, mm) => { b.workHours = `${pad2(hh)}:${pad2(mm)}-${(b.workHours || '').split('-')[1] || '20:00'}`; markDirty(); }),
              h('span', { class: 'muted' }, '—'),
              timeInput(th, tm, (hh, mm) => { b.workHours = `${(b.workHours || '').split('-')[0] || '08:00'}-${pad2(hh)}:${pad2(mm)}`; markDirty(); })),
            field('Рабочие дни', dayToggles(b.workDays.map(d => DAY_NUM[d]), days => { b.workDays = days.map(d => DAY_NAME[d]); markDirty(); })),
            h('div', { class: 'form-grid' },
              field('В рабочее время', speedInput(b, 'workHoursUploadLimitKiB'), '0 — без ограничения'),
              field('В остальное время', speedInput(b, 'offHoursUploadLimitKiB'), '0 — без ограничения')),
            h('div', { class: 'hint' }, 'Если копирование идёт на границе рабочего времени, restic перезапускается с новым ограничением — уже переданное не пропадает.'))
          : field('Ограничение', speedInput(b, 'offHoursUploadLimitKiB'), '0 — без ограничения')),
      panel('Повторы при обрывах связи', null,
        h('div', { class: 'form-grid narrow' },
          field('Неудачных попыток', inNumber(c.retry, 'maxAttempts', { min: 1, max: 100 })),
          field('Первая пауза, мин', minutesInput(c.retry, 'initialDelay')),
          field('Наибольшая пауза, мин', minutesInput(c.retry, 'maxDelay'))),
        h('div', { class: 'hint' }, 'Пауза после каждой неудачи удваивается: 1, 2, 4… минут. Повторы прекращаются, когда истекает время задачи («сколько можно докачивать»). Уже переданные данные повторно не отправляются.')));
  };
  render();
  return box;
}

const DAY_NAME = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const DAY_NUM = Object.fromEntries(DAY_NAME.map((d, i) => [d, i]));

function minutesInput(obj, key) {
  const holder = { v: Math.round(tsToSec(obj[key]) / 60) };
  return inNumber(holder, 'v', { min: 1, max: 1440, onInput: v => { obj[key] = secToTs(Math.max(1, v) * 60); markDirty(); } });
}

function settingsTelegram(c) {
  const t = c.telegram;
  const chatsBox = h('div', { class: 'stack tight' });
  const unknownBox = h('div', { class: 'stack tight' });
  const testResult = h('div');

  const renderChats = () => {
    const add = { v: '' };
    const addInput = inText(add, 'v', { mono: true, placeholder: 'chat_id, например 123456789' });
    addInput.classList.add('w-auto');
    const doAdd = () => {
      const id = add.v.trim();
      if (!/^-?\d+$/.test(id)) { toast('chat_id — это число', 'warn'); return; }
      if (!t.allowedChatIds.includes(+id)) t.allowedChatIds = [...t.allowedChatIds, +id];
      markDirty();
      renderChats();
    };
    addInput.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); doAdd(); } });
    fill(chatsBox, 
      t.allowedChatIds.length
        ? h('div', { class: 'chips' }, t.allowedChatIds.map(id => chip(String(id), () => { t.allowedChatIds = t.allowedChatIds.filter(x => x !== id); markDirty(); renderChats(); })))
        : h('div', { class: 'muted small' }, 'Нет разрешённых чатов — отчёты никуда не отправляются.'),
      h('div', { class: 'row wrap' }, addInput, h('button', { class: 'btn small', type: 'button', onclick: doAdd }, icon('plus'), 'Добавить')));
  };

  const loadUnknown = async () => {
    try {
      const list = await api('GET', '/api/telegram/chats');
      const fresh = list.filter(x => !t.allowedChatIds.includes(x.chatId));
      fill(unknownBox, fresh.length
        ? h('div', { class: 'stack tight' }, h('span', { class: 'field-label' }, 'Писали боту'),
          fresh.map(x => h('div', { class: 'row wrap notice info small' },
            icon('users', 'sm'),
            h('span', null, [x.title || x.firstName, x.userName ? '@' + x.userName : null].filter(Boolean).join(' '), ' · ', h('code', null, String(x.chatId)), ' · ', fmtRel(x.lastSeen)),
            h('button', { class: 'btn small primary', type: 'button', onclick: () => { t.allowedChatIds = [...t.allowedChatIds, x.chatId]; markDirty(); renderChats(); loadUnknown(); toast('Чат добавлен — сохраните настройки'); } }, 'Разрешить'))))
        : h('div', { class: 'hint' }, 'Чтобы добавить чат: включите бота и сохраните настройки, напишите боту любое сообщение и нажмите «Обновить» — чат появится здесь.'));
    } catch { /* не критично */ }
  };

  renderChats();
  loadUnknown();

  const test = h('button', {
    class: 'btn', type: 'button', onclick: async () => {
      if (isDirty()) { fill(testResult, saveFirstNotice()); return; }
      test.disabled = true;
      fill(testResult, h('div', { class: 'row muted' }, spinner(), 'Отправка…'));
      try {
        const r = await api('POST', '/api/telegram/test');
        fill(testResult, r.errors.length
          ? errorBox(`Бот @${r.bot}: ошибки — ${r.errors.join('; ')}`)
          : h('div', { class: 'notice ok small' }, icon('check', 'sm'), h('span', null, r.sent.length ? `Бот @${r.bot}: сообщение отправлено в ${r.sent.length} чат(а)` : `Бот @${r.bot} работает, но разрешённых чатов нет`)));
      } catch (e) {
        fill(testResult, errorBox(e.message));
      }
      test.disabled = false;
    },
  }, icon('send'), 'Отправить проверочное сообщение');

  return h('div', { class: 'stack' },
    panel('Бот', null,
      inSwitch(t, 'enabled', 'Уведомления и команды в Telegram'),
      field('Токен бота', inSecret(t, 'botToken', { placeholder: '123456789:AA…' }), h('span', null, 'Создайте бота у ', h('a', { href: 'https://t.me/BotFather', target: '_blank', rel: 'noopener' }, '@BotFather'), ' и вставьте токен')),
      field('Прокси', inText(t, 'proxy', { mono: true, nullable: true, placeholder: 'socks5://127.0.0.1:1080 — если api.telegram.org недоступен напрямую' })),
      h('div', { class: 'row wrap' }, test, h('span', { class: 'hint' }, 'Используются сохранённые настройки')),
      testResult),
    panel('Кому отправлять', 'команды принимаются только из этих чатов',
      chatsBox, unknownBox, h('div', null, h('button', { class: 'btn small ghost', type: 'button', onclick: loadUnknown }, icon('refresh'), 'Обновить'))),
    panel('Сообщения', null,
      inSwitch(t, 'notifyOnStart', 'Сообщать о начале копирования', 'Отчёт о завершении приходит всегда'),
      inSwitch(t, 'enableCommands', 'Принимать команды', '/status — что выполняется, /last — последний отчёт, /run — запустить, /stop — остановить, /jobs — список задач')));
}

function settingsAccess(c) {
  const meta = S.cfgMeta;
  const cur = { v: '' }, next = { v: '' }, again = { v: '' };
  const pwInput = (obj, ac) => {
    const el = h('input', { type: 'password', class: 'input', autocomplete: ac });
    el.addEventListener('input', () => { obj.v = el.value; });
    return el;
  };
  const err = h('div', { class: 'form-error' });
  const btn = h('button', { class: 'btn primary', type: 'submit' }, 'Сменить пароль');
  const form = h('form', {
    class: 'stack', onsubmit: async e => {
      e.preventDefault();
      err.textContent = '';
      if (next.v !== again.v) { err.textContent = 'Новые пароли не совпадают'; return; }
      btn.disabled = true;
      try {
        await api('POST', '/api/password', { current: cur.v, new: next.v });
        toast('Пароль изменён. Другие открытые сеансы завершены.');
        form.reset();
      } catch (ex) {
        err.textContent = ex.message;
      }
      btn.disabled = false;
    },
  },
  h('div', { class: 'form-grid' },
    field('Текущий пароль', pwInput(cur, 'current-password')),
    field('Новый пароль', pwInput(next, 'new-password'), 'Не короче 8 символов'),
    field('Ещё раз', pwInput(again, 'new-password'))),
  err, h('div', null, btn));

  const kv = (k, v) => [h('span', null, k), h('span', { class: 'mono small' }, v)];
  return h('div', { class: 'stack' },
    panel('Служба', 'меняется только в config.json, затем перезапуск службы',
      h('div', { class: 'kv' },
        kv('Имя сервера в снимках', c.serverName),
        kv('Файл настроек', meta.path),
        kv('Папка данных', meta.dataDirectory),
        kv('Адреса веб-интерфейса', (c.web?.urls || []).join(', ')),
        kv('Права администратора', meta.isAdministrator ? 'есть (VSS доступен)' : 'нет — VSS недоступен'))),
    panel('Пароль веб-интерфейса', null, form),
    panel('Если пароль забыт', null,
      h('div', { class: 'hint' }, 'На сервере в PowerShell от имени администратора выполните:'),
      h('code', { class: 'block' }, '& "C:\\Program Files\\MyBackup\\MyBackup.exe" web-password')));
}

/* =============== запуск =============== */

(async function boot() {
  try {
    S.session = await api('GET', '/api/session');
  } catch (e) {
    $('#root').replaceChildren(errorBox(e.message));
    return;
  }
  if (S.session.authenticated) startApp(); else renderLogin();
})();
