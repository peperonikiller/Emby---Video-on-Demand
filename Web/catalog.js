"use strict";

const apiRoot = new URL("../", location.href);
const appUrl = new URL("../web/index.html", location.href);
const pageSizes = [20, 50, 100];
const $ = id => document.getElementById(id);
const shelfIds = {};
const state = { shelf: "movies", path: [], page: 0, query: "", sort: "popularity", pageSize: 20 };
const defaultState = { ...state };
const catalogSorts = {
    title: ["SortName", "Ascending"],
    "title-desc": ["SortName", "Descending"],
    new: ["PremiereDate,SortName", "Descending"],
    old: ["PremiereDate,SortName", "Ascending"],
    rating: ["CommunityRating,SortName", "Descending"],
    added: ["DateCreated,SortName", "Descending"]
};
let session;
let openWatchInNewTab = false;
let loadVersion = 0;
let detailVersion = 0;
let requestController;
let objectUrls = [];
let detailArt = [];

$("emby-home").href = appUrl.href;
if (window.parent !== window) {
    document.body.classList.add("embedded");
    $("emby-home").target = "_top";
}

async function api(path, params, signal) {
    const url = new URL(path, apiRoot);
    url.search = new URLSearchParams(params);
    const headers = session ? { "X-Emby-Token": session.token } : {};
    const response = await fetch(url, { headers, signal });
    if (!response.ok) {
        const error = new Error("Emby returned " + response.status);
        error.status = response.status;
        throw error;
    }
    return response.json();
}

function storedSession(serverId) {
    try {
        const data = JSON.parse(localStorage.getItem("servercredentials3") || "{}");
        const server = (data.Servers || []).find(row =>
            String(row.Id).toLowerCase() === String(serverId).toLowerCase());
        if (!server || !server.UserId) return null;
        const user = (server.Users || []).find(row => row.UserId === server.UserId);
        const token = user?.AccessToken || server.AccessToken;
        return token ? { token, userId: server.UserId, serverId: server.Id } : null;
    } catch {
        return null;
    }
}

async function boot() {
    status("Connecting to Emby…");
    try {
        const server = await api("System/Info/Public", {});
        session = storedSession(server.Id);
        if (!session) return signIn();
        const viewerOptions = api("Vod/ViewerOptions", {}).catch(() => ({}));
        const result = await api("Channels", { UserId: session.userId });
        const channel = (result.Items || []).find(item => item.Name === "Video On Demand");
        if (!channel) return status("Video On Demand is unavailable for this Emby account.");
        const shelves = await items({ ParentId: channel.Id });
        shelfIds.movies = shelves.Items.find(item => item.Name === "Movies")?.Id;
        shelfIds.tv = shelves.Items.find(item => item.Name === "TV Shows")?.Id;
        if (!shelfIds.movies || !shelfIds.tv) {
            return status("The VoD catalog is empty or still indexing in Emby.");
        }
        if (history.state?.vod) Object.assign(state, history.state.vod);
        history.replaceState({ vod: { ...state } }, "");
        $("query").value = state.query;
        $("sort").value = state.sort;
        $("page-size").value = state.pageSize;
        openWatchInNewTab = (await viewerOptions).OpenWatchInNewTab === true;
        void refreshStatus();
        await load();
    } catch (error) {
        if (error.status === 401 || error.status === 403) signIn();
        else status("Couldn’t reach the Emby catalog. Reload this page to try again.");
    }
}

async function refreshStatus() {
    try {
        const result = await api("Vod/Status", {});
        $("refresh-panel").hidden = false;
        $("refresh-phase").textContent = result.Phase;
        const counts = [["movies", result.Movies], ["shows", result.Shows], ["episodes", result.Episodes]];
        $("refresh-counts").textContent = counts.filter(([, value]) => Number.isInteger(value))
            .map(([label, value]) => value.toLocaleString() + " " + label).join(" · ");
        $("last-refresh").textContent = result.LastSuccessfulRefresh ?
            "Last successful index: " + new Date(result.LastSuccessfulRefresh).toLocaleString() : "No completed index recorded.";
        $("refresh-help").textContent = result.Phase === "Refresh failed"
            ? "Check the playlist in the Emby dashboard. The previous index is retained when the playlist cannot be read."
            : "";
        window.setTimeout(refreshStatus, 10000);
    } catch { /* The status endpoint is available to Emby administrators. */ }
}

