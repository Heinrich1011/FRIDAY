(function (global) {
    "use strict";

    /**
     * Voice Mode Controller for FRIDAY.
     * Coordinates mic permissions, Speech-to-Text, shared /api/chat/send execution,
     * safe SpeechResponseCleaner + Text-to-Speech playback, feedback-loop prevention,
     * and state-accurate audio visualization.
     */

    global.initFridayVoiceController = function initFridayVoiceController(chatBridge) {
        const FV = global.FridayVoice;
        if (!FV || !chatBridge) return null;

        const textModeTabBtn = document.getElementById("modeTextBtn");
        const voiceModeTabBtn = document.getElementById("modeVoiceBtn");
        const chatMessagesEl = document.getElementById("chatMessages");
        const chatComposerWrap = document.getElementById("chatComposerWrap");
        const voiceWorkspaceEl = document.getElementById("voiceWorkspace");

        if (!textModeTabBtn || !voiceModeTabBtn || !voiceWorkspaceEl) {
            return null;
        }

        const voiceStatusBadge = document.getElementById("voiceStatusBadge");
        const voiceStatusSub = document.getElementById("voiceStatusSub");
        const voiceVisualizerCanvas = document.getElementById("voiceVisualizerCanvas");
        const voiceOrbContainer = document.getElementById("voiceOrbRing");
        const voiceTelemetryBadge = document.getElementById("voiceTelemetryBadge");
        const voiceTranscriptBox = document.getElementById("voiceTranscriptText");
        const voiceResponseBox = document.getElementById("voiceResponseText");
        const voiceArtifactsBox = document.getElementById("voiceResponseArtifacts");
        const voiceErrorBanner = document.getElementById("voiceErrorBanner");
        const voiceErrorText = document.getElementById("voiceErrorText");
        const voiceMicBtn = document.getElementById("voiceMicBtn");
        const voiceMicBtnLabel = document.getElementById("voiceMicBtnLabel");
        const voiceStopSpeakingBtn = document.getElementById("voiceStopSpeakingBtn");
        const voiceReplayBtn = document.getElementById("voiceReplayBtn");
        const voicePreferenceSelect = document.getElementById("voicePreferenceSelect");
        const voiceContinuousToggle = document.getElementById("voiceContinuousToggle");
        const voiceCompatibilityHint = document.getElementById("voiceCompatibilityHint");
        const voiceActiveNameLabel = document.getElementById("voiceActiveNameLabel");

        const stt = FV.createSpeechRecognitionService();
        const tts = FV.createTextToSpeechService();
        const visualizer = new FV.VoiceVisualizer(voiceVisualizerCanvas, voiceOrbContainer, voiceTelemetryBadge);

        let activeMode = "text"; // "text" | "voice"
        let voiceState = "IDLE"; // IDLE | LISTENING | PROCESSING | SPEAKING | ERROR
        let isSubmittingRequest = false;
        let isStartingMic = false;
        let activeRequestSeq = 0;
        let lastSpokenText = "";
        let latestInterimTranscript = "";
        let continuousResumeTimer = null;
        let manualStopFallbackTimer = null;

        // Reflect browser STT/TTS capability upfront without blocking access
        if (!stt.isSupported()) {
            if (voiceCompatibilityHint) {
                voiceCompatibilityHint.textContent = "Speech recognition requires a Chromium browser (Edge/Chrome) with SpeechRecognition enabled. Text Chat remains fully available.";
                voiceCompatibilityHint.classList.remove("d-none");
            }
        }
        if (tts.isSupported()) {
            tts.getVoicesAsync().then(() => {
                const chosen = tts.selectPreferredVoice({ preference: voicePreferenceSelect?.value || "female", lang: "en" });
                if (voiceActiveNameLabel && chosen) {
                    voiceActiveNameLabel.textContent = `Voice: ${chosen.name}`;
                }
            });
        }

        function clearContinuousTimer() {
            if (continuousResumeTimer !== null) {
                clearTimeout(continuousResumeTimer);
                continuousResumeTimer = null;
            }
        }

        function clearManualStopTimer() {
            if (manualStopFallbackTimer !== null) {
                clearTimeout(manualStopFallbackTimer);
                manualStopFallbackTimer = null;
            }
        }

        function setVoiceState(nextState, customStatusText = null, customSubText = null) {
            voiceState = nextState;
            visualizer.setState(nextState);
            voiceWorkspaceEl.setAttribute("data-voice-state", nextState);

            const config = {
                IDLE: {
                    badge: "● Ready",
                    sub: "Press Start Listening or Space to speak to Friday.",
                    micLabel: "Start Listening",
                    micDisabled: false,
                    stopSpeakVisible: false
                },
                LISTENING: {
                    badge: "🎤 FRIDAY is listening...",
                    sub: "Speak naturally — release or click Stop when done.",
                    micLabel: "Stop Listening",
                    micDisabled: false,
                    stopSpeakVisible: false
                },
                PROCESSING: {
                    badge: "◉ FRIDAY is thinking...",
                    sub: "Processing your request through FRIDAY orchestrator...",
                    micLabel: "Processing...",
                    micDisabled: true,
                    stopSpeakVisible: false
                },
                SPEAKING: {
                    badge: "🔊 FRIDAY is speaking...",
                    sub: "Microphone paused during playback to prevent feedback.",
                    micLabel: "Speaking...",
                    micDisabled: true,
                    stopSpeakVisible: true
                },
                ERROR: {
                    badge: "⚠ Attention needed",
                    sub: "Check the message below or press Start Listening to try again.",
                    micLabel: "Start Listening",
                    micDisabled: false,
                    stopSpeakVisible: false
                }
            }[nextState] || {};

            if (voiceStatusBadge) {
                voiceStatusBadge.textContent = customStatusText || config.badge || nextState;
            }
            if (voiceStatusSub) {
                voiceStatusSub.textContent = customSubText || config.sub || "";
            }
            if (voiceMicBtn) {
                voiceMicBtn.disabled = Boolean(config.micDisabled) || !stt.isSupported();
                voiceMicBtn.classList.toggle("is-listening", nextState === "LISTENING");
                voiceMicBtn.setAttribute("aria-pressed", nextState === "LISTENING" ? "true" : "false");
            }
            if (voiceMicBtnLabel) {
                voiceMicBtnLabel.textContent = config.micLabel || "Start Listening";
            }
            if (voiceStopSpeakingBtn) {
                voiceStopSpeakingBtn.classList.toggle("d-none", !config.stopSpeakVisible);
                voiceStopSpeakingBtn.disabled = !config.stopSpeakVisible;
            }
        }

        function showError(message) {
            const safe = String(message || "FRIDAY couldn't process that request right now.");
            if (voiceErrorBanner && voiceErrorText) {
                voiceErrorText.textContent = safe;
                voiceErrorBanner.classList.remove("d-none");
            }
            setVoiceState("ERROR", "⚠ Voice Notice", safe);
        }

        function clearError() {
            if (voiceErrorBanner) {
                voiceErrorBanner.classList.add("d-none");
            }
        }

        function renderTranscript(text, isInterim = false) {
            if (!voiceTranscriptBox) return;
            voiceTranscriptBox.textContent = text ? String(text) : "Waiting for your voice command...";
            voiceTranscriptBox.classList.toggle("is-interim", Boolean(isInterim));
            voiceTranscriptBox.classList.toggle("text-secondary", !text);
        }

        function renderVoiceResponse(data) {
            if (!voiceResponseBox || !voiceArtifactsBox) return;

            voiceResponseBox.innerHTML = global.renderSafeMarkdown(data?.message || "");
            voiceArtifactsBox.innerHTML = "";

            if (data?.pendingConfirmation) {
                const card = document.createElement("div");
                card.className = "friday-confirm-card p-3 border-warning border-opacity-50 mt-2";
                card.innerHTML = `
                    <div class="d-flex flex-wrap align-items-center justify-content-between gap-2">
                        <span class="small text-warning fw-semibold">Confirmation required before deleting record</span>
                        <div class="d-flex gap-2">
                            <button type="button" class="btn btn-sm btn-danger js-voice-confirm" data-reply="Yes">Yes, Delete</button>
                            <button type="button" class="btn btn-sm btn-outline-light js-voice-confirm" data-reply="Cancel">Cancel</button>
                        </div>
                    </div>`;
                voiceArtifactsBox.appendChild(card);
                card.querySelectorAll(".js-voice-confirm").forEach(btn => {
                    btn.addEventListener("click", () => {
                        const reply = btn.getAttribute("data-reply");
                        card.remove();
                        submitVoiceTranscript(reply);
                    });
                });
            }

            if (data?.table && Array.isArray(data.table.rows) && data.table.rows.length > 0) {
                const wrapper = document.createElement("div");
                wrapper.innerHTML = global.buildTableHtml(data.table);
                voiceArtifactsBox.appendChild(wrapper);
            }

            if (data?.chart && Array.isArray(data.chart.datasets) && data.chart.datasets.length > 0) {
                const cid = "voice_chart_" + Math.random().toString(36).slice(2, 9);
                const card = document.createElement("div");
                card.className = "friday-chart-card mt-2";
                card.innerHTML = `
                    <div class="friday-card-header">
                        <span>${escapeBasic(data.chart.title || "Chart")}</span>
                        <span class="text-secondary small">${escapeBasic(data.chart.sourceDescription || "")}</span>
                    </div>
                    <div class="chart-canvas-wrap">
                        <canvas id="${cid}"></canvas>
                    </div>`;
                voiceArtifactsBox.appendChild(card);
                const canvasEl = document.getElementById(cid);
                global.renderFridayChart(canvasEl, data.chart);
            }

            if (data?.report && data.report.reportId) {
                const repWrap = document.createElement("div");
                repWrap.innerHTML = global.buildReportHtml(data.report);
                voiceArtifactsBox.appendChild(repWrap);
            }

            if (voiceReplayBtn) {
                voiceReplayBtn.disabled = !lastSpokenText || !tts.isSupported();
            }
        }

        function escapeBasic(str) {
            return String(str || "").replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
        }

        async function requestMicrophonePermissionStream() {
            if (!navigator.mediaDevices || typeof navigator.mediaDevices.getUserMedia !== "function") {
                return { stream: null, unsupportedApi: true };
            }

            try {
                const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
                return { stream, denied: false };
            } catch (err) {
                const name = String(err?.name || "");
                if (name === "NotAllowedError" || name === "PermissionDeniedError" || name === "SecurityError") {
                    return {
                        stream: null,
                        denied: true,
                        message: "Microphone access is required for Voice Mode. Please allow microphone access in your browser settings."
                    };
                }
                if (name === "NotFoundError" || name === "DevicesNotFoundError") {
                    return {
                        stream: null,
                        denied: true,
                        message: "No microphone was found on this device. Connect a microphone or switch to Text Chat."
                    };
                }
                return { stream: null, denied: false };
            }
        }

        async function startListening() {
            if (activeMode !== "voice") return;
            if (isStartingMic || isSubmittingRequest || voiceState === "PROCESSING" || voiceState === "SPEAKING") return;

            if (!stt.isSupported()) {
                showError("Speech recognition is not supported in this browser. Please switch to Microsoft Edge or Google Chrome, or use Text Chat.");
                return;
            }

            clearContinuousTimer();
            clearError();
            isStartingMic = true;

            // Cancel any lingering speech synthesis before opening mic
            tts.stop();

            try {
                const perm = await requestMicrophonePermissionStream();
                if (perm.denied) {
                    showError(perm.message);
                    return;
                }

                if (activeMode !== "voice") {
                    perm.stream?.getTracks().forEach(t => t.stop());
                    return;
                }

                if (perm.stream) {
                    await visualizer.attachMicStream(perm.stream);
                }

                latestInterimTranscript = "";
                renderTranscript("Listening...", true);
                setVoiceState("LISTENING");

                const started = stt.start({
                    lang: "en-US",
                    interimResults: true,
                    continuous: false,
                    onInterim: (interim) => {
                        if (voiceState === "LISTENING") {
                            latestInterimTranscript = String(interim || "").trim();
                            renderTranscript(latestInterimTranscript, true);
                        }
                    },
                    onFinal: (finalTranscript) => {
                        clearManualStopTimer();
                        stt.abort();
                        visualizer.detachAudioStream();
                        latestInterimTranscript = "";

                        const cleanTranscript = String(finalTranscript || "").trim();
                        if (!cleanTranscript) {
                            showError("I couldn't understand that. Please try again.");
                            return;
                        }

                        renderTranscript(cleanTranscript, false);
                        submitVoiceTranscript(cleanTranscript);
                    },
                    onStoppedManually: () => {
                        clearManualStopTimer();
                        visualizer.detachAudioStream();
                        latestInterimTranscript = "";
                        if (voiceState === "LISTENING" && !isSubmittingRequest) {
                            renderTranscript("", false);
                            setVoiceState("IDLE", "● Ready", "Listening stopped.");
                        }
                    },
                    onEndWithoutSpeech: () => {
                        clearManualStopTimer();
                        visualizer.detachAudioStream();
                        latestInterimTranscript = "";
                        if (voiceState === "LISTENING" && !isSubmittingRequest) {
                            renderTranscript("", false);
                            showError("I couldn't hear anything. Press Start Listening and speak clearly.");
                        }
                    },
                    onAborted: () => {
                        clearManualStopTimer();
                        visualizer.detachAudioStream();
                        latestInterimTranscript = "";
                        if (voiceState === "LISTENING") {
                            renderTranscript("", false);
                            setVoiceState("IDLE", "● Ready", "Listening stopped.");
                        }
                    },
                    onEnd: () => {
                        clearManualStopTimer();
                        if (voiceState === "LISTENING" && !isSubmittingRequest) {
                            visualizer.detachAudioStream();
                            renderTranscript("", false);
                            setVoiceState("IDLE", "● Ready", "Listening stopped.");
                        }
                    },
                    onError: (errObj) => {
                        clearManualStopTimer();
                        visualizer.detachAudioStream();
                        latestInterimTranscript = "";
                        if (!isSubmittingRequest) {
                            showError(errObj?.message || "I couldn't understand your voice.");
                        }
                    }
                });

                if (!started) {
                    visualizer.detachAudioStream();
                }
            } finally {
                isStartingMic = false;
            }
        }

        function stopListeningManually() {
            if (voiceState !== "LISTENING" || manualStopFallbackTimer !== null) return;
            clearManualStopTimer();
            visualizer.detachAudioStream();

            if (voiceMicBtn) {
                voiceMicBtn.disabled = true;
            }
            if (voiceMicBtnLabel) {
                voiceMicBtnLabel.textContent = "Stopping...";
            }

            const bufferedInterim = latestInterimTranscript.trim();
            stt.stop();

            // Guarantee responsive UI exit from LISTENING even if browser SpeechRecognition.stop() delays or stalls on onend
            manualStopFallbackTimer = setTimeout(() => {
                manualStopFallbackTimer = null;
                if (voiceState !== "LISTENING") return;

                stt.abort();
                latestInterimTranscript = "";

                if (bufferedInterim) {
                    renderTranscript(bufferedInterim, false);
                    submitVoiceTranscript(bufferedInterim);
                } else {
                    renderTranscript("", false);
                    setVoiceState("IDLE", "● Ready", "Listening stopped.");
                }
            }, 220);
        }

        async function submitVoiceTranscript(transcriptText) {
            const text = String(transcriptText || "").trim();
            if (!text) {
                showError("I couldn't understand that. Please try again.");
                return;
            }
            if (isSubmittingRequest) return;

            clearContinuousTimer();
            clearError();

            // Guarantee mic & STT are completely stopped before processing & speaking (prevents feedback loop)
            stt.abort();
            visualizer.detachAudioStream();

            isSubmittingRequest = true;
            const reqSeq = ++activeRequestSeq;
            setVoiceState("PROCESSING");
            renderTranscript(text, false);

            let responseData;
            try {
                responseData = await chatBridge.sendPrompt(text, { inputMode: "voice" });
            } catch {
                responseData = {
                    isError: true,
                    message: "FRIDAY couldn't process that request right now."
                };
            } finally {
                isSubmittingRequest = false;
            }

            if (reqSeq !== activeRequestSeq) return;

            if (!responseData || responseData.isError) {
                const safeErr = responseData?.message && !/exception|stacktrace/i.test(responseData.message)
                    ? responseData.message
                    : "FRIDAY couldn't process that request right now.";
                renderVoiceResponse({ isError: true, message: safeErr });
                showError(safeErr);
                return;
            }

            const cleanedSpeech = FV.SpeechResponseCleaner.clean(responseData);
            lastSpokenText = cleanedSpeech;
            renderVoiceResponse(responseData);

            if (activeMode !== "voice") {
                setVoiceState("IDLE");
                return;
            }

            speakResponseText(cleanedSpeech);
        }

        function speakResponseText(speechText) {
            if (!tts.isSupported()) {
                setVoiceState("IDLE", "● Ready (TTS unavailable in browser)", "Response shown below; speech playback isn't supported here.");
                return;
            }

            // Ensure mic stays OFF while speaking
            stt.abort();
            visualizer.detachAudioStream();
            setVoiceState("SPEAKING");

            const pref = voicePreferenceSelect?.value || "female";
            tts.speak(speechText, {
                voicePreference: pref,
                lang: "en",
                onStart: (voiceName) => {
                    if (voiceActiveNameLabel && voiceName) {
                        voiceActiveNameLabel.textContent = `Voice: ${voiceName}`;
                    }
                },
                onEnd: () => {
                    if (voiceState === "SPEAKING") {
                        setVoiceState("IDLE");
                        if (activeMode === "voice" && voiceContinuousToggle?.checked) {
                            clearContinuousTimer();
                            continuousResumeTimer = setTimeout(() => {
                                if (activeMode === "voice" && voiceState === "IDLE") {
                                    startListening();
                                }
                            }, 400);
                        }
                    }
                },
                onError: (err) => {
                    showError(err?.message || "Unable to play voice response.");
                }
            });
        }

        function stopSpeaking() {
            clearContinuousTimer();
            tts.stop();
            if (voiceState === "SPEAKING") {
                setVoiceState("IDLE", "● Ready", "Speech stopped. Conversation context preserved.");
            }
        }

        function switchMode(mode) {
            const target = mode === "voice" ? "voice" : "text";
            activeMode = target;

            const isVoice = target === "voice";
            textModeTabBtn.classList.toggle("active", !isVoice);
            textModeTabBtn.setAttribute("aria-selected", String(!isVoice));
            voiceModeTabBtn.classList.toggle("active", isVoice);
            voiceModeTabBtn.setAttribute("aria-selected", String(isVoice));

            chatMessagesEl?.classList.toggle("d-none", isVoice);
            chatComposerWrap?.classList.toggle("d-none", isVoice);
            voiceWorkspaceEl.classList.toggle("d-none", !isVoice);

            if (!isVoice) {
                clearContinuousTimer();
                stt.abort();
                tts.stop();
                visualizer.detachAudioStream();
                setVoiceState("IDLE");
            } else {
                visualizer.setState(voiceState);
                // Sync latest assistant message preview if user started in Text Chat first
                const latestMsg = chatBridge.getLatestAssistantMessage?.();
                if (latestMsg && voiceResponseBox && !voiceResponseBox.textContent.trim()) {
                    lastSpokenText = FV.SpeechResponseCleaner.clean(latestMsg);
                    renderVoiceResponse(latestMsg);
                }
            }
        }

        // Event wiring
        textModeTabBtn.addEventListener("click", () => switchMode("text"));
        voiceModeTabBtn.addEventListener("click", () => switchMode("voice"));

        [textModeTabBtn, voiceModeTabBtn].forEach(tab => {
            tab.addEventListener("keydown", (e) => {
                if (e.key === "ArrowRight" || e.key === "ArrowLeft") {
                    e.preventDefault();
                    switchMode(activeMode === "text" ? "voice" : "text");
                    (activeMode === "voice" ? voiceModeTabBtn : textModeTabBtn).focus();
                }
            });
        });

        voiceMicBtn?.addEventListener("click", () => {
            if (voiceState === "LISTENING") {
                stopListeningManually();
            } else {
                startListening();
            }
        });

        voiceStopSpeakingBtn?.addEventListener("click", stopSpeaking);

        voiceReplayBtn?.addEventListener("click", () => {
            if (lastSpokenText && voiceState !== "PROCESSING") {
                speakResponseText(lastSpokenText);
            }
        });

        voicePreferenceSelect?.addEventListener("change", () => {
            if (tts.isSupported()) {
                tts.getVoicesAsync().then(() => {
                    const chosen = tts.selectPreferredVoice({ preference: voicePreferenceSelect.value, lang: "en" });
                    if (voiceActiveNameLabel && chosen) {
                        voiceActiveNameLabel.textContent = `Voice: ${chosen.name}`;
                    }
                });
            }
        });

        document.addEventListener("keydown", (e) => {
            if (activeMode !== "voice") return;
            const tag = (e.target && e.target.tagName) ? e.target.tagName.toUpperCase() : "";

            if (e.key === "Escape") {
                if (voiceState === "SPEAKING") {
                    e.preventDefault();
                    stopSpeaking();
                } else if (voiceState === "LISTENING") {
                    e.preventDefault();
                    stt.abort();
                    visualizer.detachAudioStream();
                    setVoiceState("IDLE");
                }
                return;
            }

            if (e.code === "Space" && tag !== "INPUT" && tag !== "TEXTAREA" && tag !== "SELECT" && tag !== "BUTTON" && tag !== "A") {
                e.preventDefault();
                if (voiceState === "LISTENING") {
                    stopListeningManually();
                } else if (voiceState === "IDLE" || voiceState === "ERROR") {
                    startListening();
                }
            }
        });

        setVoiceState("IDLE");

        return {
            switchMode,
            startListening,
            stopListening: stopListeningManually,
            stopSpeaking,
            submitVoiceTranscript,
            getState: () => voiceState,
            getMode: () => activeMode
        };
    };
})(window);
