(function (global) {
    "use strict";

    /**
     * FRIDAY Futuristic Voice Welcome / Boot Controller.
     * Reuses window.FridayVoice STT, TTS, female voice selection, and VoiceVisualizer.
     * Handles the local "PROCEED" command without calling Groq or backend AI orchestrators.
     */

    const GREETING_TEXT = "Hello. I am FRIDAY, your personal AI assistant. I'm ready when you are. Say 'PROCEED' to continue.";
    const ACK_TEXT = "Welcome back. Let's begin.";

    // Purely in-memory guard against duplicate initialization within the SAME page load.
    // Destroyed cleanly whenever the page is reloaded, reopened, or revisited.
    let hasInitializedThisPageLoad = false;

    const PROCEED_PATTERNS = [
        /\bproceed\b/i,
        /\bcontinue\b/i,
        /\blet\s*'?s?\s+go\b/i,
        /\blets\s+go\b/i,
        /\bstart\b/i,
        /\bbegin\b/i,
        /\benter\b/i,
        /\bgo\s+ahead\b/i,
        /\byes\b/i
    ];

    function normalizeAndMatchProceed(rawTranscript) {
        const cleaned = String(rawTranscript || "")
            .trim()
            .toLowerCase()
            .replace(/['’]/g, "")
            .replace(/[.,!?"`]+/g, " ")
            .replace(/\s+/g, " ")
            .trim();

        if (!cleaned) return false;
        return PROCEED_PATTERNS.some(rx => rx.test(cleaned));
    }

    function isRootEntryPoint() {
        try {
            const p = (global.location?.pathname || "/").toLowerCase();
            const isRoot = p === "/" || p === "/home" || p === "/home/index";
            if (!isRoot) return false;
            const params = new URLSearchParams(global.location.search || "");
            return !params.has("sessionId");
        } catch {
            return true;
        }
    }

    class ParticleField {
        constructor(canvasEl) {
            this.canvas = canvasEl;
            this._rafId = null;
            this._particles = [];
            this._running = false;
        }

        start() {
            if (!this.canvas || this._running) return;
            if (global.matchMedia?.("(prefers-reduced-motion: reduce)").matches) return;

            this._running = true;
            const isSmall = (global.innerWidth || 1024) < 768;
            const count = isSmall ? 18 : 38;
            this._particles = Array.from({ length: count }, () => this._spawn(true));

            const frame = () => {
                if (!this._running) return;
                this._draw();
                this._rafId = global.requestAnimationFrame(frame);
            };
            this._rafId = global.requestAnimationFrame(frame);
        }

        stop() {
            this._running = false;
            if (this._rafId !== null) {
                global.cancelAnimationFrame(this._rafId);
                this._rafId = null;
            }
        }

        _spawn(randomProgress = false) {
            const angle = Math.random() * Math.PI * 2;
            const maxRadius = Math.max(global.innerWidth || 900, global.innerHeight || 700) * 0.58;
            const radius = randomProgress ? Math.random() * maxRadius : maxRadius * (0.7 + Math.random() * 0.35);
            return {
                angle,
                radius,
                speed: 0.45 + Math.random() * 0.85,
                size: 1 + Math.random() * 2,
                alpha: 0.2 + Math.random() * 0.55
            };
        }

        _draw() {
            const ctx = this.canvas.getContext("2d");
            if (!ctx) return;

            const w = this.canvas.clientWidth || global.innerWidth || 800;
            const h = this.canvas.clientHeight || global.innerHeight || 600;
            if (this.canvas.width !== w || this.canvas.height !== h) {
                this.canvas.width = w;
                this.canvas.height = h;
            }

            ctx.clearRect(0, 0, w, h);
            const cx = w / 2;
            const cy = h * 0.43;

            for (let i = 0; i < this._particles.length; i++) {
                const p = this._particles[i];
                p.radius -= p.speed;
                p.angle += 0.0012;
                if (p.radius <= 38) {
                    this._particles[i] = this._spawn(false);
                    continue;
                }

                const x = cx + Math.cos(p.angle) * p.radius;
                const y = cy + Math.sin(p.angle) * p.radius;

                ctx.fillStyle = `rgba(56, 189, 248, ${p.alpha})`;
                ctx.beginPath();
                ctx.arc(x, y, p.size, 0, Math.PI * 2);
                ctx.fill();
            }
        }
    }

    function initFridayWelcome() {
        const overlay = document.getElementById("fridayWelcomeOverlay");
        if (!overlay) {
            document.documentElement.classList.remove("friday-boot-pending");
            return null;
        }

        if (hasInitializedThisPageLoad) {
            return null;
        }

        if (!isRootEntryPoint()) {
            overlay.remove();
            document.documentElement.classList.remove("friday-boot-pending");
            return null;
        }

        hasInitializedThisPageLoad = true;
        overlay.classList.add("is-active");
        document.documentElement.classList.add("friday-boot-pending");

        const FV = global.FridayVoice;
        const phaseLabelEl = document.getElementById("welcomeSystemPhaseLabel");
        const statusTextEl = document.getElementById("welcomeVoiceStatusText");
        const hintEl = document.getElementById("welcomeTranscriptHint");
        const micDotEl = document.getElementById("welcomeMicIndicator");
        const proceedBtn = document.getElementById("welcomeProceedBtn");
        const waveCanvas = document.getElementById("welcomeWaveformCanvas");
        const hudCore = document.getElementById("welcomeHudCore");
        const particleCanvas = document.getElementById("welcomeParticleCanvas");

        const tts = FV ? FV.createTextToSpeechService() : null;
        const stt = FV ? FV.createSpeechRecognitionService() : null;
        const visualizer = FV ? new FV.VoiceVisualizer(waveCanvas, hudCore, null) : null;
        const particles = new ParticleField(particleCanvas);

        let state = "FRIDAY_IDLE";
        let disposed = false;
        const timers = [];

        function schedule(fn, delayMs) {
            const id = setTimeout(() => {
                if (!disposed) fn();
            }, delayMs);
            timers.push(id);
            return id;
        }

        function setBootState(nextState, statusLabel, hintText, showMicDot = false) {
            state = nextState;
            overlay.setAttribute("data-boot-state", nextState);

            if (statusTextEl && statusLabel) statusTextEl.textContent = statusLabel;
            if (hintEl && hintText !== undefined) hintEl.textContent = hintText;
            if (micDotEl) micDotEl.classList.toggle("d-none", !showMicDot);

            if (visualizer) {
                const mapToVisualizer = {
                    FRIDAY_IDLE: "IDLE",
                    FRIDAY_INITIALIZING: "PROCESSING",
                    FRIDAY_APPEARING: "PROCESSING",
                    FRIDAY_SPEAKING: "SPEAKING",
                    FRIDAY_LISTENING: "LISTENING",
                    FRIDAY_PROCESSING: "PROCESSING",
                    FRIDAY_PROCEEDING: "SPEAKING",
                    FRIDAY_ERROR: "ERROR"
                };
                visualizer.setState(mapToVisualizer[nextState] || "IDLE");
            }
        }

        // Phase sequence: 1 (Darkness 0-400ms) -> 2 (Particles 400ms) -> 3 (Rings 1100ms) -> 4 (AI Reveal 1950ms) -> Greeting (~2850ms)
        function runStagedIntro() {
            overlay.setAttribute("data-boot-phase", "1");
            setBootState("FRIDAY_INITIALIZING", "BOOTING CORE", "Initializing FRIDAY neural environment...", false);

            schedule(() => {
                overlay.setAttribute("data-boot-phase", "2");
                particles.start();
                if (phaseLabelEl) phaseLabelEl.textContent = "CALIBRATING ENERGY MATRIX";
            }, 400);

            schedule(() => {
                overlay.setAttribute("data-boot-phase", "3");
                if (phaseLabelEl) phaseLabelEl.textContent = "FORMING HOLOGRAPHIC HUD";
            }, 1150);

            schedule(() => {
                overlay.setAttribute("data-boot-phase", "4");
                if (phaseLabelEl) phaseLabelEl.textContent = "FRIDAY INTERFACE ONLINE";
                setBootState("FRIDAY_APPEARING", "FRIDAY ONLINE", "Preparing voice greeting...", false);
            }, 2000);

            schedule(() => {
                speakGreetingThenListen();
            }, 2900);
        }

        function speakGreetingThenListen() {
            if (disposed || state === "FRIDAY_PROCEEDING") return;

            if (!tts || !tts.isSupported()) {
                beginProceedListening("Voice greeting unavailable in this browser.");
                return;
            }

            setBootState("FRIDAY_SPEAKING", "FRIDAY IS SPEAKING", GREETING_TEXT, false);

            let finishedGreeting = false;
            let startedSpeaking = false;
            const finishGreeting = (note = null, delayMs = 220) => {
                if (finishedGreeting || disposed || state === "FRIDAY_PROCEEDING") return;
                finishedGreeting = true;
                schedule(() => {
                    tts?.stop();
                    beginProceedListening(note);
                }, delayMs);
            };

            // If browser blocks/stalls autoplay before first click without firing onerror, recover briskly in ~1.8s
            const startTimeout = schedule(() => {
                if (!startedSpeaking && !finishedGreeting) {
                    tts.stop();
                    finishGreeting("Say 'PROCEED' to continue, or click PROCEED below.", 0);
                }
            }, 1800);

            tts.speak(GREETING_TEXT, {
                voicePreference: "female",
                lang: "en",
                rate: 1.0,
                pitch: 1.03,
                onStart: () => {
                    startedSpeaking = true;
                    clearTimeout(startTimeout);
                    schedule(() => finishGreeting(null, 100), 9500);
                },
                onEnd: () => {
                    clearTimeout(startTimeout);
                    finishGreeting(null, 220);
                },
                onError: (err) => {
                    clearTimeout(startTimeout);
                    const note = err?.code === "autoplay-blocked"
                        ? "Voice audio muted until interaction. Say 'PROCEED' or click PROCEED."
                        : "Voice playback unavailable. Say 'PROCEED' or click PROCEED.";
                    finishGreeting(note, 0);
                }
            });
        }

        async function beginProceedListening(fallbackNote = null) {
            if (disposed || state === "FRIDAY_PROCEEDING") return;

            if (!stt || !stt.isSupported()) {
                setBootState("FRIDAY_ERROR", "VOICE COMMANDS UNAVAILABLE IN THIS BROWSER", "Voice commands are unavailable in this browser. Click PROCEED to continue.", false);
                return;
            }

            if (navigator.mediaDevices && typeof navigator.mediaDevices.getUserMedia === "function") {
                try {
                    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
                    if (disposed || state === "FRIDAY_PROCEEDING") {
                        stream.getTracks().forEach(t => t.stop());
                        return;
                    }
                    await visualizer?.attachMicStream(stream);
                } catch (err) {
                    const name = String(err?.name || "");
                    if (name === "NotAllowedError" || name === "PermissionDeniedError" || name === "SecurityError") {
                        setBootState("FRIDAY_ERROR", "VOICE CONTROL UNAVAILABLE", "Voice control is unavailable. Click PROCEED below to enter.", false);
                        return;
                    }
                }
            }

            if (disposed || state === "FRIDAY_PROCEEDING") return;

            setBootState(
                "FRIDAY_LISTENING",
                "SAY PROCEED TO CONTINUE",
                fallbackNote || "Listening locally for 'PROCEED' (or click the button below)...",
                true);

            startLocalProceedRecognition();
        }

        function startLocalProceedRecognition() {
            if (disposed || state !== "FRIDAY_LISTENING" || !stt) return;

            stt.start({
                lang: "en-US",
                interimResults: true,
                continuous: false,
                onInterim: (interim) => {
                    if (state !== "FRIDAY_LISTENING") return;
                    if (normalizeAndMatchProceed(interim)) {
                        handleProceedCommandDetected();
                        return;
                    }
                    if (hintEl) hintEl.textContent = `Heard: "${interim}" — Say "PROCEED" to continue`;
                },
                onFinal: (finalText) => {
                    if (state !== "FRIDAY_LISTENING") return;
                    if (normalizeAndMatchProceed(finalText)) {
                        handleProceedCommandDetected();
                    } else if (!disposed) {
                        if (hintEl) hintEl.textContent = `Say "PROCEED" to continue (heard "${finalText}").`;
                        schedule(() => startLocalProceedRecognition(), 250);
                    }
                },
                onEndWithoutSpeech: () => {
                    if (state === "FRIDAY_LISTENING" && !disposed) {
                        schedule(() => startLocalProceedRecognition(), 350);
                    }
                },
                onError: (err) => {
                    if (state === "FRIDAY_PROCEEDING" || disposed) return;
                    if (err?.code === "permission-denied" || err?.code === "audio-capture") {
                        setBootState("FRIDAY_ERROR", "VOICE CONTROL UNAVAILABLE", "Voice control is unavailable. Click PROCEED to continue.", false);
                    } else if (err?.code === "unsupported") {
                        setBootState("FRIDAY_ERROR", "VOICE COMMANDS UNAVAILABLE IN THIS BROWSER", "Voice commands are unavailable in this browser.", false);
                    } else {
                        setBootState("FRIDAY_ERROR", "CLICK PROCEED OR TRY SPEAKING", err?.message || "Click PROCEED below to continue.", false);
                    }
                }
            });
        }

        function handleProceedCommandDetected() {
            if (disposed || state === "FRIDAY_PROCEEDING") return;

            stt?.abort();
            visualizer?.detachAudioStream();
            setBootState("FRIDAY_PROCEEDING", "ACCESS GRANTED", ACK_TEXT, false);

            if (tts && tts.isSupported()) {
                let done = false;
                const finish = () => {
                    if (done || disposed) return;
                    done = true;
                    completeBootTransition();
                };
                schedule(finish, 2100);
                tts.speak(ACK_TEXT, {
                    voicePreference: "female",
                    lang: "en",
                    onEnd: finish,
                    onError: finish
                });
            } else {
                schedule(() => completeBootTransition(), 250);
            }
        }

        function handleManualProceedClick() {
            if (disposed || state === "FRIDAY_PROCEEDING") return;

            if (proceedBtn) proceedBtn.disabled = true;
            setBootState("FRIDAY_PROCEEDING", "ENTERING FRIDAY", "Opening workspace...", false);
            stt?.abort();
            tts?.stop();
            visualizer?.detachAudioStream();
            completeBootTransition();
        }

        function completeBootTransition() {
            if (disposed) return;
            disposed = true;
            timers.forEach(t => clearTimeout(t));
            proceedBtn?.removeEventListener("click", handleManualProceedClick);
            overlay.removeEventListener("keydown", onOverlayKeyDown);

            overlay.classList.add("is-exiting");

            setTimeout(() => {
                particles.stop();
                visualizer?.destroy();
                overlay.remove();
                document.documentElement.classList.remove("friday-boot-pending");
                document.getElementById("fridayInput")?.focus({ preventScroll: true });
            }, 680);
        }

        function onOverlayKeyDown(e) {
            if (e.key === "Escape") {
                e.preventDefault();
                handleManualProceedClick();
            }
        }

        proceedBtn?.addEventListener("click", handleManualProceedClick);
        overlay.addEventListener("keydown", onOverlayKeyDown);

        runStagedIntro();

        return {
            normalizeAndMatchProceed,
            proceedNow: handleManualProceedClick,
            getState: () => state
        };
    }

    global.FridayWelcome = {
        normalizeAndMatchProceed,
        init: initFridayWelcome
    };

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initFridayWelcome);
    } else {
        initFridayWelcome();
    }
})(window);
