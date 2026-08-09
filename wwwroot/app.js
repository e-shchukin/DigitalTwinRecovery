const form = document.querySelector("#experiment-form");
const runButton = document.querySelector("#run-button");
const statusElement = document.querySelector("#status");
const resultsElement = document.querySelector("#results");
const resultRows = document.querySelector("#result-rows");
const reportNotes = document.querySelector("#report-notes");
const rawJson = document.querySelector("#raw-json");

const modeNames = {
    Hmac: "HMAC",
    HmacAndSequence: "HMAC + Seq",
    ProposedMethod: "Разработанный метод"
};

function requestFromForm() {
    const data = new FormData(form);

    return {
        runsPerScenario: Number(data.get("runsPerScenario")),
        seed: Number(data.get("seed")),
        probeIntervalMs: Number(data.get("probeIntervalMs")),
        minRoundTripMs: Number(data.get("minRoundTripMs")),
        maxRoundTripMs: Number(data.get("maxRoundTripMs")),
        retryCount: Number(data.get("retryCount"))
    };
}

function setBusy(value) {
    runButton.disabled = value;
}

function setStatus(text, isError = false) {
    statusElement.textContent = text;
    statusElement.classList.toggle("error", isError);
}

function formatPercent(value) {
    return `${Number(value).toFixed(2)}%`;
}

function metricCell(value, applicable = true) {
    const td = document.createElement("td");
    td.className = "metric";

    if (!applicable) {
        td.classList.add("not-applicable");
        td.textContent = "–";
        td.title = "В этом сценарии восстановление не выполнялось";
        return td;
    }

    const progress = document.createElement("progress");
    progress.max = 100;
    progress.value = value;

    const text = document.createElement("span");
    text.textContent = formatPercent(value);

    td.append(progress, text);
    return td;
}

function renderReport(report) {
    resultRows.replaceChildren();

    for (const scenario of report.scenarios) {
        scenario.modes.forEach((mode, index) => {
            const row = document.createElement("tr");

            if (index === 0) {
                const scenarioCell = document.createElement("td");
                scenarioCell.className = "scenario";
                scenarioCell.rowSpan = scenario.modes.length;
                const scenarioName = document.createElement("strong");
                scenarioName.textContent = scenario.name;
                const scenarioPurpose = document.createElement("small");
                scenarioPurpose.textContent = scenario.purpose;
                scenarioCell.append(scenarioName, scenarioPurpose);
                row.append(scenarioCell);
            }

            const modeCell = document.createElement("td");
            modeCell.textContent = modeNames[mode.mode] ?? mode.mode;
            row.append(modeCell);
            row.append(metricCell(mode.detectionRatePercent));
            row.append(metricCell(mode.communicationLossRatePercent));
            row.append(metricCell(
                mode.recoverySuccessRatePercent,
                mode.recoveryAttempts > 0));
            resultRows.append(row);
        });
    }

    const latency = report.terminalOmissionDetectionLatency;
    document.querySelector("#latency-mean").textContent = `${latency.meanMs.toFixed(2)} мс`;
    document.querySelector("#latency-median").textContent = `${latency.medianMs.toFixed(2)} мс`;
    document.querySelector("#latency-p95").textContent = `${latency.percentile95Ms.toFixed(2)} мс`;
    document.querySelector("#latency-max").textContent = `${latency.maximumMs.toFixed(2)} мс`;

    rawJson.textContent = JSON.stringify(report, null, 2);
    reportNotes.replaceChildren();
    for (const note of report.notes) {
        const item = document.createElement("li");
        item.textContent = note;
        reportNotes.append(item);
    }
    resultsElement.hidden = false;
}

async function readError(response) {
    try {
        const body = await response.json();
        return body.error ?? `HTTP ${response.status}`;
    } catch {
        return `HTTP ${response.status}`;
    }
}

form.addEventListener("submit", async event => {
    event.preventDefault();
    setBusy(true);
    setStatus("Выполняется расчет...");

    try {
        const response = await fetch("/api/experiment/run", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(requestFromForm())
        });

        if (!response.ok) {
            throw new Error(await readError(response));
        }

        renderReport(await response.json());
        setStatus("Расчет завершен.");
    } catch (error) {
        setStatus(error.message, true);
    } finally {
        setBusy(false);
    }
});
