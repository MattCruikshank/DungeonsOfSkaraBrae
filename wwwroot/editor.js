(() => {
  if (!window.inkClient || !window.inkClient.burgerEnabled) return;

  const MONACO_VERSION = '0.52.2';
  const MONACO_BASE = `https://cdn.jsdelivr.net/npm/monaco-editor@${MONACO_VERSION}/min/`;
  const GLOBALS_ID = '@globals';

  window.MonacoEnvironment = {
    getWorkerUrl: () =>
      'data:text/javascript;charset=utf-8,' + encodeURIComponent(`
        self.MonacoEnvironment = { baseUrl: '${MONACO_BASE}' };
        importScripts('${MONACO_BASE}vs/base/worker/workerMain.js');
      `),
  };

  const loader = document.createElement('script');
  loader.src = `${MONACO_BASE}vs/loader.min.js`;
  loader.onload = () => {
    require.config({ paths: { vs: `${MONACO_BASE}vs` } });
    require(['vs/editor/editor.main'], bootMonaco);
  };
  document.head.appendChild(loader);

  // ──────────────────────────────────────────────────────────────────────────
  let editor = null;
  let currentFileId = null;     // '@globals' or knot name (e.g. 'tavern')
  let lastSavedContent = '';
  let knotsCache = [];
  let knotsCacheAt = 0;
  const sessionPref = { value: null }; // 'always' | 'never' | null

  const displayName = id => id === GLOBALS_ID ? 'globals.ink' : id;
  const saveUrl = id => id === GLOBALS_ID ? '/api/globals' : `/api/knot/${encodeURIComponent(id)}`;

  function bootMonaco() {
    monaco.languages.register({ id: 'ink' });
    monaco.languages.setMonarchTokensProvider('ink', {
      defaultToken: '',
      tokenizer: {
        root: [
          [/^\s*\/\/.*$/, 'comment'],
          [/\/\*[\s\S]*?\*\//, 'comment'],
          [/^\s*={2,}\s*\w+\s*=*\s*$/, 'keyword.knot'],
          [/^\s*=\s+\w+/, 'keyword.stitch'],
          [/^\s*[*+]\s/, 'keyword.choice'],
          [/^\s*-\s+(?!>)/, 'keyword.gather'],
          [/->\s*\w+(\.\w+)?/, 'type.identifier'],
          [/\b(VAR|LIST|CONST|INCLUDE|EXTERNAL|RETURN|TEMP|true|false|not|and|or)\b/, 'keyword'],
          [/\{[^}]*\}/, 'string.interpolated'],
          [/#\w+(:\w+)?/, 'annotation'],
          [/\bEND\b|\bDONE\b/, 'type.identifier'],
          [/"[^"]*"/, 'string'],
          [/\b\d+\b/, 'number'],
        ],
      },
    });

    monaco.editor.defineTheme('ink-dark', {
      base: 'vs-dark',
      inherit: true,
      rules: [
        { token: 'comment', foreground: '6a737d', fontStyle: 'italic' },
        { token: 'keyword.knot', foreground: 'd2a8ff', fontStyle: 'bold' },
        { token: 'keyword.stitch', foreground: 'd2a8ff' },
        { token: 'keyword.choice', foreground: 'e3b341', fontStyle: 'bold' },
        { token: 'keyword.gather', foreground: '8b949e' },
        { token: 'type.identifier', foreground: '79c0ff' },
        { token: 'keyword', foreground: 'ff7b72' },
        { token: 'string.interpolated', foreground: 'a5d6ff' },
        { token: 'annotation', foreground: '7ee787' },
        { token: 'string', foreground: 'a5d6ff' },
      ],
      colors: { 'editor.background': '#0d1117' },
    });

    monaco.languages.registerCompletionItemProvider('ink', {
      triggerCharacters: [' ', '>'],
      provideCompletionItems: async (model, position) => {
        const lineUntil = model.getValueInRange({
          startLineNumber: position.lineNumber,
          startColumn: 1,
          endLineNumber: position.lineNumber,
          endColumn: position.column,
        });
        if (!/->\s*\w*$/.test(lineUntil)) return { suggestions: [] };
        const word = model.getWordUntilPosition(position);
        const range = {
          startLineNumber: position.lineNumber,
          endLineNumber: position.lineNumber,
          startColumn: word.startColumn,
          endColumn: word.endColumn,
        };
        const knots = await getKnotsCached();
        return {
          suggestions: knots.map(k => ({
            label: k,
            kind: monaco.languages.CompletionItemKind.Function,
            insertText: k,
            range,
          })),
        };
      },
    });

    editor = monaco.editor.create(document.getElementById('editor'), {
      value: '',
      language: 'ink',
      theme: 'ink-dark',
      automaticLayout: true,
      minimap: { enabled: false },
      fontSize: 13,
      fontFamily: "'Cascadia Code', Consolas, 'Courier New', monospace",
      tabSize: 4,
      readOnly: true,
      scrollBeyondLastLine: false,
      lineNumbers: 'on',
    });

    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => { saveNow(); });
    editor.onDidChangeModelContent(updateDirtyIndicator);
    editor.onMouseDown(onEditorMouseDown);

    document.getElementById('editor-knot-name').addEventListener('click', showPicker);

    if (window.inkClient.lastKnotMsg) handleKnot(window.inkClient.lastKnotMsg);
    window.addEventListener('ink:knot', e => handleKnot(e.detail));
    window.addEventListener('beforeunload', e => {
      if (isDirty()) { e.preventDefault(); e.returnValue = ''; }
    });
  }

  function isDirty() {
    return editor != null && editor.getValue() !== lastSavedContent;
  }

  function updateDirtyIndicator() {
    document.getElementById('editor-dirty').textContent = isDirty() ? '●' : '';
  }

  async function getKnotsCached() {
    const now = Date.now();
    if (now - knotsCacheAt < 2000 && knotsCache.length) return knotsCache;
    try {
      const r = await fetch('/api/knots');
      knotsCache = await r.json();
      knotsCacheAt = now;
    } catch { /* keep stale */ }
    return knotsCache;
  }

  async function onEditorMouseDown(e) {
    const isCtrl = e.event.ctrlKey || e.event.metaKey;
    if (!isCtrl) return;
    const pos = e.target.position;
    if (!pos) return;
    const model = editor.getModel();
    const word = model.getWordAtPosition(pos);
    if (!word) return;
    const lineUntil = model.getValueInRange({
      startLineNumber: pos.lineNumber,
      startColumn: 1,
      endLineNumber: pos.lineNumber,
      endColumn: word.endColumn,
    });
    if (!/->\s*\w+$/.test(lineUntil)) return;
    e.event.preventDefault();
    e.event.stopPropagation();
    await navigateTo(word.word);
  }

  async function navigateTo(fileId) {
    if (currentFileId === fileId) return;
    if (!await maybeSaveDirtyBuffer(`Save changes to "${displayName(currentFileId)}" before opening "${displayName(fileId)}"?`)) return;
    let source;
    try {
      const r = await fetch(saveUrl(fileId));
      if (r.status === 404 && fileId !== GLOBALS_ID) {
        const stub = `=== ${fileId} ===\nTODO: write \`${fileId}\`.\n-> END\n`;
        await fetch(saveUrl(fileId), { method: 'PUT', body: stub });
        source = stub;
      } else if (r.ok) {
        source = await r.text();
      } else {
        return;
      }
    } catch { return; }
    loadIntoEditor(fileId, source);
  }

  function loadIntoEditor(fileId, source) {
    currentFileId = fileId;
    lastSavedContent = source ?? '';
    editor.setValue(lastSavedContent);
    editor.updateOptions({ readOnly: false });
    document.getElementById('editor-knot-name').textContent = displayName(fileId);
    updateDirtyIndicator();
  }

  async function handleKnot(msg) {
    if (!editor) return;
    if (currentFileId === msg.name) return;
    if (!await maybeSaveDirtyBuffer(`Save changes to "${displayName(currentFileId)}" before moving to "${msg.name}"?`)) return;
    let source = msg.source;
    if (source == null) {
      try {
        const r = await fetch(saveUrl(msg.name));
        if (r.ok) source = await r.text();
      } catch { /* ignore */ }
    }
    loadIntoEditor(msg.name, source ?? '');
  }

  async function saveNow() {
    if (!editor || !currentFileId) return;
    const body = editor.getValue();
    try {
      const r = await fetch(saveUrl(currentFileId), {
        method: 'PUT',
        headers: { 'Content-Type': 'text/plain' },
        body,
      });
      if (!r.ok) {
        window.inkClient.writeToTerm(`\x1b[31m(save failed: HTTP ${r.status})\x1b[0m`);
        return;
      }
      lastSavedContent = body;
      updateDirtyIndicator();
    } catch (err) {
      window.inkClient.writeToTerm(`\x1b[31m(save error: ${err.message})\x1b[0m`);
    }
  }

  async function maybeSaveDirtyBuffer(prompt) {
    if (!isDirty()) return true;
    if (sessionPref.value === 'always') { await saveNow(); return true; }
    if (sessionPref.value === 'never') return true;
    const choice = await showModal(prompt);
    if (choice === 'save')   { await saveNow(); }
    if (choice === 'always') { sessionPref.value = 'always'; await saveNow(); }
    if (choice === 'never')  { sessionPref.value = 'never'; }
    return true;
  }

  function showModal(prompt) {
    return new Promise(resolve => {
      const backdrop = document.getElementById('modal-backdrop');
      document.getElementById('modal-prompt').textContent = prompt;
      backdrop.classList.add('open');
      const buttons = Array.from(backdrop.querySelectorAll('button'));
      const handlers = new Map();
      const close = (choice) => {
        backdrop.classList.remove('open');
        buttons.forEach(b => b.removeEventListener('click', handlers.get(b)));
        resolve(choice);
      };
      buttons.forEach(b => {
        const h = () => close(b.dataset.choice);
        handlers.set(b, h);
        b.addEventListener('click', h);
      });
    });
  }

  // ── picker ──────────────────────────────────────────────────────────────
  let closeOpenPicker = null;

  async function showPicker() {
    if (closeOpenPicker) { closeOpenPicker(); return; }
    const picker = document.getElementById('picker');
    const list = document.getElementById('picker-list');
    const knots = await getKnotsCached();

    list.innerHTML = '';
    const addItem = (id, label) => {
      const div = document.createElement('div');
      div.className = 'item' + (currentFileId === id ? ' current' : '');
      div.textContent = label;
      div.addEventListener('click', () => { closePicker(); navigateTo(id); });
      list.appendChild(div);
    };
    const addSep = () => {
      const s = document.createElement('div');
      s.className = 'sep';
      list.appendChild(s);
    };

    addItem(GLOBALS_ID, 'globals.ink');
    if (knots.length > 0) addSep();
    for (const k of knots) addItem(k, k);

    const titleRect = document.getElementById('editor-knot-name').getBoundingClientRect();
    picker.style.left = titleRect.left + 'px';
    picker.style.top = (titleRect.bottom + 2) + 'px';
    picker.classList.remove('picker-hidden');

    const onDocDown = (e) => {
      if (picker.contains(e.target)) return;
      if (e.target.id === 'editor-knot-name') return;
      closePicker();
    };
    const onKey = (e) => { if (e.key === 'Escape') closePicker(); };
    function closePicker() {
      picker.classList.add('picker-hidden');
      document.removeEventListener('mousedown', onDocDown);
      document.removeEventListener('keydown', onKey);
      closeOpenPicker = null;
    }
    closeOpenPicker = closePicker;
    setTimeout(() => {
      document.addEventListener('mousedown', onDocDown);
      document.addEventListener('keydown', onKey);
    }, 0);
  }
})();
