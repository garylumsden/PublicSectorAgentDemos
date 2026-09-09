"use strict";

const form = document.querySelector("#compare-form");
const promptInput = document.querySelector("#prompt");
const compareButton = document.querySelector("#compare");
const cancelButton = document.querySelector("#cancel");
const resetButton = document.querySelector("#reset");
const runStatus = document.querySelector("#run-status");
const assessButton = document.querySelector("#assess-quality");
const cancelAssessmentButton = document.querySelector("#cancel-assessment");
const assessmentStatus = document.querySelector("#assessment-status");
const assessmentResult = document.querySelector("#assessment-result");
const panels = new Map(["foundation", "ground"].map(name => [name, {
    element: document.getElementById(name), state: "ready", started: 0, eligible: false
}]));
let session;
let controller;
let assessmentController;
let activeComparisonId;
let submittedPrompt;

function updateCount() {
    document.querySelector("#count").textContent = `${promptInput.value.length} / 8000 characters`;
    document.querySelector("#scenario").textContent = promptInput.value === session?.prompt ? "MPM-005" : "Custom input";
}

function clearAssessment(message = "Run both agents to enable assessment.") {
    assessmentController?.abort();
    assessmentController = null;
    assessmentResult.hidden = true;
    document.querySelector("#assessment-summary").textContent = "";
    document.querySelector("#assessment-criteria").replaceChildren();
    document.querySelector("#assessment-reference").textContent = "";
    document.querySelector("#assessment-limitations").replaceChildren();
    assessmentStatus.textContent = message;
    cancelAssessmentButton.disabled = true;
    updateAssessmentAvailability();
}

function updateAssessmentAvailability() {
    const eligible = session?.assessment?.available && activeComparisonId && submittedPrompt === promptInput.value &&
        [...panels.values()].every(panel => panel.eligible);
    assessButton.disabled = !eligible || Boolean(controller) || Boolean(assessmentController);
}

function invalidateAssessment() {
    if (activeComparisonId && submittedPrompt !== promptInput.value) {
        activeComparisonId = null;
        for (const panel of panels.values()) panel.eligible = false;
        clearAssessment("The prompt changed. Run both agents again before assessment.");
    }
}

function setStatus(panel, state, text) {
    panel.state = state;
    panel.element.dataset.state = state;
    panel.element.querySelector(".status").textContent = text;
}

function stopPending(state, text) {
    for (const panel of panels.values()) {
        if (panel.state === "running") {
            const seconds = ((performance.now() - panel.started) / 1000).toFixed(1);
            setStatus(panel, state, `${text} (${seconds}s)`);
        }
    }
}

function showUpdate(update) {
    const panel = panels.get(update.agent);
    if (!update.comparisonId || (activeComparisonId && update.comparisonId !== activeComparisonId)) {
        throw new Error("Unexpected comparison identity");
    }
    activeComparisonId = update.comparisonId;
    if (!panel || panel.state !== "running") {
        throw new Error("Unexpected comparison update");
    }
    const seconds = (update.elapsedMs / 1000).toFixed(1);
    if (update.answer) {
        const answer = update.answer;
        // Only the server's encoded Markdown subset enters this HTML container.
        panel.element.querySelector(".rendered").innerHTML = answer.renderedHtml;
        panel.element.querySelector(".original").value = answer.originalText;
        panel.element.querySelector(".response-json").value = answer.originalResponse;
        panel.element.querySelector(".copy").disabled = false;
        panel.element.querySelector(".copy-json").disabled = false;
        const citations = panel.element.querySelector(".citations");
        const label = document.createElement("p");
        label.textContent = answer.citations.length
            ? "Service citation annotations (not independently verified):"
            : "No service citation annotations. Any citation markers remain in the original answer.";
        citations.append(label);
        for (const citation of answer.citations) {
            const item = document.createElement("p");
            item.textContent = `${citation.type}: ${citation.title} ${citation.reference}`;
            citations.append(item);
        }
        const serviceStatus = answer.responseStatus || "not supplied";
        panel.eligible = serviceStatus === "completed" && Boolean(answer.originalText.trim());
        setStatus(panel, "completed", `Response received in ${seconds}s. Service status: ${serviceStatus}.${answer.originalText ? "" : " No answer text was returned; inspect the original JSON."}`);
    } else {
        panel.eligible = false;
        setStatus(panel, update.state, `${update.error} (${seconds}s)`);
    }
    updateAssessmentAvailability();
}

async function copyText(panel, selector) {
    const field = panel.element.querySelector(selector);
    try {
        await navigator.clipboard.writeText(field.value);
        runStatus.textContent = "Original content copied.";
    } catch {
        field.focus();
        field.select();
        runStatus.textContent = "Clipboard access failed. Copy the selected original content manually.";
    }
}

