function refresh() {
  chrome.runtime.sendMessage({type:"status"}).then(info => {
    document.getElementById("status").textContent = info.connected
      ? (info.enabled ? "Подключено к Synvaldi. " + info.mode : "Подключено к Synvaldi. Выбери профиль в приложении.")
      : "Ожидает локальное приложение Synvaldi.";
  }).catch(() => { document.getElementById("status").textContent = "Фоновый процесс расширения недоступен."; });
}
refresh();
