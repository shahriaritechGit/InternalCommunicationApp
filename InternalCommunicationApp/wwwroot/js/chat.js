"use strict";

console.log("chat.js loaded");

const connection = new signalR.HubConnectionBuilder()
    .withUrl("/chathub", {
        transport: signalR.HttpTransportType.WebSockets
    }).withAutomaticReconnect()
    .build();
const currentUser = document.querySelector(".chat-user span").textContent.trim();
const messageContainer = document.getElementById("messageContainer");
const messageInput = document.getElementById("messageInput");
const sendButton = document.getElementById("sendButton");
const recipientInput = document.getElementById("recipientInput");
const conversationUsername = document.getElementById("conversationUsername");
const conversationAvatar = document.getElementById("conversationAvatar");
const profileCard = document.querySelector(".profile-card");
const profileStatus = document.getElementById("profileStatus");
const conversationList = document.querySelector(".conversation-list");
const userSearch = document.getElementById("userSearch");
const userResults = document.getElementById("userResults");

const emptyChatHtml =
    '<div class="empty-chat"><h3>No messages yet</h3><p>Send a message to start the conversation.</p></div>';
const rendered = new Set();      // message ids already on screen (prevents duplicates)
const pending = new Map();   // clientId -> message element waiting for server confirmation
let activeUsername = null;
const conversationStatus = document.getElementById("conversationStatus");
const onlineUsers = new Set();
const lastSeen = new Map();   // username -> unix ms
let typingUser = null;
let typingTimer;


function setMyStatus(state, text) {
    profileCard.dataset.status = state;
    profileStatus.textContent = text;
}
function renderStatus() {
    const header = document.querySelector(".conversation-header");
    if (!activeUsername) {
        conversationStatus.textContent = "";
        header.removeAttribute("data-online");
        return;
    }
    header.toggleAttribute("data-online", onlineUsers.has(activeUsername));
    conversationStatus.textContent =
        typingUser === activeUsername ? "typing…" :
        onlineUsers.has(activeUsername) ? "Online" :
        formatLastSeen(lastSeen.get(activeUsername));
}

function refreshPresenceUI() {
    conversationList.querySelectorAll(".conversation-item").forEach(item => {
        item.toggleAttribute("data-online", onlineUsers.has(item.dataset.username));
        refreshItemLabel(item);
    });
    renderStatus();
}

function setOnline(username, isOnline, lastSeenMs) {
    if (isOnline) {
        onlineUsers.add(username);
        lastSeen.delete(username);
    } else {
        onlineUsers.delete(username);
        if (lastSeenMs) lastSeen.set(username, lastSeenMs);
    }
    refreshPresenceUI();
}

function loadPresence(username) {
    return connection.invoke("GetPresence", username)
        .then(p => setOnline(username, p.online, p.lastSeen))
        .catch(() => {});
}

function formatLastSeen(ms) {
    if (!ms) return "Offline";
    const mins = Math.floor((Date.now() - ms) / 60000);
    if (mins < 1) return "Last seen just now";
    if (mins < 60) return `Last seen ${mins} min ago`;

    const d = new Date(ms);
    const time = d.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
    return d.toDateString() === new Date().toDateString()
        ? `Last seen today at ${time}`
        : `Last seen ${d.toLocaleDateString()} ${time}`;
}

function setComposerEnabled(enabled) {
    messageInput.disabled = !enabled;
    sendButton.disabled = !enabled;
}
setComposerEnabled(false);

function ensureSidebarItem(username) {
    const existing = [...conversationList.querySelectorAll(".conversation-item")]
        .find(i => i.dataset.username === username);
    if (existing) return existing;

    const item = document.createElement("button");
    item.type = "button";
    item.className = "conversation-item";
    item.dataset.username = username;
    item.setAttribute("aria-label", `Open conversation with ${username}`);

    const avatar = document.createElement("div");
    avatar.className = "avatar";
    avatar.setAttribute("aria-hidden", "true");
    avatar.textContent = username.charAt(0).toUpperCase();

    const info = document.createElement("div");
    info.className = "conversation-info";
    const name = document.createElement("strong");
    name.textContent = username;
    const preview = document.createElement("span");
    preview.textContent = "Start a conversation";
    info.append(name, preview);

    item.append(avatar, info);
    conversationList.prepend(item);
    return item;
}

