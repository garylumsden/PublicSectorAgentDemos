export function initializeInvestigation(root = document) {
    const prompt = root.querySelector("#Prompt");
    const scenarioId = root.querySelector("#ScenarioId");
    const form = root.querySelector("[data-investigation-form]");
    const progress = root.querySelector("[data-investigation-progress]");
    const submit = root.querySelector("[data-investigation-submit]");

    root.querySelectorAll("[data-scenario-prompt]").forEach((button) => {
        if (button.dataset.scenarioInitialized === "true") {
            return;
        }

        button.dataset.scenarioInitialized = "true";
        button.addEventListener("click", () => {
            if (!prompt || !scenarioId) {
                return;
            }

            prompt.value = button.dataset.scenarioPrompt ?? "";
            scenarioId.value = button.dataset.scenarioId ?? "";
            prompt.dispatchEvent(new Event("input", { bubbles: true }));
            prompt.focus();
            prompt.scrollIntoView({ behavior: "smooth", block: "center" });
        });
    });

    if (!form ||
        !progress ||
        !submit ||
        form.dataset.investigationInitialized === "true") {
        return;
    }

    form.dataset.investigationInitialized = "true";
    form.addEventListener("submit", () => {
        if (!form.reportValidity()) {
            return;
        }

        document.querySelectorAll("[data-validation-errors]").forEach((element) => element.remove());
        document.querySelectorAll("[data-investigation-result]").forEach((element) => element.remove());
        submit.disabled = true;
        form.setAttribute("aria-busy", "true");
        progress.hidden = false;
    });
}

initializeInvestigation();