function changeState(changes, push = false, focus = true) {
    Object.assign(state, changes);
    history[push ? "pushState" : "replaceState"]({ vod: { ...state } }, "");
    $("query").value = state.query;
    $("sort").value = state.sort;
    $("page-size").value = state.pageSize;
    load(focus);
}

window.addEventListener("popstate", event => {
    Object.assign(state, event.state?.vod || defaultState);
    $("query").value = state.query;
    $("sort").value = state.sort;
    $("page-size").value = state.pageSize;
    load(true);
});

function status(message) { $("status").textContent = message; }

function signIn() {
    status("Sign in to Emby on this server, then return to this page and reload.");
    $("emby-home").textContent = "Sign in to Emby ↗";
}

function items(query, signal) {
    return api("Users/" + encodeURIComponent(session.userId) + "/Items", query, signal);
}

function clearArtwork(urls) {
    for (const url of urls) URL.revokeObjectURL(url);
    urls.length = 0;
}

function initial(name) { return [...(name || "").trim()][0]?.toUpperCase() || "•"; }

async function art(element, item, urls, signal, heroImage) {
    element.querySelector("span").textContent = initial(item.Name);
    if (!item.ImageTags?.Primary) return;
    try {
        const url = new URL("Items/" + encodeURIComponent(item.Id) + "/Images/Primary", apiRoot);
        url.search = new URLSearchParams({ maxWidth: "420", quality: "82", tag: item.ImageTags.Primary });
        const response = await fetch(url, { headers: { "X-Emby-Token": session.token }, signal });
        if (!response.ok) return;
        const blob = await response.blob();
        if (signal?.aborted || !element.isConnected || !blob.type.startsWith("image/")) return;
        const objectUrl = URL.createObjectURL(blob);
        urls.push(objectUrl);
        const image = element.querySelector("img");
        image.src = objectUrl;
        image.hidden = false;
        if (heroImage?.isConnected) {
            heroImage.src = objectUrl;
            heroImage.hidden = false;
        }
    } catch { /* Keep the text fallback if artwork is unavailable. */ }
}

function title() {
    return state.path.at(-1)?.Name || (state.shelf === "tv" ? "TV Shows" : "Movies");
}

function card(item, signal, feature) {
    const view = $("card").content.firstElementChild.cloneNode(true);
    view.querySelector(".label").textContent = item.Name;
    view.querySelector(".sub").textContent = item.ProductionYear || "";
    if (item.Type === "Episode") view.classList.add("episode");
    view.addEventListener("click", () => {
        if (item.IsFolder) {
            changeState({ path: [...state.path, item], page: 0, query: "" }, true);
        } else {
            details(item);
        }
    });
    $("grid").append(view);
    let heroImage;
    if (feature && item.ImageTags?.Primary) {
        heroImage = document.createElement("img");
        heroImage.alt = "";
        heroImage.hidden = true;
        $("hero-art").append(heroImage);
    }
    void art(view.querySelector(".art"), item, objectUrls, signal, heroImage);
}

function controls() {
    $("intro").classList.toggle("compact", state.path.length > 0);
    for (const key of ["movies", "tv"]) $(key).setAttribute("aria-pressed", state.shelf === key);
    $("heading").textContent = state.query ? title() + " · Search" : title();
    $("location").replaceChildren();
    $("sort-control").hidden = state.path.length > 0;
    if (state.path.length) {
        const back = document.createElement("button");
        back.textContent = "← " + (state.path.at(-2)?.Name || "TV Shows");
        back.addEventListener("click", () => history.back());
        $("location").append(back);
    }
}

