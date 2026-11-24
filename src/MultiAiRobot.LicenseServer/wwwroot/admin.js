"use strict";

(() => {
  const $ = (id) => document.getElementById(id);
  const TOKEN_KEY = "multiairobot.admin.token";
  const THEME_KEY = "multiairobot.admin.theme";
  const iconPaths = {
    overview: ["M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z"],
    key: ["M14 3a7 7 0 0 0-6.7 9L3 16.3V21h4v-3h3v-3l1-1A7 7 0 1 0 14 3Z", "M16.5 7.5h.01"],
    users: ["M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 M16 3a4 4 0 0 1 0 8 M22 21v-2a4 4 0 0 0-3-3.87", "M13 7a4 4 0 1 1-8 0 4 4 0 0 1 8 0Z"],
    activity: ["M3 12h4l3-8 4 16 3-8h4"],
    shield: ["M12 3 3 7v5c0 5 9 9 9 9s9-4 9-9V7l-9-4Z", "m8 12 3 3 5-5"],
    monitor: ["M3 4h18v13H3z M8 21h8 M12 17v4"],
    check: ["m5 12 4 4L19 6"], x: ["m6 6 12 12 M18 6 6 18"],
    plus: ["M12 5v14 M5 12h14"], arrow: ["M5 12h14 M13 6l6 6-6 6"],
    refresh: ["M20 7v5h-5 M4 17v-5h5 M5.2 7a8 8 0 0 1 13.1-2L20 7 M4 17l1.7 2A8 8 0 0 0 18.8 17"],
    search: ["M10.5 18a7.5 7.5 0 1 1 0-15 7.5 7.5 0 0 1 0 15Z M16 16l5 5"],
    moon: ["M21 12.7A9 9 0 0 1 11.3 3a9 9 0 1 0 9.7 9.7Z"],
    sun: ["M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8Z M12 2v2 M12 20v2 M2 12h2 M20 12h2 M4.93 4.93l1.41 1.41 M17.66 17.66l1.41 1.41 M4.93 19.07l1.41-1.41 M17.66 6.34l1.41-1.41"],
    logout: ["M9 3H4v18h5 M10 12h11 M17 8l4 4-4 4"],
    menu: ["M4 6h16 M4 12h16 M4 18h16"],
    copy: ["M8 8h12v13H8z M16 8V3H3v13h5"],
    download: ["M12 3v12 M7 10l5 5 5-5 M4 16v5h16v-5"],
    alert: ["m12 3 10 18H2L12 3Z M12 9v5 M12 17h.01"],
    clock: ["M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0Z M12 7v5l3 2"],
    empty: ["M4 4h16v16H4z M8 9h8 M8 13h5"],
    lock: ["M5 10h14v11H5z M8 10V6a4 4 0 0 1 8 0v4 M12 14v3"]
  };
  const state = {
    token: storageGet(TOKEN_KEY), username: "Administrator", view: "overview",
    data: null, loading: false, error: "", updatedAt: null, loadSequence: 0,
    query: { licenses: "", accounts: "", activity: "" },
    filter: { licenses: "all", accounts: "all" }, modalBusy: false, modalLocked: false,
    theme: storageGet(THEME_KEY) || "light"
  };
  const pageDetails = {
    overview: ["AT A GLANCE", "Overview", "A clear picture of your licenses and connected devices."],
    licenses: ["ACCESS MANAGEMENT", "Licenses", "Issue access, manage expiry dates, and control device activations."],
    accounts: ["YOUR USERS", "Accounts", "Manage sign-in credentials and account availability."],
    activity: ["ADMINISTRATIVE RECORD", "Activity", "A chronological record of changes to your license service."]
  };
  let fieldSequence = 0;

  function storageGet(key) { try { return sessionStorage.getItem(key); } catch { return null; } }
  function storageSet(key, value) { try { value == null ? sessionStorage.removeItem(key) : sessionStorage.setItem(key, value); } catch { /* Memory-only fallback. */ } }
  function node(tag, className, text) {
    const result = document.createElement(tag);
    if (className) result.className = className;
    if (text != null) result.textContent = String(text);
    return result;
  }
  function add(parent, ...children) { children.flat().forEach((child) => { if (child != null) parent.append(child); }); return parent; }
  function icon(name) {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("viewBox", "0 0 24 24"); svg.setAttribute("class", "icon"); svg.setAttribute("aria-hidden", "true");
    (iconPaths[name] || iconPaths.empty).forEach((d) => { const path = document.createElementNS(svg.namespaceURI, "path"); path.setAttribute("d", d); svg.append(path); });
    return svg;
  }
  function button(label, variant = "secondary", onClick, iconName) {
    const result = node("button", "button " + variant); result.type = "button";
    if (iconName) result.append(icon(iconName)); result.append(node("span", "", label));
    if (onClick) result.addEventListener("click", onClick);
    return result;
  }
  function iconButton(name, label, onClick) {
    const result = node("button", "icon-button"); result.type = "button"; result.title = label; result.setAttribute("aria-label", label);
    result.append(icon(name)); if (onClick) result.addEventListener("click", onClick); return result;
  }
  function badge(label, kind) { return node("span", "badge " + kind, label); }
  function initials(value) { return String(value || "?").slice(0, 2).toUpperCase(); }
  function count(value) { return new Intl.NumberFormat().format(Number(value) || 0); }
  function date(value, time = false) {
    const parsed = new Date(value); if (!value || !Number.isFinite(parsed.getTime())) return "—";
    return new Intl.DateTimeFormat(undefined, time ? { month: "short", day: "numeric", year: "numeric", hour: "2-digit", minute: "2-digit" } : { month: "short", day: "numeric", year: "numeric" }).format(parsed);
  }
  function relative(value) {
    const seconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000));
    if (!Number.isFinite(seconds)) return "—";
    if (seconds < 60) return "Just now";
    if (seconds < 3600) return Math.floor(seconds / 60) + "m ago";
    if (seconds < 86400) return Math.floor(seconds / 3600) + "h ago";
    if (seconds < 604800) return Math.floor(seconds / 86400) + "d ago";
    return date(value);
  }
  function actionLabel(value) { return String(value || "Activity").replace(/[._-]+/g, " ").replace(/^\w/, (c) => c.toUpperCase()); }
  function status(license) {
    if (!license.enabled) return { name: "Disabled", kind: "disabled" };
    if (new Date(license.expiresAtUtc).getTime() <= Date.now()) return { name: "Expired", kind: "expired" };
    const account = state.data?.users.find((user) => user.username.toLowerCase() === license.username.toLowerCase());
    if (account && !account.enabled) return { name: "Account disabled", kind: "disabled" };
    return { name: "Active", kind: "active" };
  }
  function empty(title, description, iconName = "empty", action, compact = false) {
    const box = node("div", "empty-state" + (compact ? " compact" : ""));
    add(box, add(node("div", "empty-icon"), icon(iconName)), node("h3", "", title), node("p", "", description));
    if (action) box.append(action); return box;
  }
  function toast(message, kind = "success") {
    const box = node("div", "toast " + kind); box.setAttribute("role", kind === "danger" ? "alert" : "status");
    add(box, icon(kind === "success" ? "check" : "alert"), node("span", "", message), iconButton("x", "Dismiss notification", () => box.remove()));
    $("toasts").append(box);
    while ($("toasts").children.length > 3) $("toasts").firstElementChild.remove();
    setTimeout(() => box.remove(), kind === "success" ? 5000 : 9000);
  }
  function showScreen(name) {
    ["boot", "auth", "app"].forEach((screen) => { $(screen + "-screen").hidden = screen !== name; });
  }
  function setTheme(theme) {
    state.theme = theme === "dark" ? "dark" : "light";
    document.documentElement.dataset.theme = state.theme; storageSet(THEME_KEY, state.theme);
    document.querySelectorAll(".theme-toggle").forEach((toggle) => {
      toggle.replaceChildren(icon(state.theme === "dark" ? "sun" : "moon"));
      const label = state.theme === "dark" ? "Use light theme" : "Use dark theme";
      toggle.setAttribute("aria-label", label); toggle.title = label;
    });
  }
  const mobileNavigation = matchMedia("(max-width: 760px)");
  function syncNavigationAccessibility() { $("sidebar").inert = mobileNavigation.matches && !$("sidebar").classList.contains("open"); }
  function closeNavigation() { $("sidebar").classList.remove("open"); $("mobile-backdrop").hidden = true; $("menu-button").setAttribute("aria-expanded", "false"); syncNavigationAccessibility(); }
  function clearSession(message = "") {
    state.token = null; state.data = null; state.username = "Administrator"; state.loadSequence++; state.loading = false; state.error = ""; state.updatedAt = null;
    storageSet(TOKEN_KEY, null); closeModal(true); closeNavigation();
    $("login-password").value = ""; $("auth-error").textContent = message; $("auth-error").hidden = !message;
    showScreen("auth"); $("login-username").focus();
  }
  async function api(path, options = {}) {
    const headers = { Accept: "application/json" };
    if (!options.public) {
      if (!state.token) throw new Error("Please sign in to continue.");
      headers.Authorization = "Bearer " + state.token;
    }
    if (options.body != null) headers["Content-Type"] = "application/json";
    let response;
    try { response = await fetch("/api/admin/" + path, { method: options.method || "GET", headers, body: options.body == null ? undefined : JSON.stringify(options.body), cache: "no-store", credentials: "same-origin" }); }
    catch { throw new Error("The license service could not be reached. Check that the server is running, then try again."); }
    let payload;
    try { payload = await response.json(); } catch { payload = null; }
    if (!response.ok || payload?.success === false) {
      if (response.status === 401 && !options.public) clearSession("Your session has expired. Sign in again to continue.");
      throw new Error(payload?.message || "The request could not be completed (" + response.status + "). Please try again.");
    }
    if (response.status === 204) return null;
    if (!payload || !("data" in payload)) throw new Error("The server returned an unexpected response. Please refresh and try again.");
    return payload.data;
  }
  async function checkSetup() {
    $("setup-refresh").disabled = true;
    try {
      const setup = await api("setup-status", { public: true });
      $("setup-panel").hidden = !setup.requiresSetup; $("login-form").hidden = !!setup.requiresSetup;
      $("auth-title").textContent = setup.requiresSetup ? "Let’s get you set up." : "Welcome back.";
      $("auth-intro").textContent = setup.requiresSetup ? "Your service is ready for its first administrator." : "Sign in to manage your license service.";
      $("auth-service-state").textContent = "Connected to your license service";
      $("auth-error").hidden = true;
      if (setup.requiresSetup) { state.token = null; storageSet(TOKEN_KEY, null); }
      return !!setup.requiresSetup;
    } catch (error) {
      $("auth-error").textContent = error.message; $("auth-error").hidden = false;
      $("auth-service-state").textContent = "Service unavailable · try again shortly"; return null;
    } finally { $("setup-refresh").disabled = false; }
  }
  async function loadData() {
    const sequence = ++state.loadSequence, token = state.token;
    state.loading = true; renderPage();
    try {
      const [overview, users, licenses, audit] = await Promise.all(["overview", "users", "licenses", "audit"].map((path) => api(path)));
      if (sequence !== state.loadSequence || token !== state.token) return false;
      if (![users, licenses, audit].every(Array.isArray)) throw new Error("The server returned an unexpected data format.");
      state.data = { overview, users, licenses, audit }; state.error = ""; state.updatedAt = new Date();
      return true;
    } catch (error) {
      if (sequence === state.loadSequence && state.token) state.error = error.message;
      return false;
    } finally { if (sequence === state.loadSequence) { state.loading = false; if (state.token) renderPage(); } }
  }
  function navigate(view) {
    state.view = Object.hasOwn(pageDetails, view) ? view : "overview"; closeNavigation(); renderPage();
  }
  function renderPage() {
    if (!state.token) return;
    const [eyebrow, title, description] = pageDetails[state.view];
    $("page-eyebrow").textContent = eyebrow; $("page-title").textContent = title; $("breadcrumb").textContent = title;
    $("page-description").textContent = description; document.title = title + " · Multi AI Robot";
    $("admin-name").textContent = state.username;
    document.querySelectorAll(".nav-item").forEach((item) => { if (item.dataset.view === state.view) item.setAttribute("aria-current", "page"); else item.removeAttribute("aria-current"); });
    $("primary-action-label").textContent = state.view === "accounts" ? "Create account" : "Issue license";
    $("primary-action").disabled = !state.data || state.loading;
    $("refresh-button").disabled = state.loading; $("refresh-button").classList.toggle("rotating", state.loading);
    $("page-error").hidden = !state.error;
    $("page-error").textContent = state.error ? (state.data ? "The dashboard could not be refreshed. The values below are from the last successful load. " : "") + state.error : "";
    $("sidebar-service-state").textContent = state.error ? "Refresh needed" : state.loading ? "Refreshing…" : "Connected";
    $("updated-label").textContent = state.loading ? "Refreshing your workspace…" : state.updatedAt ? "Last loaded " + date(state.updatedAt, true) + " · Local time" : "Waiting for service data";
    $("nav-license-count").textContent = state.data ? count(state.data.licenses.length) : "—";
    $("nav-account-count").textContent = state.data ? count(state.data.users.length) : "—";
    const content = $("page-content"); content.replaceChildren(); content.setAttribute("aria-busy", String(state.loading));
    if (!state.data) {
      if (state.loading) content.append(skeleton());
      else content.append(add(node("section", "panel"), empty("Your workspace couldn’t load", "Check the service connection and try loading your data again.", "alert", button("Try again", "primary", loadData, "refresh"))));
      return;
    }
    if (state.view === "overview") renderOverview(content); else renderDirectory(content);
  }
  function skeleton() {
    const box = node("div"), grid = node("div", "stats-grid");
    for (let i = 0; i < 4; i++) add(grid, add(node("div", "skeleton-card"), node("div", "skeleton-line short"), node("div", "skeleton-line number"), node("div", "skeleton-line short")));
    const panel = node("div", "panel skeleton-panel"); for (let i = 0; i < 7; i++) panel.append(node("div", "skeleton-line"));
    return add(box, grid, panel);
  }
  function panelHead(title, subtitle, action) {
    const head = node("div", "panel-head"), text = node("div"); add(text, node("h2", "panel-title", title), node("p", "panel-subtitle", subtitle)); add(head, text, action); return head;
  }
  function linkButton(label, view) { const result = button(label, "", () => { location.hash = view; }, "arrow"); result.className = "panel-link"; return result; }
  function renderOverview(content) {
    const data = state.data, stats = node("div", "stats-grid");
    const definitions = [["Total licenses", data.overview.licenses, "Issued across all accounts", "key"], ["Active licenses", data.overview.activeLicenses, "Enabled and within their term", "shield"], ["Accounts", data.overview.users, "Users in your workspace", "users"], ["Online devices", data.overview.onlineDevices, "Recent client heartbeats", "monitor"]];
    definitions.forEach(([label, value, hint, glyph]) => {
      const card = node("section", "stat-card"); add(card, add(node("div", "stat-top"), node("span", "stat-label", label), add(node("span", "stat-icon"), icon(glyph))), node("div", "stat-value", count(value)), node("p", "stat-note", hint)); stats.append(card);
    });
    const columns = node("div", "overview-grid"), main = node("div"), recent = node("section", "panel");
    recent.append(panelHead("Recent licenses", "The latest access you’ve issued", linkButton("View all", "licenses")));
    const licenses = [...data.licenses].sort((a, b) => new Date(b.createdAtUtc) - new Date(a.createdAtUtc)).slice(0, 6);
    recent.append(licenses.length ? licenseTable(licenses, true) : empty("Your first license starts here", "Create an account, then issue a license with the access period and device limit you choose.", "key", button("Issue a license", "primary", () => issueLicense(), "plus")));
    main.append(recent);
    const activity = node("section", "panel activity-panel"); activity.append(panelHead("Recent activity", "Changes made in your admin workspace", linkButton("View all", "activity")));
    const entries = [...data.audit].sort((a, b) => new Date(b.occurredAtUtc) - new Date(a.occurredAtUtc)).slice(0, 4);
    if (!entries.length) activity.append(empty("No activity yet", "Account and license changes will appear here.", "activity", null, true));
    else {
      const list = node("div", "activity-list"); entries.forEach((entry) => {
        const time = node("time", "activity-time", relative(entry.occurredAtUtc)); time.dateTime = entry.occurredAtUtc; time.title = date(entry.occurredAtUtc, true);
        add(list, add(node("div", "activity-item"), add(node("span", "activity-symbol"), icon("activity")), add(node("div", "activity-content"), node("strong", "", actionLabel(entry.action)), node("p", "", (entry.subject || "Workspace") + " · " + (entry.actor || "System"))), time));
      }); activity.append(list);
    }
    main.append(activity);
    const side = node("div", "side-stack"), capacity = node("section", "panel");
    capacity.append(panelHead("License health", "Your current access distribution"));
    const active = data.licenses.filter((item) => status(item).kind === "active").length;
    const expired = data.licenses.filter((item) => status(item).kind === "expired").length;
    const disabled = data.licenses.length - active - expired;
    const body = node("div", "capacity-body"), progress = node("progress"); progress.max = Math.max(1, data.licenses.length); progress.value = active; progress.setAttribute("aria-label", active + " of " + data.licenses.length + " licenses are active");
    add(body, add(node("div", "capacity-total"), node("strong", "", data.licenses.length ? Math.round(active / data.licenses.length * 100) + "%" : "—"), node("span", "", "active")), node("p", "capacity-subtitle", "At the time of your last refresh"), progress);
    const rows = node("div", "capacity-rows"); [["Active", active, ""], ["Expired", expired, "expired"], ["Disabled access", disabled, "disabled"]].forEach(([label, value, kind]) => add(rows, add(node("div", "capacity-row"), add(node("span"), node("i", "legend-dot " + kind), document.createTextNode(label)), node("strong", "", count(value)))));
    add(body, rows); capacity.append(body); side.append(capacity);
    const quick = node("section", "quick-panel"); add(quick, add(node("div", "quick-panel-icon"), icon("users")), node("h3", "", "Adding someone new?"), node("p", "", "Start with an account, then issue a license. You can adjust access and device limits whenever you need."), button("Create an account", "secondary", () => createAccount(), "plus")); side.append(quick);
    add(columns, main, side); add(content, stats, columns);
  }
  function table(headers, label) {
    const wrapper = node("div", "table-wrap"), result = node("table"), head = node("thead"), row = node("tr"), body = node("tbody");
    wrapper.tabIndex = 0; wrapper.setAttribute("role", "region"); wrapper.setAttribute("aria-label", label + " table");
    const caption = node("caption", "sr-only", label); headers.forEach((title) => { const th = node("th", "", title); th.scope = "col"; row.append(th); });
    add(head, row); add(result, caption, head, body); wrapper.append(result); return { wrapper, body };
  }
  function accountCell(username, secondary) { return add(node("td"), add(node("div", "cell-account"), node("span", "table-avatar", initials(username)), add(node("div"), node("span", "cell-primary", username), node("span", "cell-secondary mono", secondary)))); }
  function licenseTable(items, compact = false) {
    const { wrapper, body } = table(compact ? ["Account / key", "Status", "Expires", ""] : ["Account / key", "Status", "Expires", "Devices", ""], "Licenses");
    items.forEach((license) => {
      const row = node("tr"), current = status(license), devices = Array.isArray(license.devices) ? license.devices : [];
      add(row, accountCell(license.username, license.keyPrefix || "—"), add(node("td"), badge(current.name, current.kind)));
      const expiry = node("td"); expiry.title = date(license.expiresAtUtc, true); add(expiry, node("span", "cell-primary", date(license.expiresAtUtc)), node("span", "cell-secondary", current.kind === "expired" ? "Renew to restore access" : "License expiry")); row.append(expiry);
      if (!compact) add(row, add(node("td"), add(node("div", "device-cell"), icon("monitor"), document.createTextNode(devices.length + " / " + license.maxDevices), node("span", "", "· " + devices.filter((d) => d.online).length + " online"))));
      add(row, add(node("td", "cell-actions"), button("Manage", "secondary small", () => manageLicense(license.id)))); body.append(row);
    }); return wrapper;
  }
  function userTable(items) {
    const { wrapper, body } = table(["Account", "Status", "Licenses", "Created", ""], "User accounts");
    items.forEach((user) => {
      const row = node("tr"), licenses = state.data.licenses.filter((item) => item.username.toLowerCase() === user.username.toLowerCase());
      add(row, accountCell(user.username, "App account"), add(node("td"), badge(user.enabled ? "Enabled" : "Disabled", user.enabled ? "active" : "disabled")), node("td", "", count(licenses.length)), node("td", "", date(user.createdAtUtc)), add(node("td", "cell-actions"), button("Manage", "secondary small", () => manageAccount(user.id)))); body.append(row);
    }); return wrapper;
  }
  function auditTable(items) {
    const { wrapper, body } = table(["Action", "Subject", "Administrator", "Time"], "Administrative activity");
    items.forEach((entry) => {
      const row = node("tr"), when = node("td"); when.title = date(entry.occurredAtUtc, true);
      add(when, node("span", "cell-primary", date(entry.occurredAtUtc)), node("span", "cell-secondary", new Date(entry.occurredAtUtc).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })));
      add(row, add(node("td"), node("span", "cell-primary", actionLabel(entry.action))), node("td", "audit-subject", entry.subject || "Workspace"), node("td", "", entry.actor || "System"), when); body.append(row);
    }); return wrapper;
  }
  function renderDirectory(content) {
    const view = state.view, panel = node("section", "panel"), toolbar = node("div", "toolbar"), left = node("div", "toolbar-left"), search = node("div", "search-box");
    const searchInput = node("input"); searchInput.type = "search"; searchInput.value = state.query[view]; searchInput.placeholder = view === "licenses" ? "Search accounts or key prefixes…" : view === "accounts" ? "Search accounts…" : "Search actions, subjects, or admins…"; searchInput.setAttribute("aria-label", "Search " + view);
    add(search, icon("search"), searchInput); left.append(search);
    if (view !== "activity") {
      const filter = select(view === "licenses" ? [["all", "All statuses"], ["active", "Active"], ["expired", "Expired"], ["disabled", "Disabled access"]] : [["all", "All accounts"], ["enabled", "Enabled"], ["disabled", "Disabled"]]);
      filter.className = "filter-select"; filter.value = state.filter[view]; filter.setAttribute("aria-label", "Filter " + view + " by status"); filter.addEventListener("change", () => { state.filter[view] = filter.value; renderRows(); }); left.append(filter);
    }
    const label = node("span", "result-count"), results = node("div"); add(toolbar, left, label); add(panel, toolbar, results); content.append(panel);
    searchInput.addEventListener("input", () => { state.query[view] = searchInput.value; renderRows(); });
    function renderRows() {
      results.replaceChildren(); const query = state.query[view].trim().toLowerCase();
      let items;
      if (view === "licenses") items = state.data.licenses.filter((license) => [license.username, license.keyPrefix, license.id].some((value) => String(value || "").toLowerCase().includes(query))).filter((license) => state.filter[view] === "all" || status(license).kind === state.filter[view]).sort((a, b) => new Date(b.createdAtUtc) - new Date(a.createdAtUtc));
      else if (view === "accounts") items = state.data.users.filter((user) => user.username.toLowerCase().includes(query)).filter((user) => state.filter[view] === "all" || user.enabled === (state.filter[view] === "enabled")).sort((a, b) => a.username.localeCompare(b.username));
      else items = state.data.audit.filter((entry) => [entry.action, entry.subject, entry.actor].some((value) => String(value || "").toLowerCase().includes(query))).sort((a, b) => new Date(b.occurredAtUtc) - new Date(a.occurredAtUtc));
      label.textContent = count(items.length) + " " + (view === "activity" ? "events" : view);
      if (items.length) results.append(view === "licenses" ? licenseTable(items) : view === "accounts" ? userTable(items) : auditTable(items));
      else if (query || (state.filter[view] && state.filter[view] !== "all")) results.append(empty("No matches found", "Try another search or clear the status filter.", "search"));
      else results.append(empty(view === "licenses" ? "No licenses yet" : view === "accounts" ? "No accounts yet" : "Your activity will appear here", view === "licenses" ? "Issue your first license to give an account access to the app." : view === "accounts" ? "Create an app account before issuing its first license." : "Administrative changes are recorded automatically.", view === "licenses" ? "key" : view === "accounts" ? "users" : "activity", view === "activity" ? null : button(view === "accounts" ? "Create account" : "Issue license", "primary", () => view === "accounts" ? createAccount() : issueLicense(), "plus")));
      const foot = node("div", "table-footer"); add(foot, node("span", "", "Showing " + count(items.length) + " " + (view === "activity" ? "recorded events" : "results")), node("span", "", "Dates shown in local time")); results.append(foot);
    }
    renderRows();
  }
  function select(options) { const result = node("select"); options.forEach(([value, label]) => { const option = node("option", "", label); option.value = value; result.append(option); }); return result; }
  function input(type, attributes = {}) { const result = node("input"); result.type = type; result.id = "field-" + (++fieldSequence); Object.assign(result, attributes); return result; }
  function field(label, control, hint) {
    if (!control.id) control.id = "field-" + (++fieldSequence);
    const box = node("div", "field"), lab = node("label", "", label); lab.htmlFor = control.id; add(box, lab, control);
    if (hint) { const help = node("p", "field-hint", hint); help.id = control.id + "-hint"; control.setAttribute("aria-describedby", help.id); box.append(help); }
    return box;
  }
  function passwordField(label, autocomplete = "new-password") {
    const control = input("password", { required: true, minLength: 12, maxLength: 256, autocomplete });
    const wrapper = node("div", "password-input"), toggle = node("button", "password-toggle", "Show"); toggle.type = "button"; toggle.setAttribute("aria-label", "Show password");
    toggle.addEventListener("click", () => { const visible = control.type === "password"; control.type = visible ? "text" : "password"; toggle.textContent = visible ? "Hide" : "Show"; toggle.setAttribute("aria-label", visible ? "Hide password" : "Show password"); }); add(wrapper, control, toggle);
    const box = node("div", "field"), lab = node("label", "", label); lab.htmlFor = control.id; add(box, lab, wrapper, node("p", "field-hint", "Use at least 12 characters. Passwords are not written to browser storage."));
    return { box, control };
  }
  function openModal(title, subtitle, eyebrow = "ADMINISTRATION", wide = false, locked = false) {
    const modal = $("modal"); if (modal.open) modal.close(); modal.replaceChildren(); modal.className = "modal" + (wide ? " wide" : ""); state.modalBusy = false; state.modalLocked = locked;
    const head = node("header", "modal-header"), heading = node("div"), h2 = node("h2", "", title); h2.id = "modal-title";
    add(heading, node("div", "modal-eyebrow", eyebrow), h2, subtitle ? node("p", "", subtitle) : null); head.append(heading);
    if (!locked) head.append(iconButton("x", "Close dialog", () => closeModal()));
    const body = node("div", "modal-body"), footer = node("footer", "modal-footer"); add(modal, head, body, footer); modal.showModal(); return { modal, body, footer };
  }
  function closeModal(force = false) { if (state.modalBusy && !force) return; $("modal").close(); $("modal").replaceChildren(); state.modalBusy = false; state.modalLocked = false; }
  function formError(parent) { const box = node("div", "notice danger form-error"); box.hidden = true; box.setAttribute("role", "alert"); parent.append(box); return box; }
  function bindForm(form, submit, errorBox, handler) {
    form.addEventListener("submit", async (event) => {
      event.preventDefault(); if (state.modalBusy || !form.reportValidity()) return;
      submit.disabled = true; state.modalBusy = true; errorBox.hidden = true;
      try { await handler(); }
      catch (error) { if (errorBox.isConnected) { errorBox.textContent = error.message; errorBox.hidden = false; } }
      finally { state.modalBusy = false; submit.disabled = false; }
    });
  }
  async function mutateButton(control, errorBox, handler) {
    if (state.modalBusy) return; state.modalBusy = true; control.disabled = true; errorBox.hidden = true;
    try { await handler(); }
    catch (error) { if (errorBox.isConnected) { errorBox.textContent = error.message; errorBox.hidden = false; } }
    finally { state.modalBusy = false; control.disabled = false; }
  }
  async function refreshAfterMutation(successMessage) {
    const fresh = await loadData();
    if (state.token) toast(fresh ? successMessage : successMessage + " Dashboard refresh failed; refresh to see the latest data.", fresh ? "success" : "warning");
    return fresh;
  }
  function createAccount(afterCreate) {
    const dialog = openModal("Create an account", "Create app sign-in credentials before issuing a license.", "ACCOUNTS"), form = node("form"); form.id = "create-account-form";
    const errorBox = formError(form), username = input("text", { required: true, minLength: 3, maxLength: 64, autocomplete: "off", spellcheck: false }); username.pattern = "[A-Za-z0-9][A-Za-z0-9._@-]{2,63}"; username.autocapitalize = "none";
    const password = passwordField("Account password"); add(form, field("Username", username, "3–64 characters. Use letters, numbers, dots, underscores, @, or hyphens; start with a letter or number."), password.box); dialog.body.append(form);
    const submit = button("Create account", "primary", null, "plus"); submit.type = "submit"; submit.setAttribute("form", form.id);
    add(dialog.footer, button("Cancel", "secondary", () => closeModal()), submit);
    bindForm(form, submit, errorBox, async () => {
      const user = await api("users", { method: "POST", body: { username: username.value.trim(), password: password.control.value } });
      password.control.value = ""; await refreshAfterMutation("Account created."); closeModal(true);
      if (state.token && afterCreate) afterCreate(user.username || username.value.trim());
    }); username.focus();
  }
  function issueLicense(preferredUsername) {
    if (!state.data) return;
    const enabled = state.data.users.filter((user) => user.enabled);
    if (!enabled.length) { createAccount((username) => issueLicense(username)); return; }
    const dialog = openModal("Issue a license", "Set the access period and number of devices for an account.", "LICENSES"), form = node("form"); form.id = "issue-license-form";
    const errorBox = formError(form), username = select(enabled.map((user) => [user.username, user.username])); username.required = true;
    if (preferredUsername) username.value = preferredUsername;
    const days = input("number", { required: true, min: "1", max: "3650", step: "1", value: "30" }), devices = input("number", { required: true, min: "1", max: "100", step: "1", value: "1" });
    add(form, field("Account", username, "Only enabled app accounts can receive a new license."), add(node("div", "field-row"), field("License term · days", days, "From 1 to 3,650 days."), field("Device limit", devices, "From 1 to 100 registered devices.")), node("div", "notice info", "The full license key will be displayed once after creation. Save it before closing that screen.")); dialog.body.append(form);
    const submit = button("Issue license", "primary", null, "key"); submit.type = "submit"; submit.setAttribute("form", form.id); add(dialog.footer, button("Cancel", "secondary", () => closeModal()), submit);
    bindForm(form, submit, errorBox, async () => {
      const result = await api("licenses", { method: "POST", body: { username: username.value, days: Number(days.value), maxDevices: Number(devices.value) } });
      if (!result?.key || !result?.license) throw new Error("The server did not return the newly issued license key. Refresh the license list before issuing another license.");
      await refreshAfterMutation("License issued."); if (state.token) showIssuedKey(result.license, result.key);
    }); username.focus();
  }
  function showIssuedKey(license, key) {
    const dialog = openModal("Your license is ready.", "Save the key below. It won’t be shown again.", "LICENSE ISSUED", false, true);
    const keyBlock = node("code", "license-key", key); keyBlock.tabIndex = 0;
    add(dialog.body, add(node("div", "key-success-icon"), icon("check")), node("p", "key-reminder", "Account: " + license.username + " · Expires " + date(license.expiresAtUtc) + " · " + license.maxDevices + " device" + (license.maxDevices === 1 ? "" : "s")), keyBlock);
    const actions = node("div", "key-actions"), copy = button("Copy key", "primary", async () => {
      try { if (!navigator.clipboard) throw new Error(); await navigator.clipboard.writeText(key); toast("License key copied."); }
      catch { keyBlock.focus(); const selection = getSelection(), range = document.createRange(); range.selectNodeContents(keyBlock); selection.removeAllRanges(); selection.addRange(range); toast("The key is selected. Use your keyboard to copy it.", "warning"); }
    }, "copy");
    const download = button("Download key", "secondary", () => {
      const text = "Multi AI Robot license\n\nAccount: " + license.username + "\nLicense key: " + key + "\nExpires: " + license.expiresAtUtc + "\nDevice limit: " + license.maxDevices + "\n\nKeep this key private.\n";
      const url = URL.createObjectURL(new Blob([text], { type: "text/plain;charset=utf-8" })), link = node("a");
      link.href = url; link.download = "MultiAiRobot-license-" + license.username.replace(/[^A-Za-z0-9._-]/g, "_") + ".txt"; document.body.append(link); link.click(); link.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000);
    }, "download");
    add(actions, copy, download); add(dialog.body, actions, node("p", "key-reminder", "Share this key with the account holder through a private channel. The account password and license key are both needed in the app."));
    dialog.footer.append(button("I’ve saved the key", "secondary", () => closeModal(true), "check")); copy.focus();
  }
  function summaryCell(label, value) { return add(node("div", "summary-cell"), node("span", "", label), node("strong", "", value)); }
  function manageLicense(id) {
    const license = state.data?.licenses.find((item) => item.id === id); if (!license) return;
    const current = status(license), dialog = openModal(license.username, "Manage this license and its registered devices.", "LICENSE DETAILS", true), errorBox = formError(dialog.body);
    const licenseSummary = add(node("div", "summary-grid"), summaryCell("Issued", date(license.createdAtUtc, true)), summaryCell("Expires", date(license.expiresAtUtc, true)));
    add(dialog.body, add(node("div", "detail-heading"), badge(current.name, current.kind), node("span", "mono", license.keyPrefix || "—")), licenseSummary);
    const form = node("form"), days = input("number", { min: "0", max: "3650", step: "1", required: true, value: "0" }), maxDevices = input("number", { min: "1", max: "100", step: "1", required: true, value: String(license.maxDevices) });
    const preview = node("p", "field-hint", "Extending starts from the current expiry, or today if the license has expired.");
    days.addEventListener("input", () => { const value = Number(days.value); preview.textContent = value > 0 && value <= 3650 ? "New expiry: " + date(new Date(Math.max(Date.now(), new Date(license.expiresAtUtc).getTime()) + value * 86400000), true) : "Extending starts from the current expiry, or today if the license has expired."; });
    add(form, node("h3", "", "License settings"), add(node("div", "field-row"), field("Extend by · days", days, "Leave at 0 to keep the current expiry."), field("Device limit", maxDevices, "Release devices first if reducing below current usage.")), preview);
    const save = button("Save changes", "primary"); save.type = "submit"; form.append(add(node("div", "modal-inline-actions"), save)); dialog.body.append(form);
    bindForm(form, save, errorBox, async () => {
      const update = { maxDevices: Number(maxDevices.value) }, extension = Number(days.value);
      if (extension > 0) update.expiresAtUtc = new Date(Math.max(Date.now(), new Date(license.expiresAtUtc).getTime()) + extension * 86400000).toISOString();
      await api("licenses/" + encodeURIComponent(id), { method: "PATCH", body: update });
      const fresh = await refreshAfterMutation("License settings updated."); if (fresh) manageLicense(id); else closeModal(true);
    });
    const toggle = button(license.enabled ? "Disable license" : "Re-enable license", license.enabled ? "danger small" : "secondary small");
    toggle.addEventListener("click", () => mutateButton(toggle, errorBox, async () => {
      await api("licenses/" + encodeURIComponent(id), { method: "PATCH", body: { enabled: !license.enabled } });
      const fresh = await refreshAfterMutation(license.enabled ? "License disabled." : "License enabled."); if (fresh) manageLicense(id); else closeModal(true);
    }));
    add(dialog.body, add(node("div", "detail-actions"), add(node("div"), node("strong", "", license.enabled ? "Pause this license" : "Restore this license"), node("p", "", license.enabled ? "Disable access while keeping the license and its device history." : "Enable the license. An expired license also needs an extension.")), toggle));
    const section = node("section", "modal-section"), devices = Array.isArray(license.devices) ? license.devices : [];
    add(section, node("h3", "", "Registered devices · " + devices.length + " / " + license.maxDevices));
    if (!devices.length) section.append(empty("No devices registered", "Devices appear here when this license is used in the app.", "monitor", null, true));
    else {
      const list = node("div", "device-list"); devices.forEach((device) => {
        const label = node("span", "device-id mono", device.instanceId); label.title = device.instanceId;
        const release = button("Release", "secondary small"); release.setAttribute("aria-label", "Release device " + device.instanceId);
        release.addEventListener("click", () => mutateButton(release, errorBox, async () => {
          await api("licenses/" + encodeURIComponent(id) + "/devices/" + encodeURIComponent(device.instanceId), { method: "DELETE" });
          const fresh = await refreshAfterMutation("Device released."); if (fresh) manageLicense(id); else closeModal(true);
        }));
        add(list, add(node("div", "device-row"), add(node("div", "device-information"), label, node("div", "device-last-seen", "Last seen " + date(device.lastSeenAtUtc, true))), add(node("div", "device-status"), badge(device.online ? "Online" : "Offline", device.online ? "online" : "offline"), release)));
      }); section.append(list); section.append(node("p", "field-hint", "Releasing a device frees one activation slot."));
    }
    dialog.body.append(section); dialog.footer.append(button("Done", "secondary", () => closeModal()));
  }
  function manageAccount(id) {
    const user = state.data?.users.find((item) => item.id === id); if (!user) return;
    const dialog = openModal(user.username, "Manage this account’s access and sign-in credentials.", "ACCOUNT DETAILS"), errorBox = formError(dialog.body);
    add(dialog.body, badge(user.enabled ? "Enabled" : "Disabled", user.enabled ? "active" : "disabled"), add(node("div", "summary-grid"), summaryCell("Created", date(user.createdAtUtc, true)), summaryCell("Licenses", count(state.data.licenses.filter((item) => item.username.toLowerCase() === user.username.toLowerCase()).length))));
    const toggle = button(user.enabled ? "Disable account" : "Enable account", user.enabled ? "danger small" : "secondary small");
    toggle.addEventListener("click", () => mutateButton(toggle, errorBox, async () => {
      await api("users/" + encodeURIComponent(id), { method: "PATCH", body: { enabled: !user.enabled } });
      const fresh = await refreshAfterMutation(user.enabled ? "Account disabled." : "Account enabled."); if (fresh) manageAccount(id); else closeModal(true);
    }));
    add(dialog.body, add(node("div", "detail-actions"), add(node("div"), node("strong", "", "Account access"), node("p", "", user.enabled ? "Disable sign-in for this account while keeping its licenses." : "Re-enable the account so it can use valid licenses.")), toggle));
    const section = node("section", "modal-section"), form = node("form"), password = passwordField("New account password");
    add(form, node("h3", "", "Reset password"), password.box);
    const submit = button("Update password", "primary"); submit.type = "submit"; form.append(add(node("div", "modal-inline-actions"), submit)); section.append(form); dialog.body.append(section);
    bindForm(form, submit, errorBox, async () => {
      await api("users/" + encodeURIComponent(id), { method: "PATCH", body: { password: password.control.value } });
      password.control.value = ""; const fresh = await refreshAfterMutation("Account password updated."); if (fresh) manageAccount(id); else closeModal(true);
    });
    dialog.footer.append(button("Done", "secondary", () => closeModal()));
  }

  document.querySelectorAll("[data-icon]").forEach((element) => element.append(icon(element.dataset.icon)));
  document.querySelectorAll(".theme-toggle").forEach((toggle) => toggle.addEventListener("click", () => setTheme(state.theme === "dark" ? "light" : "dark")));
  document.querySelectorAll("[data-password-target]").forEach((toggle) => toggle.addEventListener("click", () => {
    const target = $(toggle.dataset.passwordTarget), visible = target.type === "password"; target.type = visible ? "text" : "password"; toggle.textContent = visible ? "Hide" : "Show"; toggle.setAttribute("aria-label", visible ? "Hide password" : "Show password");
  }));
  $("modal").addEventListener("cancel", (event) => { if (state.modalBusy || state.modalLocked) event.preventDefault(); });
  $("modal").addEventListener("close", () => { if (!$("modal").open) { $("modal").replaceChildren(); state.modalBusy = false; state.modalLocked = false; } });
  $("menu-button").addEventListener("click", () => { const open = !$("sidebar").classList.contains("open"); $("sidebar").classList.toggle("open", open); $("mobile-backdrop").hidden = !open; $("menu-button").setAttribute("aria-expanded", String(open)); syncNavigationAccessibility(); });
  mobileNavigation.addEventListener("change", () => { if (!mobileNavigation.matches) closeNavigation(); syncNavigationAccessibility(); });
  $("mobile-backdrop").addEventListener("click", closeNavigation);
  $("refresh-button").addEventListener("click", loadData);
  $("primary-action").addEventListener("click", () => state.view === "accounts" ? createAccount() : issueLicense());
  $("setup-refresh").addEventListener("click", checkSetup);
  window.addEventListener("hashchange", () => navigate(location.hash.slice(1)));
  $("logout-button").addEventListener("click", async () => {
    const control = $("logout-button"); control.disabled = true;
    try { await api("logout", { method: "POST" }); clearSession(); }
    catch (error) { if (state.token) toast("Sign-out could not be confirmed: " + error.message, "danger"); }
    finally { control.disabled = false; }
  });
  $("login-form").addEventListener("submit", async (event) => {
    event.preventDefault(); const submit = $("login-submit"); if (submit.disabled || !$("login-form").reportValidity()) return;
    submit.disabled = true; $("auth-error").hidden = true;
    try {
      const result = await api("login", { method: "POST", public: true, body: { username: $("login-username").value.trim(), password: $("login-password").value } });
      if (!result?.token) throw new Error("The server did not return an administrator session.");
      state.token = result.token; state.username = result.username || $("login-username").value.trim(); storageSet(TOKEN_KEY, state.token); $("login-password").value = "";
      showScreen("app"); navigate(location.hash.slice(1)); await loadData();
    } catch (error) { $("auth-error").textContent = error.message; $("auth-error").hidden = false; }
    finally { submit.disabled = false; }
  });
  setTheme(state.theme);
  (async () => {
    const requiresSetup = await checkSetup();
    if (state.token && requiresSetup === false) { showScreen("app"); navigate(location.hash.slice(1)); await loadData(); }
    else { showScreen("auth"); if (!requiresSetup) $("login-username").focus(); }
  })();
})();
