const { ipcRenderer, shell, webUtils } = require("electron");
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");
const { pathToFileURL } = require("url");

const channel = new URLSearchParams(location.search).get("channel");
const app = document.getElementById("app");
const petStrip = document.getElementById("petStrip");
const titlebar = document.getElementById("titlebar");
const composer = document.getElementById("composer");
const minimizeButton = document.getElementById("minimizeButton");
const clearButton = document.getElementById("clearButton");
const confirmOverlay = document.getElementById("confirmOverlay");
const confirmOk = document.getElementById("confirmOk");
const confirmCancel = document.getElementById("confirmCancel");
const compactPetName = document.getElementById("compactPetName");
const messageList = document.getElementById("messageList");
const input = document.getElementById("input");
const sendButton = document.getElementById("sendButton");
const restoreButton = document.getElementById("restoreButton");
const attachButton = document.getElementById("attachButton");
const imageInput = document.getElementById("imageInput");
const imagePreview = document.getElementById("imagePreview");
const screenshotButton = document.getElementById("screenshotButton");
const regionScreenshotButton = document.getElementById("regionScreenshotButton");
let screenshotBusy = false;

const state = {
  pets: [],
  selectedId: null,
  maxVisibleMessages: 6,
  loadedMessageCount: 6,
  showAllMessages: false,
  loadingOlderMessages: false,
  hideOnEscape: true,
  compact: false,
  autoHeight: true,
  minWindowHeight: 96,
  maxWindowHeight: 460,
  manualSizeOverride: false
};

const messagesByPet = new Map();
let lastStateSignature = "";
let lastPetSignature = "";
let lastMessageSignature = "";
let lastComposerSignature = "";

let manualResizeEdge = null;
let resizeDebounceTimer = null;
let lastSentResizeSignature = "";
let lastInputHeight = 0;
let pendingResizeMove = null;
let resizeMoveScheduled = false;


const pendingImages = [];
const quickChatImageDirectory = path.join(__dirname, "..", "..", "Temp", "Images");

// 快聊记录不持久化，重启后旧临时文件没有任何引用者，启动时直接清空。
// 语义 = "Alife 退出/重启清理"（默认开）。窗口每次会话只创建一次，因此每次会话只清一遍。
function cleanupTemporaryImages() {
  try {
    if (fs.existsSync(quickChatImageDirectory) === false)
      return;
    for (const entry of fs.readdirSync(quickChatImageDirectory)) {
      try {
        fs.unlinkSync(path.join(quickChatImageDirectory, entry));
      } catch {
        // 单个文件删除失败不影响其余清理。
      }
    }
  } catch {
    // 清理失败不影响启动。
  }
}
cleanupTemporaryImages();

function sendResizeMove() {
  if (manualResizeEdge == null)
    return;

  sendMessage("resize-move");
}


function sendMessage(type, payload = {}) {
  if (!channel)
    return;
  ipcRenderer.send(channel, JSON.stringify({ type, ...payload }));
}

function hashText(text) {
  let hash = 0;
  const value = String(text || "");
  for (let index = 0; index < value.length; index++)
    hash = ((hash * 31) + value.charCodeAt(index)) | 0;
  return Math.abs(hash);
}

function colorFor(text) {
  return `hsl(${hashText(text) % 360}, 68%, 71%)`;
}

function clampNumber(value, min, max, fallback) {
  const number = Number(value);
  if (!Number.isFinite(number))
    return fallback;
  return Math.min(max, Math.max(min, number));
}