async function load(focus = false) {
    if (!session || !shelfIds[state.shelf]) return;
    const version = ++loadVersion;
    const pageSize = state.pageSize;
    requestController?.abort();
    requestController = new AbortController();
    const signal = requestController.signal;
    clearArtwork(objectUrls);
    $("grid").replaceChildren();
    $("show-details").replaceChildren();
    $("show-details").hidden = true;
    $("hero-art").replaceChildren();
    $("pager").hidden = true;
    controls();
    if (state.shelf === "tv" && state.path.length === 1) void showDetails(state.path[0], signal);
    status("Loading titles…");
    const [sort, order] = state.path.length ? ["IndexNumber,SortName", "Ascending"] :
        (catalogSorts[state.sort] || catalogSorts.title);
    try {
        const start = state.page * pageSize;
        const result = !state.path.length && state.sort === "popularity"
            ? await api("Vod/Popularity", {
                Series: state.shelf === "tv",
                SearchTerm: state.query || "",
                StartIndex: start,
                Limit: pageSize
            }, signal)
            : await items({
                ParentId: state.path.at(-1)?.Id || shelfIds[state.shelf],
                StartIndex: start,
                Limit: pageSize,
                SortBy: sort,
                SortOrder: order,
                ...(state.query ? { SearchTerm: state.query } : {})
            }, signal);
        if (version !== loadVersion) return;
        const total = result.TotalRecordCount || 0;
        if (state.page > 0 && !result.Items.length) { state.page = 0; return load(focus); }
        const first = state.page * pageSize + 1;
        status(total ? first + "–" + (first + result.Items.length - 1) + " of " + total.toLocaleString() +
            (result.PopularityReady === false ? " · Showing title order while popularity data is unavailable" :
                result.NoPopularity ? " · No TMDB popularity scores for these titles" : "") :
            (state.query ? "No titles match that search." : "No titles in this section yet."));
        result.Items.forEach((item, index) =>
            card(item, signal, index < 3 && !state.path.length && !state.query && state.page === 0));
        const pages = Math.ceil(total / pageSize);
        if (pages > 1) {
            $("pager").hidden = false;
            $("page").textContent = "Page " + (state.page + 1) + " of " + pages;
            $("previous").disabled = state.page === 0;
            $("next").disabled = state.page + 1 >= pages;
        }
        if (focus) $("heading").focus();
    } catch (error) {
        if (signal.aborted) return;
        if (error.status === 401 || error.status === 403) signIn();
        else status("Couldn’t load titles. Reload to try again.");
    }
}

async function showDetails(item, signal) {
    let data = item;
    try {
        data = await api("Users/" + encodeURIComponent(session.userId) + "/Items/" +
            encodeURIComponent(item.Id), {}, signal);
    } catch { /* The show card can supply its artwork as a fallback. */ }
    if (signal.aborted) return;
    const area = $("show-details");
    const cover = $("card").content.querySelector(".art").cloneNode(true);
    const text = document.createElement("div");
    const rating = document.createElement("div");
    rating.className = "sub";
    rating.textContent = metadata(data);
    const overview = document.createElement("p");
    overview.textContent = data.Overview || "Choose a season below.";
    text.append(rating, overview);
    area.replaceChildren(cover, text);
    area.hidden = false;
    void art(cover, data, objectUrls, signal);
}

function metadata(data) {
    return [data.ProductionYear, data.OfficialRating,
        data.CommunityRating ? Number(data.CommunityRating).toFixed(1) + "/10" : null].filter(Boolean).join(" · ");
}

async function details(item) {
    const version = ++detailVersion;
    const dialog = $("details");
    if (!dialog.open) dialog.showModal();
    const body = $("detail-body");
    body.textContent = "Loading details…";
    clearArtwork(detailArt);
    let data = item;
    try {
        data = await api("Users/" + encodeURIComponent(session.userId) + "/Items/" +
            encodeURIComponent(item.Id), {});
    } catch { /* The catalog card can still open in Emby. */ }
    if (version !== detailVersion || !dialog.open) return;
    const cover = $("card").content.querySelector(".art").cloneNode(true);
    const copy = document.createElement("div");
    const heading = document.createElement("h3");
    heading.textContent = data.Name;
    heading.id = "detail-title";
    dialog.setAttribute("aria-labelledby", heading.id);
    const subtitle = document.createElement("div");
    subtitle.className = "sub";
    subtitle.textContent = metadata(data);
    const overview = document.createElement("p");
    overview.textContent = data.Overview || "No synopsis available.";
    const open = document.createElement("button");
    open.type = "button";
    const url = new URL(appUrl);
    url.hash = "!/item?" + new URLSearchParams({ id: data.Id, serverId: session.serverId });
    open.className = "action";
    open.textContent = (data.UserData?.PlaybackPositionTicks > 0 ? "Resume in Emby" : "Watch in Emby") +
        (openWatchInNewTab ? " ↗" : "");
    const playbackStatus = document.createElement("p");
    playbackStatus.className = "playback-status";
    playbackStatus.setAttribute("role", "status");
    const detailsLink = document.createElement("a");
    detailsLink.href = url.href;
    detailsLink.target = openWatchInNewTab ? "_blank" : window.parent !== window ? "_top" : "_self";
    detailsLink.rel = "noopener";
    detailsLink.textContent = "Open item details in Emby";
    detailsLink.hidden = true;
    detailsLink.addEventListener("click", event => {
        if (!openWatchInNewTab && window.parent !== window && typeof window.parent.openVodItemDetails === "function") {
            event.preventDefault();
            window.parent.openVodItemDetails(data.Id);
        }
    });
    open.addEventListener("click", () => watch(data, url, open, playbackStatus, detailsLink));
    copy.append(heading, subtitle, overview, open, playbackStatus, detailsLink);
    body.replaceChildren(cover, copy);
    void art(cover, data, detailArt);
}

