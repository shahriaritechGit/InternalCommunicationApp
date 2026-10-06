/* wwwroot/js/chat-ui.js
   Presentation-only helpers. Does not touch SignalR or message logic (chat.js owns that).
   - Mobile: switches between the conversation list and the chat pane
   - Skeleton loaders for the message area and the conversation list
   - Keeps aria-current in sync for screen readers
   Public API: window.ChatUI.setMessagesLoading(bool), setListLoading(bool), showView('list'|'chat') */
(() => {
  'use strict';

  const app = document.querySelector('[data-chat-app]');
  if (!app) return;

  const list = app.querySelector('.conversation-list');
  const messages = document.getElementById('messageContainer');
  const messageSkeleton = document.getElementById('messageSkeleton');
  const listSkeleton = document.getElementById('conversationSkeleton');
  const title = document.getElementById('conversationUsername');
  const backButton = app.querySelector('[data-chat-back]');

  const SETTLE_MS = 150;     // wait for chat.js to finish rendering
  const FALLBACK_MS = 10000;  // never leave the skeleton up forever
  let settleTimer = 0;
  let fallbackTimer = 0;

  function showView(view) {
    app.dataset.view = view;
  }

  function setMessagesLoading(isLoading) {
    clearTimeout(settleTimer);
    clearTimeout(fallbackTimer);
    messageSkeleton.hidden = !isLoading;
    messages.setAttribute('aria-busy', String(isLoading));
    if (isLoading) {
      fallbackTimer = setTimeout(() => setMessagesLoading(false), FALLBACK_MS);
    }
  }

  function setListLoading(isLoading) {
    listSkeleton.hidden = !isLoading;
    list.setAttribute('aria-busy', String(isLoading));
    list.querySelectorAll('.conversation-item').forEach((el) => { el.hidden = isLoading; });
  }

//   Hide the skeleton shortly after chat.js stops changing the message list
//   new MutationObserver(() => {
//     if (messageSkeleton.hidden) return;
//     clearTimeout(settleTimer);
//     settleTimer = setTimeout(() => setMessagesLoading(false), SETTLE_MS);
//   }).observe(messages, { childList: true, subtree: true });

  list.addEventListener('click', (event) => {
    const item = event.target.closest('.conversation-item');
    if (!item) return;

    list.querySelectorAll('.conversation-item[aria-current]')
        .forEach((el) => el.removeAttribute('aria-current'));
    item.setAttribute('aria-current', 'true');

    // The skeleton is NOT shown automatically: chat.js loads no history on click.
    // Call ChatUI.setMessagesLoading(true/false) around your history fetch instead.
    showView('chat');
    requestAnimationFrame(() => title.focus({ preventScroll: true }));
  });

  if (backButton) {
    backButton.addEventListener('click', () => {
      showView('list');
      const current = list.querySelector('[aria-current="true"]') || list.querySelector('.conversation-item');
      if (current) current.focus({ preventScroll: true });
    });
  }

  window.ChatUI = { setMessagesLoading, setListLoading, showView };
})();