function hexToRgba(color, opacity, fallback = "rgba(0, 0, 0, 0)") {
  let value = String(color ?? "").trim();
  if (!value.startsWith("#"))
    value = `#${value}`;
  if (/^#[0-9a-f]{3}$/i.test(value))
    value = `#${value[1]}${value[1]}${value[2]}${value[2]}${value[3]}${value[3]}`;
  if (!/^#[0-9a-f]{6}$/i.test(value))
    return fallback;

  const red = parseInt(value.slice(1, 3), 16);
  const green = parseInt(value.slice(3, 5), 16);
  const blue = parseInt(value.slice(5, 7), 16);
  return `rgba(${red}, ${green}, ${blue}, ${clampNumber(opacity, 0, 1, 1)})`;
}

function applyTheme(theme = {}) {
  const root = document.documentElement.style;
  root.setProperty("--panel-color", hexToRgba(theme.panelColor, theme.panelOpacity, "rgba(17, 20, 28, 0.55)"));
  root.setProperty("--drop-blur", clampNumber(theme.dragBackdropBlur, 0, 80, 42) + "px");
  root.setProperty("--message-list-color", hexToRgba(theme.messageListColor, theme.messageListOpacity, "rgba(0, 0, 0, 0)"));
  root.setProperty("--assistant-bubble-color", hexToRgba(theme.assistantBubbleColor, theme.assistantBubbleOpacity, "rgba(255, 255, 255, 0.10)"));
  root.setProperty("--user-bubble-color", hexToRgba(theme.userBubbleColor, theme.userBubbleOpacity, "rgba(85, 130, 245, 0.25)"));
  root.setProperty("--input-color", hexToRgba(theme.inputColor, theme.inputOpacity, "rgba(255, 255, 255, 0.085)"));
  root.setProperty("--text-color", String(theme.textColor || "#EEF1F6"));
}


function ensureQuickChatImageDirectory() {
  fs.mkdirSync(quickChatImageDirectory, { recursive: true });
  return quickChatImageDirectory;
}

function normalizeLocalPath(value) {
  return path.resolve(String(value || ""));
}

function isImagePath(value) {
  return /^([A-Za-z]:[\\/]|\\\\)[^\n\r<>|*?"']+\.(png|jpe?g|gif|webp|bmp)$/i.test(String(value || "").trim());
}

function isAttachmentPathLine(value) {
  const text = String(value || "").trim();
  if (!text)
    return false;
  if (/^用户(?:发送了|文字)/.test(text))
    return false;
  return true;
}

function pathToFileUrl(value) {
  try {
    return pathToFileURL(normalizeLocalPath(value)).href;
  } catch {
    return "";
  }
}

function sha1(value) {
  return crypto.createHash("sha1").update(String(value)).digest("hex");
}

function thumbnailPathForSource(sourcePath) {
  return path.join(ensureQuickChatImageDirectory(), "thumb-" + sha1(normalizeLocalPath(sourcePath)).slice(0, 16) + ".jpg");
}

function imageExtension(file) {
  const extension = path.extname(file.name || "");
  if (extension)
    return extension.toLowerCase();
  const type = String(file.type || "");
  if (type.startsWith("image/")) {
    const subtype = type.split("/")[1] || "png";
    return subtype === "jpeg" ? ".jpg" : "." + subtype;
  }
  const subtype = type.split("/")[1] || "";
  const cleaned = subtype.split(";")[0].replace(/[^a-z0-9]/gi, "");
  return cleaned ? "." + cleaned : ".bin";
}

function getElectronFilePath(file) {
  if (file.path)
    return normalizeLocalPath(file.path);
  try {
    if (webUtils && typeof webUtils.getPathForFile === "function")
      return normalizeLocalPath(webUtils.getPathForFile(file));
  } catch {
    // Clipboard screenshots do not always expose a source path.
  }
  return "";
}

async function saveTemporaryImage(file) {
  ensureQuickChatImageDirectory();
  const fileName = "file-" + Date.now() + "-" + Math.random().toString(36).slice(2, 8) + imageExtension(file);
  const filePath = path.join(quickChatImageDirectory, fileName);
  const buffer = Buffer.from(await file.arrayBuffer());
  fs.writeFileSync(filePath, buffer);
  return normalizeLocalPath(filePath);
}

async function createThumbnail(file, sourcePath) {
  const thumbnailPath = thumbnailPathForSource(sourcePath);
  try {
    const buffer = getElectronFilePath(file)
      ? fs.readFileSync(getElectronFilePath(file))
      : Buffer.from(await file.arrayBuffer());
    const blob = new Blob([buffer], { type: file.type || "image/png" });
    const bitmap = await createImageBitmap(blob);
    const maxSize = 320;
    const scale = Math.min(1, maxSize / Math.max(bitmap.width, bitmap.height, 1));
    const width = Math.max(1, Math.round(bitmap.width * scale));
    const height = Math.max(1, Math.round(bitmap.height * scale));
    const canvas = document.createElement("canvas");
    canvas.width = width;
    canvas.height = height;
    const context = canvas.getContext("2d");
    context.drawImage(bitmap, 0, 0, width, height);
    const dataUrl = canvas.toDataURL(file.type === "image/png" ? "image/png" : "image/jpeg", 0.86);
    fs.writeFileSync(thumbnailPath, Buffer.from(dataUrl.split(",")[1], "base64"));
    bitmap.close?.();
    return normalizeLocalPath(thumbnailPath);
  } catch {
    try {
      if (fs.existsSync(thumbnailPath) === false)
        fs.copyFileSync(sourcePath, thumbnailPath);
    } catch {
      // Thumbnail creation is best-effort; the original path is still sent.
    }
    return normalizeLocalPath(thumbnailPath);
  }
}

async function addPendingImage(file, preferTemporary = false) {
  if (!file)
    return;

  const isImageFile = String(file.type || "").startsWith("image/");
  let filePath = preferTemporary ? "" : getElectronFilePath(file);
  if (!filePath)
    filePath = await saveTemporaryImage(file);

  let thumbnailPath = null;
  let previewUrl = "";
  if (isImageFile) {
    thumbnailPath = await createThumbnail(file, filePath);
    previewUrl = pathToFileUrl(thumbnailPath || filePath);
  }

  pendingImages.push({
    id: Date.now() + "-" + Math.random().toString(36).slice(2, 8),
    kind: isImageFile ? "image" : "file",
    name: file.name || path.basename(filePath),
    path: filePath,
    thumbnailPath,
    previewUrl
  });
  renderImagePreview();
  requestResize();
}

function removePendingImage(id) {
  const index = pendingImages.findIndex(image => image.id === id);
  if (index >= 0)
    pendingImages.splice(index, 1);
  renderImagePreview();
  requestResize();
}

function renderImagePreview() {
  if (!imagePreview)
    return;
  imagePreview.replaceChildren();
  imagePreview.classList.toggle("has-images", pendingImages.length > 0);
  if (pendingImages.length === 0)
    return;

  for (const image of pendingImages) {
    const item = document.createElement("div");
    item.className = image.kind === "file" ? "pending-image pending-file" : "pending-image";

    if (image.kind === "file") {
      const label = document.createElement("span");
      label.className = "pending-file-name";
      label.textContent = image.name;
      item.title = image.name;
      item.appendChild(label);
    } else {
      const img = document.createElement("img");
      img.src = image.previewUrl || pathToFileUrl(image.thumbnailPath || image.path);
      img.alt = image.name;
      item.appendChild(img);
    }

    const removeButton = document.createElement("button");
    removeButton.type = "button";
    removeButton.className = "pending-image-remove";
    removeButton.title = "移除";
    removeButton.textContent = "×";
    removeButton.addEventListener("click", event => {
      event.stopPropagation();
      removePendingImage(image.id);
    });
    item.appendChild(removeButton);
    imagePreview.appendChild(item);
  }
}



function setImageDragActive(active) {
  app.classList.toggle("drag-over", active);
  app.classList.toggle("drop-blur", active);
}

function isFileDragEvent(event) {
  return Array.from(event.dataTransfer?.types || []).includes("Files");
}
function filesFromDataTransfer(dataTransfer) {
  return Array.from(dataTransfer?.files || []).filter(file => file != null);
}

async function addDroppedImages(files) {
  for (const file of files)
    await addPendingImage(file, false);
}

function buildOutgoingText(text) {
  const images = pendingImages.filter(item => item.kind !== "file");
  const files = pendingImages.filter(item => item.kind === "file");
  if (images.length === 0 && files.length === 0)
    return text;

  const lines = [];
  if (images.length > 0) {
    const countText = images.length === 1 ? "一张" : images.length + "张";
    lines.push("用户发送了" + countText + "图片：");
    for (const image of images)
      lines.push(image.path);
  }
  if (files.length > 0) {
    if (lines.length > 0)
      lines.push("");
    const countText = files.length === 1 ? "一个" : files.length + "个";
    lines.push("用户发送了" + countText + "文件：");
    for (const file of files)
      lines.push(file.path);
  }
  lines.push("", "用户文字：");
  if (text.length > 0)
    lines.push(text);
  return lines.join("\n");
}

async function sendDroppedImagesNow(files) {
  await addDroppedImages(files);
  const pet = getSelectedPet();
  if (!pet || pendingImages.length === 0)
    return;

  sendMessage("send", { petId: pet.id, text: buildOutgoingText(""), attachmentPaths: pendingImages.map(item => item.path) });
  pendingImages.length = 0;
  renderImagePreview();
  requestResize();
}

function formatScreenshotTimestamp(date) {
  const value = date instanceof Date ? date : new Date();
  const pad = number => String(number).padStart(2, "0");
  return `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())} ${pad(value.getHours())}:${pad(value.getMinutes())}:${pad(value.getSeconds())}`;
}

function screenshotFileTimestamp(date) {
  return formatScreenshotTimestamp(date).replace(/:/g, "-");
}

function openImagePath(filePath) {
  if (!filePath || !shell || typeof shell.openPath !== "function")
    return;
  shell.openPath(filePath).catch(error => console.error("QuickChat open image failed", error));
}

function parseAttachmentPaths(block) {
  return String(block || "")
    .split(/\r?\n/)
    .map(line => line.trim())
    .filter(line => isAttachmentPathLine(line));
}

function parseAttachmentMessageText(text) {
  const value = String(text ?? "");
  const images = [];
  const files = [];
  let rest = value;

  const imageBlock = rest.match(/用户发送了(?:一张|\d+\s*张)图片\s*[:：]\s*\n([\s\S]*?)(?=\n\s*用户发送了|\n\s*用户文字\s*[:：]|$)/i);
  if (imageBlock) {
    for (const line of parseAttachmentPaths(imageBlock[1])) {
      if (isImagePath(line))
        images.push({ path: line, thumbnailPath: thumbnailPathForSource(line) });
    }
    rest = rest.replace(imageBlock[0], "");
  }

  const fileBlock = rest.match(/用户发送了(?:一个|\d+\s*个)文件\s*[:：]\s*\n([\s\S]*?)(?=\n\s*用户发送了|\n\s*用户文字\s*[:：]|$)/i);
  if (fileBlock) {
    for (const line of parseAttachmentPaths(fileBlock[1]))
      files.push({ path: line, name: path.basename(line) });
    rest = rest.replace(fileBlock[0], "");
  }

  const userTextMatch = rest.match(/用户文字\s*[:：]\s*([\s\S]*)$/i);
  const hasAttachments = images.length > 0 || files.length > 0;
  const userText = cleanDisplayText(userTextMatch ? userTextMatch[1] : rest);
  return { text: hasAttachments ? userText : cleanDisplayText(value), images, files };
}

function messageImageUrl(image) {
  if (image.thumbnailPath && fs.existsSync(image.thumbnailPath))
    return pathToFileUrl(image.thumbnailPath);
  return pathToFileUrl(image.path);
}

function cleanDisplayText(text) {
  return String(text ?? "")
    .replace(/<\s*(?:think|thinking|reasoning)\b[^>]*>[\s\S]*?<\s*\/\s*(?:think|thinking|reasoning)\s*>/gi, "")
    .replace(/<[^>\n]{0,300}>/g, "")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&nbsp;/g, " ")
    .replace(/&amp;/g, "&")
    .trim();
}

function escapeHtml(value) {
  return String(value ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function sanitizeMarkdownLink(url) {
  const text = String(url || "").trim();
  if (/^(https?|mailto):/i.test(text))
    return text;
  return "";
}

function renderInlineMarkdown(value) {
  let text = String(value ?? "");
  const codes = [];
  text = text.replace(/`([^`\n]+)`/g, (_m, code) => {
    codes.push(code);
    return " " + codes.length + " ";
  });
  text = escapeHtml(text);
  text = text.replace(/!\[([^\]\n]*)\]\(([^)\s]+)\)/g, (_m, label, url) => {
    const safe = sanitizeMarkdownLink(url);
    return safe ? "<a href=\"" + safe + "\" title=\"" + url + "\">" + (escapeHtml(label) || url) + " ↗</a>" : (escapeHtml(label) || url);
  });
  text = text.replace(/\[([^\]\n]+)\]\(([^)\s]+)\)/g, (_m, label, url) => {
    const safe = sanitizeMarkdownLink(url);
    return safe ? "<a href=\"" + safe + "\" title=\"" + url + "\">" + label + "</a>" : label;
  });
  text = text.replace(/\*\*([^*\n]+)\*\*/g, "<strong>$1</strong>");
  text = text.replace(/(^|[^*])\*([^*\n]+)\*/g, "$1<em>$2</em>");
  text = text.replace(/~~([^~\n]+)~~/g, "<del>$1</del>");
  text = text.replace(/ (\d+) /g, (m, index) => {
    const code = codes[Number(index) - 1];
    return code === undefined ? m : "<code>" + escapeHtml(code) + "</code>";
  });
  return text;
}

function renderMarkdown(value) {
  const lines = String(value ?? "").replace(/\r\n?/g, "\n").split("\n");
  const html = [];
  let paragraph = [];
  let listState = null;
  let quoteLines = null;
  let codeBlock = null;
  let tableState = null;

  const flushParagraph = () => {
    if (paragraph.length === 0)
      return;
    html.push("<p>" + paragraph.map(renderInlineMarkdown).join("<br>") + "</p>");
    paragraph = [];
  };
  const flushList = () => {
    if (listState) {
      html.push(listState.ordered ? "</ol>" : "</ul>");
      listState = null;
    }
  };
  const flushQuote = () => {
    if (quoteLines) {
      html.push("<blockquote>" + quoteLines.map(renderInlineMarkdown).join("<br>") + "</blockquote>");
      quoteLines = null;
    }
  };
  const flushCode = () => {
    if (codeBlock) {
      html.push("<pre><code>" + escapeHtml(codeBlock.lines.join("\n")) + "</code></pre>");
      codeBlock = null;
    }
  };
  const splitTableRow = (line) => {
    const trimmed = line.trim().replace(/^\|/, "").replace(/\|\s*$/, "");
    return trimmed.split("|").map(cell => cell.trim());
  };
  const isTableSeparator = (line) => {
    const cells = splitTableRow(line);
    return cells.length > 0 && cells.every(cell => /^:?-{3,}:?$/.test(cell));
  };
  const flushTable = () => {
    if (!tableState)
      return;
    const rows = [tableState.header].concat(tableState.rows);
    let tableHtml = "<table>";
    rows.forEach((cells, rowIndex) => {
      const tag = rowIndex === 0 ? "th" : "td";
      tableHtml += "<tr>";
      for (const cell of cells)
        tableHtml += "<" + tag + ">" + renderInlineMarkdown(cell) + "</" + tag + ">";
      tableHtml += "</tr>";
    });
    tableHtml += "</table>";
    html.push(tableHtml);
    tableState = null;
  };
  const flushAll = () => {
    flushParagraph();
    flushList();
    flushQuote();
    flushCode();
    flushTable();
  };

  for (const line of lines) {
    if (codeBlock) {
      if (/^\s*```\s*$/.test(line))
        flushCode();
      else
        codeBlock.lines.push(line);
      continue;
    }

    const fence = line.match(/^\s*```(\S*)\s*$/);
    if (fence) {
      flushAll();
      codeBlock = { lines: [] };
      continue;
    }

    if (/^\s*$/.test(line)) {
      flushAll();
      continue;
    }

    const heading = line.match(/^(#{1,6})\s+(.+)$/);
    if (heading) {
      flushAll();
      const level = heading[1].length;
      html.push("<h" + level + ">" + renderInlineMarkdown(heading[2]) + "</h" + level + ">");
      continue;
    }

    if (/^\s*(-{3,}|\*{3,}|_{3,})\s*$/.test(line)) {
      flushAll();
      html.push("<hr>");
      continue;
    }

    const unordered = line.match(/^\s*[-*+]\s+(.+)$/);
    const ordered = line.match(/^\s*\d+[.)]\s+(.+)$/);
    if (unordered || ordered) {
      flushParagraph();
      flushQuote();
      flushTable();
      const isOrdered = ordered !== null;
      if (!listState || listState.ordered !== isOrdered) {
        flushList();
        html.push(isOrdered ? "<ol>" : "<ul>");
        listState = { ordered: isOrdered };
      }
      html.push("<li>" + renderInlineMarkdown((unordered || ordered)[1]) + "</li>");
      continue;
    }

    if (/^\s*>/.test(line)) {
      flushParagraph();
      flushList();
      flushTable();
      if (!quoteLines)
        quoteLines = [];
      quoteLines.push(line.replace(/^\s*>\s?/, ""));
      continue;
    }

    if (/^\s*\|/.test(line)) {
      if (isTableSeparator(line))
        continue;
      if (!tableState) {
        flushParagraph();
        flushList();
        flushQuote();
        tableState = { header: splitTableRow(line), rows: [] };
      } else {
        tableState.rows.push(splitTableRow(line));
      }
      continue;
    }

    flushList();
    flushQuote();
    flushTable();
    paragraph.push(line);
  }
  flushAll();
  return html.join("");
}

function getMessages(petId) {
  const key = petId || "";
  if (messagesByPet.has(key) === false)
    messagesByPet.set(key, []);
  return messagesByPet.get(key);
}

function setMessages(petId, messages) {
  messagesByPet.set(petId || "", messages || []);
}

function appendMessage(message) {
  const list = getMessages(message.petId);
  list.push(message);
}

function formatTime(value) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime()))
    return "";
  return `${String(date.getHours()).padStart(2, "0")}:${String(date.getMinutes()).padStart(2, "0")}`;
}

function getSelectedPet() {
  return state.pets.find(pet => pet.id === state.selectedId) || null;
}

function petSignature() {
  return JSON.stringify({
    pets: state.pets.map(pet => [pet.id, pet.name, pet.busy === true]),
    selectedId: state.selectedId
  });
}

function renderPets() {
  const signature = petSignature();
  if (signature === lastPetSignature)
    return;

  lastPetSignature = signature;
  petStrip.replaceChildren();

  if (state.pets.length === 0) {
    const empty = document.createElement("div");
    empty.className = "pet-chip";
    empty.style.opacity = ".5";
    empty.textContent = "没有已激活桌宠";
    petStrip.appendChild(empty);
    return;
  }

  for (const pet of state.pets) {
    const chip = document.createElement("button");
    chip.type = "button";
    chip.className = "pet-chip";
    if (pet.id === state.selectedId)
      chip.classList.add("selected");
    if (pet.busy)
      chip.classList.add("busy");

    chip.style.setProperty("--chip-color", colorFor(pet.name));
    chip.title = pet.busy ? `${pet.name} 正在回复` : pet.name;

    const name = document.createElement("span");
    name.textContent = pet.name;
    chip.appendChild(name);
    chip.addEventListener("click", () => {
      if (pet.id !== state.selectedId)
        sendMessage("select", { id: pet.id });
    });

    petStrip.appendChild(chip);
  }
}

function createMessageElement(message) {
  const bubble = document.createElement("article");
  const role = message.role || "assistant";
  bubble.className = "bubble " + role;

  if (role !== "system") {
    const meta = document.createElement("div");
    meta.className = "message-meta";

    const name = document.createElement("span");
    name.className = "role-name";
    name.style.setProperty("--chip-color", colorFor(role === "user" ? "我" : message.petName));
    name.textContent = role === "user" ? "我" : message.petName || "桌宠";

    const time = document.createElement("span");
    time.className = "bubble-time";
    time.textContent = formatTime(message.createdAt);
    meta.append(name, time);
    bubble.appendChild(meta);
  }

  const parsedMessage = parseAttachmentMessageText(message.text);
  const structuredAttachments = Array.isArray(message.attachments)
    ? message.attachments.filter(item => item && item.path)
    : [];
  const images = [
    ...structuredAttachments.filter(item => item.kind === "image" || (!item.kind && isImagePath(item.path))),
    ...parsedMessage.images
  ];
  const files = [
    ...structuredAttachments.filter(item => item.kind !== "image" && (item.kind || isImagePath(item.path) === false)),
    ...parsedMessage.files
  ];
  const hasAttachments = images.length > 0 || files.length > 0;

  if (images.length > 0) {
    const imageBox = document.createElement("div");
    imageBox.className = "message-images";

    for (const image of images.slice(0, 6)) {
      const imageButton = document.createElement("button");
      imageButton.type = "button";
      imageButton.className = "message-image-button";
      imageButton.title = image.path;
      imageButton.addEventListener("click", event => {
        event.stopPropagation();
        openImagePath(image.path);
      });

      const img = document.createElement("img");
      img.src = messageImageUrl(image);
      img.alt = path.basename(image.path || "image");
      imageButton.appendChild(img);
      imageBox.appendChild(imageButton);
    }
    bubble.appendChild(imageBox);
  }

  if (files.length > 0) {
    const fileBox = document.createElement("div");
    fileBox.className = "message-files";

    for (const file of files.slice(0, 8)) {
      const fileButton = document.createElement("button");
      fileButton.type = "button";
      fileButton.className = "message-file-button";
      fileButton.title = file.path;
      fileButton.addEventListener("click", event => {
        event.stopPropagation();
        openImagePath(file.path);
      });

      const icon = document.createElement("span");
      icon.className = "message-file-icon";
      icon.textContent = "📎";
      const name = document.createElement("span");
      name.className = "message-file-name";
      name.textContent = file.name || path.basename(file.path || "file");
      fileButton.append(icon, name);
      fileBox.appendChild(fileButton);
    }
    bubble.appendChild(fileBox);
  }

  const content = document.createElement("div");
  content.className = "message-content";
  const displayText = hasAttachments ? parsedMessage.text : cleanDisplayText(message.text);
  if (displayText || hasAttachments === false) {
    content.innerHTML = renderMarkdown(displayText || "（无文本内容）");
    bubble.appendChild(content);
  }

  return bubble;
}

function messageSignature(messages) {
  const visibleMessages = state.showAllMessages
    ? messages
    : messages.slice(-state.loadedMessageCount);
  return JSON.stringify({
    petId: state.selectedId,
    compact: state.compact,
    loadedMessageCount: state.loadedMessageCount,
    showAllMessages: state.showAllMessages,
    messages: visibleMessages.map(message => [
      message.id, message.role, message.petId, message.petName,
      message.text, message.createdAt, message.attachments || []
    ])
  });
}

function renderMessages(options = {}) {
  const preserveScroll = options.preserveScroll === true;
  const previousBottomDistance = messageList.scrollHeight - messageList.scrollTop;
  const messages = getMessages(state.selectedId);
  messageList.classList.toggle("hidden", state.compact);

  const signature = messageSignature(messages);
  if (signature === lastMessageSignature)
    return;

  lastMessageSignature = signature;
  messageList.replaceChildren();

  if (messages.length === 0) {
    const empty = document.createElement("div");
    empty.className = "empty-state";
    empty.textContent = state.selectedId ? "开始对话吧。" : "激活桌宠后即可开始对话。";
    messageList.appendChild(empty);
    requestResize();
    return;
  }

  const visibleMessages = state.showAllMessages
    ? messages
    : messages.slice(-state.loadedMessageCount);
  for (const message of visibleMessages)
    messageList.appendChild(createMessageElement(message));

  if (preserveScroll)
    messageList.scrollTop = Math.max(0, messageList.scrollHeight - previousBottomDistance);
  else
    messageList.scrollTop = messageList.scrollHeight;

  requestResize();
}

function loadOlderMessages() {
  if (state.showAllMessages || state.loadingOlderMessages)
    return;

  const messages = getMessages(state.selectedId);
  if (messages.length <= state.loadedMessageCount)
    return;

  state.loadingOlderMessages = true;
  state.loadedMessageCount += state.maxVisibleMessages;
  renderMessages({ preserveScroll: true });
  requestAnimationFrame(() => {
    state.loadingOlderMessages = false;
  });
}

function sendResizePayload(desired, compact) {
  const height = Math.max(52, Math.round(desired / 2) * 2);
  const signature = `${compact}:${height}`;
  if (signature === lastSentResizeSignature)
    return;

  lastSentResizeSignature = signature;
  sendMessage("resize", { height, compact });
}

function requestResize() {
  if (manualResizeEdge != null)
    return;

  clearTimeout(resizeDebounceTimer);
  resizeDebounceTimer = setTimeout(() => {
    if (manualResizeEdge != null)
      return;

    // Minimal mode is an explicit mode switch, so always allow it to shrink.
    if (state.compact) {
      sendResizePayload(12 + composer.getBoundingClientRect().height, true);
      return;
    }

    if (state.autoHeight === false)
      return;

    if (state.manualSizeOverride) {
      const desired = document.documentElement.scrollHeight;
      if (desired > window.innerHeight) {
        lastSentResizeSignature = "";
        sendMessage("resize", { height: Math.ceil(desired), compact: state.compact });
      }
      return;
    }

    const listStyle = getComputedStyle(messageList);
    let contentHeight = 0;
    for (const child of messageList.children)
      contentHeight += child.getBoundingClientRect().height;

    const gap = Number.parseFloat(listStyle.rowGap) || 0;
    const gaps = Math.max(0, messageList.children.length - 1) * gap;
    const padding = (Number.parseFloat(listStyle.paddingTop) || 0)
      + (Number.parseFloat(listStyle.paddingBottom) || 0);
    const listHeight = contentHeight + gaps + padding;
    const desired = 16 + titlebar.getBoundingClientRect().height
      + composer.getBoundingClientRect().height + listHeight;
    sendResizePayload(desired, state.compact);
  }, 80);
}

function updateComposer() {
  const pet = getSelectedPet();
  const disabled = pet == null;
  const signature = JSON.stringify([state.selectedId, pet?.name, pet?.busy === true]);
  if (signature === lastComposerSignature)
    return;

  lastComposerSignature = signature;
  input.disabled = disabled;
  sendButton.disabled = disabled;

  compactPetName.style.setProperty("--chip-color", pet ? colorFor(pet.name) : "#8fa3bf");
  compactPetName.replaceChildren();
  const compactName = document.createElement("span");
  compactName.textContent = pet?.name || "无桌宠";
  compactPetName.appendChild(compactName);
  compactPetName.title = pet ? `当前：${pet.name}${pet.busy ? "（回复中）" : ""}` : "没有已激活桌宠";

  input.placeholder = disabled
    ? "没有可对话的桌宠"
    : pet.busy
      ? `${pet.name} 正在回复/播报，可继续输入打断`
      : "输入消息，Enter 发送；可粘贴图片或文件";
}

function selectByOffset(offset) {
  if (state.pets.length === 0)
    return;

  const currentIndex = Math.max(0, state.pets.findIndex(pet => pet.id === state.selectedId));
  const nextIndex = (currentIndex + offset + state.pets.length) % state.pets.length;
  const nextPet = state.pets[nextIndex];
  if (nextPet && nextPet.id !== state.selectedId)
    sendMessage("select", { id: nextPet.id });
}

function submitInput() {
  const text = input.value.trim();
  const pet = getSelectedPet();
  if (!pet || (text.length === 0 && pendingImages.length === 0))
    return;

  const outgoingText = buildOutgoingText(text);

  sendMessage("send", { petId: pet.id, text: outgoingText, attachmentPaths: pendingImages.map(item => item.path) });
  pendingImages.length = 0;
  renderImagePreview();
  input.value = "";
  input.style.height = "auto";
  input.focus();
  requestResize();
}

ipcRenderer.on(channel, (_event, json) => {
  try {
    const payload = JSON.parse(json);
    switch (payload.type) {
      case "screenshot-failed":
      case "screenshot-complete":
        if (screenshotButton)
          screenshotButton.disabled = false;
        if (regionScreenshotButton)
          regionScreenshotButton.disabled = false;
        screenshotBusy = false;
        break;

      case "state": {
        state.pets = payload.pets || [];
        applyTheme(payload.theme || {});
        if ((state.selectedId == null || state.pets.some(pet => pet.id === state.selectedId) === false) && state.pets.length > 0) {
          payload.selectedId = state.pets[0].id;
          sendMessage("select", { id: payload.selectedId });
        }

        const pageSize = Math.max(1, Number(payload.maxVisibleMessages) || 6);
        const pageSizeChanged = pageSize !== state.maxVisibleMessages;
        state.maxVisibleMessages = pageSize;
        state.loadedMessageCount = pageSize;

        state.showAllMessages = payload.showAllMessages === true;
        state.hideOnEscape = payload.hideOnEscape !== false;
        state.autoHeight = payload.autoHeight !== false;
        state.minWindowHeight = Math.max(52, Number(payload.minWindowHeight) || 96);
        state.maxWindowHeight = Math.max(state.minWindowHeight, Number(payload.maxWindowHeight) || 460);

        const selectedChanged = state.selectedId !== payload.selectedId;
        state.selectedId = payload.selectedId || null;
        if (selectedChanged) {
          state.loadedMessageCount = state.maxVisibleMessages;
          if (messagesByPet.has(state.selectedId) === false)
            setMessages(state.selectedId, []);
        }

        const stateSignature = JSON.stringify({
          pets: state.pets.map(pet => [pet.id, pet.name, pet.busy === true]),
          selectedId: state.selectedId,
          maxVisibleMessages: state.maxVisibleMessages,
          loadedMessageCount: state.loadedMessageCount,
          showAllMessages: state.showAllMessages,
          hideOnEscape: state.hideOnEscape,
          autoHeight: state.autoHeight,
          minWindowHeight: state.minWindowHeight,
          maxWindowHeight: state.maxWindowHeight,
          theme: payload.theme || {}
        });

        if (stateSignature === lastStateSignature)
          break;

        lastStateSignature = stateSignature;
        renderPets();
        renderMessages();
        updateComposer();
        break;
      }

      case "history": {
        setMessages(payload.petId, payload.messages || []);
        if (payload.petId === state.selectedId) {
          state.loadedMessageCount = state.maxVisibleMessages;
          renderMessages();
          updateComposer();
        }
        break;
      }

      case "message": {
        appendMessage(payload);
        if (payload.petId === state.selectedId) {
          const followBottom = messageList.scrollHeight - messageList.scrollTop - messageList.clientHeight < 48;
          renderMessages({ preserveScroll: followBottom === false });
          updateComposer();
        }
        break;
      }
    }
  } catch (error) {
    console.error("QuickChat message error", error);
  }
});

document.addEventListener("DOMContentLoaded", () => {
  minimizeButton.addEventListener("click", () => {
    state.compact = true;
    state.manualSizeOverride = false;
    app.dataset.mode = "compact";
    renderMessages();
    updateComposer();
    requestResize();
  });

  restoreButton.addEventListener("click", () => {
    state.compact = false;
    state.manualSizeOverride = false;
    app.dataset.mode = "expanded";
    renderMessages();
    updateComposer();
    requestResize();
  });

  compactPetName.addEventListener("click", () => {
    selectByOffset(1);
  });

  function hideClearConfirm() {
    confirmOverlay.classList.remove("visible");
    confirmOverlay.setAttribute("aria-hidden", "true");
  }

  screenshotButton.addEventListener("click", () => {
    const pet = getSelectedPet();
    if (!pet || screenshotButton.disabled)
      return;

    screenshotButton.disabled = true;
    if (regionScreenshotButton)
      regionScreenshotButton.disabled = true;
    screenshotBusy = true;
    sendMessage("screenshot-request", { petId: pet.id });
  });

  regionScreenshotButton?.addEventListener("click", () => {
    const pet = getSelectedPet();
    if (!pet || regionScreenshotButton.disabled)
      return;

    screenshotButton.disabled = true;
    regionScreenshotButton.disabled = true;
    screenshotBusy = true;
    sendMessage("screenshot-region-request", { petId: pet.id });
  });

  clearButton.addEventListener("click", () => {
    if (state.selectedId == null)
      return;

    confirmOverlay.classList.add("visible");
    confirmOverlay.setAttribute("aria-hidden", "false");
    confirmOk.focus();
  });

  confirmOk.addEventListener("click", () => {
    hideClearConfirm();
    if (state.selectedId == null)
      return;

    state.loadedMessageCount = state.maxVisibleMessages;
    state.manualSizeOverride = false;
    setMessages(state.selectedId, []);
    sendMessage("clear");
    renderMessages();
    updateComposer();
    requestResize();
  });

  confirmCancel.addEventListener("click", hideClearConfirm);

  composer.addEventListener("submit", event => {
    event.preventDefault();
    submitInput();
  });

  window.addEventListener("keydown", event => {
    if (event.key === "Escape" && confirmOverlay.classList.contains("visible")) {
      hideClearConfirm();
      return;
    }

    if (event.key === "Escape" && state.hideOnEscape) {
      sendMessage("hide");
      return;
    }

    if (event.key === "Enter" && event.target === input && event.shiftKey === false) {
      event.preventDefault();
      submitInput();
      return;
    }

    // Numpad 0-9 must remain ordinary input keys. Only the separate arrow keys switch pets.
    if (event.key === "ArrowLeft" || event.key === "ArrowRight" || event.key === "ArrowUp" || event.key === "ArrowDown") {
      const usingModifier = event.altKey || event.ctrlKey || event.metaKey;
      const canSwitch = usingModifier || document.activeElement !== input || input.value.length === 0;
      if (canSwitch) {
        event.preventDefault();
        selectByOffset(event.key === "ArrowLeft" || event.key === "ArrowUp" ? -1 : 1);
      }
    }
  });
  for (const handle of document.querySelectorAll(".resize-handle")) {
    const edge = handle.dataset.edge;
    handle.addEventListener("pointerdown", event => {
      if (event.button !== 0)
        return;

      event.preventDefault();
      event.stopPropagation();
      manualResizeEdge = edge;
      state.manualSizeOverride = true;
      handle.setPointerCapture(event.pointerId);
      sendMessage("resize-start", { edge });
    });

    handle.addEventListener("pointermove", event => {
      if (manualResizeEdge == null)
        return;

      pendingResizeMove = true;
      if (resizeMoveScheduled == false) {
        resizeMoveScheduled = true;
        requestAnimationFrame(() => {
          resizeMoveScheduled = false;
          sendResizeMove();
        });
      }
    });

    const endManualResize = event => {
      if (manualResizeEdge == null)
        return;

      if (event.type === "pointerup" && pendingResizeMove != null) {
        sendResizeMove();
      }

      manualResizeEdge = null;
      pendingResizeMove = null;
      if (handle.hasPointerCapture?.(event.pointerId) === true)
        handle.releasePointerCapture(event.pointerId);
      sendMessage("resize-end");
    };

    handle.addEventListener("pointerup", endManualResize);
    handle.addEventListener("pointercancel", endManualResize);
    handle.addEventListener("lostpointercapture", () => {
      if (manualResizeEdge === edge) {
        manualResizeEdge = null;
        pendingResizeMove = null;
        sendMessage("resize-end");
      }
    });
  }

  // 滚动分页已注释：聊天记录固定只显示最近 MaxVisibleMessages 条，
  // 只有打开“显示所有消息”时才会显示全部。
  // messageList.addEventListener("scroll", () => {
  //   if (state.compact || state.selectedId == null || state.loadingOlderMessages || state.showAllMessages)
  //     return;
  //
  //   const loadThreshold = Math.min(48, Math.max(16, messageList.clientHeight * 0.10));
  //   if (messageList.scrollTop <= loadThreshold)
  //     loadOlderMessages();
  // });

  lastInputHeight = input.offsetHeight;
  input.addEventListener("input", () => {
    input.style.height = "auto";
    input.style.height = `${Math.min(96, input.scrollHeight)}px`;
    if (input.offsetHeight !== lastInputHeight) {
      lastInputHeight = input.offsetHeight;
      requestResize();
    }
  });

  // 老版本 Electron 的已知问题：页面里存在文本选区时，标题栏的 app-region 拖动会失灵。
  // 指针进入标题栏或按下时清掉选区，保证拖动始终能开始。
  titlebar.addEventListener("mouseenter", () => {
    const selection = window.getSelection?.();
    if (selection && selection.rangeCount > 0)
      selection.removeAllRanges();
  });

  titlebar.addEventListener("mousedown", () => {
    const selection = window.getSelection?.();
    if (selection && selection.rangeCount > 0)
      selection.removeAllRanges();
  });
  attachButton.addEventListener("click", () => {
    imageInput.click();
  });

  imageInput.addEventListener("change", () => {
    for (const file of imageInput.files || [])
      addPendingImage(file, false);
    imageInput.value = "";
  });

  input.addEventListener("paste", event => {
    const files = Array.from(event.clipboardData?.files || []);
    if (files.length === 0)
      return;

    event.preventDefault();
    for (const file of files)
      addPendingImage(file, true);
  });

  let imageDragDepth = 0;

  document.addEventListener("dragenter", event => {
    if (!isFileDragEvent(event))
      return;
    imageDragDepth++;
    setImageDragActive(true);
  });

  document.addEventListener("dragover", event => {
    if (!isFileDragEvent(event))
      return;
    event.preventDefault();
    event.dataTransfer.dropEffect = "copy";
    setImageDragActive(true);
  });

  document.addEventListener("dragleave", event => {
    if (!isFileDragEvent(event))
      return;
    imageDragDepth = Math.max(0, imageDragDepth - 1);
    if (imageDragDepth === 0)
      setImageDragActive(false);
  });

  document.addEventListener("drop", event => {
    imageDragDepth = 0;
    setImageDragActive(false);
    const files = filesFromDataTransfer(event.dataTransfer);
    if (files.length === 0)
      return;

    event.preventDefault();
    event.stopPropagation();
    const dropTarget = event.target instanceof Element ? event.target : null;
    if (dropTarget?.closest("#messageList"))
      sendDroppedImagesNow(files);
    else
      addDroppedImages(files);
  });

  renderPets();
  renderMessages();
  renderImagePreview();
  updateComposer();
  input.focus();
  sendMessage("ready");
});