async function watch(item, detailUrl, button, message, detailsLink) {
    button.disabled = true;
    detailsLink.hidden = true;
    message.classList.add("busy");
    message.textContent = "Looking for a stream…";
    const newTab = openWatchInNewTab ? window.open("about:blank", "_blank") : null;
    if (newTab) {
        newTab.opener = null;
        newTab.document.title = "Looking for a stream";
        newTab.document.body.textContent = "Looking for a stream in Emby…";
    }
    const timeout = new AbortController();
    const timer = window.setTimeout(() => timeout.abort(), 15000);
    try {
        const result = await api("Items/" + encodeURIComponent(item.Id) + "/PlaybackInfo",
            { UserId: session.userId }, timeout.signal);
        window.clearTimeout(timer);
        if (!result.MediaSources?.length || result.ErrorCode) {
            message.textContent = "Emby did not find a playable stream for this title.";
            newTab?.close();
            return;
        }
        message.textContent = "Stream found. Starting Emby’s player…";
        if (!openWatchInNewTab && window.parent !== window && typeof window.parent.playFromVod === "function") {
            await window.parent.playFromVod(item.Id);
            message.textContent = "Playing in Emby.";
        } else if (newTab && !newTab.closed) {
            newTab.location.replace(detailUrl.href);
            message.textContent = "Stream found. Opened the item in Emby.";
            if (window.parent !== window && typeof window.parent.closeVodBrowser === "function") {
                window.parent.closeVodBrowser();
            }
        } else {
            message.textContent = "Stream found. Open the item in Emby to play it.";
            detailsLink.hidden = false;
            detailsLink.focus();
            if (!openWatchInNewTab) detailsLink.click();
        }
    } catch (error) {
        newTab?.close();
        message.textContent = timeout.signal.aborted ? "Emby took too long to find a stream. Try again." :
            error.status === 401 || error.status === 403 ? "Emby denied playback access for this account." :
            "Emby could not start playback. Try again or open the item in Emby.";
        detailsLink.hidden = false;
    } finally {
        window.clearTimeout(timer);
        message.classList.remove("busy");
        button.disabled = false;
    }
}

for (const shelf of ["movies", "tv"]) $(shelf).addEventListener("click", () => {
    changeState({ shelf, path: [], page: 0 }, true);
});
$("search").addEventListener("submit", event => {
    event.preventDefault();
    changeState({ query: $("query").value.trim(), path: [], page: 0 }, true);
});
$("sort").addEventListener("change", event => {
    changeState({ sort: event.target.value, page: 0 }, false, false);
});
$("page-size").addEventListener("change", event => {
    const size = Number(event.target.value);
    if (!pageSizes.includes(size)) return;
    changeState({ pageSize: size, page: 0 }, false, false);
});
$("previous").addEventListener("click", () => changeState({ page: state.page - 1 }));
$("next").addEventListener("click", () => changeState({ page: state.page + 1 }));
$("close").addEventListener("click", () => $("details").close());
$("details").addEventListener("close", () => { detailVersion++; clearArtwork(detailArt); });
void boot();
