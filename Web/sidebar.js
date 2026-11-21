"use strict";

(() => {
    const script = document.currentScript;
    if (!script || window.top !== window) return;

    const webview = new URL("../Browse", script.src);
    const marker = "vodWebview";
    let frame;
    let resizeObserver;
    let drawerObserver;

    function isVoDRoute() {
        const query = location.hash.split("?")[1] || "";
        return new URLSearchParams(query).get(marker) === "1";
    }

    function position() {
        if (!frame) return;
        const drawer = document.querySelector(".mainDrawer.drawer-docked");
        const header = document.querySelector(".skinHeader");
        const left = drawer ? Math.max(0, drawer.getBoundingClientRect().right) : 0;
        const top = header ? Math.max(0, header.getBoundingClientRect().bottom) : 0;
        frame.style.left = left + "px";
        frame.style.top = top + "px";
    }

    function close() {
        resizeObserver?.disconnect();
        drawerObserver?.disconnect();
        frame?.remove();
        frame = undefined;
    }

    function leaveVod() {
        if (isVoDRoute()) {
            const url = new URL(location.href);
            const [route, query] = url.hash.split("?", 2);
            const params = new URLSearchParams(query);
            params.delete(marker);
            url.hash = route + "?" + params;
            history.replaceState(history.state, "", url);
        }
        close();
    }

    function open() {
        if (frame || !document.body) return;
        frame = document.createElement("iframe");
        frame.title = "Video on Demand";
        frame.src = webview.href;
        Object.assign(frame.style, {
            position: "fixed", inset: "0", border: "0", width: "auto", height: "auto",
            minWidth: "0", minHeight: "0", zIndex: "8", background: "#101418"
        });
        // Fixed-position iframes are replaced elements and need explicit viewport sizing.
        frame.style.width = "calc(100% - var(--vod-left, 0px))";
        frame.style.height = "calc(100% - var(--vod-top, 0px))";
        document.body.append(frame);
        const drawer = document.querySelector(".mainDrawer");
        const header = document.querySelector(".skinHeader");
        resizeObserver = new ResizeObserver(syncSize);
        if (drawer) {
            resizeObserver.observe(drawer);
            drawerObserver = new MutationObserver(syncSize);
            drawerObserver.observe(drawer, { attributes: true, attributeFilter: ["class"] });
        }
        if (header) resizeObserver.observe(header);
        syncSize();
    }

    function syncSize() {
        if (!frame) return;
        position();
        frame.style.setProperty("--vod-left", frame.style.left);
        frame.style.setProperty("--vod-top", frame.style.top);
    }

    function syncRoute() {
        if (isVoDRoute()) open();
        else close();
    }

    window.openVodItemDetails = itemId => {
        if (!frame || !/^[a-f0-9-]+$/i.test(itemId)) return;
        const url = new URL(window.location.href);
        url.hash = "!/item?" + new URLSearchParams({ id: itemId, serverId: window.ApiClient.serverId() });
        leaveVod();
        window.location.assign(url);
    };

    window.playFromVod = async itemId => {
        if (!frame || !isVoDRoute() || !/^[a-f0-9-]+$/i.test(itemId)) {
            throw new Error("VoD playback is unavailable.");
        }
        const activeFrame = frame;
        activeFrame.hidden = true;
        try {
            const client = window.ApiClient;
            const [playback, events, item] = await Promise.all([
                window.Emby.importModule("./modules/common/playback/playbackmanager.js"),
                window.Emby.importModule("./modules/emby-apiclient/events.js"),
                client.getItem(client.getCurrentUserId(), itemId)
            ]);
            if (!item || item.IsFolder || frame !== activeFrame) throw new Error("This item cannot be played.");

            await new Promise((resolve, reject) => {
                const timer = window.setTimeout(() => fail(new Error("The player did not report that it started.")), 20000);
                function fail(error) {
                    window.clearTimeout(timer);
                    events.off(playback, "playbackstart", started);
                    reject(error);
                }
                function started() {
                    window.clearTimeout(timer);
                    events.off(playback, "playbackstart", started);
                    resolve();
                }
                events.on(playback, "playbackstart", started);
                try {
                    Promise.resolve(playback.play({ items: [item], fullscreen: true })).catch(fail);
                } catch (error) {
                    fail(error);
                }
            });
            if (frame === activeFrame) leaveVod();
        } catch (error) {
            if (frame === activeFrame && isVoDRoute()) activeFrame.hidden = false;
            throw error;
        }
    };

    window.closeVodBrowser = leaveVod;

    document.addEventListener("click", event => {
        if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey ||
            !(event.target instanceof Element)) return;
        const option = event.target.closest(".mainDrawer .navMenuOption");
        const label = option?.querySelector(".listItemBodyText")?.textContent || option?.textContent || "";
        if (label.replace(/\s+/g, " ").trim().toLowerCase() !== "video on demand") {
            const link = event.target.closest("a[href]");
            if (frame && (option || (link && (!link.target || link.target === "_self")))) leaveVod();
            return;
        }
        const anchor = option.closest("a") || option.querySelector("a");
        const href = anchor?.getAttribute("href") || option.getAttribute("data-href");
        const id = option.dataset.id;
        if (!href && (option.dataset.action !== "custom" || !id || !/^[a-f0-9-]+$/i.test(id))) return;
        const url = new URL(href || "#!/list/list.html?" + new URLSearchParams({ parentId: id }), location.href);
        if (url.origin !== location.origin || !/^#!\/list\/list\.html\?/.test(url.hash)) return;

        event.preventDefault();
        event.stopImmediatePropagation();
        if (!isVoDRoute()) {
            const [route, query] = url.hash.split("?", 2);
            const params = new URLSearchParams(query);
            params.set(marker, "1");
            url.hash = route + "?" + params;
            history.pushState(history.state, "", url);
        }
        open();
    }, true);

    window.addEventListener("resize", syncSize);
    window.addEventListener("popstate", syncRoute);
    window.addEventListener("hashchange", syncRoute);
    // Emby can navigate with the History API without dispatching either browser event.
    for (const method of ["pushState", "replaceState"]) {
        const original = history[method];
        history[method] = function (...args) {
            const result = original.apply(this, args);
            if (frame && !isVoDRoute()) close();
            return result;
        };
    }
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", syncRoute);
    else syncRoute();
})();
