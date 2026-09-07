'use strict';

(() => {
  const $ = (s, el = document) => el.querySelector(s);
  const $$ = (s, el = document) => Array.from(el.querySelectorAll(s));
  const $id = (id) => document.getElementById(id);
  const DAY = 86400000;
  const LS_THEME = 'wordbook.theme';

  let words = [];
  const ui = { filter: 'all', sort: 'newest', query: '' };
  const editing = { id: null, busy: false };
  let fileWords = null;
  let confirmResolver = null;

  /* ---------- 工具 ---------- */
  const esc = (s) => String(s == null ? '' : s)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  const now = () => Date.now();
  const lines = (s) => String(s || '').split('\n').map((x) => x.trim()).filter(Boolean);

  function aiOf(w) {
    const a = w.ai || {};
    return {
      status: a.status || 'none',
      inContext: a.inContext || '',
      why: a.why || '',
      rephrase: a.rephrase || '',
      tip: a.tip || '',
      error: a.error || '',
    };
  }

  function normalize(w) {
    if (!w || typeof w !== 'object') return null;
    if (!w.word || !String(w.word).trim()) return null;
    w.word = String(w.word).trim();
    w.phonetic = w.phonetic || '';
    w.translation = w.translation || '';
    w.definitions = Array.isArray(w.definitions) ? w.definitions.filter(Boolean) : [];
    w.audio = w.audio || '';
    w.sentence = w.sentence || '';
    w.note = w.note || '';
    w.ai = aiOf(w);
    w.createdAt = Number(w.createdAt) || now();
    w.updatedAt = Number(w.updatedAt) || w.createdAt;
    return w;
  }

  function fmtWhen(ts) {
    const d = new Date(ts);
    const t = new Date();
    const hm = `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
    const startOf = (x) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
    const diffDay = Math.round((startOf(t) - startOf(d)) / DAY);
    if (diffDay === 0) return `今天 ${hm}`;
    if (diffDay === 1) return `昨天 ${hm}`;
    if (d.getFullYear() === t.getFullYear()) return `${d.getMonth() + 1}月${d.getDate()}日 ${hm}`;
    return `${d.getFullYear()}年${d.getMonth() + 1}月${d.getDate()}日 ${hm}`;
  }

  /* ---------- API ---------- */
  async function api(path, options = {}) {
    const res = await fetch(path, {
      headers: { 'Content-Type': 'application/json' },
      ...options,
    });
    let body = null;
    try { body = await res.json(); } catch (e) { /* 忽略 */ }
    if (!res.ok) {
      const err = new Error((body && body.error) || ('请求失败：' + res.status));
      err.code = body && body.code;
      throw err;
    }
    return body || {};
  }

  async function loadData(silent) {
    try {
      const d = await api('/api/data');
      const next = (d.words || []).map(normalize).filter(Boolean);
      const sig = words.length + '|' + (words[0] ? words[0].updatedAt : '');
      words = next;
      if (!silent || sig !== (next.length + '|' + (next[0] ? next[0].updatedAt : ''))) {
        renderAll();
      }
    } catch (e) {
      if (!silent) toast('无法连接本机生词本服务：' + (e.message || ''), { type: 'error', ms: 6000 });
    }
  }

  async function refreshConfig() {
    try {
      const c = await api('/api/config');
      $id('storageInfo').textContent = c.aiConfigured
        ? 'DeepSeek AI 讲解已就绪 · 数据保存在本机 data 文件夹'
        : '未检测到 DeepSeek key：填写 .env.local 后会自动生效，无需重启 · 数据保存在 data 文件夹';
    } catch (e) { /* 忽略 */ }
  }

  /* ---------- Toast / 确认 ---------- */
  function toast(msg, opts = {}) {
    const el = document.createElement('div');
    el.className = 'toast' + (opts.type ? ' ' + opts.type : '');
    const m = document.createElement('span');
    m.className = 'toast-msg';
    m.textContent = msg;
    el.appendChild(m);
    const dismiss = () => { el.classList.add('out'); setTimeout(() => el.remove(), 260); };
    $id('toastZone').appendChild(el);
    setTimeout(dismiss, opts.ms || 3200);
  }

  function askConfirm(opts) {
    return new Promise((resolve) => {
      $id('confirmTitle').textContent = opts.title || '确认';
      $id('confirmMessage').textContent = opts.message || '';
      const ok = $id('confirmOk');
      ok.textContent = opts.confirmText || '确认';
      confirmResolver = { resolve };
      $id('confirmDialog').showModal();
    });
  }

  /* ---------- 渲染 ---------- */
  function aiStatus(w) {
    const a = aiOf(w);
    const done = a.status === 'done' && (a.inContext || a.why || a.rephrase || a.tip);
    return done ? 'done' : (a.status === 'error' ? 'error' : (a.status === 'pending' ? 'pending' : 'none'));
  }

  const AI_LABEL = { done: '已讲解', pending: '讲解中…', error: '讲解失败', none: '未讲解' };

  function getFiltered() {
    let list = words.slice();
    const q = ui.query.trim().toLowerCase();
    if (ui.filter === 'pending') list = list.filter((w) => aiStatus(w) === 'pending');
    else if (ui.filter === 'error') list = list.filter((w) => aiStatus(w) === 'error');
    else if (ui.filter === 'none') list = list.filter((w) => aiStatus(w) === 'none');
    if (q) {
      list = list.filter((w) => {
        const hay = [w.word, w.phonetic, w.translation, w.sentence, w.note,
          w.ai.inContext, w.ai.why, w.ai.rephrase, w.ai.tip]
          .concat(w.definitions || []).join('\n').toLowerCase();
        return hay.includes(q);
      });
    }
    if (ui.sort === 'alpha') list.sort((a, b) => a.word.toLowerCase().localeCompare(b.word.toLowerCase()));
    else list.sort((a, b) => b.createdAt - a.createdAt);
    return list;
  }

  function renderStats() {
    const today = words.filter((w) => {
      const d = new Date(w.createdAt), t = new Date();
      return d.getFullYear() === t.getFullYear() && d.getMonth() === t.getMonth() && d.getDate() === t.getDate();
    }).length;
    const done = words.filter((w) => aiStatus(w) === 'done').length;
    const pending = words.filter((w) => aiStatus(w) === 'pending').length;
    $id('statsStrip').innerHTML = words.length
      ? `<span class="stat"><b>${words.length}</b>个生词</span>
         <span class="stat">今日记录 <b>${today}</b></span>
         <span class="stat">已讲解 <b>${done}</b></span>
         ${pending ? `<span class="stat"><b style="color:var(--accent-ink)">${pending}</b>个正在讲解</span>` : ''}`
      : '<span class="stat">词库还空着，按 Ctrl+Alt+Shift+W 随时记一个</span>';
  }

  function renderTabs() {
    const defs = [
      ['all', '全部'],
      ['pending', '讲解中'],
      ['error', '待重试'],
      ['none', '未讲解'],
    ];
    $id('tabs').innerHTML = defs.map(([key, label]) => {
      const n = key === 'all' ? words.length : words.filter((w) => aiStatus(w) === key).length;
      return `<button class="tab${ui.filter === key ? ' active' : ''}" data-filter="${key}" role="tab" aria-selected="${ui.filter === key}">
        ${label}${n ? `<span class="count">${n}</span>` : ''}</button>`;
    }).join('');
  }

  function rowHtml(w) {
    const a = aiOf(w);
    const st = aiStatus(w);
    const gloss = a.inContext || w.translation || (w.definitions && w.definitions[0]) || '';
    const aiLine = gloss
      ? `<div class="row-ai">${esc(gloss)}</div>`
      : `<div class="row-ai placeholder">${st === 'pending' ? 'AI 正在结合原句讲解…' : st === 'error' ? '讲解失败，点开可重试' : '还没有讲解，点开填写原句后让 AI 讲'}</div>`;
    return `<div class="word-row" data-id="${esc(w.id)}" role="button" tabindex="0" aria-label="查看 ${esc(w.word)}">
      <div class="row-main">
        <div class="row-head">
          <span class="row-word">${esc(w.word)}</span>
          ${w.phonetic ? `<span class="row-phonetic">${esc(w.phonetic)}</span>` : ''}
          <button class="row-speak" data-act="speak" title="朗读">🔊</button>
        </div>
        <div class="row-sentence">${esc(w.sentence)}</div>
        ${aiLine}
      </div>
      <div class="row-side">
        <span class="row-time">${fmtWhen(w.createdAt)}</span>
        <span>
          <span class="chip ai-${st}${st === 'pending' ? ' pending-dot' : ''}">${AI_LABEL[st]}</span>
          ${st === 'error' ? ' <button class="retry-btn" data-act="retry">重试</button>' : ''}
        </span>
      </div>
    </div>`;
  }

  function renderList() {
    const list = getFiltered();
    $id('wordList').innerHTML = list.map(rowHtml).join('');
    const empty = $id('emptyState');
    const show = words.length === 0 || list.length === 0;
    empty.hidden = !show;
    if (!show) return;
    if (words.length === 0) {
      empty.innerHTML = `<span class="empty-emoji">🪄</span>
        <h3>词库还空着</h3>
        <p>阅读时选中一句含生词的话复制，然后按热键记录；</p>
        <div class="steps">① 复制单词或整句<br>② 按 <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>W</kbd> 弹出小框<br>③ 勾选生词回车，AI 会结合原句给你讲解</div>`;
    } else {
      empty.innerHTML = `<span class="empty-emoji">🔍</span><h3>没有匹配的记录</h3><p>换个关键词或筛选条件。</p>`;
    }
  }

  function renderAll() {
    renderStats();
    renderTabs();
    renderList();
  }

  /* ---------- 朗读 ---------- */
  function speakWord(w) {
    if (w.audio && /^https?:/i.test(w.audio)) {
      const a = new Audio(w.audio);
      a.play().catch(() => synth(w.word));
    } else synth(w.word);
  }
  function synth(word) {
    if (!('speechSynthesis' in window)) return;
    try {
      speechSynthesis.cancel();
      const u = new SpeechSynthesisUtterance(word);
      u.lang = 'en-US';
      u.rate = 0.92;
      speechSynthesis.speak(u);
    } catch (e) { /* 忽略 */ }
  }

  /* ---------- 编辑器 ---------- */
  function openEditor(idOrNull) {
    editing.id = idOrNull || null;
    editing.busy = false;
    const w = idOrNull ? words.find((x) => x.id === idOrNull) : null;
    $id('editorTitle').textContent = w ? '编辑词条' : '记一个词';
    $id('btnDelete').hidden = !w;
    $id('fWord').value = w ? w.word : '';
    $id('fSentence').value = w ? w.sentence : '';
    $id('fPhonetic').value = w ? w.phonetic : '';
    $id('fTranslation').value = w ? w.translation : '';
    $id('fDefinitions').value = w ? (w.definitions || []).join('\n') : '';
    $id('fNote').value = w ? w.note : '';
    $id('fInContext').value = w ? w.ai.inContext : '';
    $id('fWhy').value = w ? w.ai.why : '';
    $id('fRephrase').value = w ? w.ai.rephrase : '';
    $id('fTip').value = w ? w.ai.tip : '';
    $id('moreFields').open = false;
    renderAiPanel(w);
    $id('editorDialog').showModal();
    $id('fWord').focus();
    if ($id('fWord').value) $id('fWord').select();
  }

  function renderAiPanel(w) {
    const panel = $id('aiPanel');
    const st = w ? aiStatus(w) : 'none';
    const hasText = !!(w && (w.ai.inContext || w.ai.why || w.ai.rephrase || w.ai.tip));
    const sentence = $id('fSentence').value.trim();
    if (!w && !sentence) { panel.hidden = true; return; }
    panel.hidden = false;
    const box = $id('aiStatus');
    const btn = $id('btnExplain');
    btn.disabled = editing.busy;
    if (editing.busy) {
      box.className = 'ai-status busy';
      box.textContent = 'AI 正在结合原句讲解，请稍候…（最长约 1 分钟）';
    } else if (st === 'pending') {
      box.className = 'ai-status busy';
      box.textContent = '后台正在讲解中…';
    } else if (st === 'done' && hasText) {
      box.className = 'ai-status done';
      box.textContent = 'AI 已结合原句讲解，以下内容可以修改。';
    } else if (st === 'error') {
      box.className = 'ai-status error';
      box.textContent = '上次讲解失败：' + (w.ai.error || '未知原因');
    } else {
      box.className = 'ai-status';
      box.textContent = sentence
        ? '填好上方的原句后，可以让 AI 结合语境讲解这个词。'
        : '没有原句也能讲：AI 会按一般词义和用法讲解；补上原句则可结合语境讲解。';
    }
  }

  function aiPayload() {
    return {
      inContext: $id('fInContext').value.trim(),
      why: $id('fWhy').value.trim(),
      rephrase: $id('fRephrase').value.trim(),
      tip: $id('fTip').value.trim(),
    };
  }

  async function submitEditor() {
    if (editing.busy) return;
    const word = $id('fWord').value.trim();
    if (!word) { toast('请填写单词', { type: 'error' }); return; }
    const payload = {
      word,
      sentence: $id('fSentence').value.trim(),
      phonetic: $id('fPhonetic').value.trim(),
      translation: $id('fTranslation').value.trim(),
      definitions: lines($id('fDefinitions').value),
      note: $id('fNote').value.trim(),
    };
    editing.busy = true;
    const btn = $id('btnSave');
    btn.disabled = true;
    try {
      if (editing.id) {
        const ai = aiPayload();
        const anyAi = !!(ai.inContext || ai.why || ai.rephrase || ai.tip);
        const cur = words.find((x) => x.id === editing.id);
        payload.ai = {
          ...ai,
          status: anyAi ? 'done' : (cur && cur.ai.status === 'done' ? 'none' : 'none'),
        };
        const d = await api('/api/words/' + encodeURIComponent(editing.id), { method: 'PATCH', body: JSON.stringify(payload) });
        const w = normalize(d.word);
        const i = words.findIndex((x) => x.id === w.id);
        if (i >= 0) words[i] = w; else words.unshift(w);
        $id('editorDialog').close();
        renderAll();
        toast(`已保存 “${w.word}”`, { type: 'success' });
      } else {
        const d = await api('/api/words/batch', {
          method: 'POST',
          body: JSON.stringify({ items: [{ word, sentence: payload.sentence }] }),
        });
        const created = (d.words || []).map(normalize).filter(Boolean);
        words = created.concat(words);
        $id('editorDialog').close();
        renderAll();
        toast(created.length === 1 ? `已加入 “${created[0].word}”，正在补释义讲解…` : `已加入 ${created.length} 个词，正在补释义讲解…`, { type: 'success', ms: 4200 });
      }
    } catch (e) {
      toast(e.message || '保存失败', { type: 'error' });
    } finally {
      editing.busy = false;
      btn.disabled = false;
    }
  }

  async function runAiExplain(id) {
    editing.busy = true;
    editing.id = id;
    renderAiPanel(words.find((x) => x.id === id));
    try {
      const d = await api('/api/words/' + encodeURIComponent(id) + '/explain', { method: 'POST' });
      const w = normalize(d.word);
      const i = words.findIndex((x) => x.id === w.id);
      if (i >= 0) words[i] = w;
      $id('fInContext').value = w.ai.inContext;
      $id('fWhy').value = w.ai.why;
      $id('fRephrase').value = w.ai.rephrase;
      $id('fTip').value = w.ai.tip;
      renderAll();
      toast(`已讲解 “${w.word}”`, { type: 'success', ms: 3600 });
    } catch (e) {
      toast('讲解失败：' + (e.message || ''), { type: 'error', ms: 5000 });
    } finally {
      editing.busy = false;
      renderAiPanel(words.find((x) => x.id === id));
    }
  }

  async function runLookup(id) {
    editing.busy = true;
    const w0 = words.find((x) => x.id === id);
    if (w0) renderAiPanel(w0);
    try {
      const d = await api('/api/words/' + encodeURIComponent(id) + '/lookup', { method: 'POST' });
      const w = normalize(d.word);
      const i = words.findIndex((x) => x.id === w.id);
      if (i >= 0) words[i] = w;
      $id('fPhonetic').value = w.phonetic;
      $id('fTranslation').value = w.translation;
      $id('fDefinitions').value = (w.definitions || []).join('\n');
      renderAll();
      toast('词典信息已更新', { type: 'success' });
    } catch (e) {
      toast('查词典失败：' + (e.message || ''), { type: 'error' });
    } finally {
      editing.busy = false;
      renderAiPanel(words.find((x) => x.id === id));
    }
  }

  async function deleteEditing() {
    const w = words.find((x) => x.id === editing.id);
    if (!w) return;
    const ok = await askConfirm({ title: '删除词条', message: `确定删除 “${w.word}”（${fmtWhen(w.createdAt)}）吗？删除后无法恢复。`, confirmText: '删除' });
    if (!ok) return;
    try {
      await api('/api/words/' + encodeURIComponent(w.id), { method: 'DELETE' });
      words = words.filter((x) => x.id !== w.id);
      $id('editorDialog').close();
      renderAll();
      toast(`已删除 “${w.word}”`, { type: 'success' });
    } catch (e) {
      toast(e.message || '删除失败', { type: 'error' });
    }
  }

  /* ---------- 备份 / 导入 ---------- */
  function exportData() {
    const a = document.createElement('a');
    a.href = '/api/export';
    a.download = '';
    document.body.appendChild(a);
    a.click();
    a.remove();
    toast('正在导出备份文件', { type: 'success' });
  }

  function pickImportFile() {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = '.json,application/json';
    input.addEventListener('change', async () => {
      const f = input.files && input.files[0];
      if (!f) return;
      try {
        const parsed = JSON.parse(await f.text());
        const list = Array.isArray(parsed) ? parsed : (parsed.words || []);
        if (!Array.isArray(list)) throw new Error('bad');
        fileWords = list;
        $id('importMessage').textContent = `备份里有 ${list.length} 个词条，想怎么导入？`;
        $id('importDialog').showModal();
      } catch (e) {
        toast('文件无法解析，请选择本工具导出的 JSON 备份', { type: 'error', ms: 4200 });
      }
    });
    input.click();
  }

  async function runImport(mode) {
    if (!fileWords) return;
    const btn1 = $id('btnImportMerge'), btn2 = $id('btnImportReplace');
    btn1.disabled = btn2.disabled = true;
    try {
      const d = await api('/api/import', { method: 'POST', body: JSON.stringify({ mode, words: fileWords }) });
      fileWords = null;
      $id('importDialog').close();
      words = (d.words || []).map(normalize).filter(Boolean);
      renderAll();
      toast(mode === 'replace' ? `已覆盖导入：词库现有 ${d.total} 个词条` : `合并完成：新增 ${d.added} 个${d.skipped ? `，跳过重复 ${d.skipped} 个` : ''}`, { type: 'success', ms: 4200 });
    } catch (e) {
      toast(e.message || '导入失败', { type: 'error' });
    } finally {
      btn1.disabled = btn2.disabled = false;
    }
  }

  /* ---------- 主题 ---------- */
  function applyTheme() {
    const saved = localStorage.getItem(LS_THEME);
    const dark = saved ? saved === 'dark' : window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
    document.documentElement.dataset.theme = dark ? 'dark' : 'light';
    $id('btnTheme').textContent = dark ? '☀️' : '🌙';
  }
  function toggleTheme() {
    const next = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
    document.documentElement.dataset.theme = next;
    localStorage.setItem(LS_THEME, next);
    $id('btnTheme').textContent = next === 'dark' ? '☀️' : '🌙';
  }

  /* ---------- 事件 ---------- */
  function bind() {
    $id('btnAdd').addEventListener('click', () => openEditor(null));
    $id('btnExport').addEventListener('click', exportData);
    $id('btnImport').addEventListener('click', pickImportFile);
    $id('btnTheme').addEventListener('click', toggleTheme);
    $id('btnImportMerge').addEventListener('click', () => runImport('merge'));
    $id('btnImportReplace').addEventListener('click', () => runImport('replace'));

    let searchTimer = null;
    $id('searchInput').addEventListener('input', (e) => {
      clearTimeout(searchTimer);
      searchTimer = setTimeout(() => { ui.query = e.target.value; renderList(); }, 140);
    });
    $id('sortSelect').addEventListener('change', (e) => { ui.sort = e.target.value; renderList(); });

    $id('tabs').addEventListener('click', (e) => {
      const btn = e.target.closest('.tab');
      if (!btn) return;
      ui.filter = btn.dataset.filter;
      renderTabs();
      renderList();
    });

    $id('wordList').addEventListener('click', (e) => {
      const act = e.target.closest('[data-act]');
      const row = e.target.closest('.word-row');
      if (!row) return;
      const id = row.dataset.id;
      if (act) {
        e.stopPropagation();
        const w = words.find((x) => x.id === id);
        if (!w) return;
        if (act.dataset.act === 'speak') speakWord(w);
        else if (act.dataset.act === 'retry') runAiExplain(id);
        return;
      }
      openEditor(id);
    });
    $id('wordList').addEventListener('keydown', (e) => {
      const row = e.target.closest('.word-row');
      if (row && (e.key === 'Enter')) { e.preventDefault(); openEditor(row.dataset.id); }
    });

    $id('wordForm').addEventListener('submit', (e) => { e.preventDefault(); submitEditor(); });
    $id('fSentence').addEventListener('input', () => {
      if (!editing.id) renderAiPanel(null);
    });
    $id('btnSpeak').addEventListener('click', () => {
      const word = $id('fWord').value.trim();
      const w = words.find((x) => x.id === editing.id);
      if (word) speakWord({ word, audio: w ? w.audio : '' });
    });
    $id('btnExplain').addEventListener('click', async () => {
      if (editing.busy) return;
      if (!editing.id) {
        toast('请先保存这个词，再用 AI 讲解', {});
        return;
      }
      const sentence = $id('fSentence').value.trim();
      const cur = words.find((x) => x.id === editing.id) || {};
      if (sentence && sentence !== cur.sentence) {
        const d = await api('/api/words/' + encodeURIComponent(editing.id), {
          method: 'PATCH',
          body: JSON.stringify({ sentence }),
        });
        const w = normalize(d.word);
        const i = words.findIndex((x) => x.id === w.id);
        if (i >= 0) words[i] = w;
      }
      await runAiExplain(editing.id);
    });
    $id('btnLookup').addEventListener('click', async () => {
      if (editing.busy) return;
      const w = words.find((x) => x.id === editing.id);
      if (w) {
        await runLookup(w.id);
      } else {
        toast('请先保存这个词，再用“查词典”补全', {});
      }
    });
    $id('btnDelete').addEventListener('click', deleteEditing);

    $$('[data-close]').forEach((btn) => {
      btn.addEventListener('click', () => $id(btn.dataset.close).close());
    });
    $id('confirmCancel').addEventListener('click', () => { $id('confirmDialog').close(); resolveConfirm(false); });
    $id('confirmOk').addEventListener('click', () => { $id('confirmDialog').close(); resolveConfirm(true); });
    $id('confirmDialog').addEventListener('close', () => resolveConfirm(false));

    document.addEventListener('keydown', (e) => {
      const typing = /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement.tagName);
      const anyOpen = !!$('dialog[open]');
      if (anyOpen) return;
      if (typing) return;
      if (e.key === 'n' || e.key === 'N') { e.preventDefault(); openEditor(null); }
      else if (e.key === '/') { e.preventDefault(); $id('searchInput').focus(); }
    });
  }

  function resolveConfirm(v) {
    if (confirmResolver) { const r = confirmResolver; confirmResolver = null; r.resolve(v); }
  }

  /* ---------- 启动 ---------- */
  async function init() {
    applyTheme();
    bind();
    $id('storageInfo').textContent = '数据保存在本机 data 文件夹 · 词典释义与 AI 讲解在后台自动补全';
    await loadData();
    try {
      const cfg = await api('/api/config');
      if (!cfg.aiConfigured) {
        refreshConfig();
        if (words.length > 0) {
          toast('尚未检测到 DeepSeek key：在项目根目录把 .env.local.example 复制为 .env.local 并填入 key，保存后会自动生效（无需重启）。当前记录与词典补全不受影响。', { ms: 8000 });
        }
      }
    } catch (e) { /* 忽略 */ }
    setInterval(() => {
      if (document.hidden) return;
      const need = words.some((w) => aiStatus(w) === 'pending');
      if (need || words.length === 0) loadData(true);
      else loadData(true);
      refreshConfig();
    }, 12000);
    const params = new URLSearchParams(location.search);
    if (params.get('new') === '1') openEditor(null);
  }

  document.addEventListener('DOMContentLoaded', init);
})();
