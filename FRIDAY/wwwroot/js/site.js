// FRIDAY — Personal AI Life Assistant Frontend Runtime
// Handles XSS-safe Markdown rendering, structured Table rendering, Chart.js + offline Canvas fallback, and Chat streaming/AJAX.

(function () {
    "use strict";

    // Persist client timezone offset (minutes EAST of UTC) in cookie for server-rendered Dashboard views
    const tzOffsetEastMinutes = -new Date().getTimezoneOffset();
    document.cookie = `friday_tz_offset=${tzOffsetEastMinutes}; path=/; SameSite=Lax`;

    function escapeHtml(raw) {
        if (raw === null || raw === undefined) return "";
        return String(raw)
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#39;");
    }

    function formatInlineMarkdown(escapedText) {
        if (!escapedText) return "";
        return escapedText
            .replace(/`([^`\n]+)`/g, "<code>$1</code>")
            .replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+|\/[^\s)]+)\)/g, '<a href="$2" target="_blank" rel="noopener noreferrer" class="text-info">$1</a>')
            .replace(/\*\*([^*\n]+)\*\*/g, "<strong>$1</strong>")
            .replace(/\*([^*\n]+)\*/g, "<em>$1</em>");
    }

    function isTableSeparatorLine(trimmedLine) {
        return /^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)+\|?$/.test(trimmedLine)
            || /^\|\s*:?-{2,}:?\s*\|$/.test(trimmedLine);
    }

    function isTableRowLine(trimmedLine) {
        return trimmedLine.includes("|") && !trimmedLine.startsWith("&gt;");
    }

    function parseTableCells(trimmedLine) {
        let s = trimmedLine.trim();
        if (s.startsWith("|")) s = s.substring(1);
        if (s.endsWith("|")) s = s.substring(0, s.length - 1);
        return s.split("|").map(cell => cell.trim());
    }

    // Safe Markdown formatter (always escapes HTML first to prevent XSS)
    window.renderSafeMarkdown = function (text) {
        if (!text) return "";
        let safe = escapeHtml(text);

        // Extract fenced code blocks first so their contents are preserved verbatim
        const codeBlocks = [];
        safe = safe.replace(/```([a-zA-Z0-9_-]*)\r?\n([\s\S]*?)```/g, function (_, _lang, code) {
            const idx = codeBlocks.length;
            codeBlocks.push(`<pre><code>${code.trim()}</code></pre>`);
            return `\n@@CODEBLOCK_${idx}@@\n`;
        });

        const lines = safe.split(/\r?\n/);
        const htmlOut = [];
        let listType = null; // "ul" | "ol"

        function closeList() {
            if (listType) {
                htmlOut.push(`</${listType}>`);
                listType = null;
            }
        }

        for (let i = 0; i < lines.length; i++) {
            const rawLine = lines[i];
            const trimmed = rawLine.trim();

            if (trimmed === "") {
                closeList();
                continue;
            }

            const codeMatch = /^@@CODEBLOCK_(\d+)@@$/.exec(trimmed);
            if (codeMatch) {
                closeList();
                htmlOut.push(codeBlocks[Number(codeMatch[1])] || "");
                continue;
            }

            // Detect Markdown table (supports optional single blank lines between header/separator/rows)
            if (isTableRowLine(trimmed)) {
                let sepIndex = i + 1;
                if (sepIndex < lines.length && lines[sepIndex].trim() === "") sepIndex++;

                if (sepIndex < lines.length && isTableSeparatorLine(lines[sepIndex].trim())) {
                    closeList();
                    const headers = parseTableCells(trimmed);
                    const bodyRows = [];
                    let j = sepIndex + 1;

                    while (j < lines.length) {
                        const rowTrim = lines[j].trim();
                        if (rowTrim === "") {
                            if (j + 1 < lines.length && isTableRowLine(lines[j + 1].trim()) && !isTableSeparatorLine(lines[j + 1].trim())) {
                                j++;
                                continue;
                            }
                            break;
                        }
                        if (!isTableRowLine(rowTrim) || isTableSeparatorLine(rowTrim)) break;
                        bodyRows.push(parseTableCells(rowTrim));
                        j++;
                    }

                    const theadHtml = headers.map(h => `<th>${formatInlineMarkdown(h)}</th>`).join("");
                    const tbodyHtml = bodyRows.map(r => {
                        const tds = headers.map((_, colIdx) => `<td>${formatInlineMarkdown(r[colIdx] ?? "")}</td>`).join("");
                        return `<tr>${tds}</tr>`;
                    }).join("");

                    htmlOut.push(
                        `<div class="friday-table-card my-2">` +
                        `<div class="table-responsive">` +
                        `<table class="friday-data-table md-table">` +
                        `<thead><tr>${theadHtml}</tr></thead>` +
                        `<tbody>${tbodyHtml}</tbody>` +
                        `</table></div></div>`
                    );

                    i = j - 1;
                    continue;
                }
            }

            if (/^####\s+/.test(trimmed)) {
                closeList();
                htmlOut.push(`<h6 class="mt-2 mb-1 fw-bold">${formatInlineMarkdown(trimmed.replace(/^####\s+/, ""))}</h6>`);
            } else if (/^###\s+/.test(trimmed)) {
                closeList();
                htmlOut.push(`<h6 class="mt-2 mb-1 fw-bold">${formatInlineMarkdown(trimmed.replace(/^###\s+/, ""))}</h6>`);
            } else if (/^##\s+/.test(trimmed)) {
                closeList();
                htmlOut.push(`<h5 class="mt-2 mb-1 fw-bold">${formatInlineMarkdown(trimmed.replace(/^##\s+/, ""))}</h5>`);
            } else if (/^#\s+/.test(trimmed)) {
                closeList();
                htmlOut.push(`<h5 class="mt-2 mb-1 fw-bold">${formatInlineMarkdown(trimmed.replace(/^#\s+/, ""))}</h5>`);
            } else if (/^([-*_]){3,}$/.test(trimmed)) {
                closeList();
                htmlOut.push('<hr class="border-secondary border-opacity-25 my-2" />');
            } else if (/^&gt;\s?/.test(trimmed)) {
                closeList();
                htmlOut.push(`<blockquote class="md-blockquote">${formatInlineMarkdown(trimmed.replace(/^&gt;\s?/, ""))}</blockquote>`);
            } else if (/^[-*]\s+/.test(trimmed)) {
                if (listType !== "ul") { closeList(); htmlOut.push('<ul class="mb-2 ps-3">'); listType = "ul"; }
                htmlOut.push(`<li>${formatInlineMarkdown(trimmed.replace(/^[-*]\s+/, ""))}</li>`);
            } else if (/^\d+\.\s+/.test(trimmed)) {
                if (listType !== "ol") { closeList(); htmlOut.push('<ol class="mb-2 ps-3">'); listType = "ol"; }
                htmlOut.push(`<li>${formatInlineMarkdown(trimmed.replace(/^\d+\.\s+/, ""))}</li>`);
            } else {
                closeList();
                htmlOut.push(`<p class="mb-2">${formatInlineMarkdown(rawLine)}</p>`);
            }
        }
        closeList();

        return htmlOut.filter(Boolean).join("\n");
    };

    window.buildTableHtml = function (table) {
        if (!table || !Array.isArray(table.rows) || table.rows.length === 0) return "";
        const cols = (table.columns || []).map(c => `<th>${escapeHtml(c)}</th>`).join("");
        const rows = table.rows.map(r => {
            const cells = (r || []).map(cell => `<td>${escapeHtml(cell)}</td>`).join("");
            return `<tr>${cells}</tr>`;
        }).join("");

        const footer = table.footerSummary || table.sourceDescription
            ? `<div class="friday-card-footer">
                 <span>${escapeHtml(table.footerSummary || "")}</span>
                 <span>${escapeHtml(table.sourceDescription || "")}</span>
               </div>`
            : "";

        return `
            <div class="friday-table-card">
                <div class="friday-card-header">
                    <span>${escapeHtml(table.title || "Results")}</span>
                    <span class="badge bg-secondary bg-opacity-25 text-light">${table.rows.length} row(s)</span>
                </div>
                <div class="table-responsive">
                    <table class="friday-data-table">
                        <thead><tr>${cols}</tr></thead>
                        <tbody>${rows}</tbody>
                    </table>
                </div>
                ${footer}
            </div>`;
    };

    window.buildReportHtml = function (report) {
        if (!report || !report.reportId) return "";
        return `
            <div class="friday-report-card p-3">
                <div class="d-flex flex-wrap align-items-center justify-content-between gap-2">
                    <div>
                        <div class="fw-bold text-info">${escapeHtml(report.title || "Generated Report")}</div>
                        <div class="small text-secondary">${escapeHtml(report.summary || "")}</div>
                    </div>
                    <div class="d-flex gap-2">
                        <a class="btn btn-sm btn-outline-info" href="${escapeHtml(report.pdfDownloadUrl)}" target="_blank" rel="noopener">Download PDF</a>
                        <a class="btn btn-sm btn-outline-light" href="${escapeHtml(report.docxDownloadUrl)}" target="_blank" rel="noopener">Download DOCX</a>
                    </div>
                </div>
            </div>`;
    };

    // Universal Chart Renderer (Chart.js primary + HTML5 2D Canvas fallback when offline)
    window.renderFridayChart = function (canvasEl, config) {
        if (!canvasEl || !config || !Array.isArray(config.datasets) || config.datasets.length === 0) return;

        const rawType = (config.chartType || "bar").toLowerCase();
        const palette = ["#6366f1", "#06b6d4", "#10b981", "#f59e0b", "#ec4899", "#8b5cf6", "#14b8a6"];

        if (typeof window.Chart !== "undefined") {
            let chartJsType = "bar";
            let stacked = false;

            if (rawType === "line" || rawType === "area") chartJsType = "line";
            else if (rawType === "pie") chartJsType = "pie";
            else if (rawType === "doughnut") chartJsType = "doughnut";
            else if (rawType === "scatter") chartJsType = "scatter";
            else if (rawType === "stackedbar") { chartJsType = "bar"; stacked = true; }

            const datasets = config.datasets.map((ds, idx) => {
                const baseColor = ds.borderColor || palette[idx % palette.length];
                const bgColors = Array.isArray(ds.backgroundColor) && ds.backgroundColor.length > 0
                    ? (chartJsType === "pie" || chartJsType === "doughnut" || config.datasets.length === 1 ? ds.backgroundColor : ds.backgroundColor[0])
                    : (chartJsType === "line" ? "rgba(99, 102, 241, 0.18)" : baseColor);

                return {
                    label: ds.label || "Series",
                    data: chartJsType === "scatter" && Array.isArray(ds.scatterPoints) ? ds.scatterPoints : (ds.data || []),
                    backgroundColor: bgColors,
                    borderColor: baseColor,
                    borderWidth: 2,
                    fill: rawType === "area" || Boolean(ds.fill),
                    tension: 0.32
                };
            });

            new window.Chart(canvasEl, {
                type: chartJsType,
                data: {
                    labels: config.labels || [],
                    datasets: datasets
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    plugins: {
                        legend: {
                            display: config.legend !== false,
                            labels: { color: "#cbd5e1" }
                        },
                        title: {
                            display: false
                        }
                    },
                    scales: (chartJsType === "pie" || chartJsType === "doughnut") ? {} : {
                        x: {
                            stacked: stacked,
                            ticks: { color: "#94a3b8" },
                            grid: { color: "rgba(148,163,184,0.1)" },
                            title: { display: Boolean(config.xAxisLabel), text: config.xAxisLabel || "", color: "#94a3b8" }
                        },
                        y: {
                            stacked: stacked,
                            beginAtZero: true,
                            ticks: { color: "#94a3b8" },
                            grid: { color: "rgba(148,163,184,0.1)" },
                            title: { display: Boolean(config.yAxisLabel), text: config.yAxisLabel || "", color: "#94a3b8" }
                        }
                    }
                }
            });
            return;
        }

        // Offline Fallback: Clean Native 2D Canvas Bar/Line Renderer
        const ctx = canvasEl.getContext("2d");
        if (!ctx) return;
        const w = canvasEl.parentElement.clientWidth || 500;
        const h = 250;
        canvasEl.width = w;
        canvasEl.height = h;

        const values = config.datasets[0].data || [];
        const labels = config.labels || [];
        if (values.length === 0) return;

        const maxVal = Math.max(...values, 1);
        const pad = 36;
        const plotW = w - pad * 2;
        const plotH = h - pad * 2;
        const step = plotW / values.length;

        ctx.strokeStyle = "rgba(148,163,184,0.25)";
        ctx.beginPath();
        ctx.moveTo(pad, pad);
        ctx.lineTo(pad, h - pad);
        ctx.lineTo(w - pad, h - pad);
        ctx.stroke();

        values.forEach((val, i) => {
            const barH = (val / maxVal) * (plotH - 12);
            const x = pad + i * step + step * 0.18;
            const bw = Math.max(12, step * 0.64);
            const y = h - pad - barH;
            ctx.fillStyle = palette[i % palette.length];
            ctx.fillRect(x, y, bw, barH);

            ctx.fillStyle = "#cbd5e1";
            ctx.font = "11px sans-serif";
            ctx.fillText(String(labels[i] || "").substring(0, 10), x, h - pad + 15);
            ctx.fillText(String(val), x, y - 5);
        });
    };
})();
