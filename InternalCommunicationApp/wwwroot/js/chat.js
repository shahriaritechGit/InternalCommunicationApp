"use strict";

console.log("chat.js loaded");
const username = prompt("Enter your username:");

const connection = new signalR.HubConnectionBuilder()
    .withUrl("/chathub?username="+ encodeURIComponent(username), {
        transport: signalR.HttpTransportType.WebSockets
    })
    .build();

connection.on("ReceiveMessage", (sender, message) => {
    console.log("MESSAGE RECEIVED:");
    console.log(`${sender}: ${message}`);
});

connection.start()
    .then(() => {
        console.log(`SignalR connected! Connected as ${username}`);
    })
    .catch(err => {
        console.error("SignalR connection failed:", err);
    });

document.getElementById("sendButton").addEventListener("click", async () => {

    console.log("Send button clicked");

    const recipient = document.getElementById("recipientInput").value;
    const message = document.getElementById("messageInput").value;

    console.log("Recipient:", recipient);
    console.log("Message:", message);

    try {
        await connection.invoke("SendMessage", recipient, message);
        console.log(`Message sent to ${recipient}`);
    }
    catch (err) {
        console.error("SendMessage failed:", err);
    }
});