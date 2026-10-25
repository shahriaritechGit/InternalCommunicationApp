
(() => {
  'use strict';

  const RING_TIMEOUT_MS = 45000;     // caller gives up
  const KEEPALIVE_MS = 30000;        // refreshes call state on the server
  const FAIL_AFTER_MS = 20000;       // network gone this long -> end the call
  const RESTART_AFTER_MS = 2000;     // "disconnected" this long -> try an ICE restart

  let conn = null;
  let getPeer = () => (document.getElementById('recipientInput')?.value || '').trim();
  let call = null;                   // null when idle
  let signalQueue = Promise.resolve();
  let ui = null;

  // ---------- overlay DOM ----------
  function buildUi() {
    const el = document.createElement('div');
    el.className = 'call-overlay';
    el.hidden = true;
    el.setAttribute('role', 'dialog');
    el.setAttribute('aria-modal', 'true');
    el.setAttribute('aria-labelledby', 'callName');
    el.innerHTML = `
      <video class="call-remote" autoplay playsinline></video>
      <audio class="call-audio" autoplay></audio>
      <div class="call-avatar" aria-hidden="true"></div>
      <div class="call-head">
        <h2 id="callName"></h2>
        <p class="call-status" role="status" aria-live="polite"></p>
      </div>
      <video class="call-local" autoplay playsinline muted></video>
      <div class="call-actions">
        <button type="button" class="call-btn call-accept" data-act="accept">Accept</button>
        <button type="button" class="call-btn call-toggle" data-act="mute" aria-pressed="false">Mute</button>
        <button type="button" class="call-btn call-toggle" data-act="camera" aria-pressed="false">Camera off</button>
        <button type="button" class="call-btn call-end" data-act="end">End</button>
      </div>`;
    document.body.appendChild(el);

    ui = {
      root: el,
      remote: el.querySelector('.call-remote'),
      audio: el.querySelector('.call-audio'),
      local: el.querySelector('.call-local'),
      avatar: el.querySelector('.call-avatar'),
      name: el.querySelector('#callName'),
      status: el.querySelector('.call-status'),
      mute: el.querySelector('[data-act="mute"]'),
      camera: el.querySelector('[data-act="camera"]'),
    };

    el.addEventListener('click', (e) => {
      const act = e.target.closest('[data-act]')?.dataset.act;
      if (!act) return;
      if (act === 'accept') accept();
      else if (act === 'end') hangup('ended');
      else if (act === 'mute') toggleTrack('audio', ui.mute, 'Mute', 'Unmute');
      else if (act === 'camera') toggleTrack('video', ui.camera, 'Camera off', 'Camera on');
    });
  }

  function show(phase) {
    // phase: incoming | calling | connecting | active
    ui.root.hidden = false;
    ui.root.dataset.phase = phase;
    ui.root.dataset.video = String(!!call?.video);
    ui.name.textContent = call?.peer ?? '';
    ui.avatar.textContent = (call?.peer?.[0] ?? '?').toUpperCase();
    ui.root.querySelector('[data-act="end"]').textContent = phase === 'incoming' ? 'Decline' : (phase === 'calling' ? 'Cancel' : 'End');
    if (phase === 'incoming') ui.root.querySelector('[data-act="accept"]').focus();
    else ui.root.querySelector('[data-act="end"]').focus();
  }

  function setStatus(text) { ui.status.textContent = text; }

  function toggleTrack(kind, button, onLabel, offLabel) {
    const tracks = kind === 'audio' ? call?.local?.getAudioTracks() : call?.local?.getVideoTracks();
    if (!tracks?.length) return;
    const enable = !tracks[0].enabled;
    tracks.forEach((t) => { t.enabled = enable; });
    button.setAttribute('aria-pressed', String(!enable));
    button.textContent = enable ? onLabel : offLabel;
  }

  // ---------- media + ICE ----------
  async function loadIceServers() {
    try {
      const r = await fetch('/api/ice', { credentials: 'same-origin' });
      if (r.ok) return (await r.json()).iceServers;
    } catch { /* fall through */ }
    return [{ urls: 'stun:stun.l.google.com:19302' }];
  }

  async function getMedia(video) {
    if (!window.isSecureContext) throw new Error('Calls need HTTPS.');
    try {
      return await navigator.mediaDevices.getUserMedia({
        audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true },
        video: video ? { width: { ideal: 1280 }, height: { ideal: 720 }, facingMode: 'user' } : false,
      });
    } catch (err) {
      if (err.name === 'NotAllowedError') throw new Error('Allow microphone' + (video ? ' and camera' : '') + ' access to make calls.');
      if (err.name === 'NotFoundError') throw new Error('No microphone' + (video ? ' or camera' : '') + ' found.');
      throw new Error('Could not start your microphone' + (video ? ' or camera' : '') + '.');
    }
  }

  function send(kind, payload) {
    return conn.invoke('Signal', call.callId, kind, JSON.stringify(payload)).catch(() => {});
  }

  function createPeerConnection() {
    const pc = new RTCPeerConnection({ iceServers: call.ice });
    call.local.getTracks().forEach((t) => pc.addTrack(t, call.local));

    pc.onicecandidate = (e) => { if (e.candidate) send('ice', e.candidate.toJSON()); };
    pc.ontrack = (e) => {
      const stream = e.streams[0];
      ui.remote.srcObject = stream;
      ui.audio.srcObject = stream;          // voice calls have no <video> output
    };
    pc.oniceconnectionstatechange = () => onIceState(pc.iceConnectionState);
    return pc;
  }

  function onIceState(s) {
    if (!call) return;
    if (s === 'connected' || s === 'completed') {
      clearTimeout(call.t.restart); clearTimeout(call.t.fail);
      if (call.phase !== 'active') { call.phase = 'active'; show('active'); startClock(); }
      else setStatus('');
    } else if (s === 'disconnected' || s === 'failed') {
      setStatus('Reconnecting…');
      clearTimeout(call.t.restart);
      call.t.restart = setTimeout(restartIce, s === 'failed' ? 0 : RESTART_AFTER_MS);
      if (!call.t.fail) call.t.fail = setTimeout(() => hangup('failed'), FAIL_AFTER_MS);
    }
  }

  // Only the caller makes offers, so there is never an offer collision.
  async function restartIce() {
    if (!call?.pc || !call.isCaller) return;
    try {
      const offer = await call.pc.createOffer({ iceRestart: true });
      await call.pc.setLocalDescription(offer);
      await send('offer', call.pc.localDescription);
    } catch { /* the fail timer will end the call if this never recovers */ }
  }

  function startClock() {
    const t0 = Date.now();
    clearInterval(call.t.clock);
    call.t.clock = setInterval(() => {
      const s = Math.floor((Date.now() - t0) / 1000);
      setStatus(`${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`);
    }, 1000);
  }

  // ---------- call flow ----------
  async function start(username, video) {
    if (!conn || call) return;
    if (!username) return alert('Open a conversation first.');

    call = newCall({ peer: username, video, isCaller: true });
    show('calling');
    setStatus('Starting…');
    try {
      [call.local, call.ice] = await Promise.all([getMedia(video), loadIceServers()]);
      attachLocal();
      call.callId = await conn.invoke('StartCall', username, video);
      setStatus('Ringing…');
      beginKeepAlive();
      call.t.ring = setTimeout(() => hangup('timeout'), RING_TIMEOUT_MS);
    } catch (err) {
      const msg = err.message?.replace(/^.*HubException: /, '') ?? 'Could not start the call.';
      teardown(msg, true);
    }
  }

  async function accept() {
    if (!call || call.phase !== 'incoming') return;
    call.phase = 'connecting';
    show('connecting');
    setStatus('Connecting…');
    try {
      [call.local, call.ice] = await Promise.all([getMedia(call.video), loadIceServers()]);
      attachLocal();
      await conn.invoke('AcceptCall', call.callId);
      beginKeepAlive();
    } catch (err) {
      const msg = err.message?.replace(/^.*HubException: /, '') ?? 'Could not answer the call.';
      if (call?.callId) conn.invoke('EndCall', call.callId, 'ended').catch(() => {});
      teardown(msg, true);
    }
  }

  function hangup(reason) {
    if (!call) return;
    if (call.callId) conn.invoke('EndCall', call.callId, reason).catch(() => {});
    teardown(reason === 'failed' ? 'Connection lost.' : '');
  }

  function attachLocal() {
    ui.local.srcObject = call.local;
    ui.local.hidden = !call.video;
    ui.camera.hidden = !call.video;
  }

  function beginKeepAlive() {
    call.t.keep = setInterval(() => conn.invoke('CallKeepAlive', call.callId).catch(() => {}), KEEPALIVE_MS);
  }

  function newCall(init) {
    return { callId: null, phase: init.isCaller ? 'calling' : 'incoming', local: null, ice: null, pc: null, t: {}, ...init };
  }

  let toastTimer = 0;
  function teardown(message, keepOpenBriefly) {
    if (!call) return;
    Object.values(call.t).forEach((id) => { clearTimeout(id); clearInterval(id); });
    call.pc?.close();
    call.local?.getTracks().forEach((t) => t.stop());
    ui.remote.srcObject = null; ui.audio.srcObject = null; ui.local.srcObject = null;
    ui.mute.setAttribute('aria-pressed', 'false'); ui.mute.textContent = 'Mute';
    ui.camera.setAttribute('aria-pressed', 'false'); ui.camera.textContent = 'Camera off';
    call = null;

    if (message) {
      ui.root.dataset.phase = 'ended';
      setStatus(message);
      clearTimeout(toastTimer);
      toastTimer = setTimeout(() => { if (!call) ui.root.hidden = true; }, keepOpenBriefly ? 3500 : 2500);
    } else {
      ui.root.hidden = true;
    }
  }

  // ---------- server events ----------
  const REASON_TEXT = {
    declined: 'Call declined.', cancelled: 'Call cancelled.', missed: 'No answer.',
    failed: 'Connection lost.', ended: '',
  };

  function wire() {
    conn.on('IncomingCall', ({ callId, from, video }) => {
      if (call) return;                         // this tab is already busy
      call = newCall({ callId, peer: from, video, isCaller: false });
      show('incoming');
      setStatus(video ? 'Incoming video call' : 'Incoming voice call');
    });

    conn.on('CallAccepted', async ({ callId }) => {
      if (!call || call.callId !== callId) return;
      clearTimeout(call.t.ring);
      call.phase = 'connecting';
      show('connecting');
      setStatus('Connecting…');
      call.pc = createPeerConnection();
      const offer = await call.pc.createOffer();
      await call.pc.setLocalDescription(offer);
      send('offer', call.pc.localDescription);
    });

    // Handled one at a time so an ICE candidate can never overtake the offer before it.
    conn.on('CallSignal', (msg) => { signalQueue = signalQueue.then(() => onSignal(msg)).catch(() => {}); });

    conn.on('CallAnsweredElsewhere', ({ callId }) => {
      if (call?.callId === callId && call.phase === 'incoming') teardown('');
    });

    conn.on('CallEnded', ({ callId, reason }) => {
      if (call?.callId !== callId) return;
      const asCallee = !call.isCaller && call.phase === 'incoming';
      teardown(asCallee ? '' : (REASON_TEXT[reason] ?? ''));
    });

    conn.on('PeerReconnected', () => {
      if (call?.isCaller && call.pc && !['connected', 'completed'].includes(call.pc.iceConnectionState)) restartIce();
    });

    conn.onreconnected(async () => {
      if (!call?.callId || call.phase === 'incoming') return;
      const ok = await conn.invoke('RejoinCall', call.callId).catch(() => false);
      if (!ok) { teardown('Call ended.'); return; }
      if (call.isCaller && call.pc && !['connected', 'completed'].includes(call.pc.iceConnectionState)) restartIce();
    });
  }

  async function onSignal({ callId, kind, data }) {
    if (!call || call.callId !== callId) return;
    const payload = JSON.parse(data);

    if (kind === 'offer') {                       // callee side (also ICE-restart offers)
      call.pc ??= createPeerConnection();
      await call.pc.setRemoteDescription(payload);
      const answer = await call.pc.createAnswer();
      await call.pc.setLocalDescription(answer);
      await send('answer', call.pc.localDescription);
    } else if (kind === 'answer') {               // caller side
      await call.pc?.setRemoteDescription(payload);
    } else if (kind === 'ice') {
      await call.pc?.addIceCandidate(payload).catch(() => {});
    }
  }

  // ---------- public API ----------
  function init(connection, options = {}) {
    conn = connection;
    if (options.getPeer) getPeer = options.getPeer;
    if (!ui) buildUi();
    wire();
    document.addEventListener('click', (e) => {
      const btn = e.target.closest('[data-call]');
      if (btn) start(getPeer(), btn.dataset.call === 'video');
    });
  }

  window.Calls = { init, start };
})();