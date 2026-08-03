/**
 * Visual Mermaid Diagram Long-Press Gesture & Liquid Fill Engine
 * MarkSmith / WinUI3 WebView2 Interop
 */
(function () {
    let audioCtx = null;

    // Initialize Web Audio API context with auto-unlock on user gesture
    function getAudioContext() {
        if (!audioCtx) {
            const AudioContextClass = window.AudioContext || window.webkitAudioContext;
            if (AudioContextClass) {
                audioCtx = new AudioContextClass();
            }
        }
        if (audioCtx && audioCtx.state === 'suspended') {
            audioCtx.resume().catch(() => {});
        }
        return audioCtx;
    }

    // Start synthesized sloshing water sound - returns encapsulated session object
    function startSloshSound() {
        const ctx = getAudioContext();
        if (!ctx) return null;

        const session = {
            lfo: null,
            lfoGain: null,
            sloshNoiseNode: null,
            sloshFilterNode: null,
            sloshGainNode: null,
            bubbleInterval: null,
            isStopped: false
        };

        try {
            // Create white noise buffer
            const bufferSize = ctx.sampleRate * 2;
            const noiseBuffer = ctx.createBuffer(1, bufferSize, ctx.sampleRate);
            const output = noiseBuffer.getChannelData(0);
            for (let i = 0; i < bufferSize; i++) {
                output[i] = Math.random() * 2 - 1;
            }

            session.sloshNoiseNode = ctx.createBufferSource();
            session.sloshNoiseNode.buffer = noiseBuffer;
            session.sloshNoiseNode.loop = true;

            // Bandpass filter to simulate water movement
            session.sloshFilterNode = ctx.createBiquadFilter();
            session.sloshFilterNode.type = 'bandpass';
            session.sloshFilterNode.frequency.setValueAtTime(350, ctx.currentTime);
            session.sloshFilterNode.Q.setValueAtTime(3.0, ctx.currentTime);

            // Modulate filter frequency for slosh LFO effect
            session.lfo = ctx.createOscillator();
            session.lfoGain = ctx.createGain();
            session.lfo.frequency.setValueAtTime(4.5, ctx.currentTime); // 4.5 Hz slosh speed
            session.lfoGain.gain.setValueAtTime(250, ctx.currentTime);  // Frequency modulation depth
            session.lfo.connect(session.lfoGain);
            session.lfoGain.connect(session.sloshFilterNode.frequency);
            session.lfo.start();

            // Gain node for volume envelope
            session.sloshGainNode = ctx.createGain();
            session.sloshGainNode.gain.setValueAtTime(0.01, ctx.currentTime);

            session.sloshNoiseNode.connect(session.sloshFilterNode);
            session.sloshFilterNode.connect(session.sloshGainNode);
            session.sloshGainNode.connect(ctx.destination);

            session.sloshNoiseNode.start();

            // Random bubble pops during hold
            session.bubbleInterval = setInterval(() => {
                if (Math.random() > 0.4) playBubblePop(ctx);
            }, 90);
        } catch (e) {
            console.warn('[Audio] Web Audio synthesis failed:', e);
        }

        return session;
    }

    function updateSloshSound(session, progress) {
        if (!session || session.isStopped || !session.sloshGainNode || !session.sloshFilterNode || !audioCtx) return;
        try {
            const now = audioCtx.currentTime;
            // Increase gain and shift cutoff frequency upwards as liquid fills
            session.sloshGainNode.gain.setTargetAtTime(0.02 + progress * 0.18, now, 0.05);
            session.sloshFilterNode.frequency.setTargetAtTime(350 + progress * 500, now, 0.05);
        } catch (e) {}
    }

    function stopSloshSound(session) {
        if (!session || session.isStopped) return;
        session.isStopped = true;

        if (session.bubbleInterval) {
            clearInterval(session.bubbleInterval);
            session.bubbleInterval = null;
        }

        if (session.lfo) {
            try { session.lfo.stop(); } catch (e) {}
            try { session.lfo.disconnect(); } catch (e) {}
            session.lfo = null;
        }
        if (session.lfoGain) {
            try { session.lfoGain.disconnect(); } catch (e) {}
            session.lfoGain = null;
        }
        if (session.sloshNoiseNode) {
            try { session.sloshNoiseNode.stop(); } catch (e) {}
            try { session.sloshNoiseNode.disconnect(); } catch (e) {}
            session.sloshNoiseNode = null;
        }
        if (session.sloshFilterNode) {
            try { session.sloshFilterNode.disconnect(); } catch (e) {}
            session.sloshFilterNode = null;
        }
        if (session.sloshGainNode) {
            try { session.sloshGainNode.disconnect(); } catch (e) {}
            session.sloshGainNode = null;
        }
    }

    function playBubblePop(ctx) {
        try {
            const osc = ctx.createOscillator();
            const gain = ctx.createGain();
            const startFreq = 600 + Math.random() * 600;
            const endFreq = 200 + Math.random() * 200;
            const now = ctx.currentTime;

            osc.frequency.setValueAtTime(startFreq, now);
            osc.frequency.exponentialRampToValueAtTime(endFreq, now + 0.04);

            gain.gain.setValueAtTime(0.05, now);
            gain.gain.exponentialRampToValueAtTime(0.001, now + 0.04);

            osc.connect(gain);
            gain.connect(ctx.destination);

            osc.start(now);
            osc.stop(now + 0.045);
        } catch (e) {}
    }

    function playCompletionChime() {
        const ctx = getAudioContext();
        if (!ctx) return;
        try {
            const now = ctx.currentTime;
            const freqs = [523.25, 659.25, 783.99, 1046.50]; // C5, E5, G5, C6 major triad
            freqs.forEach((freq, idx) => {
                const osc = ctx.createOscillator();
                const gain = ctx.createGain();
                osc.type = 'sine';
                osc.frequency.setValueAtTime(freq, now + idx * 0.03);
                gain.gain.setValueAtTime(0.12, now + idx * 0.03);
                gain.gain.exponentialRampToValueAtTime(0.0001, now + idx * 0.03 + 0.6);
                osc.connect(gain);
                gain.connect(ctx.destination);
                osc.start(now + idx * 0.03);
                osc.stop(now + idx * 0.03 + 0.65);
            });
        } catch (e) {}
    }

    // Attach 800ms Long-Press & Liquid Fill to Mermaid Diagram Cards
    function attachMermaidLongPress() {
        const HOLD_DURATION = 800; // ms
        const MOVE_THRESHOLD = 8;  // px

        document.querySelectorAll('.mermaid').forEach((card, idx) => {
            // Capture the diagram source on first sight — this runs before mermaid's startOnLoad
            // render replaces the div's text with the SVG, so dataset.raw holds the real Mermaid
            // code (card.innerText after render would just be the node labels). completeHold below
            // prefers dataset.raw, so the Studio always receives the correct source.
            if (!card.dataset.raw) card.dataset.raw = card.textContent;
            if (card.dataset.longpressAttached === 'true') return;
            card.dataset.longpressAttached = 'true';

            let timerId = null;
            let startTime = 0;
            let startX = 0;
            let startY = 0;
            let activeOverlay = null;
            let isHolding = false;
            let activeAudioSession = null;

            function createOverlay() {
                const overlay = document.createElement('div');
                overlay.className = 'mermaid-liquid-overlay';
                overlay.innerHTML = `
                    <div class="liquid-fill">
                        <svg class="liquid-wave wave-back" viewBox="0 0 1200 120" preserveAspectRatio="none">
                            <path d="M0,0 C150,90 350,-40 500,40 C650,120 900,-20 1200,40 L1200,120 L0,120 Z"></path>
                        </svg>
                        <svg class="liquid-wave wave-front" viewBox="0 0 1200 120" preserveAspectRatio="none">
                            <path d="M0,40 C300,-30 450,80 700,10 C950,-50 1050,70 1200,20 L1200,120 L0,120 Z"></path>
                        </svg>
                        <div class="liquid-glare"></div>
                    </div>
                    <div class="liquid-percentage-badge">0%</div>
                `;
                card.appendChild(overlay);
                return overlay;
            }

            function startHold(e) {
                // Ignore secondary clicks or edit button clicks
                if (e.button !== undefined && e.button !== 0) return;
                if (e.target.closest('.mermaid-edit-btn')) return;

                if (activeAudioSession) {
                    stopSloshSound(activeAudioSession);
                    activeAudioSession = null;
                }

                isHolding = true;
                startX = typeof e.clientX === 'number' ? e.clientX : (e.touches && e.touches[0] ? e.touches[0].clientX : 0);
                startY = typeof e.clientY === 'number' ? e.clientY : (e.touches && e.touches[0] ? e.touches[0].clientY : 0);
                startTime = performance.now();

                activeOverlay = card.querySelector('.mermaid-liquid-overlay') || createOverlay();
                activeOverlay.classList.remove('draining', 'complete');
                activeOverlay.classList.add('active');

                activeAudioSession = startSloshSound();

                function frame(now) {
                    if (!isHolding) return;
                    const elapsed = now - startTime;
                    const progress = Math.min(1.0, elapsed / HOLD_DURATION);

                    // Update UI fill width & badge
                    const fillEl = activeOverlay.querySelector('.liquid-fill');
                    const badgeEl = activeOverlay.querySelector('.liquid-percentage-badge');
                    
                    if (fillEl) {
                        fillEl.style.width = (progress * 100) + '%';
                        fillEl.style.opacity = Math.min(1.0, 0.2 + progress * 0.8);
                    }
                    if (badgeEl) {
                        badgeEl.textContent = Math.round(progress * 100) + '%';
                        badgeEl.style.opacity = progress > 0.1 ? '1' : '0';
                    }

                    updateSloshSound(activeAudioSession, progress);

                    if (progress >= 1.0) {
                        completeHold();
                    } else {
                        timerId = requestAnimationFrame(frame);
                    }
                }

                timerId = requestAnimationFrame(frame);
            }

            function cancelHold() {
                if (!isHolding) return;
                isHolding = false;
                if (timerId) {
                    cancelAnimationFrame(timerId);
                    timerId = null;
                }
                if (activeAudioSession) {
                    stopSloshSound(activeAudioSession);
                    activeAudioSession = null;
                }

                if (activeOverlay) {
                    activeOverlay.classList.add('draining');
                    activeOverlay.classList.remove('active');
                    const fillEl = activeOverlay.querySelector('.liquid-fill');
                    if (fillEl) {
                        fillEl.style.width = '0%';
                    }
                    setTimeout(() => {
                        if (activeOverlay && !isHolding) {
                            activeOverlay.classList.remove('draining');
                        }
                    }, 300);
                }
            }

            function completeHold() {
                isHolding = false;
                if (timerId) {
                    cancelAnimationFrame(timerId);
                    timerId = null;
                }
                if (activeAudioSession) {
                    stopSloshSound(activeAudioSession);
                    activeAudioSession = null;
                }
                playCompletionChime();

                if (activeOverlay) {
                    activeOverlay.classList.add('complete');
                }
                card.classList.add('mermaid-card-bounce');

                // Retrieve diagram code & index
                const rawCode = card.dataset.raw || card.innerText || '';

                // Trigger WebView2 host interop message to C#
                try {
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage(JSON.stringify({
                            type: 'launch-mermaid-studio',
                            index: idx,
                            code: rawCode,
                            gesture: 'long-press-800ms',
                            timestamp: Date.now()
                        }));
                    }
                } catch (err) {
                    console.error('[WebView2 Interop] Failed to post message:', err);
                }

                // Clean up card bounce after animation completes
                setTimeout(() => {
                    card.classList.remove('mermaid-card-bounce');
                    if (activeOverlay) {
                        activeOverlay.classList.remove('complete', 'active');
                        const fillEl = activeOverlay.querySelector('.liquid-fill');
                        if (fillEl) fillEl.style.width = '0%';
                    }
                }, 600);
            }

            function checkMove(e) {
                if (!isHolding) return;
                const curX = typeof e.clientX === 'number' ? e.clientX : (e.touches && e.touches[0] ? e.touches[0].clientX : 0);
                const curY = typeof e.clientY === 'number' ? e.clientY : (e.touches && e.touches[0] ? e.touches[0].clientY : 0);
                const dist = Math.hypot(curX - startX, curY - startY);
                if (dist > MOVE_THRESHOLD) {
                    cancelHold();
                }
            }

            // Bind Unified Pointer and Touch Listeners
            card.addEventListener('pointerdown', startHold);
            card.addEventListener('pointermove', checkMove);
            card.addEventListener('pointerup', cancelHold);
            card.addEventListener('pointercancel', cancelHold);
            card.addEventListener('pointerleave', cancelHold);

            // Prevent unwanted context menus during press
            card.addEventListener('contextmenu', (e) => {
                if (isHolding || (activeOverlay && activeOverlay.classList.contains('active'))) {
                    e.preventDefault();
                }
            });
        });
    }

    // Auto-init on DOM Ready & Mutation updates
    if (document.readyState === 'loading') {
        window.addEventListener('DOMContentLoaded', attachMermaidLongPress);
    } else {
        attachMermaidLongPress();
    }
    window.addEventListener('load', () => setTimeout(attachMermaidLongPress, 500));

    // This script loads in <head>, where document.body is still null — observe() on null throws
    // and would silently kill re-attachment for diagrams added after first paint. Defer the
    // observer until the body exists.
    function watchForNewDiagrams() {
        if (!document.body) return;
        new MutationObserver(() => attachMermaidLongPress())
            .observe(document.body, { childList: true, subtree: true });
    }
    if (document.body) watchForNewDiagrams();
    else window.addEventListener('DOMContentLoaded', watchForNewDiagrams);
})();

