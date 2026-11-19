"use strict";

const modes = {
  generate: {
    title: "Генерация кода",
    description: "Попроси модель написать метод или объяснить решение.",
    emptyTitle: "Начни с задачи на C#",
    label: "Что нужно написать?",
    placeholder: "Например, напиши метод C# для поиска максимума.",
    example: "Напиши метод C# для поиска максимального элемента в массиве. Обработай пустой массив.",
    action: "Сгенерировать"
  },
  chat: {
    title: "Чат с помощником C#",
    description: "Помощник учитывает три последних обмена сообщениями.",
    emptyTitle: "Задай вопрос о C#",
    label: "Сообщение",
    placeholder: "Задай вопрос о C# или уточни предыдущий ответ.",
    example: "Напиши на C# метод FindPeak для поиска максимума в массиве.",
    action: "Отправить"
  },
  review: {
    title: "Ревью кода",
    description: "Модель объяснит ошибки в коде и предложит исправления.",
    emptyTitle: "Проверь метод или класс",
    label: "Код для проверки",
    placeholder: "Вставь сюда метод или класс C#.",
    example: `public static int FindMax(int[] numbers)
{
    int max = 0;
    foreach (var number in numbers)
    {
        if (number > max)
            max = number;
    }
    return max;
}`,
    action: "Проверить код"
  },
  pipeline: {
    title: "Конвейер агентов",
    description: "Помощник пишет код, затем ревьюер проверяет получившийся результат.",
    emptyTitle: "Сгенерируй код и проверь его",
    label: "Задача для генерации",
    placeholder: "Опиши, какой код должен написать помощник.",
    example: "Напиши рекурсивный метод C# для вычисления n-го числа Фибоначчи.",
    action: "Запустить конвейер"
  }
};

const byId = id => document.getElementById(id);
const form = byId("request");
const prompt = byId("prompt");
const model = byId("model");
const reviewModel = byId("review-model");
const run = byId("run");
const stop = byId("stop");
const refresh = byId("refresh");
const example = byId("example");
const emptyExample = byId("empty-example");
const clearChat = byId("clear-chat");
const jumpLatest = byId("jump-latest");
const results = byId("messages");
const outputBody = byId("output-body");
const modeButtons = [...document.querySelectorAll("[data-mode]")];

const views = Object.fromEntries(Object.keys(modes).map(name => [name, {
  draft: "", model: "", reviewModel: "", messages: [], status: "", error: "", scrollTop: 0
}]));

let mode = "generate";
let models = [];
let developer = "";
let reviewer = "";
let loadingModels = false;
let currentRun = null;
const chatHistory = [];

function fillOptions(select, saved, preferred) {
  if (models.length === 0) {
    select.replaceChildren(new Option(loadingModels ? "Загрузка моделей…" : "Модели недоступны", ""));
    return;
  }
  select.replaceChildren(...models.map(name => new Option(name, name)));
  select.value = [saved, preferred, developer, reviewer].find(name => models.includes(name)) ?? models[0];
}

function showModels() {
  const view = views[mode];
  fillOptions(model, view.model, mode === "review" ? reviewer : developer);
  fillOptions(reviewModel, view.reviewModel, reviewer);
}

function updateControls() {
  const running = currentRun !== null;
  const modelUnavailable = loadingModels || models.length === 0;
  run.disabled = running || modelUnavailable;
  run.hidden = running;
  model.disabled = running || modelUnavailable;
  reviewModel.disabled = running || modelUnavailable;
  refresh.disabled = running || loadingModels;
  stop.hidden = !running;
  stop.disabled = currentRun?.signal.aborted ?? false;
  prompt.readOnly = running;
  example.disabled = running;
  example.hidden = views[mode].messages.length === 0;
  emptyExample.disabled = running;
  byId("stream").disabled = running;
  clearChat.disabled = running || views[mode].messages.length === 0;
  clearChat.hidden = views[mode].messages.length === 0;
  modeButtons.forEach(button => { button.disabled = running; });
}

function showNotice(element, message) {
  element.textContent = message;
  element.hidden = !message;
}

function showOutput() {
  const view = views[mode];
  results.replaceChildren(...view.messages);
  byId("empty-output").hidden = view.messages.length > 0;
  outputBody.scrollTop = view.scrollTop;
  updateJump();
  showRunStatus();
}

function showRunStatus() {
  byId("run-status").textContent = views[mode].status;
  showNotice(byId("run-error"), views[mode].error);
}