for (const panel of panels.values()) {
    panel.element.querySelector(".copy").addEventListener("click", () => copyText(panel, ".original"));
    panel.element.querySelector(".copy-json").addEventListener("click", () => copyText(panel, ".response-json"));
}
promptInput.addEventListener("input", () => {
    updateCount();
    invalidateAssessment();
});
resetButton.addEventListener("click", () => {
    promptInput.value = session.prompt;
    updateCount();
    activeComparisonId = null;
    for (const panel of panels.values()) panel.eligible = false;
    clearAssessment("The prompt was restored. Run both agents again before assessment.");
});
cancelButton.addEventListener("click", () => {
    controller?.abort();
    stopPending("cancelled", "Cancelled locally");
    runStatus.textContent = "Pending requests cancelled locally. Completed answers remain visible.";
    cancelButton.disabled = true;
});

cancelAssessmentButton.addEventListener("click", () => {
    assessmentController?.abort();
    assessmentStatus.textContent = "Assessment cancelled locally. The original answers remain available.";
    cancelAssessmentButton.disabled = true;
});

function renderAnswerAssessment(cell, answer) {
    const score = document.createElement("span");
    score.className = "score";
    score.textContent = answer.score === null ? "Not assessed" : `${answer.score} / 5`;
    cell.append(score);
    const explanation = document.createElement("span");
    explanation.textContent = answer.explanation;
    cell.append(explanation);
    if (answer.quote) {
        const quote = document.createElement("span");
        quote.className = "answer-quote";
        quote.textContent = `“${answer.quote}”`;
        cell.append(quote);
    }
}

function renderAssessment(assessment) {
    document.querySelector("#assessment-summary").textContent = assessment.summary;
    const body = document.querySelector("#assessment-criteria");
    body.replaceChildren();
    for (const criterion of assessment.criteria) {
        const row = document.createElement("tr");
        const label = document.createElement("th");
        label.scope = "row";
        label.textContent = criterion.label;
        const foundation = document.createElement("td");
        const ground = document.createElement("td");
        const difference = document.createElement("td");
        renderAnswerAssessment(foundation, criterion.foundation);
        renderAnswerAssessment(ground, criterion.ground);
        difference.textContent = criterion.difference === null ? "Not assessed" : `${criterion.difference > 0 ? "+" : ""}${criterion.difference}`;
        row.append(label, foundation, ground, difference);
        body.append(row);
    }
    document.querySelector("#assessment-reference").textContent =
        `Reference: ${assessment.referenceBasis} Judge: ${assessment.model}. Rubric: ${assessment.rubricVersion}.`;
    const limitations = document.querySelector("#assessment-limitations");
    limitations.replaceChildren();
    for (const limitation of assessment.limitations) {
        const item = document.createElement("li");
        item.textContent = limitation;
        limitations.append(item);
    }
    assessmentResult.hidden = false;
}

assessButton.addEventListener("click", async () => {
    if (!activeComparisonId || assessmentController || assessButton.disabled) return;
    assessmentController = new AbortController();
    const active = assessmentController;
    const expectedComparisonId = activeComparisonId;
    const started = performance.now();
    assessButton.disabled = true;
    cancelAssessmentButton.disabled = false;
    assessmentResult.hidden = true;
    assessmentStatus.textContent = "Assessing the displayed answers - 0.0s elapsed (90s limit)";
    const timer = setInterval(() => {
        assessmentStatus.textContent = `Assessing the displayed answers - ${((performance.now() - started) / 1000).toFixed(1)}s elapsed (90s limit)`;
    }, 100);
    try {
        const response = await fetch("/api/assess-quality", {
            method: "POST",
            headers: { "Content-Type": "application/json", "X-Demo1-CSRF": session.requestToken },
            credentials: "same-origin",
            body: JSON.stringify({ comparisonId: expectedComparisonId }),
            signal: active.signal
        });
        const result = await response.json();
        if (expectedComparisonId !== activeComparisonId || submittedPrompt !== promptInput.value) return;
        if (!response.ok || !result.assessment) {
            assessmentStatus.textContent = result.error || "The quality assessment failed. The original answers remain available.";
            return;
        }
        renderAssessment(result.assessment);
        assessmentStatus.textContent = `Assessment finished in ${(result.assessment.elapsedMs / 1000).toFixed(1)}s${result.assessment.cached ? " (cached)" : ""}.`;
    } catch {
        if (active.signal.aborted) {
            assessmentStatus.textContent = "Assessment cancelled locally. The original answers remain available.";
        } else {
            assessmentStatus.textContent = "The assessment connection failed. The original answers remain available.";
        }
    } finally {
        clearInterval(timer);
        if (assessmentController === active) assessmentController = null;
        cancelAssessmentButton.disabled = true;
        updateAssessmentAvailability();
    }
});

