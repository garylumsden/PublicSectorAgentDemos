window.defraDialogs = {
    showModal(dialog, owner) {
        if (dialog && !dialog.open) {
            const previousFocus = document.activeElement;
            const deny = event => {
                event.preventDefault();
                owner.invokeMethodAsync("CancelAgentApprovalAsync").catch(() => {
                    // The server connection is unavailable. Never turn this into an approval.
                    dialog.querySelector(".approval-modal__confirm").disabled = true;
                    dialog.querySelector(".approval-modal__lead").textContent =
                        "The server connection is unavailable. No approval was sent. Reload before requesting another reservation.";
                });
            };
            dialog.addEventListener("cancel", deny);
            dialog.addEventListener("close", deny);
            dialog.approvalCleanup = () => {
                dialog.removeEventListener("cancel", deny);
                dialog.removeEventListener("close", deny);
                if (dialog.open) {
                    dialog.close();
                }
                if (previousFocus instanceof HTMLElement && previousFocus.isConnected) {
                    previousFocus.focus();
                }
            };
            dialog.showModal();
            dialog.querySelector("#support-approval-title")?.focus();
        }
    },
    close(dialog) {
        if (dialog) {
            dialog.approvalCleanup?.();
            delete dialog.approvalCleanup;
            if (dialog.open) {
                dialog.close();
            }
        }
    }
};
