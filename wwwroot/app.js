window.inkClient = (() => {
  const urlBurger = new URLSearchParams(location.search).get('burger') === 'true';
  if (urlBurger) {
    document.body.classList.add('burger');
    fetch('/api/auth/burger', { method: 'POST' }).catch(() => {});
  }

  const term = new Terminal({
    fontFamily: "'Cascadia Code', Consolas, 'Courier New', monospace",
    fontSize: 14,
    cursorBlink: false,
    cursorStyle: 'bar',
    cursorInactiveStyle: 'none',
    disableStdin: false,
    theme: {
      background: '#0d1117',
      foreground: '#c9d1d9',
      cursor: '#58a6ff',
      selectionBackground: '#264f78',
    },
    linkHandler: {
      allowNonHttpProtocols: true,
      activate: (_event, uri) => {
        if (uri.startsWith('ink://choice/')) {
          const idx = parseInt(uri.substring('ink://choice/'.length), 10);
          if (Number.isFinite(idx)) sendChoice(idx);
          return;
        }
        window.open(uri, '_blank', 'noopener,noreferrer');
      },
      hover: () => {},
      leave: () => {},
    },
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(document.getElementById('terminal'));
  fit.fit();
  term.write('\x1b[?25l'); // hide cursor
  term.focus();
  new ResizeObserver(() => { try { fit.fit(); } catch { /* term closed */ } })
    .observe(document.getElementById('terminal-pane'));

  let socket = null;
  let lastKnotMsg = null;
  let choiceState = null; // { items: [{i, text}], selectedIndex }

  function sendChoice(i) {
    if (socket && socket.readyState === WebSocket.OPEN) {
      socket.send(JSON.stringify({ type: 'choose', i }));
    }
    choiceState = null;
  }

  function osc8Link(uri, text) {
    return `\x1b]8;;${uri}\x07${text}\x1b]8;;\x07`;
  }

  function renderChoiceLine(rawText, idx, selected) {
    const link = osc8Link(`ink://choice/${idx}`, rawText);
    if (selected) {
      return `\x1b[1;93m> ${idx + 1}. ${link}\x1b[0m`;
    }
    return `\x1b[2m  ${idx + 1}. ${link}\x1b[0m`;
  }

  function renderChoiceBlock(state, isInitial) {
    let out = '';
    if (!isInitial) out += `\x1b[${state.items.length}A`;
    out += '\r';
    for (let i = 0; i < state.items.length; i++) {
      out += '\x1b[2K' + renderChoiceLine(state.items[i].text, i, i === state.selectedIndex) + '\r\n';
    }
    term.write(out);
  }

  function moveSelection(delta) {
    if (!choiceState) return;
    const n = choiceState.items.length;
    const next = Math.max(0, Math.min(n - 1, choiceState.selectedIndex + delta));
    if (next === choiceState.selectedIndex) return;
    choiceState.selectedIndex = next;
    renderChoiceBlock(choiceState, false);
  }

  function connect() {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    socket = new WebSocket(`${proto}//${location.host}/ws`);
    socket.addEventListener('open', () => term.writeln('\x1b[2m(connected)\x1b[0m'));
    socket.addEventListener('close', () => term.writeln('\x1b[2;31m(disconnected)\x1b[0m'));
    socket.addEventListener('error', () => term.writeln('\x1b[31m(socket error)\x1b[0m'));
    socket.addEventListener('message', e => {
      let msg;
      try { msg = JSON.parse(e.data); } catch { return; }
      switch (msg.type) {
        case 'text':
          choiceState = null;
          term.writeln(msg.ansi ?? '');
          break;
        case 'choices':
          choiceState = { items: msg.items ?? [], selectedIndex: 0 };
          term.writeln('');
          renderChoiceBlock(choiceState, /*isInitial*/ true);
          break;
        case 'warn':
          choiceState = null;
          term.writeln(msg.ansi ?? '');
          break;
        case 'end':
          choiceState = null;
          term.writeln('');
          term.writeln('\x1b[2m-- END --\x1b[0m');
          break;
        case 'knot':
          lastKnotMsg = msg;
          window.dispatchEvent(new CustomEvent('ink:knot', { detail: msg }));
          break;
      }
    });
  }
  connect();

  term.attachCustomKeyEventHandler(e => {
    if (e.type !== 'keydown') return true;
    if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && (e.key === 'r' || e.key === 'R')) {
      location.reload();
      return false;
    }
    if (!choiceState) return true;
    if (e.key === 'ArrowUp')   { moveSelection(-1); return false; }
    if (e.key === 'ArrowDown') { moveSelection(+1); return false; }
    if (e.key === 'Enter')     { sendChoice(choiceState.selectedIndex); return false; }
    return true;
  });

  if (urlBurger) {
    const splitter = document.getElementById('splitter');
    const editor = document.getElementById('editor-pane');
    let dragging = false;
    splitter.addEventListener('mousedown', e => {
      dragging = true;
      e.preventDefault();
      document.body.style.cursor = 'ns-resize';
    });
    window.addEventListener('mousemove', e => {
      if (!dragging) return;
      const h = window.innerHeight - e.clientY;
      const clamped = Math.max(80, Math.min(window.innerHeight - 100, h));
      editor.style.flexBasis = clamped + 'px';
    });
    window.addEventListener('mouseup', () => {
      if (!dragging) return;
      dragging = false;
      document.body.style.cursor = '';
    });
  }

  return {
    sendChoice,
    get lastKnotMsg() { return lastKnotMsg; },
    get burgerEnabled() { return urlBurger; },
    writeToTerm: (s) => term.writeln(s),
  };
})();