function refreshItemLabel(item) {
    const parts = [`Open conversation with ${item.dataset.username}`];
    if (item.dataset.online !== undefined) parts.push("online");
    if (item.dataset.unread) parts.push(`${item.dataset.unread} unread`);
    item.setAttribute("aria-label", parts.join(", "));
}

function setUnread(item, count) {
    if (count > 0) item.dataset.unread = count;
    else delete item.dataset.unread;
    refreshItemLabel(item);
}

function findSidebarItem(username) {
    return [...conversationList.querySelectorAll(".conversation-item")]
        .find(i => i.dataset.username === username);
}
async function openConversation(username) {
    document.querySelector("[data-chat-app]").dataset.chatOpen = "true";
    activeUsername = username;
    recipientInput.value = username;
    conversationUsername.textContent = username;
    conversationAvatar.textContent = username.charAt(0).toUpperCase();

    conversationList.querySelectorAll(".conversation-item").forEach(i =>
        i.classList.toggle("active", i.dataset.username === username));
    setUnread(ensureSidebarItem(username), 0);
    typingUser = null;
    connection.invoke("MarkRead", username).catch(() => {});
    loadPresence(username);    
    renderStatus();

    setComposerEnabled(true);
    rendered.clear();
    messageContainer.innerHTML = emptyChatHtml;

    window.ChatUI?.setMessagesLoading(true);
    try {
        const res = await fetch(
            `${location.pathname}?handler=Messages&with=${encodeURIComponent(username)}`,
            { headers: { Accept: "application/json" } });
        if (!res.ok) throw new Error(`History request failed: ${res.status}`);
        const history = await res.json();
        if (activeUsername !== username) return;          // user already switched chats
        history.forEach(m => addMessage(m.sender, m.text, m.id, m.sentAt,
        { edited: !!m.editedAt, deleted: m.deleted }));
    } catch (err) {
        console.error(err);
    } finally {
        if (activeUsername === username) window.ChatUI?.setMessagesLoading(false);
    }
}
// ---------- time / preview helpers ----------
function parseUtc(value) {
    if (!value) return new Date();
    if (value instanceof Date) return value;
    const s = String(value);
    return new Date(/(Z|[+-]\d\d:?\d\d)$/i.test(s) ? s : s + "Z");   // SQL dates arrive without "Z"
}

function formatMsgTime(d) {
    const time = d.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
    return d.toDateString() === new Date().toDateString()
        ? time
        : `${d.toLocaleDateString([], { month: "short", day: "numeric" })}, ${time}`;
}

function previewText(sender, text) {
    return (sender === currentUser ? "You: " : "") + text;
}

function setPreview(username, text) {
    const item = findSidebarItem(username);
    if (item) item.querySelector(".conversation-info span").textContent = text;
}

// ---------- edit / delete UI ----------
function closeAllActions() {
    messageContainer.querySelectorAll(".message-actions").forEach(a => { a.hidden = true; });
    messageContainer.querySelectorAll('[data-action="menu"]')
        .forEach(b => b.setAttribute("aria-expanded", "false"));
}

function toggleActions(wrapper) {
    const actions = wrapper.querySelector(".message-actions");
    const btn = wrapper.querySelector('[data-action="menu"]');
    const open = actions.hidden;
    closeAllActions();
    actions.hidden = !open;
    btn.setAttribute("aria-expanded", String(open));
}

function makeActionButton(action, label, cls) {
    const b = document.createElement("button");
    b.type = "button";
    b.className = `btn btn-sm ${cls}`;
    b.dataset.action = action;
    b.textContent = label;
    return b;
}

