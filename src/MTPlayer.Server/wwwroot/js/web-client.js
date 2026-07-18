(() => {
    "use strict";

    const STORAGE_KEY = "mtplayer.web.v1";
    const HOME_ROWS = [
        ["电影 Top 10", item => /电影|动作|喜剧|爱情|科幻|恐怖|剧情|纪录/.test(textOf(item)) && !/动漫|动画/.test(textOf(item))],
        ["电视剧 Top 10", item => /电视|连续|国产|欧美|日韩|港剧|台剧|短剧/.test(textOf(item)) && !/动漫|动画/.test(textOf(item))],
        ["动漫电影 Top 10", item => /动漫|动画/.test(textOf(item)) && /电影|剧场|OVA/.test(textOf(item))],
        ["动漫番剧 Top 10", item => /动漫|动画|番剧|日漫|国漫/.test(textOf(item)) && !/电影|剧场/.test(textOf(item))],
        ["综艺 Top 10", item => /综艺|真人秀|脱口秀|晚会/.test(textOf(item))],
    ];
    const pageMeta = {
        home: ["MT 精选", "热门片单"], search: ["跨接口检索", "搜索"],
        favorites: ["YOUR LIBRARY", "我的收藏"], history: ["CONTINUE WATCHING", "观看记录"],
        live: ["LIVE & M3U8", "直播频道"], account: ["ACCOUNT & SYNC", "账户与同步"],
        settings: ["DATA SOURCES", "设置"], about: ["ABOUT MT PLAYER", "关于软件"],
    };
    const defaults = {
        schemaVersion: 1,
        deviceId: crypto.randomUUID(),
        auth: { email: "", accessToken: "", refreshToken: "", expiresAtUtc: "" },
        cursor: 0,
        groups: [],
        disabledSiteKeys: [],
        favorites: [],
        history: [],
        skipMarkers: [],
        preferences: { defaultSpeed: 1, defaultVolume: 80, posterDensity: "standard" },
        preferenceStates: {},
        customLives: [],
    };
    let state = loadState();
    let currentView = "home";
    let catalogue = [];
    let itemRegistry = new Map();
    let currentDetail = null;
    let currentLineIndex = 0;
    let currentEpisodeIndex = 0;
    let hls = null;
    let idleTimer = null;
    let saveProgressTimer = 0;
    let syncTimer = null;
    let toastTimer = null;

    const $ = selector => document.querySelector(selector);
    const $$ = selector => [...document.querySelectorAll(selector)];
    const video = $("#video");

    document.addEventListener("DOMContentLoaded", initialize);

    function loadState() {
        try {
            const saved = JSON.parse(localStorage.getItem(STORAGE_KEY) || "{}");
            return {
                ...structuredClone(defaults), ...saved,
                auth: { ...defaults.auth, ...(saved.auth || {}) },
                preferences: { ...defaults.preferences, ...(saved.preferences || {}) },
                preferenceStates: saved.preferenceStates || {},
                groups: Array.isArray(saved.groups) ? saved.groups : [],
                disabledSiteKeys: Array.isArray(saved.disabledSiteKeys) ? saved.disabledSiteKeys : [],
                favorites: Array.isArray(saved.favorites) ? saved.favorites : [],
                history: Array.isArray(saved.history) ? saved.history : [],
                skipMarkers: Array.isArray(saved.skipMarkers) ? saved.skipMarkers : [],
                customLives: Array.isArray(saved.customLives) ? saved.customLives : [],
            };
        } catch { return structuredClone(defaults); }
    }

    function saveState() {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
        updateCounters();
    }

    async function initialize() {
        bindEvents();
        $("#default-speed").value = String(state.preferences.defaultSpeed);
        $("#default-volume").value = String(state.preferences.defaultVolume);
        $("#volume-output").textContent = `${state.preferences.defaultVolume}%`;
        $("#poster-density").value = state.preferences.posterDensity;
        document.body.dataset.density = state.preferences.posterDensity;
        updateAccountUi();
        renderAllLocalViews();
        renderHome();
        navigate(location.hash.slice(1) || "home", false);
        if (state.groups.some(group => group.isEnabled)) {
            const stale = state.groups.filter(group => group.isEnabled && !group.isDeleted && (!group.lastUpdatedUtc || Date.now() - Date.parse(group.lastUpdatedUtc) > 20 * 60 * 1000));
            if (stale.length) await Promise.allSettled(stale.map(group => refreshGroup(group)));
            await refreshHome(false);
        }
        if (state.auth.accessToken) scheduleSync(600);
        setInterval(refreshEnabledSourcesInBackground, 20 * 60 * 1000);
    }

    function bindEvents() {
        document.addEventListener("click", handleClick);
        $("#mobile-menu").addEventListener("click", () => $("#sidebar").classList.toggle("open"));
        $("#global-search").addEventListener("submit", event => { event.preventDefault(); search($("#search-input").value); });
        $("#source-form").addEventListener("submit", addSource);
        $("#live-form").addEventListener("submit", addLive);
        $("#live-search").addEventListener("input", renderLive);
        $("#live-group").addEventListener("change", renderLive);
        $("#login-form").addEventListener("submit", login);
        $("#default-speed").addEventListener("change", event => setPreference("defaultSpeed", Number(event.target.value)));
        $("#poster-density").addEventListener("change", event => { document.body.dataset.density = event.target.value; setPreference("posterDensity", event.target.value); });
        $("#default-volume").addEventListener("input", event => $("#volume-output").textContent = `${event.target.value}%`);
        $("#default-volume").addEventListener("change", event => setPreference("defaultVolume", Number(event.target.value)));
        $("#player-volume").addEventListener("input", event => { video.volume = Number(event.target.value) / 100; video.muted = false; });
        $("#player-speed").addEventListener("change", event => video.playbackRate = Number(event.target.value));
        $("#seek").addEventListener("input", event => { if (Number.isFinite(video.duration)) video.currentTime = video.duration * Number(event.target.value) / 1000; });
        video.addEventListener("loadedmetadata", restorePlayback);
        video.addEventListener("timeupdate", onTimeUpdate);
        video.addEventListener("play", updatePlayButton);
        video.addEventListener("pause", updatePlayButton);
        video.addEventListener("waiting", () => $("#player-loading").classList.remove("hidden"));
        video.addEventListener("playing", () => $("#player-loading").classList.add("hidden"));
        video.addEventListener("ended", () => changeEpisode(1));
        video.addEventListener("error", () => { $("#player-loading").classList.add("hidden"); toast("当前线路播放失败，请切换线路或剧集。", true); });
        ["mousemove", "mousedown", "keydown", "touchstart"].forEach(name => $("#player-stage").addEventListener(name, showPlayerChrome, { passive: true }));
        window.addEventListener("hashchange", () => navigate(location.hash.slice(1) || "home", false));
        window.addEventListener("beforeunload", () => savePlaybackProgress(true));
    }

    async function handleClick(event) {
        const viewButton = event.target.closest("[data-view]");
        if (viewButton) { navigate(viewButton.dataset.view); return; }
        const action = event.target.closest("[data-action]")?.dataset.action;
        if (action) {
            const target = event.target.closest("[data-action]");
            const handlers = {
                "refresh-home": () => refreshHome(true), "refresh-sources": refreshAllSources,
                "clear-history": clearHistory, register, logout,
                "sync-all": syncAll, "sync-upload": syncUpload, "sync-download": () => syncDownload(true),
                "close-detail": closeDetail, "close-player": closePlayer, "toggle-play": togglePlay,
                "prev-episode": () => changeEpisode(-1), "next-episode": () => changeEpisode(1),
                "back-10": () => video.currentTime = Math.max(0, video.currentTime - 10),
                "forward-10": () => video.currentTime = Math.min(video.duration || Infinity, video.currentTime + 10),
                "set-intro": setIntro, "set-outro": setOutro, "clear-skip": clearSkip,
                mute: () => { video.muted = !video.muted; toast(video.muted ? "已静音" : "已恢复声音"); },
                fullscreen: toggleFullscreen,
                "toggle-favorite": toggleFavorite,
                "play-selected": () => playEpisode(currentLineIndex, currentEpisodeIndex),
                "remove-live": () => removeLive(target.dataset.id),
                "play-live": () => playLive(target.dataset.url, target.dataset.name),
                "toggle-group": () => toggleGroup(target.dataset.id),
                "refresh-group": () => refreshGroupById(target.dataset.id, true),
                "delete-group": () => deleteGroup(target.dataset.id),
            };
            if (handlers[action]) await handlers[action]();
            return;
        }
        const card = event.target.closest("[data-item]");
        if (card) { await openDetail(itemRegistry.get(card.dataset.item)); return; }
        const sourceTab = event.target.closest("[data-line-index]");
        if (sourceTab) { selectLine(Number(sourceTab.dataset.lineIndex)); return; }
        const episode = event.target.closest("[data-episode-index]");
        if (episode) await playEpisode(currentLineIndex, Number(episode.dataset.episodeIndex));
    }

    function navigate(view, updateHash = true) {
        if (!pageMeta[view]) view = "home";
        currentView = view;
        $$(".view").forEach(element => element.classList.toggle("active", element.id === `view-${view}`));
        $$(".nav-item").forEach(element => element.classList.toggle("active", element.dataset.view === view));
        $("#page-kicker").textContent = pageMeta[view][0];
        $("#page-title").textContent = pageMeta[view][1];
        $("#sidebar").classList.remove("open");
        if (updateHash && location.hash !== `#${view}`) history.pushState(null, "", `#${view}`);
        if (view === "favorites") renderFavorites();
        if (view === "history") renderHistory();
        if (view === "live") renderLive();
        if (view === "settings") renderSettings();
        window.scrollTo({ top: 0, behavior: "smooth" });
    }

    async function api(path, options = {}, retry = true) {
        const headers = { ...(options.body ? { "Content-Type": "application/json" } : {}), ...(options.headers || {}) };
        if (state.auth.accessToken) headers.Authorization = `Bearer ${state.auth.accessToken}`;
        const response = await fetch(path, { ...options, headers });
        if (response.status === 401 && retry && state.auth.refreshToken && !path.endsWith("/refresh")) {
            if (await refreshToken()) return api(path, options, false);
        }
        if (!response.ok) {
            const problem = await response.json().catch(() => ({}));
            throw new Error(problem.detail || problem.title || problem.message || `请求失败 (${response.status})`);
        }
        if (response.status === 204) return null;
        return response.json();
    }

    async function refreshToken() {
        try {
            const response = await fetch("/api/v1/auth/refresh", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ refreshToken: state.auth.refreshToken }) });
            if (!response.ok) throw new Error();
            setTokens(await response.json(), state.auth.email);
            return true;
        } catch { logout(false); return false; }
    }

    async function addSource(event) {
        event.preventDefault();
        const name = $("#source-name").value.trim();
        const address = $("#source-url").value.trim();
        if (!/^https?:\/\//i.test(address)) { toast("配置地址必须以 http:// 或 https:// 开头。", true); return; }
        if (state.groups.some(group => group.address === address && !group.isDeleted)) { toast("该配置源已经存在。", true); return; }
        busy(true, "正在读取配置源…");
        try {
            const id = await stableId("configuration", address);
            const group = { id, name, address, isEnabled: true, sites: [], lives: [], modifiedAtUtc: now(), version: 0, isDeleted: false, dirty: true };
            const result = await api("/api/v1/web/config/inspect", { method: "POST", body: JSON.stringify({ groupId: id, url: address }) });
            group.sites = result.sites || [];
            group.lives = result.lives || [];
            group.warnings = result.warnings || [];
            state.groups.push(group);
            saveState(); renderSettings();
            event.target.reset();
            toast(`已添加 ${group.sites.length} 个影视接口、${group.lives.length} 个直播频道。${group.warnings.length ? ` ${group.warnings[0]}` : ""}`, group.warnings.length > 0 && group.lives.length === 0);
            await refreshHome(false);
            scheduleSync();
        } catch (error) { toast(error.message, true); }
        finally { busy(false); }
    }

    async function refreshGroup(group, notify = false) {
        const result = await api("/api/v1/web/config/inspect", { method: "POST", body: JSON.stringify({ groupId: group.id, url: group.address }) });
        group.sites = result.sites || [];
        group.lives = result.lives || [];
        group.warnings = result.warnings || [];
        group.lastUpdatedUtc = now();
        touch(group);
        saveState();
        scheduleSync();
        if (notify) toast(`${group.name} 已更新：${group.sites.length} 个影视接口、${group.lives.length} 个直播频道。${group.warnings.length ? ` ${group.warnings[0]}` : ""}`, group.warnings.length > 0 && group.lives.length === 0);
    }

    async function refreshGroupById(id, notify) {
        const group = state.groups.find(value => value.id === id);
        if (!group) return;
        busy(true, "正在更新配置源…");
        try { await refreshGroup(group, notify); renderSettings(); await refreshHome(false); }
        catch (error) { toast(error.message, true); }
        finally { busy(false); }
    }

    async function refreshAllSources() {
        const groups = state.groups.filter(group => !group.isDeleted);
        if (!groups.length) { toast("请先添加配置源。", true); return; }
        busy(true, "正在更新全部配置源…");
        const results = await Promise.allSettled(groups.map(group => refreshGroup(group)));
        busy(false); renderSettings(); await refreshHome(false);
        const failed = results.filter(result => result.status === "rejected").length;
        toast(failed ? `${groups.length - failed} 个更新成功，${failed} 个失败。` : "全部配置源已更新。", failed > 0);
    }

    async function refreshEnabledSourcesInBackground() {
        const groups = state.groups.filter(group => group.isEnabled && !group.isDeleted);
        if (!groups.length) return;
        await Promise.allSettled(groups.map(group => refreshGroup(group)));
        if (currentView === "settings") renderSettings();
        await refreshHome(false);
    }

    function toggleGroup(id) {
        const group = state.groups.find(value => value.id === id);
        if (!group) return;
        group.isEnabled = !group.isEnabled; touch(group);
        saveState(); renderSettings(); refreshHome(false); scheduleSync();
    }

    function deleteGroup(id) {
        const group = state.groups.find(value => value.id === id);
        if (!group || !confirm(`确定删除配置源“${group.name}”吗？`)) return;
        const removedSiteKeys = new Set((group.sites || []).map(site => site.key));
        state.disabledSiteKeys = state.disabledSiteKeys.filter(key => !removedSiteKeys.has(key));
        if (group.version > 0 || state.auth.accessToken) { group.isDeleted = true; group.sites = []; group.lives = []; touch(group); }
        else state.groups = state.groups.filter(value => value.id !== id);
        catalogue = [];
        saveState(); renderSettings(); renderLive(); refreshHome(false); scheduleSync();
    }

    function enabledSites() {
        const disabled = new Set(state.disabledSiteKeys);
        return state.groups.filter(group => group.isEnabled && !group.isDeleted)
            .flatMap(group => group.sites || []).filter(site => !disabled.has(site.key));
    }

    async function refreshHome(notify) {
        const sites = enabledSites();
        if (!sites.length) { catalogue = []; renderHome(); if (notify) toast("没有已启用的播放接口。", true); return; }
        busy(true, "正在整理热门片单…");
        try {
            catalogue = await api("/api/v1/web/catalogue/latest", { method: "POST", body: JSON.stringify({ sites, limit: 120 }) });
            renderHome();
            if (notify) toast(`首页已更新，共 ${catalogue.length} 部内容。`);
        } catch (error) { renderHome(); toast(error.message, true); }
        finally { busy(false); }
    }

    function renderHome() {
        const root = $("#home-sections");
        if (!enabledSites().length) { root.innerHTML = empty("尚未添加配置源，请到“设置”中添加 TVBox 配置地址。"); return; }
        if (!catalogue.length) { root.innerHTML = empty("已启用的接口暂时没有返回首页内容，可使用顶部搜索查找影片。"); return; }
        const used = new Set();
        root.innerHTML = HOME_ROWS.map(([title, predicate]) => {
            const primary = catalogue.filter(predicate);
            const fill = [...primary, ...catalogue.filter(item => !primary.includes(item))]
                .filter(item => { const key = contentKey(item); if (used.has(`${title}:${key}`)) return false; used.add(`${title}:${key}`); return true; }).slice(0, 10);
            return `<section class="home-row"><h2>${title}</h2><div class="poster-row">${fill.map(posterCard).join("")}</div></section>`;
        }).join("");
    }

    async function search(keyword) {
        keyword = keyword.trim();
        if (!keyword) { toast("请输入搜索关键词。", true); return; }
        const sites = enabledSites();
        if (!sites.length) { toast("请先在设置中添加并启用配置源。", true); navigate("settings"); return; }
        navigate("search"); busy(true, `正在搜索“${keyword}”…`);
        try {
            const results = await api("/api/v1/web/catalogue/search", { method: "POST", body: JSON.stringify({ sites, keyword, limit: 300 }) });
            $("#search-summary").textContent = `“${keyword}”共找到 ${results.length} 个结果，来自所有已启用接口。`;
            $("#search-grid").innerHTML = results.length ? results.map(posterCard).join("") : empty("没有找到相关影片，请尝试其他关键词。");
        } catch (error) { $("#search-grid").innerHTML = empty(error.message); toast(error.message, true); }
        finally { busy(false); }
    }

    async function openDetail(item) {
        if (!item) return;
        const site = enabledSites().find(value => value.key === item.sourceKey) || allSites().find(value => value.key === item.sourceKey);
        if (!site) { toast("原播放接口已停用或被删除。", true); return; }
        busy(true, "正在读取影片详情…");
        try {
            currentDetail = await api("/api/v1/web/catalogue/detail", { method: "POST", body: JSON.stringify({ site, id: item.id || item.contentId }) });
            currentLineIndex = Math.max(0, Math.min(Number(historyFor(currentDetail.item)?.sourceIndex || 0), currentDetail.sources.length - 1));
            currentEpisodeIndex = Math.max(0, Math.min(Number(historyFor(currentDetail.item)?.episodeIndex || 0), (currentDetail.sources[currentLineIndex]?.episodes.length || 1) - 1));
            renderDetail(); $("#detail-dialog").showModal();
        } catch (error) { toast(error.message, true); }
        finally { busy(false); }
    }

    function renderDetail() {
        const detail = currentDetail;
        const item = detail.item;
        const meta = detail.metadata || {};
        const favorite = favoriteFor(item);
        const line = detail.sources[currentLineIndex];
        $("#detail-content").innerHTML = `<article class="detail-hero">
            <img class="detail-poster" src="${attr(item.coverUrl)}" alt="${attr(item.title)}" onerror="this.style.opacity=.2" />
            <div class="detail-copy"><span>${escapeHtml(item.typeName || "影视")}</span><h2>${escapeHtml(item.title)}</h2>
            <p class="detail-meta">${[meta.year, meta.area, meta.language, meta.score && `评分 ${meta.score}`, item.remarks].filter(Boolean).map(escapeHtml).join(" · ")}</p>
            ${meta.director ? `<p>导演：${escapeHtml(meta.director)}</p>` : ""}${meta.actors ? `<p>主演：${escapeHtml(meta.actors)}</p>` : ""}
            <p>${escapeHtml(detail.description || "暂无影片介绍。")}</p>
            <div class="detail-actions"><button class="primary" data-action="play-selected">▶ 播放所选集</button><button data-action="toggle-favorite">${favorite ? "♥ 已收藏" : "♡ 加入收藏"}</button></div>
            <h3>播放接口</h3><div class="source-tabs">${detail.sources.map((source, index) => `<button class="${index === currentLineIndex ? "active" : ""}" data-line-index="${index}">${escapeHtml(source.name || `线路 ${index + 1}`)} · ${source.episodes.length} 集</button>`).join("") || "暂无可播放线路"}</div>
            <h3>选择剧集</h3><div class="episode-panel"><div class="episode-grid">${line ? line.episodes.map((episode, index) => `<button class="${index === currentEpisodeIndex ? "active" : ""}" data-episode-index="${index}">${escapeHtml(episode.name || `第 ${index + 1} 集`)}</button>`).join("") : ""}</div></div>
            </div></article>`;
    }

    function selectLine(index) { currentLineIndex = index; currentEpisodeIndex = 0; renderDetail(); }
    function closeDetail() { $("#detail-dialog").close(); }

    async function playEpisode(lineIndex, episodeIndex) {
        const line = currentDetail?.sources[lineIndex];
        const episode = line?.episodes[episodeIndex];
        if (!episode) { toast("该剧集没有有效播放地址。", true); return; }
        currentLineIndex = lineIndex; currentEpisodeIndex = episodeIndex;
        closeDetail();
        await startVideo(episode.url, episode.isHls, currentDetail.item.title, `${line.name} · ${episode.name}`);
    }

    async function startVideo(url, isHls, title, subtitle) {
        destroyHls();
        $("#player-layer").classList.remove("hidden");
        $("#player-title").textContent = title;
        $("#player-subtitle").textContent = subtitle;
        $("#player-loading").classList.remove("hidden");
        video.volume = state.preferences.defaultVolume / 100;
        video.playbackRate = Number(state.preferences.defaultSpeed);
        $("#player-volume").value = String(state.preferences.defaultVolume);
        $("#player-speed").value = String(state.preferences.defaultSpeed);
        if (isHls && window.Hls?.isSupported()) {
            hls = new Hls({ enableWorker: true, lowLatencyMode: true, backBufferLength: 60 });
            hls.loadSource(url); hls.attachMedia(video);
            hls.on(Hls.Events.MANIFEST_PARSED, () => video.play().catch(() => {}));
            hls.on(Hls.Events.ERROR, (_, data) => { if (data.fatal) toast("视频流连接失败，请尝试其他线路。", true); });
        } else {
            video.src = url;
            await video.play().catch(() => {});
        }
        showPlayerChrome();
    }

    async function playLive(rawUrl, name) {
        busy(true, "正在连接直播流…");
        try {
            const result = await api("/api/v1/web/media/sign", { method: "POST", body: JSON.stringify({ url: rawUrl }) });
            currentDetail = null;
            await startVideo(result.url, /\.m3u8(?:$|\?)/i.test(rawUrl), name || "直播频道", "直播");
        } catch (error) { toast(error.message, true); }
        finally { busy(false); }
    }

    function restorePlayback() {
        if (!currentDetail) return;
        const history = historyFor(currentDetail.item);
        if (history && history.episodeIndex === currentEpisodeIndex && history.sourceIndex === currentLineIndex && history.positionMs > 0 && history.positionMs < (video.duration * 1000 - 15000)) video.currentTime = history.positionMs / 1000;
    }

    function onTimeUpdate() {
        $("#current-time").textContent = formatTime(video.currentTime);
        $("#duration").textContent = formatTime(video.duration);
        $("#seek").value = Number.isFinite(video.duration) && video.duration > 0 ? String(Math.round(video.currentTime / video.duration * 1000)) : "0";
        if (!currentDetail) return;
        const marker = skipForCurrent();
        if (marker?.introEndSeconds > 0 && video.currentTime < marker.introEndSeconds && video.currentTime > 1) video.currentTime = marker.introEndSeconds;
        if (marker?.outroRemainingSeconds > 0 && Number.isFinite(video.duration) && video.currentTime >= video.duration - marker.outroRemainingSeconds) changeEpisode(1);
        const nowMs = Date.now();
        if (nowMs - saveProgressTimer > 5000) { saveProgressTimer = nowMs; savePlaybackProgress(); }
    }

    async function savePlaybackProgress(force = false) {
        if (!currentDetail || !Number.isFinite(video.currentTime)) return;
        const item = currentDetail.item;
        let entry = historyFor(item);
        if (!entry) {
            entry = { id: await stableId("playback", item.sourceKey, item.id), sourceKey: item.sourceKey, contentId: item.id, version: 0 };
            state.history.unshift(entry);
        }
        Object.assign(entry, { interfaceKey: item.sourceKey, lineName: currentDetail.sources[currentLineIndex]?.name || "", sourceIndex: currentLineIndex, episodeIndex: currentEpisodeIndex, positionMs: Math.round(video.currentTime * 1000), durationMs: Math.round((video.duration || 0) * 1000), category: item.typeName, title: item.title, caption: currentDetail.sources[currentLineIndex]?.episodes[currentEpisodeIndex]?.name || item.remarks, coverUrl: item.originalCoverUrl || item.coverUrl, displayCoverUrl: item.coverUrl, isDeleted: false });
        if (force || !entry.lastDirtyAt || Date.now() - entry.lastDirtyAt > 12000) { touch(entry); entry.lastDirtyAt = Date.now(); scheduleSync(2500); }
        state.history.sort((a, b) => String(b.modifiedAtUtc).localeCompare(String(a.modifiedAtUtc)));
        saveState();
    }

    function closePlayer() { savePlaybackProgress(true); video.pause(); video.removeAttribute("src"); video.load(); destroyHls(); $("#player-layer").classList.add("hidden"); }
    function destroyHls() { if (hls) { hls.destroy(); hls = null; } }
    function togglePlay() { video.paused ? video.play() : video.pause(); }
    function updatePlayButton() { const button = $("[data-action='toggle-play']"); if (button) button.textContent = video.paused ? "播放" : "暂停"; }
    function changeEpisode(delta) {
        if (!currentDetail) return;
        const episodes = currentDetail.sources[currentLineIndex]?.episodes || [];
        const next = currentEpisodeIndex + delta;
        if (next < 0 || next >= episodes.length) { toast(delta > 0 ? "已经是最后一集。" : "已经是第一集。"); return; }
        savePlaybackProgress(true); playEpisode(currentLineIndex, next);
    }
    function showPlayerChrome() { if ($("#player-layer").classList.contains("hidden")) return; $("#player-stage").classList.remove("idle"); clearTimeout(idleTimer); idleTimer = setTimeout(() => { if (!video.paused) $("#player-stage").classList.add("idle"); }, 5000); }
    function toggleFullscreen() { document.fullscreenElement ? document.exitFullscreen() : $("#player-stage").requestFullscreen(); }

    async function setIntro() { const marker = await ensureSkip(); marker.introEndSeconds = Math.round(video.currentTime); touch(marker); saveState(); scheduleSync(); toast(`已将片头设为 ${formatTime(marker.introEndSeconds)}。`); }
    async function setOutro() { if (!Number.isFinite(video.duration)) return; const marker = await ensureSkip(); marker.outroRemainingSeconds = Math.max(0, Math.round(video.duration - video.currentTime)); touch(marker); saveState(); scheduleSync(); toast(`已设置片尾剩余 ${marker.outroRemainingSeconds} 秒。`); }
    async function clearSkip() { const marker = skipForCurrent(); if (!marker) return; marker.introEndSeconds = 0; marker.outroRemainingSeconds = 0; touch(marker); saveState(); scheduleSync(); toast("已清除该影片当前线路的片头片尾设置。"); }
    async function ensureSkip() {
        let marker = skipForCurrent();
        if (marker) return marker;
        const item = currentDetail.item, lineName = currentDetail.sources[currentLineIndex]?.name || "";
        marker = { id: await stableId("skip", item.sourceKey, item.id, item.sourceKey, lineName), sourceKey: item.sourceKey, contentId: item.id, interfaceKey: item.sourceKey, lineName, introEndSeconds: 0, outroRemainingSeconds: 0, version: 0, modifiedAtUtc: now(), isDeleted: false, dirty: true };
        state.skipMarkers.push(marker); return marker;
    }
    function skipForCurrent() { if (!currentDetail) return null; const item = currentDetail.item, line = currentDetail.sources[currentLineIndex]?.name || ""; return state.skipMarkers.find(value => !value.isDeleted && value.sourceKey === item.sourceKey && value.contentId === item.id && value.lineName === line); }

    async function toggleFavorite() {
        if (!currentDetail) return;
        const item = currentDetail.item;
        let favorite = favoriteFor(item);
        if (favorite) { favorite.isDeleted = true; touch(favorite); }
        else {
            favorite = { id: await stableId("favorite", item.sourceKey, item.id), sourceKey: item.sourceKey, contentId: item.id, category: item.typeName, title: item.title, caption: item.remarks, coverUrl: item.originalCoverUrl || item.coverUrl, displayCoverUrl: item.coverUrl, version: 0, modifiedAtUtc: now(), isDeleted: false, dirty: true };
            state.favorites.push(favorite);
        }
        saveState(); renderDetail(); renderFavorites(); scheduleSync(); toast(favorite.isDeleted ? "已取消收藏。" : "已加入收藏。");
    }

    function renderFavorites() {
        const items = state.favorites.filter(item => !item.isDeleted).sort(sortModified).map(entityToItem);
        $("#favorites-grid").innerHTML = items.length ? items.map(posterCard).join("") : empty("还没有收藏影片。打开影片详情后即可加入收藏。");
    }
    function renderHistory() {
        const entries = state.history.filter(item => !item.isDeleted).sort(sortModified);
        $("#history-grid").innerHTML = entries.length ? entries.map(entry => posterCard(entityToItem(entry), entry.durationMs ? entry.positionMs / entry.durationMs * 100 : 0)).join("") : empty("还没有观看记录，播放影片后会自动保存在这里。");
    }
    function clearHistory() {
        if (!state.history.some(item => !item.isDeleted) || !confirm("确定清空全部观看记录吗？")) return;
        state.history.forEach(item => { if (item.version > 0) { item.isDeleted = true; touch(item); } });
        state.history = state.history.filter(item => item.version > 0);
        saveState(); renderHistory(); scheduleSync();
    }

    async function addLive(event) {
        event.preventDefault(); const name = $("#live-name").value.trim() || "自定义频道"; const address = $("#live-url").value.trim();
        if (!/^https?:\/\//i.test(address)) { toast("直播地址必须以 http:// 或 https:// 开头。", true); return; }
        busy(true, "正在读取直播源…");
        try {
            const result = await api("/api/v1/web/live/inspect", { method: "POST", body: JSON.stringify({ name, url: address }) });
            if (!result.lives?.length) throw new Error(result.warnings?.[0] || "该地址没有识别到可播放的直播频道。");
            for (const channel of result.lives) state.customLives.push({ ...channel, id: crypto.randomUUID() });
            await setPreference("customLives", state.customLives);
            renderLive(); event.target.reset();
            toast(`已添加 ${result.lives.length} 个直播频道。${result.warnings?.length ? ` ${result.warnings[0]}` : ""}`);
        } catch (error) { toast(error.message, true); }
        finally { busy(false); }
    }
    async function removeLive(id) { state.customLives = state.customLives.filter(item => item.id !== id); await setPreference("customLives", state.customLives); renderLive(); }
    function renderLive() {
        const configured = state.groups.filter(group => group.isEnabled && !group.isDeleted).flatMap(group => (group.lives || []).map(item => ({ ...item, id: "", origin: group.name })));
        const custom = state.customLives.map(item => ({ ...item, group: item.group || "自定义", origin: "自定义" }));
        const lives = [...custom, ...configured];
        const groupSelect = $("#live-group");
        const selectedGroup = groupSelect.value;
        const groups = [...new Set(lives.map(item => item.group || "直播"))].sort((a, b) => a.localeCompare(b, "zh-CN"));
        groupSelect.innerHTML = `<option value="">全部分组</option>${groups.map(group => `<option value="${attr(group)}">${escapeHtml(group)}</option>`).join("")}`;
        groupSelect.value = groups.includes(selectedGroup) ? selectedGroup : "";
        const keyword = $("#live-search").value.trim().toLocaleLowerCase("zh-CN");
        const filtered = lives.filter(item => (!groupSelect.value || (item.group || "直播") === groupSelect.value) &&
            (!keyword || `${item.name} ${item.group || ""} ${item.origin || ""}`.toLocaleLowerCase("zh-CN").includes(keyword)));
        $("#live-count").textContent = `${filtered.length} / ${lives.length} 个频道`;
        $("#live-grid").innerHTML = filtered.length ? filtered.map(item => `<article class="live-card"><div class="live-card-main">${item.logoAddress ? `<img src="${attr(item.logoAddress)}" alt="" loading="lazy" />` : ""}<div><strong>${escapeHtml(item.name)}</strong><small>${escapeHtml(item.group || "直播")} · ${escapeHtml(item.origin)}</small></div></div><div class="live-actions"><button class="primary" data-action="play-live" data-url="${attr(item.address)}" data-name="${attr(item.name)}">播放</button>${item.id ? `<button data-action="remove-live" data-id="${item.id}">删除</button>` : ""}</div></article>`).join("") : empty(lives.length ? "没有符合当前搜索或分组条件的频道。" : "暂无直播频道，请刷新配置源或在上方添加 M3U8 地址。");
    }

    function renderSettings() {
        const groups = state.groups.filter(group => !group.isDeleted);
        $("#source-list").innerHTML = groups.length ? groups.map(group => `<div class="stack-item"><div><strong>${escapeHtml(group.name)}</strong><small>${escapeHtml(group.address)} · ${group.sites?.length || 0} 个影视接口 · ${group.lives?.length || 0} 个直播频道${group.lastUpdatedUtc ? ` · ${new Date(group.lastUpdatedUtc).toLocaleString()}` : ""}</small>${group.warnings?.length ? `<small class="source-warning">${escapeHtml(group.warnings[0])}</small>` : ""}</div><div class="item-actions"><button data-action="toggle-group" data-id="${group.id}">${group.isEnabled ? "停用" : "启用"}</button><button data-action="refresh-group" data-id="${group.id}">更新</button><button class="danger" data-action="delete-group" data-id="${group.id}">删除</button></div></div>`).join("") : empty("尚未添加配置源。");
        const sites = allSites(); const disabled = new Set(state.disabledSiteKeys);
        $("#interface-summary").textContent = `${sites.length - state.disabledSiteKeys.filter(key => sites.some(site => site.key === key)).length} / ${sites.length} 个接口已启用`;
        $("#interface-list").innerHTML = sites.length ? sites.map(site => `<label title="${attr(site.api)}"><input type="checkbox" data-site-key="${attr(site.key)}" ${disabled.has(site.key) ? "" : "checked"} /><span>${escapeHtml(site.name)}</span></label>`).join("") : empty("添加配置源后会在这里显示播放接口。");
        $$("[data-site-key]").forEach(input => input.addEventListener("change", event => toggleSite(event.target.dataset.siteKey, event.target.checked)));
        updateCounters();
    }
    function toggleSite(key, enabled) { const values = new Set(state.disabledSiteKeys); enabled ? values.delete(key) : values.add(key); state.disabledSiteKeys = [...values]; saveState(); setPreference("disabledSiteKeys", state.disabledSiteKeys); renderSettings(); refreshHome(false); }
    function allSites() { return state.groups.filter(group => !group.isDeleted).flatMap(group => group.sites || []).filter((site, index, array) => array.findIndex(value => value.key === site.key) === index); }

    async function setPreference(key, value) {
        state.preferences[key] = value;
        const existing = state.preferenceStates[key] || { id: await stableId("preference", key), version: 0 };
        Object.assign(existing, { key, value: JSON.stringify(value), modifiedAtUtc: now(), isDeleted: false, dirty: true });
        state.preferenceStates[key] = existing; saveState(); scheduleSync();
    }

    async function login(event) {
        event.preventDefault(); busy(true, "正在登录…");
        const email = $("#account-email").value.trim(), password = $("#account-password").value;
        try {
            const tokens = await api("/api/v1/auth/login", { method: "POST", body: JSON.stringify({ email, password, deviceName: "MT播放器网页端", platform: "web" }) });
            setTokens(tokens, email); $("#account-password").value = ""; updateAccountUi(); toast("登录成功，正在双向同步…"); await syncAll();
        } catch (error) { toast(error.message, true); }
        finally { busy(false); }
    }
    async function register() {
        const email = $("#account-email").value.trim(), password = $("#account-password").value;
        if (!email || password.length < 10) { toast("请输入邮箱和至少 10 位密码。", true); return; }
        busy(true, "正在注册…");
        try { const result = await api("/api/v1/auth/register", { method: "POST", body: JSON.stringify({ email, password }) }); toast(result.message || "注册申请已提交，请检查验证邮件。"); }
        catch (error) { toast(error.message, true); }
        finally { busy(false); }
    }
    function setTokens(tokens, email) {
        state.auth = { email, accessToken: tokens.accessToken, refreshToken: tokens.refreshToken, expiresAtUtc: tokens.expiresAtUtc };
        const sessionId = jwtClaim(tokens.accessToken, "sid");
        if (sessionId) state.deviceId = sessionId;
        saveState();
    }
    function logout(notify = true) { state.auth = { ...defaults.auth }; state.cursor = 0; saveState(); updateAccountUi(); if (notify) toast("已退出登录，本地数据仍然保留。 "); }
    function updateAccountUi() {
        const signed = Boolean(state.auth.accessToken);
        $("#account-chip").classList.toggle("signed-in", signed);
        $("#account-chip span").textContent = signed ? state.auth.email : "游客模式";
        $("#login-state").textContent = signed ? `已登录：${state.auth.email}` : "未登录";
        $("#account-email").value = state.auth.email || "";
        $("#account-message").textContent = signed ? "已连接云端。配置源、收藏、记录、片头片尾和播放偏好可双向同步。" : "游客模式可正常使用本地播放与记录；登录后才会上传和下载同步数据。";
    }

    async function syncAll() { if (!requireLogin()) return; busy(true, "正在双向同步…"); try { await syncUpload(); await syncDownload(false); toast("双向同步完成。 "); } catch (error) { toast(error.message, true); } finally { busy(false); } }
    async function syncUpload() {
        if (!requireLogin()) return;
        const entities = dirtyEntities();
        if (!entities.length) { $("#sync-state").textContent = "本地数据已上传"; return; }
        for (let offset = 0; offset < entities.length; offset += 100) {
            const chunk = entities.slice(offset, offset + 100);
            const result = await api("/api/v1/sync/push", { method: "POST", body: JSON.stringify({ deviceId: state.deviceId, mutations: chunk.map(toMutation) }) });
            result.forEach(item => {
                const entity = findEntity(item.id);
                if (!entity) return;
                if (item.accepted) { entity.version = item.version; entity.modifiedAtUtc = item.serverModifiedAtUtc; entity.dirty = false; }
                else if (item.current) applyRemote(item.current);
            });
        }
        saveState(); $("#sync-state").textContent = `上传完成 · ${new Date().toLocaleTimeString()}`;
    }
    async function syncDownload(full = false) {
        if (!requireLogin()) return;
        let cursor = full ? 0 : Number(state.cursor || 0), pages = 0;
        do {
            const response = await api(`/api/v1/sync/pull?cursor=${cursor}&limit=500`);
            response.changes.forEach(applyRemote);
            const next = Number(response.cursor || cursor);
            pages++;
            if (response.changes.length < 500 || next === cursor) { cursor = next; break; }
            cursor = next;
        } while (pages < 20);
        state.cursor = cursor; saveState(); renderAllLocalViews(); renderSettings(); updateAccountUi();
        await refreshRemoteGroups();
        $("#sync-state").textContent = `下载完成 · ${new Date().toLocaleTimeString()}`;
    }
    async function refreshRemoteGroups() {
        const stale = state.groups.filter(group => !group.isDeleted && (!group.sites || !group.sites.length));
        if (!stale.length) return;
        await Promise.allSettled(stale.map(group => refreshGroup(group)));
        renderSettings(); await refreshHome(false);
    }
    function scheduleSync(delay = 1600) {
        if (!state.auth.accessToken) return;
        clearTimeout(syncTimer);
        syncTimer = setTimeout(async () => {
            try { await syncUpload(); await syncDownload(false); }
            catch (error) { $("#sync-state").textContent = "自动同步失败"; }
        }, delay);
    }
    function requireLogin() { if (state.auth.accessToken) return true; toast("请先登录账户再使用云同步。", true); navigate("account"); return false; }

    function dirtyEntities() { return [...state.groups, ...state.favorites, ...state.history, ...state.skipMarkers, ...Object.values(state.preferenceStates)].filter(item => item.dirty); }
    function toMutation(entity) {
        let kind, payload;
        if ("address" in entity) { kind = "ConfigurationGroup"; payload = { name: entity.name, address: entity.address, isEnabled: entity.isEnabled, sites: entity.sites || [], lives: entity.lives || [] }; }
        else if ("introEndSeconds" in entity) { kind = "SkipMarker"; payload = pick(entity, ["sourceKey", "contentId", "interfaceKey", "lineName", "introEndSeconds", "outroRemainingSeconds"]); }
        else if ("positionMs" in entity) { kind = "PlaybackHistory"; payload = pick(entity, ["sourceKey", "contentId", "interfaceKey", "lineName", "episodeIndex", "positionMs", "durationMs", "sourceIndex", "category", "title", "caption", "coverUrl"]); }
        else if ("contentId" in entity) { kind = "Favorite"; payload = pick(entity, ["sourceKey", "contentId", "category", "title", "caption", "coverUrl"]); }
        else { kind = "Preference"; payload = { key: entity.key, value: entity.value }; }
        return { id: entity.id, kind, baseVersion: Number(entity.version || 0), modifiedAtUtc: entity.modifiedAtUtc || now(), isDeleted: Boolean(entity.isDeleted), payload };
    }
    function applyRemote(mutation) {
        const payload = mutation.payload || {}, base = { id: mutation.id, version: mutation.baseVersion || mutation.version || 0, modifiedAtUtc: mutation.modifiedAtUtc, isDeleted: mutation.isDeleted, dirty: false };
        let collection, entity;
        if (mutation.kind === "ConfigurationGroup") { collection = state.groups; entity = { ...base, ...payload, sites: Array.isArray(payload.sites) ? payload.sites : [], lives: Array.isArray(payload.lives) ? payload.lives : [] }; }
        else if (mutation.kind === "Favorite") { collection = state.favorites; entity = { ...base, ...payload }; }
        else if (mutation.kind === "PlaybackHistory") { collection = state.history; entity = { ...base, ...payload }; }
        else if (mutation.kind === "SkipMarker") { collection = state.skipMarkers; entity = { ...base, ...payload }; }
        else if (mutation.kind === "Preference") {
            entity = { ...base, ...payload }; state.preferenceStates[payload.key] = entity;
            if (!mutation.isDeleted) {
                try { state.preferences[payload.key] = JSON.parse(payload.value); } catch { state.preferences[payload.key] = payload.value; }
                if (payload.key === "disabledSiteKeys" && Array.isArray(state.preferences[payload.key])) state.disabledSiteKeys = state.preferences[payload.key];
                if (payload.key === "customLives" && Array.isArray(state.preferences[payload.key])) state.customLives = state.preferences[payload.key];
            }
            return;
        } else return;
        const index = collection.findIndex(item => item.id === mutation.id);
        if (index >= 0) {
            if (collection[index].dirty && Date.parse(collection[index].modifiedAtUtc) > Date.parse(mutation.modifiedAtUtc)) return;
            entity = { ...collection[index], ...entity };
            if (!Array.isArray(payload.sites)) entity.sites = collection[index].sites || [];
            if (!Array.isArray(payload.lives)) entity.lives = collection[index].lives || [];
            collection[index] = entity;
        } else collection.push(entity);
    }
    function findEntity(id) { return [...state.groups, ...state.favorites, ...state.history, ...state.skipMarkers, ...Object.values(state.preferenceStates)].find(item => item.id === id); }

    function renderAllLocalViews() { renderFavorites(); renderHistory(); renderLive(); renderSettings(); updateCounters(); }
    function updateCounters() {
        $("#source-count").textContent = `${enabledSites().length} 个播放接口`;
        $("#sync-sources").textContent = state.groups.filter(item => !item.isDeleted).length;
        $("#sync-favorites").textContent = state.favorites.filter(item => !item.isDeleted).length;
        $("#sync-history").textContent = state.history.filter(item => !item.isDeleted).length;
    }
    function posterCard(item, progress = 0) {
        const key = crypto.randomUUID(); itemRegistry.set(key, item);
        const cover = item.coverUrl ? `<img src="${attr(item.coverUrl)}" alt="" loading="lazy" onerror="this.style.opacity=.12" />` : "";
        return `<button class="poster-card" data-item="${key}"><figure>${cover}<span class="source-badge">${escapeHtml(item.sourceName || item.category || "影视")}</span><div class="poster-copy"><strong>${escapeHtml(item.title || "未命名影片")}</strong><small>${escapeHtml(item.remarks || item.caption || "")}</small></div>${progress > 0 ? `<span class="progress"><i style="width:${Math.min(100, progress)}%"></i></span>` : ""}</figure></button>`;
    }
    function entityToItem(entity) { const site = allSites().find(value => value.key === entity.sourceKey); return { sourceKey: entity.sourceKey, sourceName: site?.name || entity.category || "原接口", id: entity.contentId, title: entity.title, coverUrl: entity.displayCoverUrl || entity.coverUrl, originalCoverUrl: entity.coverUrl, remarks: entity.caption, typeName: entity.category }; }
    function favoriteFor(item) { return state.favorites.find(value => !value.isDeleted && value.sourceKey === item.sourceKey && value.contentId === item.id); }
    function historyFor(item) { return state.history.find(value => !value.isDeleted && value.sourceKey === item.sourceKey && value.contentId === item.id); }
    function contentKey(item) { return `${item.sourceKey}\n${item.id || item.contentId}`; }
    function textOf(item) { return `${item.typeName || ""} ${item.title || ""} ${item.remarks || ""}`; }
    function sortModified(a, b) { return String(b.modifiedAtUtc || "").localeCompare(String(a.modifiedAtUtc || "")); }
    function touch(entity) { entity.modifiedAtUtc = now(); entity.dirty = true; }
    function now() { return new Date().toISOString(); }
    function pick(object, keys) { return Object.fromEntries(keys.filter(key => object[key] !== undefined).map(key => [key, object[key]])); }
    function empty(message) { return `<div class="empty-state">${escapeHtml(message)}</div>`; }
    function escapeHtml(value) { return String(value ?? "").replace(/[&<>"']/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[char]); }
    function attr(value) { return escapeHtml(value); }
    function formatTime(value) { if (!Number.isFinite(value)) return "00:00"; const seconds = Math.max(0, Math.floor(value)); const h = Math.floor(seconds / 3600), m = Math.floor(seconds % 3600 / 60), s = seconds % 60; return h ? `${h}:${String(m).padStart(2,"0")}:${String(s).padStart(2,"0")}` : `${String(m).padStart(2,"0")}:${String(s).padStart(2,"0")}`; }
    function jwtClaim(token, name) {
        try {
            const body = token.split(".")[1].replace(/-/g, "+").replace(/_/g, "/");
            const payload = JSON.parse(decodeURIComponent([...atob(body)].map(char => `%${char.charCodeAt(0).toString(16).padStart(2, "0")}`).join("")));
            return payload[name] || payload[`http://schemas.xmlsoap.org/ws/2005/05/identity/claims/${name}`] || "";
        } catch { return ""; }
    }
    function busy(show, message = "正在处理…") { $("#busy span").textContent = message; $("#busy").classList.toggle("hidden", !show); }
    function toast(message, error = false) { clearTimeout(toastTimer); const element = $("#toast"); element.textContent = message; element.classList.toggle("error", error); element.classList.add("show"); toastTimer = setTimeout(() => element.classList.remove("show"), 4200); }
    async function stableId(kind, ...values) {
        const bytes = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(`${kind}\n${values.join("\n")}`))).slice(0, 16);
        bytes[6] = (bytes[6] & 0x0f) | 0x50; bytes[8] = (bytes[8] & 0x3f) | 0x80;
        const hex = [...bytes].map(value => value.toString(16).padStart(2, "0"));
        return `${hex.slice(0,4).reverse().join("")}-${hex.slice(4,6).reverse().join("")}-${hex.slice(6,8).reverse().join("")}-${hex.slice(8,10).join("")}-${hex.slice(10).join("")}`;
    }
})();
