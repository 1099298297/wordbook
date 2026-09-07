'use strict';

(() => {
  const $ = (s, el = document) => el.querySelector(s);
  const $$ = (s, el = document) => Array.from(el.querySelectorAll(s));
  const $id = (id) => document.getElementById(id);
  const DAY = 86400000;
  const LS_THEME = 'wordbook.theme';
  const STATUS_LABEL = { done: '已讲解', pending: '讲解中…', error: '待重试', none: '未讲解' };

  let words = [];
  const ui = { filter: 'all', sort: 'newest', query: '' };
  const editing = { id: null, sceneIndex: 0, busy: false };
  let fileWords = null;
  let confirmResolver = null;

  /* ---------- 工具 ---------- */
  const esc = (s) => String(s == null ? '' : s)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  const now = () => Date.now();
  const lines = (s) => String(s || '').split('\n').map((x) => x.trim()).filter(Boolean);

  function aiOf(sc) {
    const a = (sc && sc.ai) || {};
    return {
      status: a.status || 'none',
      inContext: a.inContext || '',
      why: a.why || '',
      rephrase: a.rephrase || '',
      tip: a.tip || '',
      error: a.error || '',
    };
  }

  function normalizeScene(sc, i) {
    if (!sc || typeof sc !== 'object') return null;
    if (!sc.id) sc.id = 's' + (i || 0) + Math.random().toString(36).slice(2, 8);
    sc.text = sc.text || '';
    sc.addedAt = Number(sc.addedAt) || now();
    sc.updatedAt = Number(sc.updatedAt) || sc.addedAt;
    sc.ai = aiOf(sc);
    return sc;
  }

  function normalize(w) {
    if (!w || typeof w !== 'object') return null;
    if (!w.word || !String(w.word).trim()) return null;
    w.word = String(w.word).trim();
    w.phonetic = w.phonetic || '';
    w.translation = w.translation || '';
    w.definitions = Array.isArray(w.definitions) ? w.definitions.filter(Boolean) : [];
    w.audio = w.audio || '';
    w.note = w.note || '';
    w.createdAt = Number(w.createdAt) || now();
    w.updatedAt = Number(w.updatedAt) || w.createdAt;
    w.sentences = (Array.isArray(w.sentences) ? w.sentences : [])
      .map(normalizeScene).filter(Boolean)
      .sort((a, b) => b.addedAt - a.addedAt);
    if (!w.sentences.length) {
      w.sentences = [{ id: 's' + Math.random().toString(36).slice(2, 8), text: '', addedAt: w.createdAt, updatedAt: w.createdAt, ai: aiOf({}) }];
    }
    w.sentence = w.sentences[0].text;
    return w;
  }

  function latestScene(w) { return w.sentences[0]; }

  function wordAiStatus(w) {
    const a = aiOf(latestScene(w));
    const done = a.status === 'done' && (a.inContext || a.why || a.rephrase || a.tip);
    return done ? 'done' : (a.status === 'error' ? 'error' : (a.status === 'pending' ? 'pending' : 'none'));
  }

  function fmtTime(ts) {
    const d = new Date(ts);
    const t = new Date();
    const hm = `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
    const so = (x) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
    const diff = Math.round((so(t) - so(d)) / DAY);
    if (diff === 0) return `今天 ${hm}`;
    if (diff === 1) return `昨天 ${hm}`;
    if (d.getFullYear() === t.getFullYear()) return `${d.getMonth() + 1}月${d.getDate()}日 ${hm}`;
    return `${d.getFullYear()}年${d.getMonth() + 1}月${d.getDate()}日 ${hm}`;
  }

  function groupLabel(ts) {
    const d = new Date(ts);
    const t = new Date();
    const so = (x) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
    const diff = Math.round((so(t) - so(d)) / DAY);
    const week = ['日', '一', '二', '三', '四', '五', '六'][d.getDay()];
    if (diff === 0) return `今天 · ${d.getMonth() + 1}月${d.getDate()}日 周${week}`;
    if (diff === 1) return `昨天 · ${d.getMonth() + 1}月${d.getDate()}日 周${week}`;
    return `${d.getFullYear()}年${d.getMonth() + 1}月${d.getDate()}日 周${week}`;
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
      throw err;
    }
    return body || {};
  }

  async function loadData(silent) {
    try {
      const d = await api('/api/data');
      const next = (d.words || []).map(normalize).filter(Boolean);
      const sig = next.length + '|' + (next[0] ? next[0].updatedAt : '');
      const oldSig = words.length + '|' + (words[0] ? words[0].updatedAt : '');
      words = next;
      if (!silent || sig !== oldSig) renderAll();
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
  function resolveConfirm(v) {
    if (confirmResolver) { const r = confirmResolver; confirmResolver = null; r.resolve(v); }
  }

  /* ---------- 渲染 ---------- */
  function getFiltered() {
    let list = words.slice();
    const q = ui.query.trim().toLowerCase();
    if (ui.filter !== 'all') list = list.filter((w) => wordAiStatus(w) === ui.filter);
    if (q) {
      list = list.filter((w) => {
        const hay = [w.word, w.phonetic, w.translation, w.note]
          .concat(w.definitions || [])
          .concat(w.sentences.map((s) => s.text + ' ' + s.ai.inContext + ' ' + s.ai.why + ' ' + s.ai.rephrase + ' ' + s.ai.tip))
          .join('\n').toLowerCase();
        return hay.includes(q);
      });
    }
    if (ui.sort === 'alpha') list.sort((a, b) => a.word.toLowerCase().localeCompare(b.word.toLowerCase()));
    else list.sort((a, b) => (latestScene(b).addedAt || b.createdAt) - (latestScene(a).addedAt || a.createdAt));
    return list;
  }

  function renderStats() {
    const today = words.filter((w) => {
      const d = new Date(latestScene(w).addedAt || w.createdAt), t = new Date();
      return d.getFullYear() === t.getFullYear() && d.getMonth() === t.getMonth() && d.getDate() === t.getDate();
    }).length;
    const done = words.filter((w) => wordAiStatus(w) === 'done').length;
    const pending = words.filter((w) => wordAiStatus(w) === 'pending').length;
    $id('statsStrip').innerHTML = words.length
      ? `<span class="stat"><b>${words.length}</b>个单词</span>
         <span class="stat">今天记 <b>${today}</b></span>
         <span class="stat">已讲解 <b>${done}</b></span>
         ${pending ? `<span class="stat"><b style="color:var(--accent-ink)">${pending}</b>个讲解中</span>` : ''}`
      : '<span class="stat">词库还空着，按 Ctrl+Alt+Shift+W 随时记一个</span>';
  }

  function renderTabs() {
    const defs = [['all', '全部'], ['pending', '讲解中'], ['error', '待重试'], ['none', '未讲解']];
    $id('tabs').innerHTML = defs.map(([key, label]) => {
      const n = key === 'all' ? words.length : words.filter((w) => wordAiStatus(w) === key).length;
      return `<button class="tab${ui.filter === key ? ' active' : ''}" data-filter="${key}" role="tab" aria-selected="${ui.filter === key}">
        ${label}${n ? `<span class="count">${n}</span>` : ''}</button>`;
    }).join('');
  }

  function rowHtml(w) {
    const sc = latestScene(w);
    const a = aiOf(sc);
    const st = wordAiStatus(w);
    const gloss = a.inContext || w.translation || (w.definitions && w.definitions[0]) || '';
    const n = w.sentences.length;
    return `<div class="word-row" data-id="${esc(w.id)}" role="button" tabindex="0" aria-label="查看 ${esc(w.word)}">
      <div class="row-main">
        <div class="row-head">
          <span class="row-word">${esc(w.word)}</span>
          ${w.phonetic ? `<span class="row-phonetic">${esc(w.phonetic)}</span>` : ''}
          ${n > 1 ? `<span class="row-scenes" title="该词共记录 ${n} 句">${n} 句</span>` : ''}
          <button class="row-speak" data-act="speak" title="朗读">🔊</button>
        </div>
        <div class="row-sentence ${sc.text ? '' : 'empty'}">${esc(sc.text || '（还没有句子，点开补一句）')}</div>
        ${gloss ? `<div class="row-ai">${esc(gloss)}</div>`
          : `<div class="row-ai placeholder">${st === 'pending' ? 'AI 正在讲解…' : st === 'error' ? '讲解失败，点开可重试' : '暂无讲解'}</div>`}
      </div>
      <div class="row-side">
        <span class="row-time">${fmtTime(sc.addedAt || w.createdAt)}</span>
        <span>
          <span class="chip ai-${st}${st === 'pending' ? ' pending-dot' : ''}">${STATUS_LABEL[st]}</span>
          ${st === 'error' ? ' <button class="retry-btn" data-act="retry">重试</button>' : ''}
        </span>
      </div>
    </div>`;
  }

  function renderList() {
    const list = getFiltered();
    const box = $id('wordList');
    const empty = $id('emptyState');
    if (list.length === 0) {
      box.innerHTML = '';
      empty.hidden = false;
      empty.innerHTML = words.length === 0
        ? `<span class="empty-emoji">🪄</span><h3>词库还空着</h3>
           <p>复制单词或句子 → 按 <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>W</kbd> 记录。</p>`
        : `<span class="empty-emoji">🔍</span><h3>没有匹配的记录</h3><p>换个关键词或筛选条件。</p>`;
      return;
    }
    empty.hidden = true;
    let html = '';
    let lastKey = '';
    for (const w of list) {
      const key = groupLabel(latestScene(w).addedAt || w.createdAt);
      if (key !== lastKey) {
        html += `<div class="day-heading">${key}</div>`;
        lastKey = key;
      }
      html += rowHtml(w);
    }
    box.innerHTML = html;
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
  function curWord() { return words.find((x) => x.id === editing.id); }

  function openEditor(idOrNull) {
    editing.id = idOrNull || null;
    editing.sceneIndex = 0;
    editing.busy = false;
    const w = idOrNull ? curWord() : null;
    $id('editorTitle').textContent = w ? '编辑词条' : '记一个词';
    $id('btnDelete').hidden = !w;
    $id('btnDeleteScene').hidden = !w;
    $id('fWord').value = w ? w.word : '';
    $id('fPhonetic').value = w ? w.phonetic : '';
    $id('fTranslation').value = w ? w.translation : '';
    $id('fDefinitions').value = w ? (w.definitions || []).join('\n') : '';
    $id('fNote').value = w ? w.note : '';
    $id('moreFields').open = false;
    fillScene(w);
    updateSceneNav();
    $id('editorDialog').showModal();
    $id('fWord').focus();
    if ($id('fWord').value) $id('fWord').select();
  }

  function fillScene(w) {
    const sc = w ? w.sentences[Math.min(editing.sceneIndex, w.sentences.length - 1)] : null;
    $id('fSentence').value = sc ? sc.text : '';
    const a = aiOf(sc);
    $id('fInContext').value = a.inContext;
    $id('fWhy').value = a.why;
    $id('fRephrase').value = a.rephrase;
    $id('fTip').value = a.tip;
    renderAiStatus(w, sc);
    updateSceneNav();
  }

  function updateSceneNav() {
    const w = curWord();
    const n = w ? w.sentences.length : 1;
    const i = editing.id ? Math.min(editing.sceneIndex, n - 1) : 0;
    $id('scenePrev').disabled = editing.id ? i <= 0 : true;
    $id('sceneNext').disabled = editing.id ? i >= n - 1 : true;
    $id('sceneIndex').textContent = editing.id ? `第 ${i + 1} / ${n} 句` : '第 1 / 1 句';
    $id('sceneDate').textContent = editing.id ? fmtTime(w.sentences[i].addedAt) : '';
    $id('btnDeleteScene').hidden = !editing.id || n <= 1;
  }

  function renderAiStatus(w, sc) {
    const panel = $id('aiPanel');
    const btn = $id('btnExplain');
    const a = aiOf(sc);
    const hasText = !!(a.inContext || a.why || a.rephrase || a.tip);
    if (!editing.id) { panel.hidden = true; return; }
    panel.hidden = false;
    btn.disabled = editing.busy;
    const box = $id('aiStatus');
    if (editing.busy) {
      box.className = 'ai-status busy';
      box.textContent = 'AI 正在结合这句话讲解，请稍候…';
    } else if (a.status === 'pending') {
      box.className = 'ai-status busy';
      box.textContent = '这句话的讲解生成中…';
    } else if (a.status === 'done' && hasText) {
      box.className = 'ai-status done';
      box.textContent = '已结合这句话讲解，以下内容可修改。';
    } else if (a.status === 'error') {
      box.className = 'ai-status error';
      box.textContent = '上次讲解失败：' + (a.error || '未知原因');
    } else {
      box.className = 'ai-status';
      box.textContent = '点下方“AI 讲解这句”即可生成讲解。';
    }
  }

  async function submitEditor() {
    if (editing.busy) return;
    const word = $id('fWord').value.trim();
    if (!word) { toast('请填写单词', { type: 'error' }); return; }
    const sentence = $id('fSentence').value.trim();
    editing.busy = true;
    const btn = $id('btnSave');
    btn.disabled = true;
    try {
      if (editing.id) {
        const w = curWord();
        const sc = w.sentences[Math.min(editing.sceneIndex, w.sentences.length - 1)];
        const patch = {
          word,
          phonetic: $id('fPhonetic').value.trim(),
          translation: $id('fTranslation').value.trim(),
          definitions: lines($id('fDefinitions').value),
          note: $id('fNote').value.trim(),
        };
        const ai = {
          inContext: $id('fInContext').value.trim(),
          why: $id('fWhy').value.trim(),
          rephrase: $id('fRephrase').value.trim(),
          tip: $id('fTip').value.trim(),
        };
        if (sentence !== sc.text) {
          await api(`/api/words/${w.id}/scenes/${sc.id}`, { method: 'PATCH', body: JSON.stringify({ text: sentence }) });
          ai.status = 'error';
          ai.error = '句子已修改，请重新讲解';
        } else {
          ai.status = 'done';
          ai.error = '';
        }
        // AI 讲解是逐句保存的：手动改过的讲解内容直接写回该句
        patch.scene = { id: sc.id, ai };
        // 场景讲解先写回服务端（简化：句子未变且讲解有内容时）
        if (sentence === sc.text && (ai.inContext || ai.why || ai.rephrase || ai.tip)) {
          await api(`/api/words/${w.id}/scenes/${sc.id}/ai`, { method: 'PATCH', body: JSON.stringify({ ai }) });
        }
        await api(`/api/words/${w.id}`, { method: 'PATCH', body: JSON.stringify(patch) });
        $id('editorDialog').close();
        toast(`已保存 “${word}”`, { type: 'success' });
      } else {
        const d = await api('/api/words/batch', {
          method: 'POST',
          body: JSON.stringify({ items: [{ word, sentence }] }),
        });
        const added = Number(d.added) || 0;
        const hit = Number(d.hit) || 0;
        $id('editorDialog').close();
        if (added === 0 && hit > 0) {
          toast(`“${word}” 已在词库中（命中，未重复创建）`, { type: 'success' });
        } else if (hit > 0) {
          toast(`新增 ${added} 个 · 命中 ${hit} 个（句子已并入原词条）`, { type: 'success' });
        } else {
          toast(`已加入 “${word}”，正在补释义讲解…`, { type: 'success', ms: 4200 });
        }
      }
      await loadData();
    } catch (e) {
      toast(e.message || '保存失败', { type: 'error' });
    } finally {
      editing.busy = false;
      btn.disabled = false;
    }
  }

  async function runAiExplain() {
    const w = curWord();
    if (!w || editing.busy) return;
    const sc = w.sentences[Math.min(editing.sceneIndex, w.sentences.length - 1)];
    editing.busy = true;
    renderAiStatus(w, sc);
    try {
      const d = await api(`/api/words/${w.id}/scenes/${sc.id}/explain`, { method: 'POST' });
      const nw = normalize(d.word);
      const i = words.findIndex((x) => x.id === nw.id);
      if (i >= 0) words[i] = nw;
      fillScene(nw);
      renderAll();
      toast(`已讲解“${w.word}”这一句`, { type: 'success' });
    } catch (e) {
      toast('讲解失败：' + (e.message || ''), { type: 'error', ms: 5000 });
    } finally {
      editing.busy = false;
      renderAiStatus(curWord(), curWord() && curWord().sentences[Math.min(editing.sceneIndex, curWord().sentences.length - 1)]);
    }
  }

  async function deleteCurrentScene() {
    const w = curWord();
    if (!w) return;
    const sc = w.sentences[Math.min(editing.sceneIndex, w.sentences.length - 1)];
    const ok = await askConfirm({
      title: '删除这一句',
      message: `确定删除 “${w.word}” 的这句场景吗？该句讲解会一起删除，单词本身保留。`,
      confirmText: '删除',
    });
    if (!ok) return;
    try {
      const d = await api(`/api/words/${w.id}/scenes/${sc.id}`, { method: 'DELETE' });
      const nw = normalize(d.word);
      const i = words.findIndex((x) => x.id === nw.id);
      if (i >= 0) words[i] = nw;
      editing.sceneIndex = Math.max(0, editing.sceneIndex - 1);
      fillScene(nw);
      renderAll();
      toast('已删除该句场景', { type: 'success' });
    } catch (e) {
      toast(e.message || '删除失败', { type: 'error' });
    }
  }

  async function deleteWord() {
    const w = curWord();
    if (!w) return;
    const ok = await askConfirm({
      title: '删除单词',
      message: `确定删除 “${w.word}” 及它的所有句子吗？删除后无法恢复。`,
      confirmText: '删除',
    });
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

  async function runLookup() {
    const w = curWord();
    if (!w || editing.busy) return;
    editing.busy = true;
    try {
      const d = await api(`/api/words/${w.id}/lookup`, { method: 'POST' });
      const nw = normalize(d.word);
      const i = words.findIndex((x) => x.id === nw.id);
      if (i >= 0) words[i] = nw;
      $id('fPhonetic').value = nw.phonetic;
      $id('fTranslation').value = nw.translation;
      $id('fDefinitions').value = (nw.definitions || []).join('\n');
      renderAll();
      toast('词典信息已更新', { type: 'success' });
    } catch (e) {
      toast(e.message || '查词典失败', { type: 'error' });
    } finally {
      editing.busy = false;
    }
  }

  /* ---------- 备份 / 导入 ---------- */
  function exportData() {
    const a = document.createElement('a');
    a.href = '/api/export';
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
    const b1 = $id('btnImportMerge'), b2 = $id('btnImportReplace');
    b1.disabled = b2.disabled = true;
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
      b1.disabled = b2.disabled = false;
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
        else if (act.dataset.act === 'retry') {
          editing.id = id;
          editing.sceneIndex = 0;
          runAiExplain();
        }
        return;
      }
      openEditor(id);
    });
    $id('wordList').addEventListener('keydown', (e) => {
      const row = e.target.closest('.word-row');
      if (row && e.key === 'Enter') { e.preventDefault(); openEditor(row.dataset.id); }
    });

    $id('wordForm').addEventListener('submit', (e) => { e.preventDefault(); submitEditor(); });
    $id('scenePrev').addEventListener('click', () => {
      const w = curWord();
      if (!w || editing.sceneIndex <= 0) return;
      editing.sceneIndex--;
      fillScene(w);
    });
    $id('sceneNext').addEventListener('click', () => {
      const w = curWord();
      if (!w || editing.sceneIndex >= w.sentences.length - 1) return;
      editing.sceneIndex++;
      fillScene(w);
    });
    $id('btnExplain').addEventListener('click', () => { if (editing.id) runAiExplain(); else toast('请先保存这个词，再对句子讲解', {}); });
    $id('btnDeleteScene').addEventListener('click', deleteCurrentScene);
    $id('btnSpeak').addEventListener('click', () => {
      const word = $id('fWord').value.trim();
      const w = curWord();
      if (word) speakWord({ word, audio: w ? w.audio : '' });
    });
    $id('btnLookup').addEventListener('click', runLookup);
    $id('btnDelete').addEventListener('click', deleteWord);

    $$('[data-close]').forEach((btn) => {
      btn.addEventListener('click', () => $id(btn.dataset.close).close());
    });
    $id('confirmCancel').addEventListener('click', () => { $id('confirmDialog').close(); resolveConfirm(false); });
    $id('confirmOk').addEventListener('click', () => { $id('confirmDialog').close(); resolveConfirm(true); });
    $id('confirmDialog').addEventListener('close', () => resolveConfirm(false));

    document.addEventListener('keydown', (e) => {
      const typing = /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement.tagName);
      if ($('dialog[open]')) return;
      if (typing) return;
      if (e.key === 'n' || e.key === 'N') { e.preventDefault(); openEditor(null); }
      else if (e.key === '/') { e.preventDefault(); $id('searchInput').focus(); }
    });
  }

  /* ---------- 启动 ---------- */
  async function init() {
    applyTheme();
    bind();
    await loadData();
    refreshConfig();
    setInterval(() => {
      if (document.hidden) return;
      loadData(true);
      refreshConfig();
    }, 12000);
  }

  document.addEventListener('DOMContentLoaded', init);
})();