function addActions(wrapper) {
    if (wrapper.querySelector(".message-actions")) return;

    const menuBtn = document.createElement("button");
    menuBtn.type = "button";
    menuBtn.className = "message-menu-btn";
    menuBtn.dataset.action = "menu";
    menuBtn.setAttribute("aria-label", "Message options");
    menuBtn.setAttribute("aria-haspopup", "true");
    menuBtn.setAttribute("aria-expanded", "false");
    menuBtn.innerHTML =
        '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">' +
        '<circle cx="12" cy="5" r="2"/><circle cx="12" cy="12" r="2"/><circle cx="12" cy="19" r="2"/></svg>';
    wrapper.querySelector(".message-meta").appendChild(menuBtn);

    const actions = document.createElement("div");
    actions.className = "message-actions";
    actions.hidden = true;
    actions.append(
        makeActionButton("edit", "Edit", "btn-light"),
        makeActionButton("delete", "Delete", "btn-outline-danger"));
    wrapper.querySelector(".message-bubble").appendChild(actions);
}

function startEdit(wrapper) {
    if (wrapper.querySelector(".message-edit")) return;

    const textEl = wrapper.querySelector(".message-text");
    const actions = wrapper.querySelector(".message-actions");
    const original = textEl.textContent;

    const input = document.createElement("input");
    input.type = "text";
    input.className = "form-control message-edit";
    input.value = original;
    input.maxLength = 2000;
    input.setAttribute("aria-label", "Edit message");

    const save = makeActionButton("save", "Save", "btn-primary");
    const cancel = makeActionButton("cancel", "Cancel", "btn-light");
    const row = document.createElement("div");
    row.className = "message-edit-row";
    row.append(save, cancel);

    textEl.hidden = true;
    actions.hidden = true;
    textEl.after(input, row);
    input.focus();
    input.select();

    const close = () => { input.remove(); row.remove(); textEl.hidden = false; };

    const commit = async () => {
        const value = input.value.trim();
        if (!value || value === original) { close(); return; }
        save.disabled = true;
        try {
            await connection.invoke("EditMessage", Number(wrapper.dataset.id), value);
            close();                                     // text updates via the MessageEdited event
        } catch (err) {
            save.disabled = false;
            alert(err.message.split("HubException:").pop().trim());
        }
    };

    save.addEventListener("click", commit);
    cancel.addEventListener("click", close);
    input.addEventListener("keydown", e => {
        if (e.key === "Enter" && !e.isComposing) { e.preventDefault(); commit(); }
        else if (e.key === "Escape") close();
    });
}

async function deleteMessage(wrapper) {
    closeAllActions();
    if (!confirm("Delete this message?")) return;
    try {
        await connection.invoke("DeleteMessage", Number(wrapper.dataset.id));
    } catch (err) {
        alert(err.message.split("HubException:").pop().trim());
    }
}

function markDeleted(wrapper) {
    wrapper.classList.add("deleted");
    wrapper.querySelectorAll(".message-menu-btn, .message-actions, .message-edit, .message-edit-row")
        .forEach(n => n.remove());
    const text = wrapper.querySelector(".message-text");
    text.textContent = "This message was deleted";
    text.hidden = false;
    wrapper.querySelector(".message-edited").hidden = true;
}

// Server accepted my pending message: attach id, real time and the menu
function confirmPending(wrapper, m) {
    wrapper.classList.remove("pending");
    wrapper.dataset.id = m.id;
    rendered.add(m.id);

    const when = parseUtc(m.sentAt);
    const time = wrapper.querySelector("time");
    time.dateTime = when.toISOString();
    time.textContent = formatMsgTime(when);
    time.title = when.toLocaleString();

    addActions(wrapper);
}

messageContainer.addEventListener("click", e => {
    const btn = e.target.closest("button[data-action]");
    const wrapper = btn?.closest(".message-wrapper");
    if (!wrapper?.dataset.id) return;

    if (btn.dataset.action === "menu") toggleActions(wrapper);
    else if (btn.dataset.action === "edit") startEdit(wrapper);
    else if (btn.dataset.action === "delete") deleteMessage(wrapper);
});

