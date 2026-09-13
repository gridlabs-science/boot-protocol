(() => {
    const status = document.getElementById("restart-status");
    if (!status) return;

    const startedAt = Date.now();

    async function waitForReady() {
        try {
            const response = await fetch("/api/dashboard/v1/summary?window=24h", {
                cache: "no-store",
                headers: { Accept: "application/json" }
            });
            if (response.ok) {
                window.location.replace("/");
                return;
            }
        } catch {
            // The expected connection drop confirms that the container is restarting.
        }

        if (Date.now() - startedAt > 120000) {
            status.textContent = "GridPool is taking longer than expected to restart. You may leave this page open or restart the app from Umbrel.";
            return;
        }
        window.setTimeout(waitForReady, 1000);
    }

    window.setTimeout(waitForReady, 1000);
})();
