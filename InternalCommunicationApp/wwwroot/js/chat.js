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
        history.forEach(m => addMessage(m.sender, m.text, m.id));
    } catch (err) {
        console.error(err);
    } finally {
        if (activeUsername === username) window.ChatUI?.setMessagesLoading(false);
    }
}
//message to ui
function addMessage(sender, message, id) {
    if (id != null) {
        if (rendered.has(id)) return;
        rendered.add(id);
    }
    const emptyChat = messageContainer.querySelector(".empty-chat");
    if (emptyChat) {
        emptyChat.remove();
    }
    const messageWrapper = document.createElement("div");
    messageWrapper.classList.add("message-wrapper");
    if (sender === currentUser) {
        messageWrapper.classList.add("sent");
    }else{
        messageWrapper.classList.add("received");
    }
    const messageBubble = document.createElement("div");
    messageBubble.classList.add("message-bubble");
    if(sender !== currentUser){
        const senderName = document.createElement("div");
        senderName.classList.add("message-sender");
        senderName.textContent = sender;
        messageBubble.appendChild(senderName);
    }
    const messageText = document.createElement("div");
    messageText.classList.add("message-text");
    messageText.textContent = message;
    messageBubble.appendChild(messageText);
    messageWrapper.appendChild(messageBubble);
    messageContainer.appendChild(messageWrapper);
    messageContainer.scrollTop = messageContainer.scrollHeight;
    return messageWrapper;
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

    searchTimer = setTimeout(async () => {
        const res = await fetch(
            `${location.pathname}?handler=Users&q=${encodeURIComponent(q)}`,
            { headers: { Accept: "application/json" } });
        if (!res.ok || userSearch.value.trim() !== q) return;   // ignore stale responses
        const names = await res.json();

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
        userResults.hidden = names.length === 0;
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
    item.querySelector(".conversation-info span").textContent = m.text;
    conversationList.prepend(item);

    const waiting = m.clientId && pending.get(m.clientId);
    if (waiting) {
        pending.delete(m.clientId);
        waiting.classList.remove("pending");
        rendered.add(m.id);
        return;
    }

    if (other === activeUsername) {
        addMessage(m.sender, m.text, m.id);
        if (m.sender !== currentUser) {
            typingUser = null;
            renderStatus();
            connection.invoke("MarkRead", other).catch(() => {});   // chat is open, so it's read
        }
    } else if (m.sender !== currentUser) {
        setUnread(item, parseInt(item.dataset.unread || "0", 10) + 1);
    }
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
    })
    .catch(err => {
        console.error("SignalR connection failed:", err);
    });
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