document.addEventListener("click", e => {
    if (!e.target.closest(".message-menu-btn, .message-actions")) closeAllActions();
});
//message to ui
function addMessage(sender, message, id, sentAt, opts = {}) {
    if (id != null) {
        if (rendered.has(id)) return;
        rendered.add(id);
    }

    const emptyChat = messageContainer.querySelector(".empty-chat");
    if (emptyChat) emptyChat.remove();

    const mine = sender === currentUser;
    const when = parseUtc(sentAt);

    const wrapper = document.createElement("div");
    wrapper.classList.add("message-wrapper", mine ? "sent" : "received");
    if (id != null) wrapper.dataset.id = id;

    const bubble = document.createElement("div");
    bubble.classList.add("message-bubble");

    if (!mine) {
        const senderName = document.createElement("div");
        senderName.classList.add("message-sender");
        senderName.textContent = sender;
        bubble.appendChild(senderName);
    }

    const text = document.createElement("div");
    text.classList.add("message-text");
    text.textContent = opts.deleted ? "This message was deleted" : message;
    bubble.appendChild(text);

    const meta = document.createElement("div");
    meta.classList.add("message-meta");

    const edited = document.createElement("span");
    edited.classList.add("message-edited");
    edited.textContent = "edited";
    edited.hidden = !opts.edited || !!opts.deleted;

    const time = document.createElement("time");
    time.dateTime = when.toISOString();
    time.textContent = formatMsgTime(when);
    time.title = when.toLocaleString();

    meta.append(edited, time);
    bubble.appendChild(meta);
    wrapper.appendChild(bubble);

    if (opts.deleted) wrapper.classList.add("deleted");
    else if (mine && id != null) addActions(wrapper);

    messageContainer.appendChild(wrapper);
    messageContainer.scrollTop = messageContainer.scrollHeight;
    return wrapper;
}
//send message
async function sendCurrentMessage() {
    const recipient = recipientInput.value.trim();
    const message = messageInput.value.trim();
    if (!recipient || !message) return;

    const clientId = crypto.randomUUID?.() ?? `${Date.now()}-${Math.random()}`;

    // Show it immediately, faded until the server confirms
    const el = addMessage(currentUser, message);
    el.classList.add("pending");
    pending.set(clientId, el);

    messageInput.value = "";
    messageInput.focus();

    try {
        await connection.invoke("SendMessage", recipient, message, clientId);
    } catch (err) {
        console.error("SendMessage failed:", err);
        pending.delete(clientId);
        el.classList.remove("pending");
        el.classList.add("failed");
    }
}

connection.on("OnlineList", names => {
    onlineUsers.clear();
    names.forEach(n => onlineUsers.add(n));
    refreshPresenceUI();
        if (activeUsername) 
        {
            loadPresence(activeUsername);
        }
});

connection.on("PresenceChanged", (name, isOnline, lastSeenMs) => setOnline(name, isOnline, lastSeenMs));

connection.on("UserTyping", name => {
    if (name !== activeUsername) return;
    typingUser = name;
    renderStatus();
    clearTimeout(typingTimer);
    typingTimer = setTimeout(() => { typingUser = null; renderStatus(); }, 3000);
});

connection.on("UnreadCleared", username => {
    const item = findSidebarItem(username);
    if (item) setUnread(item, 0);
});
conversationList.querySelectorAll(".conversation-item").forEach(refreshItemLabel);

