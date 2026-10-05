"use strict";

console.log("chat.js loaded");

const connection = new signalR.HubConnectionBuilder()
    .withUrl("/chathub", {
        transport: signalR.HttpTransportType.WebSockets
    })
    .build();
const currentUser = document.querySelector(".chat-user span").textContent.trim();
const messageContainer = document.getElementById("messageContainer");
const messageInput = document.getElementById("messageInput");
const sendButton = document.getElementById("sendButton");
const recipientInput = document.getElementById("recipientInput");
const conversations = document.querySelectorAll(".conversation-item");
const conversationUsername = document.getElementById("conversationUsername");
const conversationAvatar = document.getElementById("conversationAvatar");

conversations.forEach(conversation => {

    conversation.addEventListener("click", () => {

        const username =
            conversation.dataset.username;

        if (!username) {
            return;
        }

        recipientInput.value = username;

        conversations.forEach(item => {
            item.classList.remove("active");
        });

        conversation.classList.add("active");

        conversationUsername.textContent =
            username;

        conversationAvatar.textContent =
            username.charAt(0).toUpperCase();

        console.log(
            `Conversation selected: ${username}`
        );

    });

});
//siganlR
connection.on("ConnectedAs", (username) => {
    console.log(`SignalR connected as ${username}`);
});
connection.on("ReceiveMessage", (sender, message) => {

    console.log("MESSAGE RECEIVED:");
    console.log(`${sender}: ${message}`);

    console.log("Current user:", currentUser);
    console.log("Message container:", messageContainer);

    addMessage(sender, message);

});
//message to ui
function addMessage(sender, message) {
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
}
//send message
sendButton.addEventListener("click", async () => {
    const recipient = recipientInput.value.trim();
    const message = messageInput.value.trim();
    if(!message){
        return;
    }
    console.log("send button clicked");
    console.log("recipient:", recipient);
    console.log("message:", message);
    try{
        await connection.invoke("SendMessage", recipient, message);
        addMessage(currentUser, message);
        messageInput.value = "";
        messageInput.focus();
        console.log(`Message sent to ${recipient}`);
    }
    catch (err) {
        console.error("SendMessage failed:", err);
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

