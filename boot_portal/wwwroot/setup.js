(() => {
    const status = document.getElementById("restart-status");
    if (!status) return;

    const startedAt = Date.now();

    async function waitForReady() {
        try {
            const response = await fetch("/health/ready", { cache: "no-store" });
            const payload = await response.json();
            if (response.ok && payload.status === "ready") {
                window.location.replace("/");
                return;
            }
        } catch {
            // The expected connection drop confirms that the container is restarting.
        }

        if (Date.now() - startedAt > 120000) {
            status.textContent = "GridPool is taking longer than expected to restart. Restart the app from Umbrel, then reopen it.";
            return;
        }
        window.setTimeout(waitForReady, 1000);
    }

    window.setTimeout(waitForReady, 1000);
})();
