const warmUpButton = document.querySelector("#warm-up");
const warmUpSummary = document.querySelector("#warm-up-summary");
const warmUpResults = document.querySelector("#warm-up-results");
const warmUpExtraResults = document.querySelector("#warm-up-extra-results");

document.querySelectorAll(".copy-input").forEach((button) => {
  button.addEventListener("click", async () => {
    const input = document.getElementById(button.dataset.copyTarget);
    await navigator.clipboard.writeText(input.textContent);
    button.textContent = "Copied";
  });
});

async function showWarmUp() {
  const response = await fetch("/api/warm-up");
  if (!response.ok) throw new Error("Warm-up status is unavailable.");
  const run = await response.json();
  warmUpSummary.textContent = run.state === "running"
    ? "Warm-up is running. The page remains available."
    : "Warm-up completed. Read each result; completion does not mean every app is ready.";
  const renderResult = (result) => {
    const item = document.createElement("li");
    item.textContent = `${result.title}: ${result.outcome} — ${result.message}`;
    return item;
  };
  warmUpResults.replaceChildren(...run.results.filter((result) => !result.extra).map(renderResult));
  warmUpExtraResults?.replaceChildren(...run.results.filter((result) => result.extra).map(renderResult));
  return run.state;
}

warmUpButton.addEventListener("click", async () => {
  warmUpButton.disabled = true;
  warmUpSummary.textContent = "Warm-up is starting. The page remains available.";
  try {
    const response = await fetch("/api/warm-up", { method: "POST" });
    if (!response.ok) throw new Error("Warm-up could not start.");
    while (await showWarmUp() === "running") {
      await new Promise((resolve) => setTimeout(resolve, 500));
    }
  } catch {
    warmUpSummary.textContent = "Warm-up status is unavailable.";
  } finally {
    warmUpButton.disabled = false;
  }
});