function showMode() {
  const definition = modes[mode];
  modeButtons.forEach(button => button.setAttribute("aria-pressed", String(button.dataset.mode === mode)));
  byId("mode-title").textContent = definition.title;
  byId("mode-description").textContent = definition.description;
  byId("empty-title").textContent = definition.emptyTitle;
  byId("prompt-label").textContent = definition.label;
  byId("model-label").textContent = mode === "pipeline" ? "Модель для генерации" : "Модель";
  byId("review-model-field").hidden = mode !== "pipeline";
  byId("stream-field").hidden = mode !== "generate";
  prompt.value = views[mode].draft;
  prompt.placeholder = definition.placeholder;
  prompt.classList.toggle("code", mode === "review");
  sizePrompt();
  run.textContent = definition.action;
  showModels();
  showOutput();
  updateControls();
}

async function loadModels() {
  const status = byId("connection-status");
  const connection = status.parentElement;
  const address = byId("server-address");
  const notice = byId("connection-error");
  loadingModels = true;
  status.textContent = "Подключение к Ollama";
  connection.dataset.state = "pending";
  showNotice(notice, "");
  updateControls();

  try {
    const response = await fetch("/lab/models", { cache: "no-store" });
    const data = await response.json();
    address.textContent = data.address ?? "";
    if (!response.ok)
      throw new Error(data.error ?? "Не удалось получить модели Ollama.");

    models = data.models;
    developer = data.developer;
    reviewer = data.reviewer;
    status.textContent = models.length ? "Ollama подключён" : "Модели отсутствуют";
    connection.dataset.state = "online";
    if (models.length === 0)
      showNotice(notice, `Загрузи модель командой ollama pull ${reviewer} и обнови список.`);
  } catch (error) {
    models = [];
    status.textContent = "Нет подключения";
    connection.dataset.state = "offline";
    showNotice(notice, error.message);
  } finally {
    loadingModels = false;
    showModels();
    updateControls();
  }
}

function addMessage(label, text, format = "plain", modelName = "") {
  const follow = format === "plain" || isNearBottom();
  const message = document.createElement("article");
  message.className = "message";
  if (format === "plain") {
    message.classList.add("message-user");
    if (mode === "review")
      message.classList.add("review-input");
  }
  const header = document.createElement("header");
  header.className = "message-header";
  const title = document.createElement("h3");
  title.className = "message-title";
  title.textContent = label;
  header.append(title);
  if (modelName) {
    const modelTag = document.createElement("span");
    modelTag.className = "message-model";
    modelTag.textContent = modelName;
    header.append(modelTag);
  }
  const content = document.createElement("div");
  content.className = format === "markdown" ? "message-markdown" : "message-plain";
  content.textContent = text;
  message.append(header, content);
  views[mode].messages.push(message);
  byId("empty-output").hidden = true;
  results.append(message);
  if (follow)
    outputBody.scrollTop = outputBody.scrollHeight;
  updateJump();
  return content;
}

function isNearBottom() {
  return outputBody.scrollHeight - outputBody.scrollTop - outputBody.clientHeight < 28;
}

function updateJump() {
  jumpLatest.hidden = views[mode].messages.length === 0 || isNearBottom();
}

async function readEvents(response, onEvent) {
  if (!response.ok) {
    const data = await response.json().catch(() => ({}));
    throw new Error(data.error ?? `Ошибка приложения (HTTP ${response.status}).`);
  }
  if (!response.body)
    throw new Error("Сервер вернул пустой ответ.");

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let pending = "";
  let finished = false;
  const handleLine = line => {
    if (line.trim()) {
      const event = JSON.parse(line);
      if (event.type === "error")
        throw new Error(event.text);
      if (event.type === "done")
        finished = true;
      else
        onEvent(event);
    }
  };

  try {
    while (true) {
      const { value, done } = await reader.read();
      if (done)
        break;
      pending += decoder.decode(value, { stream: true });
      const lines = pending.split("\n");
      pending = lines.pop();
      lines.forEach(handleLine);
    }
    handleLine(pending + decoder.decode());
    if (!finished)
      throw new Error("Соединение прервалось до окончания ответа.");
  } finally {
    await reader.cancel().catch(() => {});
    reader.releaseLock();
  }
}

