(function (global) {
    "use strict";

    /**
     * Future-ready Voice Abstractions for FRIDAY.
     * Supports browser-native SpeechRecognition + SpeechSynthesis today while isolating
     * provider details so Whisper/Azure/ElevenLabs/multilingual providers can swap in later.
     */

    const SpeechRecognitionCtor = global.SpeechRecognition || global.webkitSpeechRecognition || null;

    class SpeechRecognitionProviderBase {
        getProviderName() { return "base"; }
        isSupported() { return false; }
        start(_options) { throw new Error("Not implemented"); }
        stop() {}
        abort() {}
    }

    class BrowserWebSpeechRecognitionService extends SpeechRecognitionProviderBase {
        constructor() {
            super();
            this._recognition = null;
            this._activeRunId = 0;
            this._isRunning = false;
            this._manualStopRequested = false;
        }

        getProviderName() {
            return "WebSpeechRecognition";
        }

        isSupported() {
            return Boolean(SpeechRecognitionCtor);
        }

        start(options = {}) {
            if (!SpeechRecognitionCtor) {
                options.onError?.({
                    code: "unsupported",
                    message: "Speech recognition is not supported in this browser. Try Microsoft Edge or Google Chrome, or use Text Chat."
                });
                return false;
            }

            this.abort();

            const runId = ++this._activeRunId;
            const rec = new SpeechRecognitionCtor();
            rec.lang = options.lang || "en-US";
            rec.interimResults = options.interimResults !== false;
            rec.continuous = Boolean(options.continuous);
            rec.maxAlternatives = 1;

            let deliveredResult = false;
            let latestInterim = "";
            this._manualStopRequested = false;
            this._isRunning = true;

            rec.onstart = () => {
                if (runId !== this._activeRunId) return;
                this._isRunning = true;
                options.onStart?.();
            };

            rec.onresult = (event) => {
                if (runId !== this._activeRunId) return;
                let interimText = "";
                let finalText = "";
                let finalConfidence = 0;

                for (let i = event.resultIndex; i < event.results.length; i++) {
                    const res = event.results[i];
                    const alt = res && res[0] ? res[0] : null;
                    if (!alt) continue;

                    const transcriptChunk = String(alt.transcript || "").trim();
                    if (res.isFinal) {
                        finalText += (finalText ? " " : "") + transcriptChunk;
                        if (typeof alt.confidence === "number" && alt.confidence > 0) {
                            finalConfidence = alt.confidence;
                        }
                    } else {
                        interimText += (interimText ? " " : "") + transcriptChunk;
                    }
                }

                if (interimText) {
                    latestInterim = interimText;
                    options.onInterim?.(interimText);
                }

                if (finalText) {
                    if (finalConfidence > 0 && finalConfidence < 0.25) {
                        deliveredResult = true;
                        this._isRunning = false;
                        options.onError?.({
                            code: "low-confidence",
                            message: "I couldn't understand that. Please try again."
                        });
                        return;
                    }

                    deliveredResult = true;
                    this._isRunning = false;
                    try { rec.stop(); } catch {}
                    options.onFinal?.(finalText, finalConfidence);
                }
            };

            rec.onerror = (event) => {
                if (runId !== this._activeRunId) return;
                this._isRunning = false;
                deliveredResult = true;

                const rawCode = String(event?.error || "unknown").toLowerCase();
                if (rawCode === "aborted") {
                    options.onAborted?.();
                    return;
                }

                const mapped = mapSpeechError(rawCode);
                options.onError?.(mapped);
            };

            rec.onend = () => {
                if (runId !== this._activeRunId) return;
                const wasRunning = this._isRunning;
                const wasManualStop = this._manualStopRequested;
                this._isRunning = false;
                this._manualStopRequested = false;

                if (!deliveredResult) {
                    const fallback = latestInterim.trim();
                    if (fallback) {
                        deliveredResult = true;
                        options.onFinal?.(fallback, 0);
                    } else if (wasManualStop) {
                        options.onStoppedManually?.();
                    } else if (wasRunning) {
                        options.onEndWithoutSpeech?.();
                    }
                }
                options.onEnd?.({ manualStop: wasManualStop });
            };

            this._recognition = rec;
            try {
                rec.start();
                return true;
            } catch {
                this._isRunning = false;
                this._manualStopRequested = false;
                options.onError?.({
                    code: "audio-capture",
                    message: "Unable to start microphone listening right now. Please check microphone permissions."
                });
                return false;
            }
        }

        stop() {
            this._manualStopRequested = true;
            if (this._recognition) {
                try {
                    this._recognition.stop();
                } catch {
                    this.abort();
                }
            } else {
                this._isRunning = false;
            }
        }

        abort() {
            this._activeRunId++;
            this._isRunning = false;
            this._manualStopRequested = false;
            if (this._recognition) {
                try {
                    this._recognition.onstart = null;
                    this._recognition.onresult = null;
                    this._recognition.onerror = null;
                    this._recognition.onend = null;
                    this._recognition.abort();
                } catch {}
                this._recognition = null;
            }
        }
    }

    function mapSpeechError(code) {
        switch (code) {
            case "not-allowed":
            case "service-not-allowed":
                return {
                    code: "permission-denied",
                    message: "Microphone access is required for Voice Mode. Please allow microphone access in your browser settings."
                };
            case "no-speech":
                return {
                    code: "no-speech",
                    message: "I couldn't hear anything. Press Start Listening and speak clearly."
                };
            case "audio-capture":
                return {
                    code: "audio-capture",
                    message: "No working microphone was detected. Please connect a microphone or check browser permissions."
                };
            case "network":
                return {
                    code: "network",
                    message: "Speech recognition encountered a network issue. Please try again or use Text Chat."
                };
            default:
                return {
                    code: "error",
                    message: "I couldn't understand your voice. Please try again."
                };
        }
    }

    class TextToSpeechProviderBase {
        getProviderName() { return "base"; }
        isSupported() { return false; }
        async getVoicesAsync() { return []; }
        selectPreferredVoice(_options) { return null; }
        async speak(_text, _options) {}
        stop() {}
        isSpeaking() { return false; }
    }

    class BrowserSpeechSynthesisService extends TextToSpeechProviderBase {
        constructor() {
            super();
            this._speakRunId = 0;
            this._speaking = false;
            this._cachedVoices = [];
        }

        getProviderName() {
            return "WebSpeechSynthesis";
        }

        isSupported() {
            return typeof global.speechSynthesis !== "undefined" && typeof global.SpeechSynthesisUtterance !== "undefined";
        }

        async getVoicesAsync() {
            if (!this.isSupported()) return [];

            const initial = global.speechSynthesis.getVoices();
            if (Array.isArray(initial) && initial.length > 0) {
                this._cachedVoices = initial;
                return initial;
            }

            return await new Promise((resolve) => {
                let done = false;
                const finish = () => {
                    if (done) return;
                    done = true;
                    const list = global.speechSynthesis.getVoices() || [];
                    this._cachedVoices = list;
                    resolve(list);
                };

                const onChanged = () => {
                    global.speechSynthesis.removeEventListener?.("voiceschanged", onChanged);
                    finish();
                };

                global.speechSynthesis.addEventListener?.("voiceschanged", onChanged);
                setTimeout(finish, 750);
            });
        }

        selectPreferredVoice({ preference = "female", lang = "en", customVoiceUri = null } = {}) {
            const voices = (this._cachedVoices && this._cachedVoices.length > 0)
                ? this._cachedVoices
                : (this.isSupported() ? global.speechSynthesis.getVoices() : []);

            if (!Array.isArray(voices) || voices.length === 0) return null;

            if (customVoiceUri) {
                const custom = voices.find(v => v.voiceURI === customVoiceUri);
                if (custom) return custom;
            }

            const langPrefix = String(lang || "en").toLowerCase().split("-")[0];
            const matchingLang = voices.filter(v => String(v.lang || "").toLowerCase().startsWith(langPrefix));
            const pool = matchingLang.length > 0 ? matchingLang : voices;

            if (preference === "default") {
                return pool.find(v => v.default) || pool[0];
            }

            const femaleHints = [
                "female", "zira", "aria", "jenny", "sonia", "libby", "hazel", "sara",
                "samantha", "victoria", "karen", "moira", "tessa", "serena", "eva",
                "emma", "ava", "claire", "kate", "allison", "google us english",
                "google uk english female", "natasha", "clara"
            ];
            const maleHints = [
                "male", "david", "mark", "george", "daniel", "guy", "ryan", "christopher",
                "eric", "alex", "fred", "james", "thomas", "arthur", "google uk english male", "rishi"
            ];

            let bestVoice = null;
            let bestScore = -9999;

            for (const v of pool) {
                const nameLower = String(v.name || "").toLowerCase();
                const voiceLangLower = String(v.lang || "").toLowerCase();
                let score = 0;

                if (femaleHints.some(h => nameLower.includes(h))) score += 45;
                if (maleHints.some(h => nameLower.includes(h))) score -= 65;
                if (nameLower.includes("natural") || nameLower.includes("neural") || nameLower.includes("online")) score += 12;
                if (voiceLangLower === "en-us") score += 8;
                else if (voiceLangLower === "en-gb" || voiceLangLower === "en-ph") score += 6;
                if (v.localService) score += 3;
                if (v.default) score += 2;

                if (score > bestScore) {
                    bestScore = score;
                    bestVoice = v;
                }
            }

            return bestVoice || pool.find(v => v.default) || pool[0];
        }

        async speak(text, options = {}) {
            if (!this.isSupported()) {
                options.onError?.({
                    code: "tts-unsupported",
                    message: "Text-to-Speech is unavailable in this browser."
                });
                return false;
            }

            const cleanText = String(text || "").trim();
            if (!cleanText) {
                options.onEnd?.();
                return false;
            }

            this.stop();
            const runId = ++this._speakRunId;
            await this.getVoicesAsync();
            if (runId !== this._speakRunId) return false;

            const utterance = new global.SpeechSynthesisUtterance(cleanText);
            const selectedVoice = this.selectPreferredVoice({
                preference: options.voicePreference || "female",
                lang: options.lang || "en",
                customVoiceUri: options.customVoiceUri || null
            });

            if (selectedVoice) {
                utterance.voice = selectedVoice;
                utterance.lang = selectedVoice.lang || options.lang || "en-US";
            } else {
                utterance.lang = options.lang || "en-US";
            }

            utterance.rate = typeof options.rate === "number" ? options.rate : 1.02;
            utterance.pitch = typeof options.pitch === "number" ? options.pitch : 1.03;

            utterance.onstart = () => {
                if (runId !== this._speakRunId) return;
                this._speaking = true;
                options.onStart?.(selectedVoice?.name || "System Default");
            };

            utterance.onend = () => {
                if (runId !== this._speakRunId) return;
                this._speaking = false;
                options.onEnd?.();
            };

            utterance.onerror = (event) => {
                if (runId !== this._speakRunId) return;
                this._speaking = false;
                const errType = String(event?.error || "").toLowerCase();
                if (errType === "canceled" || errType === "interrupted") {
                    options.onEnd?.();
                    return;
                }
                if (errType === "not-allowed") {
                    options.onError?.({
                        code: "autoplay-blocked",
                        message: "Browser blocked automatic voice playback before interaction."
                    });
                    return;
                }
                options.onError?.({
                    code: "tts-error",
                    message: "Unable to play voice audio right now."
                });
            };

            setTimeout(() => {
                if (runId !== this._speakRunId) return;
                try {
                    global.speechSynthesis.speak(utterance);
                } catch {
                    this._speaking = false;
                    options.onError?.({
                        code: "tts-error",
                        message: "Unable to play voice audio right now."
                    });
                }
            }, 40);

            return true;
        }

        stop() {
            this._speakRunId++;
            this._speaking = false;
            if (this.isSupported()) {
                try { global.speechSynthesis.cancel(); } catch {}
            }
        }

        isSpeaking() {
            return this._speaking || (this.isSupported() && global.speechSynthesis.speaking);
        }
    }

    /**
     * Final frontend safeguard so Text-to-Speech NEVER reads Markdown, raw tables, JSON,
     * tool identifiers, API credentials, or internal stack traces aloud.
     */
    const SpeechResponseCleaner = {
        clean(responseObj) {
            if (!responseObj) return "";

            if (responseObj.spokenSummary && String(responseObj.spokenSummary).trim()) {
                return this.sanitizeText(String(responseObj.spokenSummary));
            }

            const rawMsg = String(responseObj.message || "");
            if (responseObj.pendingConfirmation && responseObj.pendingConfirmation.promptMessage) {
                return this.sanitizeText(`${responseObj.pendingConfirmation.promptMessage} Say Yes to confirm, or Cancel to keep it.`);
            }

            let cleaned = this.sanitizeText(rawMsg);
            if (!cleaned && responseObj.chart && responseObj.chart.title) {
                cleaned = `Here is the chart for ${this.sanitizeText(responseObj.chart.title)}.`;
            }
            return cleaned || "Done.";
        },

        sanitizeText(input) {
            if (!input) return "";
            let text = String(input);

            // Drop code blocks & raw JSON objects
            text = text.replace(/```[\s\S]*?```/g, " ");
            text = text.replace(/\{[\s\S]{0,1200}?\}/g, " ");

            // Strip markdown pipe tables (convert body rows to concise phrases; skip header + separator)
            const lines = text.split(/\r?\n/);
            const out = [];
            for (let i = 0; i < lines.length; i++) {
                const t = lines[i].trim();
                if (!t) continue;
                if (/^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)+\|?$/.test(t)) continue;
                if (/^([-*_]){3,}$/.test(t)) continue;
                if (t.includes("|")) {
                    const nextTrim = i + 1 < lines.length ? lines[i + 1].trim() : "";
                    const isHeaderRow = /^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)+\|?$/.test(nextTrim);
                    if (isHeaderRow) {
                        i++; // skip both header & separator
                        continue;
                    }
                    const parts = t.replace(/^\|/, "").replace(/\|$/, "").split("|").map(s => s.trim()).filter(Boolean);
                    if (parts.length > 0) {
                        out.push(parts.join(", "));
                    }
                    continue;
                }
                out.push(t);
            }

            text = out.join(" ");
            // Strip links, URLs, IDs, internal tool names, keys & diagnostics BEFORE stripping _ / # / *
            text = text
                .replace(/\[([^\]]+)\]\(([^)]+)\)/g, "$1")
                .replace(/https?:\/\/\S+/gi, "")
                .replace(/\s*\(ID\s*#\d+[^)]*\)/gi, "")
                .replace(/\b(?:Task|Project|Note|Event)\s*#\d+\b/gi, "")
                .replace(/\b(?:get|create|update|delete|complete|search|add|set|generate|build|log|universal)_[a-z0-9_]+\b/gi, "")
                .replace(/\b(?:CorrelationId|SessionId|PendingConfirmation|GroqLlmClient|FridayOrchestrator|FridayMcpClient|StackTrace|gsk_[A-Za-z0-9_-]+)\b/gi, "")
                .replace(/`/g, "")
                .replace(/[*_~#>]/g, " ")
                .replace(/[\uD800-\uDBFF][\uDC00-\uDFFF]|[\u2600-\u27BF]/g, " ")
                .replace(/\s{2,}/g, " ")
                .trim();

            if (text.length > 700) {
                const cut = text.slice(0, 700);
                const idx = Math.max(cut.lastIndexOf("."), cut.lastIndexOf("!"), cut.lastIndexOf("?"));
                text = idx > 300 ? cut.slice(0, idx + 1) : (cut.trim() + "...");
            }
            return text;
        }
    };

    /**
     * Reusable VoiceVisualizer component:
     * Uses Web Audio AnalyserNode ONLY when live mic audio is active and available.
     * Falls back transparently to honest state-based visual animations (never fake mic amplitude).
     */
    class VoiceVisualizer {
        constructor(canvasEl, orbContainerEl, telemetryBadgeEl) {
            this.canvas = canvasEl;
            this.orbContainer = orbContainerEl;
            this.telemetryBadge = telemetryBadgeEl;
            this.state = "IDLE";
            this._rafId = null;
            this._phase = 0;

            this._audioCtx = null;
            this._analyser = null;
            this._source = null;
            this._stream = null;
            this._freqData = null;
            this.hasLiveAudioSignal = false;
        }

        async attachMicStream(stream) {
            this.detachAudioStream();
            if (!stream) return false;
            const AudioCtx = global.AudioContext || global.webkitAudioContext;
            if (!AudioCtx) return false;

            try {
                this._stream = stream;
                this._audioCtx = new AudioCtx();
                if (this._audioCtx.state === "suspended") {
                    await this._audioCtx.resume();
                }
                this._analyser = this._audioCtx.createAnalyser();
                this._analyser.fftSize = 64;
                this._analyser.smoothingTimeConstant = 0.78;
                this._source = this._audioCtx.createMediaStreamSource(stream);
                this._source.connect(this._analyser);
                this._freqData = new Uint8Array(this._analyser.frequencyBinCount);
                this.hasLiveAudioSignal = true;
                this._updateBadge();
                return true;
            } catch {
                this.detachAudioStream();
                return false;
            }
        }

        detachAudioStream() {
            this.hasLiveAudioSignal = false;
            try { this._source?.disconnect(); } catch {}
            try { this._analyser?.disconnect(); } catch {}
            if (this._stream) {
                try {
                    this._stream.getTracks().forEach(t => t.stop());
                } catch {}
            }
            if (this._audioCtx) {
                try { this._audioCtx.close(); } catch {}
            }
            this._source = null;
            this._analyser = null;
            this._stream = null;
            this._audioCtx = null;
            this._freqData = null;
            this._updateBadge();
        }

        setState(newState) {
            const valid = ["IDLE", "LISTENING", "PROCESSING", "SPEAKING", "ERROR"];
            this.state = valid.includes(newState) ? newState : "IDLE";

            if (this.orbContainer) {
                this.orbContainer.setAttribute("data-voice-state", this.state);
            }

            if (this.state !== "LISTENING") {
                this.detachAudioStream();
            } else {
                this._updateBadge();
            }

            this.startLoop();
        }

        _updateBadge() {
            if (!this.telemetryBadge) return;
            if (this.state === "LISTENING" && this.hasLiveAudioSignal) {
                this.telemetryBadge.textContent = "Live Microphone Audio";
            } else {
                const labels = {
                    IDLE: "Standby Visualizer",
                    LISTENING: "Listening State Animation",
                    PROCESSING: "Thinking State Animation",
                    SPEAKING: "Voice Output State Animation",
                    ERROR: "Attention State"
                };
                this.telemetryBadge.textContent = labels[this.state] || "Standby Visualizer";
            }
        }

        startLoop() {
            if (this._rafId !== null) return;
            const tick = () => {
                this._drawFrame();
                this._rafId = global.requestAnimationFrame(tick);
            };
            this._rafId = global.requestAnimationFrame(tick);
        }

        destroy() {
            this.detachAudioStream();
            if (this._rafId !== null) {
                global.cancelAnimationFrame(this._rafId);
                this._rafId = null;
            }
        }

        _drawFrame() {
            if (!this.canvas) return;
            const ctx = this.canvas.getContext("2d");
            if (!ctx) return;

            const dpr = global.devicePixelRatio || 1;
            const displayW = this.canvas.clientWidth || 340;
            const displayH = this.canvas.clientHeight || 68;
            if (this.canvas.width !== Math.floor(displayW * dpr) || this.canvas.height !== Math.floor(displayH * dpr)) {
                this.canvas.width = Math.floor(displayW * dpr);
                this.canvas.height = Math.floor(displayH * dpr);
            }

            ctx.save();
            ctx.scale(dpr, dpr);
            ctx.clearRect(0, 0, displayW, displayH);

            this._phase += 0.06;
            const bars = 28;
            const gap = 5;
            const barWidth = Math.max(3, (displayW - gap * (bars - 1)) / bars);
            const midY = displayH / 2;

            let liveSpectrum = null;
            if (this.state === "LISTENING" && this.hasLiveAudioSignal && this._analyser && this._freqData) {
                this._analyser.getByteFrequencyData(this._freqData);
                liveSpectrum = this._freqData;
            }

            const colorByState = {
                IDLE: "rgba(56, 189, 248, 0.35)",
                LISTENING: "#22d3ee",
                PROCESSING: "#818cf8",
                SPEAKING: "#38bdf8",
                ERROR: "#f87171"
            };
            ctx.fillStyle = colorByState[this.state] || colorByState.IDLE;

            for (let i = 0; i < bars; i++) {
                let ampRatio = 0.08;
                if (liveSpectrum) {
                    const binIdx = Math.min(liveSpectrum.length - 1, Math.floor((i / bars) * liveSpectrum.length));
                    ampRatio = Math.max(0.1, liveSpectrum[binIdx] / 255);
                } else if (this.state === "PROCESSING") {
                    ampRatio = 0.16 + 0.28 * Math.abs(Math.sin(this._phase * 1.4 - i * 0.35));
                } else if (this.state === "SPEAKING") {
                    const centerEnvelope = 1 - Math.abs(i - (bars - 1) / 2) / (bars / 2);
                    ampRatio = 0.14 + 0.45 * centerEnvelope * Math.abs(Math.sin(this._phase * 1.8 + i * 0.45));
                } else if (this.state === "LISTENING") {
                    ampRatio = 0.16 + 0.24 * Math.abs(Math.sin(this._phase + i * 0.28));
                } else if (this.state === "ERROR") {
                    ampRatio = 0.12;
                } else {
                    ampRatio = 0.08 + 0.05 * Math.sin(this._phase * 0.6 + i * 0.25);
                }

                const barH = Math.max(4, ampRatio * (displayH - 12));
                const x = i * (barWidth + gap);
                const y = midY - barH / 2;
                ctx.beginPath();
                if (typeof ctx.roundRect === "function") {
                    ctx.roundRect(x, y, barWidth, barH, 3);
                } else {
                    ctx.rect(x, y, barWidth, barH);
                }
                ctx.fill();
            }

            ctx.restore();
        }
    }

    global.FridayVoice = {
        SpeechRecognitionProviderBase,
        BrowserWebSpeechRecognitionService,
        TextToSpeechProviderBase,
        BrowserSpeechSynthesisService,
        SpeechResponseCleaner,
        VoiceVisualizer,
        createSpeechRecognitionService: () => new BrowserWebSpeechRecognitionService(),
        createTextToSpeechService: () => new BrowserSpeechSynthesisService()
    };
})(window);