// One delegated listener also covers sidebar items created later
conversationList.addEventListener("click", e => {
    const item = e.target.closest(".conversation-item");
    if (item?.dataset.username) openConversation(item.dataset.username);
});
// People search (debounced)
let searchTimer;
userSearch.addEventListener("input", () => {
    clearTimeout(searchTimer);
    const q = userSearch.value.trim();
    if (!q) { userResults.replaceChildren(); userResults.hidden = true; return; }

   function showSearchMessage(text) {
        const li = document.createElement("li");
        li.className = "px-3 py-2 text-muted small";
        li.textContent = text;
        userResults.replaceChildren(li);
        userResults.hidden = false;
    }

    searchTimer = setTimeout(async () => {
        try {
            const res = await fetch(
                `${location.pathname}?handler=Users&q=${encodeURIComponent(q)}`,
                { headers: { Accept: "application/json" }, cache: "no-store", redirect: "manual" });

            if (userSearch.value.trim() !== q) return;                 // stale response
            if (res.type === "opaqueredirect") { showSearchMessage("Session expired. Please log in again."); return; }
            if (!res.ok) { showSearchMessage(`Search failed (${res.status}).`); return; }

            const names = await res.json();
            if (names.length === 0) { showSearchMessage("No one found."); return; }

            userResults.replaceChildren(...names.map(name => {
                const li = document.createElement("li");
                const btn = document.createElement("button");
                btn.type = "button";
                btn.className = "user-result";
                btn.textContent = name;
                btn.addEventListener("click", () => {
                    userSearch.value = "";
                    userResults.replaceChildren();
                    userResults.hidden = true;
                    ensureSidebarItem(name);
                    openConversation(name);
                    window.ChatUI?.showView("chat");
                });
                li.appendChild(btn);
                return li;
            }));
            userResults.hidden = false;
        } catch (err) {
            console.error("User search failed:", err);
            showSearchMessage("Search failed.");
        }
    }, 300);
});
// Tell the other person we're typing (client throttle: once per 2s)
let lastTyping = 0;
messageInput.addEventListener("input", () => {
    const now = Date.now();
    if (!activeUsername || now - lastTyping < 2000) return;
    lastTyping = now;
    connection.invoke("Typing", activeUsername).catch(() => {});
});

//siganlR
connection.on("ConnectedAs", (username) => {
    console.log(`SignalR connected as ${username}`);
});
connection.on("ReceiveMessage", (m) => {
    const other = m.sender === currentUser ? m.recipient : m.sender;

    const item = ensureSidebarItem(other);
    item.querySelector(".conversation-info span").textContent = previewText(m.sender, m.text);
    conversationList.prepend(item);

    const waiting = m.clientId && pending.get(m.clientId);
    if (waiting) {
        pending.delete(m.clientId);
        confirmPending(waiting, m);
        return;
    }

    if (other === activeUsername) {
        addMessage(m.sender, m.text, m.id, m.sentAt);
        if (m.sender !== currentUser) {
            typingUser = null;
            renderStatus();
            connection.invoke("MarkRead", other).catch(() => {});
        }
    } else if (m.sender !== currentUser) {
        setUnread(item, parseInt(item.dataset.unread || "0", 10) + 1);
    }
});

connection.on("MessageEdited", (m) => {
    const el = messageContainer.querySelector(`.message-wrapper[data-id="${m.id}"]`);
    if (el && !el.classList.contains("deleted")) {
        el.querySelector(".message-text").textContent = m.text;
        el.querySelector(".message-edited").hidden = false;
    }
    if (m.isLast)
        setPreview(m.sender === currentUser ? m.recipient : m.sender, previewText(m.sender, m.text));
});

connection.on("MessageDeleted", (m) => {
    const el = messageContainer.querySelector(`.message-wrapper[data-id="${m.id}"]`);
    if (el) markDeleted(el);
    if (m.isLast)
        setPreview(m.sender === currentUser ? m.recipient : m.sender, "This message was deleted");
});

sendButton.addEventListener("click", sendCurrentMessage);
messageInput.addEventListener("keydown", e => {
    if (e.key === "Enter" && !e.isComposing) {
        e.preventDefault();
        sendCurrentMessage();
    }
});
//start signalR
connection.start()
    .then(() => {
        console.log("SignalR connection established");
        setMyStatus("online", "Online");
    })
    .catch(err => {
        console.error("SignalR connection failed:", err);
        setMyStatus("offline", "Offline");
    });

connection.onreconnecting(() => setMyStatus("connecting", "Reconnecting…"));
connection.onreconnected(() => setMyStatus("online", "Online"));
connection.onclose(() => setMyStatus("offline", "Offline"));
// Heartbeat: server treats 90s of silence as a dead connection
function sendHeartbeat() {
    if (connection.state === signalR.HubConnectionState.Connected)
        connection.invoke("Ping").catch(() => {});
}
setInterval(sendHeartbeat, 25000);
document.addEventListener("visibilitychange", () => { if (!document.hidden) sendHeartbeat(); });

// Keep "Last seen 5 min ago" fresh
setInterval(renderStatus, 60000);

connection.onreconnecting(() => { sendButton.disabled = true; });
connection.onreconnected(() => { sendButton.disabled = !activeUsername; });
