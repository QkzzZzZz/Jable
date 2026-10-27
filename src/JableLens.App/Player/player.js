// JableLens 播放器：注入到站点自己的播放页里运行（同源，视频请求由浏览器发出，能通过 Cloudflare）。
// 宿主（C#）调用 window.JL.*；播放器通过 chrome.webview.postMessage 回报。
(() => {
  if (window.JL) return;

  const post = (msg) => { try { window.chrome.webview.postMessage(msg); } catch (e) { } };
  const fmt = (s) => {
    if (!isFinite(s) || s < 0) s = 0;
    s = Math.floor(s);
    const h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, sec = s % 60;
    const mm = String(m).padStart(2, '0'), ss = String(sec).padStart(2, '0');
    return h > 0 ? `${h}:${mm}:${ss}` : `${mm}:${ss}`;
  };
  const fmtOffset = (v) => (v >= 0 ? '+' : '−') + Math.abs(v).toFixed(1) + ' 秒';

  const svg = (d) => `<svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor">${d}</svg>`;
  const ICON = {
    play: svg('<path d="M8 5.14v13.72a1 1 0 0 0 1.52.85l10.6-6.86a1 1 0 0 0 0-1.7L9.52 4.29A1 1 0 0 0 8 5.14z"/>'),
    pause: svg('<rect x="6" y="4.5" width="4" height="15" rx="1.2"/><rect x="14" y="4.5" width="4" height="15" rx="1.2"/>'),
    back: svg('<path d="M12 5V2L7 6l5 4V7a6 6 0 1 1-6 6H4a8 8 0 1 0 8-8z"/><text x="12" y="16.2" font-size="6.5" text-anchor="middle" font-family="Segoe UI" font-weight="700">10</text>'),
    fwd: svg('<path d="M12 5V2l5 4-5 4V7a6 6 0 1 0 6 6h2a8 8 0 1 1-8-8z"/><text x="12" y="16.2" font-size="6.5" text-anchor="middle" font-family="Segoe UI" font-weight="700">10</text>'),
    subs: svg('<path d="M4 5h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2zm1 9v2h6v-2H5zm8 0v2h6v-2h-6zm-8-3v2h3v-2H5zm5 0v2h9v-2h-9z"/>'),
    mini: svg('<path d="M3 5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v6h-2V5H5v14h6v2H5a2 2 0 0 1-2-2V5zm10 9a1 1 0 0 1 1-1h7a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1h-7a1 1 0 0 1-1-1v-6z"/>'),
    vol: svg('<path d="M3 9v6h4l5 4V5L7 9H3z"/><path d="M16.5 12a4.5 4.5 0 0 0-2.5-4v8a4.5 4.5 0 0 0 2.5-4zM14 3.2v2.1a7 7 0 0 1 0 13.4v2.1a9 9 0 0 0 0-17.6z"/>'),
    volLow: svg('<path d="M3 9v6h4l5 4V5L7 9H3z"/><path d="M16.5 12a4.5 4.5 0 0 0-2.5-4v8a4.5 4.5 0 0 0 2.5-4z"/>'),
    mute: svg('<path d="M3 9v6h4l5 4V5L7 9H3z"/><path d="M16 9.4l1.4-1.4 2.1 2.1 2.1-2.1 1.4 1.4-2.1 2.1 2.1 2.1-1.4 1.4-2.1-2.1-2.1 2.1-1.4-1.4 2.1-2.1z"/>'),
    dl: svg('<path d="M11 4h2v8.2l3.3-3.3 1.4 1.4-5.7 5.7-5.7-5.7 1.4-1.4 3.3 3.3V4zM5 18h14v2H5z"/>'),
    full: svg('<path d="M4 4h6v2H6v4H4V4zm10 0h6v6h-2V6h-4V4zM4 14h2v4h4v2H4v-6zm14 0h2v6h-6v-2h4v-4z"/>'),
  };

  const CSS = `
  html, body { margin:0; padding:0; background:#000; height:100%; overflow:hidden; }
  #jl { position:fixed; inset:0; background:#000; font-family:"Microsoft YaHei UI","Segoe UI",sans-serif; color:#fff; user-select:none; z-index:2147483647; }
  #jl video { position:absolute; inset:0; width:100%; height:100%; object-fit:contain; background:#000; }
  #jl .sub { position:absolute; left:5%; right:5%; bottom:12%; text-align:center; pointer-events:none; transition:bottom .2s; }
  #jl.controls-on .sub { bottom:calc(12% + 56px); }
  #jl .sub span { display:inline; background:rgba(0,0,0,.55); padding:2px 10px; border-radius:4px; line-height:1.6;
                  font-size:clamp(14px, 3.2vw, 34px); white-space:pre-line; text-shadow:0 0 3px #000, 0 0 3px #000; box-decoration-break:clone; -webkit-box-decoration-break:clone; }
  #jl .toast { position:absolute; top:12%; left:50%; transform:translateX(-50%); background:rgba(0,0,0,.7); padding:8px 16px; border-radius:6px; font-size:15px; opacity:0; transition:opacity .2s; pointer-events:none; }
  #jl .toast.on { opacity:1; }
  #jl .title { position:absolute; top:0; left:0; right:0; padding:12px 16px 24px; font-size:15px; background:linear-gradient(rgba(0,0,0,.7),transparent); opacity:0; transition:opacity .2s; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  #jl .bar { position:absolute; left:0; right:0; bottom:0; padding:22px 12px 8px; background:linear-gradient(transparent,rgba(0,0,0,.8)); opacity:0; transition:opacity .2s; }
  #jl.controls-on .bar, #jl.controls-on .title { opacity:1; }
  #jl.controls-off { cursor:none; }
  #jl .seek { position:relative; height:16px; cursor:pointer; margin:0 4px 4px; }
  #jl .seek .track { position:absolute; left:0; right:0; top:6px; height:4px; background:rgba(255,255,255,.25); border-radius:2px; }
  #jl .seek .buf { position:absolute; left:0; top:6px; height:4px; background:rgba(255,255,255,.4); border-radius:2px; }
  #jl .seek .done { position:absolute; left:0; top:6px; height:4px; background:#e50914; border-radius:2px; }
  #jl .seek .knob { position:absolute; top:2px; width:12px; height:12px; margin-left:-6px; border-radius:50%; background:#e50914; }
  #jl .seek .hover { position:absolute; bottom:18px; transform:translateX(-50%); background:rgba(0,0,0,.8); padding:2px 6px; border-radius:3px; font-size:12px; display:none; }
  #jl .seek:hover .hover { display:block; }
  #jl .row { display:flex; align-items:center; gap:4px; }
  #jl button { background:none; border:0; color:#fff; font:inherit; font-size:14px; padding:6px 9px; border-radius:4px; cursor:pointer; white-space:nowrap; }
  #jl button:hover { background:rgba(255,255,255,.15); }
  #jl button.on { color:#ff4d55; }
  #jl .seek .done, #jl .seek .knob { background:#e50914; }
  #jl .time { font-size:13px; opacity:.9; padding:0 6px; font-variant-numeric:tabular-nums; }
  #jl .grow { flex:1; }
  #jl .menu { position:absolute; bottom:56px; background:rgba(20,20,20,.95); border-radius:6px; padding:6px; display:none; min-width:150px; max-height:60vh; overflow:auto; }
  #jl .menu.open { display:block; }
  #jl .menu .item { padding:7px 12px; border-radius:4px; cursor:pointer; font-size:14px; white-space:nowrap; }
  #jl .menu .item:hover { background:rgba(255,255,255,.12); }
  #jl .menu .item.sel { color:#ff4d55; }
  #jl .menu .sep { height:1px; background:rgba(255,255,255,.15); margin:5px 0; }
  #jl .menu .hint { font-size:12px; opacity:.6; padding:4px 12px; white-space:normal; max-width:260px; }
  #jl .menu .btns { display:flex; gap:4px; padding:2px 6px; flex-wrap:wrap; }
  #jl .menu .btns button { background:rgba(255,255,255,.1); font-size:13px; padding:5px 8px; }
  #jl .center { position:absolute; top:50%; left:50%; transform:translate(-50%,-50%); display:none; pointer-events:none; }
  #jl.loading .center { display:block; }
  #jl .spinner { width:54px; height:54px; border-radius:50%; border:4px solid rgba(255,255,255,.2); border-top-color:#e50914; animation:jlspin .8s linear infinite; }
  @keyframes jlspin { to { transform:rotate(360deg); } }
  #jl .bigplay { position:absolute; top:50%; left:50%; width:84px; height:84px; margin:-42px 0 0 -42px; border-radius:50%; background:rgba(0,0,0,.55);
                 display:flex; align-items:center; justify-content:center; pointer-events:none; opacity:0; transform:scale(.85); transition:opacity .2s, transform .2s; backdrop-filter:blur(6px); }
  #jl .bigplay svg { width:40px; height:40px; margin-left:4px; }
  #jl.paused:not(.loading) .bigplay { opacity:1; transform:scale(1); }
  #jl button.icon { padding:7px 8px; display:inline-flex; align-items:center; }
  #jl button.icon svg { width:24px; height:24px; }
  #jl .bar { backdrop-filter:none; }
  #jl .seek:hover .track, #jl .seek:hover .buf, #jl .seek:hover .done { height:6px; top:5px; }
  #jl.mini .title, #jl.mini .opt-hide-mini { display:none; }
  #jl .vol { display:inline-flex; align-items:center; }
  #jl .vol .vslide { width:0; overflow:hidden; transition:width .18s ease; display:flex; align-items:center; height:32px; }
  #jl .vol:hover .vslide, #jl .vol.drag .vslide { width:142px; }
  #jl .vtrack { position:relative; width:88px; height:4px; margin:0 8px; background:rgba(255,255,255,.3); border-radius:2px; cursor:pointer; flex:none; }
  #jl .vtrack::before { content:""; position:absolute; left:0; right:0; top:-10px; bottom:-10px; }
  #jl .vfill { position:absolute; left:0; top:0; bottom:0; background:#fff; border-radius:2px; }
  #jl .vknob { position:absolute; top:-4px; width:12px; height:12px; margin-left:-6px; border-radius:50%; background:#fff; box-shadow:0 0 4px rgba(0,0,0,.5); }
  #jl .vpct { font-size:12px; opacity:.85; width:34px; font-variant-numeric:tabular-nums; }
  #jl.mini .sub span { font-size:clamp(12px, 4.5vw, 20px); }
  `;

  const state = {
    video: null, hls: null, cues: [], offset: 0, subOn: true, tracks: [], track: null,
    rate: 1, lastPost: 0, hideTimer: 0, root: null, els: {}, mini: false, toastTimer: 0,
  };

  function el(tag, cls, html) { const e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; }

  function toast(text) {
    const t = state.els.toast; t.textContent = text; t.classList.add('on');
    clearTimeout(state.toastTimer); state.toastTimer = setTimeout(() => t.classList.remove('on'), 1400);
  }

  function showControls() {
    const r = state.root; r.classList.add('controls-on'); r.classList.remove('controls-off');
    clearTimeout(state.hideTimer);
    state.hideTimer = setTimeout(() => {
      if (state.video && !state.video.paused && !r.querySelector('.menu.open')) { r.classList.remove('controls-on'); r.classList.add('controls-off'); }
    }, 2500);
  }

  // 站点自己的播放器、广告、定时器尽量停掉
  function silenceSite() {
    try { window.stop(); } catch (e) { }
    try { if (window.hls && window.hls.destroy) window.hls.destroy(); } catch (e) { }
    try { if (window.player && window.player.destroy) window.player.destroy(); } catch (e) { }
    document.querySelectorAll('video, audio').forEach(v => { try { v.pause(); v.removeAttribute('src'); v.load(); } catch (e) { } });
    const maxId = setTimeout(() => { }, 0);
    for (let i = 0; i <= maxId; i++) { clearTimeout(i); clearInterval(i); }
    window.open = () => null;
  }

  function build(opts) {
    document.head.innerHTML = '<meta charset="utf-8"><title>' + (opts.title || '').replace(/</g, '&lt;') + '</title>';
    document.body.innerHTML = '';
    document.body.removeAttribute('class');
    document.body.removeAttribute('style');
    const style = el('style'); style.textContent = CSS; document.head.appendChild(style);

    const r = el('div'); r.id = 'jl'; r.className = 'loading controls-on paused';
    r.innerHTML = `
      <video playsinline></video>
      <div class="sub"><span></span></div>
      <div class="center"><div class="spinner"></div></div>
      <div class="bigplay">${ICON.play}</div>
      <div class="toast"></div>
      <div class="title"></div>
      <div class="bar">
        <div class="seek"><div class="track"></div><div class="buf"></div><div class="done"></div><div class="knob"></div><div class="hover"></div></div>
        <div class="row">
          <button data-a="play" class="icon" title="播放/暂停（空格）">${ICON.play}</button>
          <button data-a="back" class="icon" title="后退 10 秒（←）">${ICON.back}</button>
          <button data-a="fwd" class="icon" title="前进 10 秒（→）">${ICON.fwd}</button>
          <span class="vol"><button data-a="mute" class="icon" title="静音（M），↑↓ 或滚轮调音量">${ICON.vol}</button><span class="vslide"><span class="vtrack"><span class="vfill"></span><span class="vknob"></span></span><span class="vpct"></span></span></span>
          <span class="time">00:00 / 00:00</span>
          <span class="grow"></span>
          <button data-a="rate" title="倍速">1.0×</button>
          <button data-a="subs" class="icon" title="字幕">${ICON.subs}</button>
          <button data-a="dl" class="icon opt-hide-mini" title="下载">${ICON.dl}</button>
          <button data-a="mini" class="icon" title="小窗（置顶）">${ICON.mini}</button>
          <button data-a="full" class="icon opt-hide-mini" title="全屏（F）">${ICON.full}</button>
        </div>
        <div class="menu rate-menu" style="right:200px"></div>
        <div class="menu subs-menu" style="right:110px"></div>
      </div>`;
    document.body.appendChild(r);
    state.root = r;
    const q = (s) => r.querySelector(s);
    state.els = {
      video: q('video'), sub: q('.sub span'), toast: q('.toast'), title: q('.title'), time: q('.time'),
      seek: q('.seek'), buf: q('.buf'), done: q('.done'), knob: q('.knob'), hover: q('.hover'),
      play: q('[data-a=play]'), rateBtn: q('[data-a=rate]'), subsBtn: q('[data-a=subs]'), miniBtn: q('[data-a=mini]'),
      muteBtn: q('[data-a=mute]'), vol: q('.vol'), vtrack: q('.vtrack'), vfill: q('.vfill'), vknob: q('.vknob'), vpct: q('.vpct'),
      rateMenu: q('.rate-menu'), subsMenu: q('.subs-menu'), center: q('.center'),
    };
    state.els.title.textContent = opts.title || '';
    state.video = state.els.video;
    wire();
  }

  function wire() {
    const v = state.video, r = state.root, E = state.els;

    r.addEventListener('mousemove', showControls);
    r.addEventListener('mouseleave', () => { if (!v.paused) { r.classList.remove('controls-on'); } });

    // 单击暂停/播放；双击左右半边快退/快进 10 秒，双击中间全屏
    let clickTimer = 0;
    v.addEventListener('click', (e) => {
      closeMenus();
      clearTimeout(clickTimer);
      clickTimer = setTimeout(() => togglePlay(), 220);
    });
    v.addEventListener('dblclick', (e) => {
      clearTimeout(clickTimer);
      const x = e.clientX / window.innerWidth;
      if (x < 0.35) seekBy(-10);
      else if (x > 0.65) seekBy(10);
      else post({ type: 'fullscreen' });
    });

    r.querySelector('.bar').addEventListener('click', (e) => {
      const b = e.target.closest('button'); if (!b) return;
      e.stopPropagation();
      switch (b.dataset.a) {
        case 'play': togglePlay(); break;
        case 'back': seekBy(-10); break;
        case 'fwd': seekBy(10); break;
        case 'rate': toggleMenu(E.rateMenu, renderRateMenu); break;
        case 'subs': toggleMenu(E.subsMenu, renderSubsMenu); break;
        case 'mute': setMuted(!v.muted); break;
        case 'dl': post({ type: 'download' }); break;
        case 'mini': post({ type: 'mini' }); break;
        case 'full': post({ type: 'fullscreen' }); break;
      }
    });

    // 进度条
    const ratio = (e) => { const b = E.seek.getBoundingClientRect(); return Math.min(1, Math.max(0, (e.clientX - b.left) / b.width)); };
    let dragging = false;
    E.seek.addEventListener('mousedown', (e) => { dragging = true; seekTo(ratio(e) * v.duration); });
    window.addEventListener('mousemove', (e) => {
      if (dragging) seekTo(ratio(e) * v.duration);
      if (e.target.closest && e.target.closest('.seek')) { const p = ratio(e); E.hover.style.left = (p * 100) + '%'; E.hover.textContent = fmt(p * v.duration); }
    });
    window.addEventListener('mouseup', () => dragging = false);

    // 音量滑块：鼠标停在喇叭上展开，点 / 拖设置音量
    const vratio = (e) => { const b = E.vtrack.getBoundingClientRect(); return Math.min(1, Math.max(0, (e.clientX - b.left) / b.width)); };
    let vdrag = false;
    E.vtrack.addEventListener('mousedown', (e) => { vdrag = true; E.vol.classList.add('drag'); setVolume(vratio(e)); e.stopPropagation(); });
    window.addEventListener('mousemove', (e) => { if (vdrag) setVolume(vratio(e)); });
    window.addEventListener('mouseup', () => { if (vdrag) { vdrag = false; E.vol.classList.remove('drag'); } });
    v.addEventListener('volumechange', renderVolume);

    document.addEventListener('keydown', (e) => {
      if (e.key === ' ' || e.key === 'k') { togglePlay(); e.preventDefault(); }
      else if (e.key === 'ArrowLeft') seekBy(e.shiftKey ? -30 : -10);
      else if (e.key === 'ArrowRight') seekBy(e.shiftKey ? 30 : 10);
      else if (e.key === 'ArrowUp') setVolume(v.volume + .1, true);
      else if (e.key === 'ArrowDown') setVolume(v.volume - .1, true);
      else if (e.key === 'm' || e.key === 'M') setMuted(!v.muted, true);
      else if (e.key === 'f' || e.key === 'F') post({ type: 'fullscreen' });
      else if (e.key === 'Escape') post({ type: 'escape' });
      else if (e.key === '[') setRate(Math.max(0.25, state.rate - 0.25));
      else if (e.key === ']') setRate(Math.min(3, state.rate + 0.25));
      else return;
      showControls();
    });
    r.addEventListener('wheel', (e) => { if (e.target.closest('.menu')) return; setVolume(v.volume - Math.sign(e.deltaY) * .05, true); showControls(); e.preventDefault(); }, { passive: false });

    v.addEventListener('play', () => { E.play.innerHTML = ICON.pause; r.classList.remove('paused'); showControls(); });
    v.addEventListener('pause', () => { E.play.innerHTML = ICON.play; r.classList.add('paused'); showControls(); reportProgress(true); });
    v.addEventListener('waiting', () => r.classList.add('loading'));
    v.addEventListener('playing', () => r.classList.remove('loading'));
    v.addEventListener('canplay', () => r.classList.remove('loading'));
    v.addEventListener('ended', () => { reportProgress(true); post({ type: 'ended' }); });
    v.addEventListener('timeupdate', () => { renderTime(); renderSub(); reportProgress(false); });
    v.addEventListener('progress', renderTime);
    v.addEventListener('ratechange', () => { state.rate = v.playbackRate; E.rateBtn.textContent = v.playbackRate.toFixed(v.playbackRate % 1 ? 2 : 1).replace(/0$/, '') + '×'; });
    window.addEventListener('beforeunload', () => reportProgress(true));
  }

  function togglePlay() { const v = state.video; if (v.paused) v.play().catch(() => { }); else v.pause(); }
  function seekTo(t) { const v = state.video; if (!isFinite(t)) return; v.currentTime = Math.max(0, Math.min(t, (v.duration || t) - 0.5)); renderTime(); renderSub(); }
  function seekBy(d) { seekTo(state.video.currentTime + d); toast((d > 0 ? '快进 ' : '快退 ') + Math.abs(d) + ' 秒'); }
  // ---------- 音量 ----------
  let volSaveTimer = 0;
  function saveVolume() { clearTimeout(volSaveTimer); volSaveTimer = setTimeout(() => post({ type: 'volume', value: state.video.volume, muted: state.video.muted }), 400); }
  function setVolume(x, announce) {
    const v = state.video; v.volume = Math.round(Math.min(1, Math.max(0, x)) * 100) / 100;
    if (v.volume > 0 && v.muted) v.muted = false;
    if (announce) toast('音量 ' + Math.round(v.volume * 100) + '%');
    saveVolume();
  }
  function setMuted(m, announce) {
    const v = state.video; v.muted = m;
    if (!m && v.volume === 0) v.volume = 0.5;
    if (announce) toast(m ? '静音' : '音量 ' + Math.round(v.volume * 100) + '%');
    saveVolume();
  }
  function renderVolume() {
    const v = state.video, E = state.els, x = v.muted ? 0 : v.volume;
    E.vfill.style.width = (x * 100) + '%'; E.vknob.style.left = (x * 100) + '%'; E.vpct.textContent = Math.round(x * 100) + '%';
    E.muteBtn.innerHTML = x === 0 ? ICON.mute : x < 0.5 ? ICON.volLow : ICON.vol;
  }

  function setRate(r) { state.video.playbackRate = r; toast('倍速 ' + r + '×'); post({ type: 'rate', value: r }); }

  function renderTime() {
    const v = state.video, E = state.els, d = v.duration || 0, t = v.currentTime || 0;
    E.time.textContent = fmt(t) + ' / ' + fmt(d);
    const p = d ? t / d * 100 : 0;
    E.done.style.width = p + '%'; E.knob.style.left = p + '%';
    try { if (v.buffered.length) E.buf.style.width = (v.buffered.end(v.buffered.length - 1) / d * 100) + '%'; } catch (e) { }
  }

  function reportProgress(force) {
    const v = state.video; if (!v || !v.duration) return;
    const now = Date.now();
    if (!force && now - state.lastPost < 5000) return;
    state.lastPost = now;
    post({ type: 'progress', position: v.currentTime, duration: v.duration });
  }

  // ---------- 字幕 ----------
  function cueAt(t) {
    const c = state.cues; let lo = 0, hi = c.length - 1, found = -1;
    while (lo <= hi) { const mid = (lo + hi) >> 1; if (c[mid].s <= t) { found = mid; lo = mid + 1; } else hi = mid - 1; }
    for (let i = found; i >= 0 && i > found - 5; i--) if (c[i].s <= t && t < c[i].e) return c[i];
    return null;
  }
  function renderSub() {
    const span = state.els.sub;
    if (!state.subOn || !state.cues.length) { span.textContent = ''; span.style.display = 'none'; return; }
    const cue = cueAt(state.video.currentTime - state.offset);
    span.textContent = cue ? cue.t : '';
    span.style.display = cue ? 'inline' : 'none';
  }
  function setOffset(v, silent) {
    state.offset = Math.round(v * 10) / 10;
    renderSub();
    if (!silent) { toast('字幕偏移 ' + fmtOffset(state.offset)); post({ type: 'offset', value: state.offset }); }
    if (state.els.subsMenu.classList.contains('open')) renderSubsMenu();
  }

  // ---------- 菜单 ----------
  function closeMenus() { state.root.querySelectorAll('.menu.open').forEach(m => m.classList.remove('open')); }
  function toggleMenu(m, render) { const open = m.classList.contains('open'); closeMenus(); if (!open) { render(); m.classList.add('open'); } }

  function renderRateMenu() {
    const m = state.els.rateMenu; m.innerHTML = '';
    [0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 2.5, 3].forEach(r => {
      const it = el('div', 'item' + (Math.abs(r - state.rate) < 0.01 ? ' sel' : ''), r + '×');
      it.onclick = (e) => { e.stopPropagation(); setRate(r); closeMenus(); };
      m.appendChild(it);
    });
  }

  function renderSubsMenu() {
    const m = state.els.subsMenu; m.innerHTML = '';
    const add = (cls, html, fn) => { const it = el('div', cls, html); if (fn) it.onclick = (e) => { e.stopPropagation(); fn(); }; m.appendChild(it); return it; };
    add('item' + (state.subOn ? ' sel' : ''), state.subOn ? '✓ 显示字幕' : '　显示字幕', () => {
      state.subOn = !state.subOn; renderSub(); renderSubsMenu(); post({ type: 'subsEnabled', value: state.subOn });
    });
    add('sep');
    if (state.tracks === null) add('hint', '正在搜索字幕…');
    else if (!state.tracks.length) add('hint', '没有字幕');
    else state.tracks.forEach(t => add('item' + (state.track === t.id ? ' sel' : ''), t.name, () => { state.track = t.id; post({ type: 'selectTrack', id: t.id }); renderSubsMenu(); }));
    if (state.cues.length) {
      add('sep');
      add('hint', '偏移 ' + fmtOffset(state.offset));
      const btns = add('btns');
      [['−10', -10], ['−1', -1], ['−0.5', -0.5], ['+0.5', 0.5], ['+1', 1], ['+10', 10]].forEach(([l, d]) => {
        const b = el('button', null, l); b.onclick = (e) => { e.stopPropagation(); setOffset(state.offset + d); }; btns.appendChild(b);
      });
      add('item', '⏱ 从这里开始第一句', () => setOffset(state.video.currentTime - state.cues[0].s));
      add('item', '↺ 偏移归零', () => setOffset(0));
    }
  }

  // ---------- 宿主接口 ----------
  function attach(url, startAt) {
    const v = state.video;
    const onReady = () => {
      if (startAt > 5 && (!v.duration || startAt < v.duration - 10)) { v.currentTime = startAt; toast('从 ' + fmt(startAt) + ' 继续播放'); }
      v.play().catch(() => { state.root.classList.remove('loading'); });
    };
    if (window.JLHls && window.JLHls.isSupported()) {
      const hls = new window.JLHls({ maxBufferLength: 60, startLevel: -1, enableWorker: true });
      state.hls = hls;
      hls.on(window.JLHls.Events.MANIFEST_PARSED, onReady);
      hls.on(window.JLHls.Events.ERROR, (_, d) => {
        if (!d.fatal) return;
        if (d.type === window.JLHls.ErrorTypes.NETWORK_ERROR) { toast('网络错误，重试中…'); hls.startLoad(); }
        else if (d.type === window.JLHls.ErrorTypes.MEDIA_ERROR) { hls.recoverMediaError(); }
        else post({ type: 'error', message: '播放失败：' + (d.details || d.type) });
      });
      hls.loadSource(url);
      hls.attachMedia(v);
    } else {
      v.src = url;
      v.addEventListener('loadedmetadata', onReady, { once: true });
    }
  }

  window.JL = {
    start(opts) {
      if (!opts.synthetic) silenceSite();
      build(opts);
      state.rate = opts.rate || 1;
      state.subOn = opts.subtitlesEnabled !== false;
      state.offset = opts.offset || 0;
      state.tracks = null;
      state.video.playbackRate = state.rate;
      state.video.volume = typeof opts.volume === 'number' ? Math.min(1, Math.max(0, opts.volume)) : 1;
      state.video.muted = !!opts.muted;
      renderVolume();
      attach(opts.url, opts.startAt || 0);
      showControls();
      post({ type: 'started' });
    },
    setTracks(tracks, selected) { state.tracks = tracks || []; state.track = selected || null; if (state.els.subsMenu.classList.contains('open')) renderSubsMenu(); },
    setCues(cues, offset) {
      state.cues = (cues || []).slice().sort((a, b) => a.s - b.s);
      if (typeof offset === 'number') setOffset(offset, true);
      renderSub();
      if (state.cues.length) toast('字幕已加载（' + state.cues.length + ' 句）');
      if (state.els.subsMenu.classList.contains('open')) renderSubsMenu();
    },
    setMini(on) { state.mini = on; state.root.classList.toggle('mini', on); state.els.miniBtn.classList.toggle('on', on); state.els.miniBtn.title = on ? '还原' : '小窗（置顶）'; },
    toast,
    position() { return state.video ? state.video.currentTime : 0; },
    pause() { if (state.video) state.video.pause(); },
  };
})();