form.addEventListener("submit", async event => {
    event.preventDefault();
    if (controller || !session) return;
    if (!promptInput.value.trim() || promptInput.value.length > session.maxPromptCharacters) {
        runStatus.textContent = "Enter a prompt with 1 to 8000 characters, not only whitespace.";
        return;
    }

    clearAssessment("Comparison running. Assessment will be available after two eligible answers.");
    activeComparisonId = null;
    submittedPrompt = promptInput.value;
    for (const panel of panels.values()) panel.eligible = false;
    controller = new AbortController();
    const active = controller;
    const prompt = promptInput.value;
    compareButton.disabled = resetButton.disabled = promptInput.disabled = true;
    cancelButton.disabled = false;
    runStatus.textContent = "Both agents are running with the same prompt. Each has a 180-second limit.";
    for (const panel of panels.values()) {
        panel.started = performance.now();
        panel.element.querySelector(".rendered").replaceChildren();
        panel.element.querySelector(".citations").replaceChildren();
        panel.element.querySelector(".original").value = "";
        panel.element.querySelector(".response-json").value = "";
        panel.element.querySelector(".copy").disabled = true;
        panel.element.querySelector(".copy-json").disabled = true;
        setStatus(panel, "running", "Waiting for the agent response...");
    }
    const timer = setInterval(() => {
        for (const panel of panels.values()) {
            if (panel.state === "running") {
                const seconds = ((performance.now() - panel.started) / 1000).toFixed(1);
                panel.element.querySelector(".status").textContent = `Waiting for the agent response - ${seconds}s elapsed (180s limit)`;
            }
        }
    }, 100);

    try {
        const response = await fetch("/api/compare", {
            method: "POST",
            headers: { "Content-Type": "application/json", "X-Demo1-CSRF": session.requestToken },
            credentials: "same-origin",
            body: JSON.stringify({ prompt }),
            signal: active.signal
        });
        if (!response.ok) {
            const message = response.status === 429 ? "Two comparisons are already running. Wait before retrying."
                : response.status === 400 ? "The request was rejected. Check the prompt or reload the page."
                : response.status === 413 ? "The request exceeds the 32 KiB UTF-8 limit. Use a shorter prompt."
                : "The local request failed. Reload the page before retrying.";
            stopPending("failed", message);
            runStatus.textContent = message;
            return;
        }

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let pending = "";
        while (true) {
            const { value, done } = await reader.read();
            pending += decoder.decode(value, { stream: !done });
            let boundary;
            while ((boundary = pending.indexOf("\n")) >= 0) {
                const line = pending.slice(0, boundary);
                pending = pending.slice(boundary + 1);
                if (line) showUpdate(JSON.parse(line));
            }
            if (done) break;
        }
        if (pending || [...panels.values()].some(panel => panel.state === "running")) {
            throw new Error("Incomplete comparison stream");
        }
        runStatus.textContent = "Comparison finished. Review each agent's outcome separately.";
        assessmentStatus.textContent = [...panels.values()].every(panel => panel.eligible)
            ? "Ready to assess the exact displayed answer pair."
            : "Assessment is unavailable because both responses must contain completed answer text.";
        updateAssessmentAvailability();
    } catch (error) {
        if (active.signal.aborted) {
            stopPending("cancelled", "Cancelled locally");
        } else {
            active.abort();
            stopPending("failed", "The connection ended before this response arrived");
            runStatus.textContent = "The comparison connection failed. Completed answers remain visible.";
        }
    } finally {
        clearInterval(timer);
        controller = null;
        compareButton.disabled = resetButton.disabled = promptInput.disabled = false;
        cancelButton.disabled = true;
        updateAssessmentAvailability();
    }
});

async function initialize() {
    try {
        const response = await fetch("/api/session", { credentials: "same-origin" });
        if (!response.ok) throw new Error("Session unavailable");
        session = await response.json();
        promptInput.value = session.prompt;
        promptInput.maxLength = session.maxPromptCharacters;
        document.querySelector('[data-agent-config="foundation"]').textContent =
            `${session.agents.model} · ${session.agents.foundationReasoning} reasoning`;
        document.querySelector('[data-agent-config="ground"]').textContent =
            `${session.agents.model} · ${session.agents.groundReasoning} reasoning · required Foundry IQ retrieval (${session.agents.groundRetrievalReasoning})`;
        document.querySelector("#timing-explanation").textContent = session.agents.timingExplanation;
        promptInput.disabled = compareButton.disabled = resetButton.disabled = false;
        updateCount();
        runStatus.textContent = "Ready. MPM-005 is loaded from the repository fixture.";
    } catch {
        runStatus.textContent = "The local session could not load. Use http://localhost:5090 and reload the page.";
    }
}

initialize();
