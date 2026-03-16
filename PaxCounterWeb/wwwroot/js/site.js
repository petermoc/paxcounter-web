// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.
(function () {
    const loader = document.getElementById("page-loader");
    if (!loader) {
        return;
    }

    let showTimer = null;

    function showLoader() {
        if (showTimer) {
            clearTimeout(showTimer);
        }

        showTimer = window.setTimeout(() => {
            loader.classList.add("is-visible");
            document.body.classList.add("is-page-loading");
        }, 120);
    }

    function hideLoader() {
        if (showTimer) {
            clearTimeout(showTimer);
            showTimer = null;
        }

        loader.classList.remove("is-visible");
        document.body.classList.remove("is-page-loading");
    }

    function shouldHandleLink(anchor) {
        if (!anchor || anchor.target === "_blank" || anchor.hasAttribute("download")) {
            return false;
        }

        const href = anchor.getAttribute("href");
        if (!href || href.startsWith("#") || href.startsWith("javascript:")) {
            return false;
        }

        const url = new URL(anchor.href, window.location.href);
        if (url.origin !== window.location.origin) {
            return false;
        }

        if (anchor.dataset.noLoader === "true") {
            return false;
        }

        return true;
    }

    document.addEventListener("click", (event) => {
        const anchor = event.target.closest("a[href]");
        if (!shouldHandleLink(anchor)) {
            return;
        }

        showLoader();
    }, true);

    document.addEventListener("submit", (event) => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement)) {
            return;
        }

        if (form.dataset.noLoader === "true") {
            return;
        }

        showLoader();
    }, true);

    window.addEventListener("pageshow", hideLoader);
    window.addEventListener("load", hideLoader);
    window.addEventListener("beforeunload", () => {
        loader.classList.add("is-visible");
        document.body.classList.add("is-page-loading");
    });
})();