modeButtons.forEach(button => button.addEventListener("click", () => {
  if (mode === button.dataset.mode)
    return;
  views[mode].draft = prompt.value;
  views[mode].scrollTop = outputBody.scrollTop;
  mode = button.dataset.mode;
  showMode();
}));
model.addEventListener("change", () => { views[mode].model = model.value; });
reviewModel.addEventListener("change", () => { views[mode].reviewModel = reviewModel.value; });
refresh.addEventListener("click", loadModels);
function fillExample() {
  prompt.value = modes[mode].example;
  sizePrompt();
  prompt.focus();
}
example.addEventListener("click", fillExample);
emptyExample.addEventListener("click", fillExample);
outputBody.addEventListener("scroll", updateJump, { passive: true });
window.addEventListener("resize", updateJump);
jumpLatest.addEventListener("click", () => {
  outputBody.focus({ preventScroll: true });
  outputBody.scrollTop = outputBody.scrollHeight;
  updateJump();
});
function sizePrompt() {
  const follow = isNearBottom();
  prompt.style.height = "";
  if (prompt.value)
    prompt.style.height = (prompt.scrollHeight + prompt.offsetHeight - prompt.clientHeight) + "px";
  if (follow)
    outputBody.scrollTop = outputBody.scrollHeight;
  updateJump();
}
prompt.addEventListener("input", sizePrompt);
prompt.addEventListener("keydown", event => {
  if (event.key === "Enter" && (event.ctrlKey || event.metaKey) && !event.isComposing && !run.disabled) {
    event.preventDefault();
    form.requestSubmit(run);
  }
});
clearChat.addEventListener("click", () => {
  if (mode === "chat")
    chatHistory.length = 0;
  const view = views[mode];
  view.messages.length = 0;
  view.scrollTop = 0;
  view.status = "История очищена.";
  view.error = "";
  showOutput();
  updateControls();
  prompt.focus();
});
stop.addEventListener("click", () => {
  currentRun?.abort();
  stop.disabled = true;
});

form.addEventListener("submit", async event => {
  event.preventDefault();
  if (currentRun || loadingModels || !model.value)
    return;

  const input = prompt.value;
  const view = views[mode];
  view.error = "";
  if (!input.trim()) {
    view.error = "Введи запрос или вставь пример.";
    showRunStatus();
    prompt.focus();
    return;
  }

  view.status = "Ожидаем ответ модели…";
  showRunStatus();
  addMessage(mode === "review" ? "Твой код" : "Ты", input);
  prompt.value = "";
  sizePrompt();

  const controller = new AbortController();
  currentRun = controller;
  updateControls();

  const labels = { answer: "Ответ модели", draft: "Генерация", review: "Ревью" };
  let activeOutput = null;
  let renderFrame = 0;

  function drawOutput(showCopy) {
    cancelAnimationFrame(renderFrame);
    renderFrame = 0;
    if (!activeOutput)
      return;

    const atBottom = isNearBottom();
    markdownOutput.render(activeOutput.element, activeOutput.source, showCopy);
    if (atBottom)
      outputBody.scrollTop = outputBody.scrollHeight;
    updateJump();
  }

  try {
    const response = await fetch("/lab/run", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        mode, prompt: input, model: model.value,
        reviewModel: mode === "pipeline" ? reviewModel.value : null,
        stream: byId("stream").checked,
        history: mode === "chat" ? chatHistory.slice(-3) : null
      }),
      signal: controller.signal
    });

    await readEvents(response, message => {
      if (message.type !== "chunk")
        return;
      if (activeOutput?.stage !== message.stage) {
        drawOutput(true);
        const label = mode === "review" ? "Ревью" : labels[message.stage];
        const modelName = message.stage === "review" ? reviewModel.value : model.value;
        activeOutput = { stage: message.stage, element: addMessage(label, "", "markdown", modelName), source: "" };
        view.status = message.stage === "review" ? "Ревьюер проверяет код…" : "Модель отвечает…";
        showRunStatus();
      }
      activeOutput.source += message.text;
      if (message.text && renderFrame === 0)
        renderFrame = requestAnimationFrame(() => drawOutput(false));
    });

    if (mode === "chat") {
      chatHistory.push({ user: input, answer: activeOutput.source });
      if (chatHistory.length > 3)
        chatHistory.shift();
    }
    view.status = "Готово.";
  } catch (error) {
    prompt.value = input;
    sizePrompt();
    view.status = controller.signal.aborted ? "Генерация остановлена." : "";
    if (!controller.signal.aborted)
      view.error = error.message;
  } finally {
    drawOutput(true);
    currentRun = null;
    showRunStatus();
    updateControls();
    if (controller.signal.aborted)
      run.focus();
    else if (document.activeElement === document.body)
      prompt.focus();
  }
});

showMode();
loadModels();
