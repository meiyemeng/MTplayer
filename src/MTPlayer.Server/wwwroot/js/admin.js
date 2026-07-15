(() => {
    const body = document.body;
    const toggles = document.querySelectorAll("[data-sidebar-toggle]");
    for (const toggle of toggles) {
        toggle.addEventListener("click", () => {
            const open = body.classList.toggle("sidebar-open");
            document.querySelector(".menu-button")?.setAttribute("aria-expanded", String(open));
        });
    }

    for (const button of document.querySelectorAll("[data-password-toggle]")) {
        button.addEventListener("click", () => {
            const input = document.getElementById(button.dataset.passwordToggle);
            if (!(input instanceof HTMLInputElement)) return;
            const reveal = input.type === "password";
            input.type = reveal ? "text" : "password";
            button.textContent = reveal ? "隐藏" : "显示";
            button.setAttribute("aria-label", reveal ? "隐藏密码" : "显示密码");
        });
    }

    for (const button of document.querySelectorAll("[data-confirm]")) {
        button.addEventListener("click", event => {
            if (!window.confirm(button.dataset.confirm)) event.preventDefault();
        });
    }
})();
